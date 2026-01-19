using BigMission.Shared.Utilities;
using MessagePack;
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
        cacheMux.ConnectionRestored += CacheMux_ConnectionRestoredAsync;
    }


    public async Task ProcessAsync(TimingMessage tm)
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

        await EnsureEventShutdownSubscriptionAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                await RunCheckForFinishedAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error reading event status stream");
            }
        }
    }

    public async Task RunCheckForFinishedAsync(CancellationToken stoppingToken)
    {
        using (await sessionContext.SessionStateLock.AcquireReadLockAsync(stoppingToken))
        {
            if (last != null)
            {
                var pc = SessionStateMapper.ToPatch(sessionContext.SessionState);
                CheckForFinished(last, SessionStateMapper.PatchToEntity(pc));
            }

            var pl = SessionStateMapper.ToPatch(sessionContext.SessionState);
            last = SessionStateMapper.PatchToEntity(pl);
        }
    }

    private async void CacheMux_ConnectionRestoredAsync(object? sender, ConnectionFailedEventArgs e)
    {
        await EnsureEventShutdownSubscriptionAsync();
    }

    private async Task EnsureEventShutdownSubscriptionAsync()
    {
        try
        {
            var sub = cacheMux.GetSubscriber();
            await sub.UnsubscribeAsync(new RedisChannel(Consts.EVENT_SHUTDOWN_SIGNAL, RedisChannel.PatternMode.Literal));
            var ch = await sub.SubscribeAsync(new RedisChannel(Consts.EVENT_SHUTDOWN_SIGNAL, RedisChannel.PatternMode.Literal));
            ch.OnMessage(HandleEventShutdown);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error ensuring event shutdown subscription");
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
        await db.Sessions
            .Where(s => s.EventId == eventId && s.Id == sessionId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                s => s.LastUpdated,
                DateTime.UtcNow), stoppingToken);
    }


    protected virtual async Task SetSessionAsLiveAsync(int eventId, int sessionId)
    {
        using var db = tsContext.CreateDbContext();

        // Set all sessions as not live for this event, then set the specific session as live
        // This is done in a single SQL UPDATE statement for efficiency
        await db.Sessions
            .Where(s => s.EventId == eventId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                s => s.IsLive,
                s => s.Id == sessionId));
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
            //Trace.WriteLine($"Session {SessionId} finishing in progress: {changes} cars completed checkered lap (last change count {lastCheckeredChangedCount})");

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
                    // Make deep copy of car so that we can track changes
                    var mp = MessagePackSerializer.Serialize(carPosition);
                    var copy = MessagePackSerializer.Deserialize<CarPosition>(mp);
                    checkeredCarPositionsLookup[carPosition.Number] = copy;
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
                    Logger.LogWarning("Session was already finalized for session {sessionId}. Checking for inconsistencies...", SessionId);

                    // We do not want to overwrite existing data with empty data. Check to see if there is more data to save on
                    // the latest dataset. This should typically allow for replacing partial session states with more complete ones,
                    // However it is possible that the finishing car positions are not as accurate if there are additional laps in the
                    // newer data. This is a trade-off best effort to ensure we do not lose data.
                    if (sessionState.EventEntries.Count >= existingResult.SessionState?.EventEntries.Count
                        && sessionState.CarPositions.Count >= existingResult.SessionState?.CarPositions.Count
                        && sessionState.FlagDurations.Count >= existingResult.SessionState?.FlagDurations.Count)
                    {
                        Logger.LogInformation("Updating session state for session {sessionId} with more data", SessionId);
                        existingResult.SessionState = sessionState;
                    }
                    else
                    {
                        Logger.LogWarning("Saved session state has more data than current for session {sessionId}. Not updating session state.", SessionId);
                    }

                    if (controlLogs.Count > existingResult.ControlLogs.Count)
                    {
                        Logger.LogInformation("Updating control logs for session {sessionId} with more entries", SessionId);
                        existingResult.ControlLogs = controlLogs;
                    }
                    else
                    {
                        Logger.LogInformation("Saved control logs have more entries than current for session {sessionId}. Not updating control logs.", SessionId);
                    }
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

    /// <summary>
    /// Prior to the service being shutdown, ensure any active session is persisted.
    /// </summary>
    /// <param name="msg">event IDs being shutdown</param>
    private void HandleEventShutdown(ChannelMessage msg)
    {
        try
        {
            var eventIds = JsonSerializer.Deserialize<List<int>>(msg.Message.ToString());
            if (eventIds?.Contains(eventId) ?? false)
            {
                Logger.LogInformation("Received shutdown signal for event {eventId}", eventId);
                FinalizeSession();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing shutdown signal");
        }
    }
}
