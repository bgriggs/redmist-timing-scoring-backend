using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.TimingAndScoringService.Utilities;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus;

public class SessionMonitor
{
    public int SessionId { get; private set; }
    private ILogger Logger { get; }

    private readonly Debouncer lastUpdatedDebouncer = new(TimeSpan.FromMilliseconds(1500));
    private readonly int eventId;
    private readonly IDbContextFactory<TsContext> tsContext;

    private readonly static Flags[] activeSessionFlags = { Flags.White, Flags.Green, Flags.Yellow, Flags.Purple35 };
    private readonly static Flags[] finishedSessionFlags = { Flags.Checkered };
    private DateTime? finishingStartedTimestamp;
    private DateTime? finishingEventLastTimestamp;
    private readonly Dictionary<string, CarPosition> checkeredCarPositionsLookup = [];
    private int lastCheckeredChangedCount;
    private DateTime? lastCheckeredChangedCountTimestamp;

    private Payload? currentPayload;
    private Payload? GetCurrentPayload()
    {
        lock (payloadLock)
        {
            return currentPayload;
        }
    }

    public void SetCurrentPayload(Payload? value)
    {
        lock (payloadLock)
        {
            if (currentPayload != value)
            {
                if (currentPayload != null && value != null)
                {
                    CheckForFinished(currentPayload, value);
                }
                currentPayload = value;
            }
        }
    }
    private readonly Lock payloadLock = new();

    public event Action? FinalizedSession;


    public SessionMonitor(int eventId, IDbContextFactory<TsContext> tsContext, ILoggerFactory loggerFactory)
    {
        this.eventId = eventId;
        this.tsContext = tsContext;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    public async Task ProcessSessionAsync(int sessionId, CancellationToken stoppingToken = default)
    {
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
        SetCurrentPayload(null);
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

                if (GetCurrentPayload() != null)
                {
                    var result = new SessionResult
                    {
                        EventId = eventId,
                        SessionId = SessionId,
                        Start = session.StartTime,
                        Payload = GetCurrentPayload()
                    };
                    db.SessionResults.Add(result);
                }

                db.SaveChanges();
                ClearSession();
                FireFinalizedSession();
            }
            else
            {
                Logger.LogWarning("Cannot finalize session {sessionId}. Not found in database.", SessionId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error finalizing session {sessionId}", SessionId);
        }
    }

    protected virtual async Task SetSessionAsLiveAsync(int eventId, int sessionId)
    {
        using var db = tsContext.CreateDbContext();
        await db.Database.ExecuteSqlAsync($"UPDATE Sessions SET IsLive = 0 WHERE EventId = {eventId}");
        await db.Database.ExecuteSqlAsync($"UPDATE Sessions SET IsLive = 1 WHERE EventId = {eventId} AND Id = {sessionId}");
    }

    /// <summary>
    /// Looks at the last and current status to for flag status changes and subsequent car finishing or event status stopping.
    /// </summary>
    private void CheckForFinished(Payload last, Payload current)
    {
        // See if the event is in the process of finishing, i.e. last lap during checkered flag
        if (finishingStartedTimestamp is not null && current.EventStatus != null)
        {
            var eventTime = PositionMetadataProcessor.ParseRMTime(current.EventStatus.LocalTimeOfDay);
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
        else if (last.EventStatus != null && current.EventStatus != null &&
            activeSessionFlags.Contains(last.EventStatus.Flag) &&
            finishedSessionFlags.Contains(current.EventStatus.Flag))
        {
            Logger.LogInformation("Session {sessionId} finishing started", SessionId);
            foreach (var carPosition in current.CarPositions)
            {
                if (carPosition.Number != null)
                {
                    checkeredCarPositionsLookup[carPosition.Number] = carPosition;
                }
            }

            finishingStartedTimestamp = PositionMetadataProcessor.ParseRMTime(current.EventStatus.LocalTimeOfDay);
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
                carPosition.LastLap != checkeredPos.LastLap)
            {
                changes++;
            }
        }

        return changes;
    }

    protected void FireFinalizedSession()
    {
        FinalizedSession?.Invoke();
    }
}
