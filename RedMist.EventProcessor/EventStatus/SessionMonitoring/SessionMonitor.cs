using BigMission.Shared.Utilities;
using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventProcessor.EventStatus.PositionEnricher;
using RedMist.EventProcessor.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Mappers;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.EventProcessor.EventStatus.SessionMonitoring;

/// <summary>
/// Tracks the current session for an event, updating its last updated timestamp and finalizing it when it ends.
/// This could be triggered by either a session change message or by detecting the end of a session at the end of an event
/// where no new session is started. Also runs as a background service to monitor for session finalization.
/// </summary>
public class SessionMonitor : BackgroundService
{
    public int SessionId { get; private set; }
    private ILogger Logger { get; }

    private readonly Debouncer lastUpdatedDebouncer = new(TimeSpan.FromMilliseconds(1500));
    private readonly int eventId;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly SessionContext sessionContext;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly static Flags[] activeSessionFlags = [Flags.White, Flags.Green, Flags.Yellow, Flags.Purple35];
    private readonly static Flags[] finishedSessionFlags = [Flags.Checkered];
    private DateTime? finishingStartedTimestamp;
    private DateTime? finishingEventLastTimestamp;
    private readonly Dictionary<string, CarPosition> checkeredCarPositionsLookup = [];
    private int lastCheckeredChangedCount;
    private DateTime? lastCheckeredChangedCountTimestamp;
    private Session? lastSession;
    private SessionState? last = null;
    public event Action? FinalizedSession;


    public SessionMonitor(IConfiguration configuration, IDbContextFactory<TsContext> tsContext, ILoggerFactory loggerFactory,
        SessionContext sessionContext, IConnectionMultiplexer cacheMux)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        eventId = configuration.GetValue("event_id", 0);
        this.tsContext = tsContext;
        this.sessionContext = sessionContext;
        this.cacheMux = cacheMux;
    }


    public async Task Process(TimingMessage tm)
    {
        if (tm.Type != Consts.EVENT_SESSION_CHANGED_TYPE)
            return;

        lastSession = JsonSerializer.Deserialize<Session>(tm.Data);
        if (lastSession != null)
        {
            await ProcessAsync(lastSession.Id, sessionContext.CancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            sessionContext.SetSessionClassMetadata();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error setting session class metadata");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                await RunCheckForFinished(stoppingToken);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error reading event status stream");
            }
        }
    }

    public async Task RunCheckForFinished(CancellationToken stoppingToken)
    {
        using (await sessionContext.SessionStateLock.AcquireReadLockAsync(stoppingToken))
        {
            if (last != null)
            {
                var pc = SessionStateMapper.ToPatch(sessionContext.SessionState);
                pc.CarPositions = null; // Don't need to keep car positions
                CheckForFinished(last, SessionStateMapper.PatchToEntity(pc));
            }

            var pl = SessionStateMapper.ToPatch(sessionContext.SessionState);
            pl.CarPositions = null; // Don't need to keep car positions
            last = SessionStateMapper.PatchToEntity(pl);
        }
    }


    public async Task ProcessAsync(int sessionId, CancellationToken stoppingToken = default)
    {
        if (sessionId == 999999)
            return;

        if (SessionId == sessionId)
        {
            _ = lastUpdatedDebouncer.ExecuteAsync(() => SaveLastUpdatedTimestampAsync(eventId, sessionId, stoppingToken), stoppingToken);
        }
        else // New session
        {
            Logger.LogInformation("New session {sessionId} received for event {eventId}", sessionId, eventId);

            // Finalize the previous session
            FinalizeSession();

            // Start the new session
            ClearSession();
            SessionId = sessionId;
            await SetSessionAsLiveAsync(eventId, sessionId);
        }
    }

    protected void ClearSession()
    {
        SessionId = 0;
        finishingStartedTimestamp = null;
        finishingEventLastTimestamp = null;
        checkeredCarPositionsLookup.Clear();
        lastCheckeredChangedCount = 0;
        lastCheckeredChangedCountTimestamp = null;
    }

    protected virtual async Task SaveLastUpdatedTimestampAsync(int eventId, int sessionId, CancellationToken stoppingToken = default)
    {
        using var db = await tsContext.CreateDbContextAsync(stoppingToken);
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.EventId == eventId && s.Id == sessionId, stoppingToken);
        if (session != null)
        {
            session.LastUpdated = DateTime.UtcNow;
            await db.SaveChangesAsync(stoppingToken);
        }
    }

    protected virtual void FinalizeSession()
    {
        Logger.LogInformation("Finalizing session {sessionId}...", SessionId);

        try
        {
            using var db = tsContext.CreateDbContext();
            var session = db.Sessions.FirstOrDefault(s => s.EventId == eventId && s.Id == SessionId);
            if (session != null)
            {
                session.IsLive = false;
                session.EndTime = DateTime.UtcNow;

                var sessionState = new SessionState();
                if (sessionContext.SessionState.SessionId == SessionId)
                    sessionState = sessionContext.SessionState;
                else if (sessionContext.PreviousSessionState.SessionId == SessionId)
                    sessionState = sessionContext.PreviousSessionState;

                // Get control logs for the session
                var logCacheKey = string.Format(Consts.CONTROL_LOG, eventId);
                var cache = cacheMux.GetDatabase();
                var json = cache.StringGet(logCacheKey);
                var controlLogs = new List<ControlLogEntry>();
                if (!json.IsNullOrEmpty)
                {
                    var ccl = JsonSerializer.Deserialize<CarControlLogs>(json.ToString());
                    controlLogs = ccl?.ControlLogEntries ?? [];
                }

                var existingResult = db.SessionResults.FirstOrDefault(r => r.EventId == eventId && r.SessionId == SessionId);
                if (existingResult != null)
                {
                    existingResult.SessionState = sessionState;
                    existingResult.ControlLogs = controlLogs;
                }
                else
                {
                    var result = new SessionResult
                    {
                        EventId = eventId,
                        SessionId = SessionId,
                        Start = session.StartTime,
                        SessionState = sessionState,
                        ControlLogs = controlLogs
                    };
                    db.SessionResults.Add(result);
                }
            }

            db.SaveChanges();
            ClearSession();
            FireFinalizedSession();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error finalizing session {sessionId}", SessionId);
        }
    }

    protected virtual async Task SetSessionAsLiveAsync(int eventId, int sessionId)
    {
        using var db = tsContext.CreateDbContext();

        // Set all sessions as not live for this event
        var sessionsToUpdate = await db.Sessions.Where(s => s.EventId == eventId).ToListAsync();
        foreach (var session in sessionsToUpdate)
        {
            session.IsLive = false;
        }

        // Set the specific session as live
        var targetSession = sessionsToUpdate.FirstOrDefault(s => s.Id == sessionId);
        targetSession?.IsLive = true;

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Looks at the last and current status to for flag status changes and subsequent car finishing or event status stopping.
    /// </summary>
    public void CheckForFinished(SessionState last, SessionState current)
    {
        // See if the event is in the process of finishing, i.e. last lap during checkered flag
        if (finishingStartedTimestamp is not null)
        {
            var eventTime = PositionMetadataProcessor.ParseRMTime(current.LocalTimeOfDay);
            var changes = GetCarCheckeredLapChangedCount(current.CarPositions);

            // As cars continue to complete their final lap, update the checkered lap count/timestamp
            if (lastCheckeredChangedCount != changes)
            {
                lastCheckeredChangedCount = changes;
                lastCheckeredChangedCountTimestamp = eventTime;
            }
            // Check for finishing conditions: 60 seconds have passed since the last car's checkered lap change
            else if (lastCheckeredChangedCountTimestamp is not null &&
                eventTime - lastCheckeredChangedCountTimestamp > TimeSpan.FromSeconds(60))
            {
                Logger.LogInformation("Session {sessionId} finishing completed 60 seconds interval", SessionId);
                FinalizeSession();
            }
            // When event time stops changing, finalize the session
            else if (finishingEventLastTimestamp is not null && finishingEventLastTimestamp == eventTime)
            {
                Logger.LogInformation("Session {sessionId} finishing completed with event time stop", SessionId);
                FinalizeSession();
            }

            finishingEventLastTimestamp = eventTime;
        }
        // See if event status changed from active to finished
        else if (activeSessionFlags.Contains(last.CurrentFlag) &&
            finishedSessionFlags.Contains(current.CurrentFlag))
        {
            Logger.LogInformation("Session {sessionId} finishing started", SessionId);
            foreach (var carPosition in current.CarPositions)
            {
                if (carPosition.Number != null)
                {
                    checkeredCarPositionsLookup[carPosition.Number] = carPosition;
                }
            }

            finishingStartedTimestamp = PositionMetadataProcessor.ParseRMTime(current.LocalTimeOfDay);
        }
    }

    /// <summary>
    /// Get number of cars that finished checkered lap since the checkered flag was thrown.
    /// </summary>
    private int GetCarCheckeredLapChangedCount(List<CarPosition> currentPositions)
    {
        int changes = 0;
        foreach (var carPosition in currentPositions)
        {
            if (carPosition.Number != null &&
                checkeredCarPositionsLookup.TryGetValue(carPosition.Number, out var checkeredPos) &&
                carPosition.LastLapCompleted != checkeredPos.LastLapCompleted)
            {
                changes++;
            }
        }

        return changes;
    }

        protected void FireFinalizedSession()
        {
            FinalizedSession?.Invoke();

            // Handle session finalization callback
            if (lastSession != null)
            {
                _ = Task.Run(async () =>
                {
                    await sessionContext.NewSession(lastSession.Id, lastSession.Name);
                    sessionContext.SetSessionClassMetadata();
                });
            }
        }
    }
