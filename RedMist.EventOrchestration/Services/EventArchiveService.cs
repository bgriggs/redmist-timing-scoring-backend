using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.EventOrchestration.Utilities;

namespace RedMist.EventOrchestration.Services;

public class EventArchiveService : BackgroundService
{
    private readonly ILoggerFactory loggerFactory;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly IArchiveStorage archiveStorage;

    private ILogger Logger { get; }


    public EventArchiveService(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IArchiveStorage archiveStorage)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.loggerFactory = loggerFactory;
        this.tsContext = tsContext;
        this.archiveStorage = archiveStorage;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const int maxRetriesPerDay = 3;
        var mountainTimeZone = TimeZoneHelper.GetMountainTimeZone();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait until after midnight Mountain Time
                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, mountainTimeZone);
                var nextMidnight = now.Date.AddDays(1);

                if (now.TimeOfDay < TimeSpan.Zero || now.Date == nextMidnight.AddDays(-1))
                {
                    // We're before midnight or already past it today, wait until next midnight
                    var delayUntilMidnight = nextMidnight - now;
                    if (delayUntilMidnight.TotalMilliseconds > 0)
                    {
                        Logger.LogInformation("Waiting until midnight Mountain Time ({nextMidnight}) to run archive process. Current time: {now}",
                            nextMidnight, now);
                        await Task.Delay(delayUntilMidnight, stoppingToken);
                        continue;
                    }
                }

                // Run archive process with retry logic
                var retryCount = 0;
                var archiveSuccessful = false;

                while (retryCount < maxRetriesPerDay && !archiveSuccessful && !stoppingToken.IsCancellationRequested)
                {
                    retryCount++;
                    Logger.LogInformation("Starting archive process, attempt {retryCount} of {maxRetries}", retryCount, maxRetriesPerDay);

                    var allEventsSuccessful = true;
                    try
                    {
                        var eventsToArchive = await LoadEventsToArchiveAsync();
                        Logger.LogInformation("Found {count} events to archive.", eventsToArchive.Count);

                        if (eventsToArchive.Count == 0)
                        {
                            Logger.LogInformation("No events to archive.");
                            archiveSuccessful = true;
                            break;
                        }

                        foreach (var eventId in eventsToArchive)
                        {
                            Logger.LogInformation("Archiving logs for event {eventId}...", eventId);
                            var eventArchive = new EventLogArchive(loggerFactory, tsContext, archiveStorage);
                            var eventLogArchived = await eventArchive.ArchiveEventLogsAsync(eventId, stoppingToken);
                            if (eventLogArchived)
                            {
                                // Archive laps for all sessions in this event
                                Logger.LogInformation("Archiving laps for event {eventId}...", eventId);
                                var lapsArchived = await ArchiveEventLapsAsync(eventId, stoppingToken);
                                if (lapsArchived)
                                {
                                    await using var dbContext = await tsContext.CreateDbContextAsync(stoppingToken);
                                    var ev = await dbContext.Events.FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken: stoppingToken);
                                    if (ev != null)
                                    {
                                        ev.IsArchived = true;
                                        await dbContext.SaveChangesAsync(stoppingToken);
                                        Logger.LogInformation("Event {eventId} archived successfully.", eventId);
                                    }
                                    else
                                    {
                                        Logger.LogWarning("Event {eventId} not found in database after archiving logs.", eventId);
                                        allEventsSuccessful = false;
                                    }
                                }
                                else
                                {
                                    Logger.LogWarning("Failed to archive laps for event {eventId}.", eventId);
                                    allEventsSuccessful = false;
                                }
                            }
                            else
                            {
                                Logger.LogWarning("Failed to archive logs for event {eventId}.", eventId);
                                allEventsSuccessful = false;
                            }
                        }

                        archiveSuccessful = allEventsSuccessful;

                        if (archiveSuccessful)
                        {
                            Logger.LogInformation("Archive process completed successfully on attempt {retryCount}", retryCount);
                        }
                        else
                        {
                            Logger.LogWarning("Archive process completed with some failures on attempt {retryCount}", retryCount);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "An error occurred during archive attempt {retryCount} of {maxRetries}", retryCount, maxRetriesPerDay);
                        archiveSuccessful = false;
                    }

                    // If not successful and retries remain, wait before retrying
                    if (!archiveSuccessful && retryCount < maxRetriesPerDay)
                    {
                        var retryDelay = TimeSpan.FromMinutes(5);
                        Logger.LogInformation("Waiting {retryDelay} minutes before retry {nextRetry} of {maxRetries}", 
                            retryDelay.TotalMinutes, retryCount + 1, maxRetriesPerDay);
                        await Task.Delay(retryDelay, stoppingToken);
                    }
                }

                if (!archiveSuccessful)
                {
                    Logger.LogWarning("Archive process failed after {maxRetries} attempts. Will retry tomorrow after midnight.", maxRetriesPerDay);
                }

                // Wait until next midnight Mountain Time
                var currentTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, mountainTimeZone);
                var tomorrowMidnight = currentTime.Date.AddDays(1);
                var delayUntilNextRun = tomorrowMidnight - currentTime;

                Logger.LogInformation("Waiting until next midnight Mountain Time ({tomorrowMidnight}) to run archive process again. Delay: {delay}", 
                    tomorrowMidnight, delayUntilNextRun);
                await Task.Delay(delayUntilNextRun, stoppingToken);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "An unexpected error occurred in the event archive service main loop.");
                // Wait 1 hour before trying again if there's an unexpected error
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
    }

    private async Task<List<int>> LoadEventsToArchiveAsync()
    {
        await using var dbContext = await tsContext.CreateDbContextAsync();
        var eventIds = await dbContext.Events
            .Where(e => !e.IsArchived && e.EndDate < DateTime.UtcNow.AddDays(-1) && !e.IsLive && !e.IsSimulation)
            .Select(e => e.Id)
            .ToListAsync();
        return eventIds;
    }

    private async Task<bool> ArchiveEventLapsAsync(int eventId, CancellationToken stoppingToken)
    {
        try
        {
            await using var dbContext = await tsContext.CreateDbContextAsync(stoppingToken);

            // Get all distinct sessions for this event that have laps
            var sessionIds = await dbContext.CarLapLogs
                .Where(l => l.EventId == eventId)
                .Select(l => l.SessionId)
                .Distinct()
                .ToListAsync(stoppingToken);

            if (sessionIds.Count == 0)
            {
                Logger.LogInformation("No laps found to archive for event {eventId}", eventId);
                return true;
            }

            Logger.LogInformation("Found {count} sessions with laps to archive for event {eventId}", sessionIds.Count, eventId);

            foreach (var sessionId in sessionIds)
            {
                Logger.LogInformation("Archiving laps for event {eventId}, session {sessionId}...", eventId, sessionId);
                var lapsArchive = new LapsLogArchive(loggerFactory, tsContext, archiveStorage);
                var success = await lapsArchive.ArchiveLapsAsync(eventId, sessionId, stoppingToken);

                if (!success)
                {
                    Logger.LogWarning("Failed to archive laps for event {eventId}, session {sessionId}", eventId, sessionId);
                    return false;
                }
            }

            Logger.LogInformation("Successfully archived laps for all sessions in event {eventId}", eventId);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error archiving laps for event {eventId}", eventId);
            return false;
        }
    }

}
