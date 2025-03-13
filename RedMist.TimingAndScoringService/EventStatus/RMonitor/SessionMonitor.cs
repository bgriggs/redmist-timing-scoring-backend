using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingAndScoringService.Utilities;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor;

public class SessionMonitor
{
    public int SessionId { get; private set; }
    private ILogger Logger { get; }

    private readonly Debouncer lastUpdatedDebouncer = new(TimeSpan.FromMilliseconds(1500));
    private readonly TimeSpan finalizeSessionDelay = TimeSpan.FromMinutes(10);
    private Timer? finalizeSessionTimer;
    private readonly int eventId;
    private readonly IDbContextFactory<TsContext> tsContext;


    public SessionMonitor(int eventId, IDbContextFactory<TsContext> tsContext, ILoggerFactory loggerFactory)
    {
        this.eventId = eventId;
        this.tsContext = tsContext;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    public async Task ProcessSession(int sessionId, CancellationToken stoppingToken = default)
    {
        if (SessionId == sessionId)
        {
            await lastUpdatedDebouncer.ExecuteAsync(() => SaveLastUpdatedTimestampUpdate(eventId, sessionId, stoppingToken), stoppingToken);
            ResetFinalizeSessionTimer();
        }
        else // New session
        {
            Logger.LogInformation("New session {sessionId} received for event {eventId}", sessionId, eventId);

            // Finalize the previous session
            FinalizeSession(null);

            // Start the new session
            SessionId = sessionId;
            await SetSessionAsLive(eventId, sessionId);
        }
    }

    protected virtual async Task SaveLastUpdatedTimestampUpdate(int eventId, int sessionId, CancellationToken stoppingToken = default)
    {
        using var db = await tsContext.CreateDbContextAsync(stoppingToken);
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.EventId == eventId && s.Id == sessionId, stoppingToken);
        if (session != null)
        {
            session.LastUpdated = DateTime.UtcNow;
            await db.SaveChangesAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Timer to track a session timeout when we stop receiving events from the timing system.
    /// </summary>
    private void ResetFinalizeSessionTimer()
    {
        try
        {
            finalizeSessionTimer?.Dispose();
        }
        catch { }
        finalizeSessionTimer = new Timer(FinalizeSession, null, (int)finalizeSessionDelay.TotalMilliseconds, Timeout.Infinite);
    }

    protected virtual void FinalizeSession(object? state)
    {
        Logger.LogInformation("Finalizing session {sessionId}...", SessionId);
        try
        {
            finalizeSessionTimer?.Dispose();
        }
        catch { }

        try
        {
            using var db = tsContext.CreateDbContext();
            var session = db.Sessions.FirstOrDefault(s => s.EventId == eventId && s.Id == SessionId);
            if (session != null)
            {
                session.IsLive = false;
                session.EndTime = DateTime.UtcNow;
                db.SaveChanges();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error finalizing session {sessionId}", SessionId);
        }
    }

    protected virtual async Task SetSessionAsLive(int eventId, int sessionId)
    {
        using var db = tsContext.CreateDbContext();
        await db.Database.ExecuteSqlAsync($"UPDATE Sessions SET IsLive = 0 WHERE EventId = {eventId}");
        await db.Database.ExecuteSqlAsync($"UPDATE Sessions SET IsLive = 1 WHERE EventId = {eventId} AND Id = {sessionId}");
    }
}
