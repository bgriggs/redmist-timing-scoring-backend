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
    private readonly EmailHelper emailHelper;

    private ILogger Logger { get; }


    public EventArchiveService(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IArchiveStorage archiveStorage, EmailHelper emailHelper)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.loggerFactory = loggerFactory;
        this.tsContext = tsContext;
        this.archiveStorage = archiveStorage;
        this.emailHelper = emailHelper;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const int maxRetriesPerDay = 3;
        var mountainTimeZone = TimeZoneHelper.GetMountainTimeZone();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Calculate next midnight Mountain Time
                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, mountainTimeZone);
                var nextMidnight = now.Date.AddDays(1);
                var delayUntilMidnight = nextMidnight - now;

                Logger.LogInformation("Waiting until midnight Mountain Time ({nextMidnight}) to run archive process. Current time: {now}, Delay: {delay}",
                    nextMidnight, now, delayUntilMidnight);
                await Task.Delay(delayUntilMidnight, stoppingToken);

                // Run archive process with retry logic
                await RunArchiveProcessWithRetriesAsync(maxRetriesPerDay, stoppingToken);

                // Run simulated event purge
                await RunSimulatedEventPurgeAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                Logger.LogInformation("Event archive service is stopping.");
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "An unexpected error occurred in the event archive service main loop.");
                await SendArchiveFailureEmailAsync($"Unexpected error in archive service main loop: {ex.Message}", null, 0, ex);
                // Wait 1 hour before trying again if there's an unexpected error
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
    }

    private async Task RunArchiveProcessWithRetriesAsync(int maxRetriesPerDay, CancellationToken stoppingToken)
    {
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
                    var eventArchived = await ArchiveSingleEventAsync(eventId, stoppingToken);
                    if (!eventArchived)
                    {
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
                await SendArchiveFailureEmailAsync("Archive process failed after all retry attempts", null, maxRetriesPerDay, null);
            }
        }

    private async Task<bool> ArchiveSingleEventAsync(int eventId, CancellationToken stoppingToken)
    {
        Exception? lastException = null;

        Logger.LogInformation("Archiving logs for event {eventId}...", eventId);
        var purgeUtilities = new PurgeUtilities(loggerFactory, tsContext);
        var eventArchive = new EventLogArchive(loggerFactory, tsContext, archiveStorage, purgeUtilities);
        var eventLogArchived = await eventArchive.ArchiveEventLogsAsync(eventId, stoppingToken);

        if (!eventLogArchived)
        {
            Logger.LogWarning("Failed to archive logs for event {eventId}.", eventId);
            await SendArchiveFailureEmailAsync("Failed to archive event logs", eventId, 0, lastException);
            return false;
        }

        // Archive laps for all sessions in this event
        Logger.LogInformation("Archiving laps for event {eventId}...", eventId);
        var (lapsArchived, lapsException) = await ArchiveEventLapsAsync(eventId, stoppingToken);
        lastException = lapsException;

        if (!lapsArchived)
        {
            Logger.LogWarning("Failed to archive laps for event {eventId}.", eventId);
            await SendArchiveFailureEmailAsync("Failed to archive laps", eventId, 0, lastException);
            return false;
        }

        // Archive X2 data
        Logger.LogInformation("Archiving X2 data for event {eventId}...", eventId);
        var x2Archiver = new X2LogArchive(loggerFactory, tsContext, archiveStorage, purgeUtilities);
        var x2Result = await x2Archiver.ArchiveX2DataAsync(eventId, stoppingToken);
        if (!x2Result)
        {
            Logger.LogWarning("Failed to archive X2 data for event {eventId}.", eventId);
            await SendArchiveFailureEmailAsync("Failed to archive X2 data", eventId, 0, lastException);
            return false;
        }

        // Archive flags for all sessions in this event
        Logger.LogInformation("Archiving flags for event {eventId}...", eventId);
        var (flagsArchived, flagsException) = await ArchiveEventFlagsAsync(eventId, stoppingToken);
        lastException = flagsException;

        if (!flagsArchived)
        {
            Logger.LogWarning("Failed to archive flags for event {eventId}.", eventId);
            await SendArchiveFailureEmailAsync("Failed to archive flags", eventId, 0, lastException);
            return false;
        }

        // Archive Competitor Metadata
        Logger.LogInformation("Archiving competitor metadata for event {eventId}...", eventId);
        var competitorMetadataArchiver = new CompetitorMetadataArchive(loggerFactory, tsContext, archiveStorage, purgeUtilities);
        var competitorMetadataResult = await competitorMetadataArchiver.ArchiveCompetitorMetadataAsync(eventId, stoppingToken);
        if (!competitorMetadataResult)
        {
            Logger.LogWarning("Failed to archive competitor metadata for event {eventId}.", eventId);
            await SendArchiveFailureEmailAsync("Failed to archive competitor metadata", eventId, 0, lastException);
            return false;
        }

        // Mark event as archived
        await using var dbContext = await tsContext.CreateDbContextAsync(stoppingToken);
        var ev = await dbContext.Events.FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken: stoppingToken);

        if (ev != null)
        {
            ev.IsArchived = true;
            await dbContext.SaveChangesAsync(stoppingToken);
            Logger.LogInformation("Event {eventId} archived successfully.", eventId);

            // Cleanup CarLastLaps for this event
            Logger.LogInformation("Cleaning up CarLastLaps for event {eventId}...", eventId);
            var (lastLapsCleanedUp, cleanupException) = await CleanupCarLastLapsAsync(eventId, stoppingToken);
            lastException = cleanupException;

            if (!lastLapsCleanedUp)
            {
                Logger.LogWarning("Failed to cleanup CarLastLaps for event {eventId}.", eventId);
                await SendArchiveFailureEmailAsync("Failed to cleanup CarLastLaps", eventId, 0, lastException);
                return false;
            }

            return true;
        }
        else
        {
            Logger.LogWarning("Event {eventId} not found in database after archiving logs.", eventId);
            return false;
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

    private async Task<(bool success, Exception? exception)> ArchiveEventLapsAsync(int eventId, CancellationToken stoppingToken)
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
                return (true, null);
            }

            Logger.LogInformation("Found {count} sessions with laps to archive for event {eventId}", sessionIds.Count, eventId);

            var processedCount = 0;
            foreach (var sessionId in sessionIds)
            {
                Logger.LogInformation("Archiving laps for event {eventId}, session {sessionId}...", eventId, sessionId);
                var purgeUtilities = new PurgeUtilities(loggerFactory, tsContext);
                var lapsArchive = new LapsLogArchive(loggerFactory, tsContext, archiveStorage, purgeUtilities);

                try
                {
                    var success = await lapsArchive.ArchiveLapsAsync(eventId, sessionId, stoppingToken);

                    if (!success)
                    {
                        var failureException = new Exception(
                            $"Failed to archive laps for event {eventId}, session {sessionId}. " +
                            $"Processed {processedCount} of {sessionIds.Count} sessions successfully before failure. " +
                            $"Remaining sessions: {string.Join(", ", sessionIds.Skip(processedCount + 1))}. " +
                            $"Check LapsLogArchive logs for details.");
                        Logger.LogWarning("Failed to archive laps for event {eventId}, session {sessionId}. Processed {processedCount}/{totalCount} sessions",
                            eventId, sessionId, processedCount, sessionIds.Count);
                        return (false, failureException);
                    }
                    processedCount++;
                }
                catch (Exception sessionEx)
                {
                    Logger.LogError(sessionEx, "Exception while archiving laps for event {eventId}, session {sessionId}. Processed {processedCount}/{totalCount} sessions",
                        eventId, sessionId, processedCount, sessionIds.Count);
                    return (false, sessionEx);
                }
            }

            Logger.LogInformation("Successfully archived laps for all {count} sessions in event {eventId}", sessionIds.Count, eventId);
            return (true, null);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error archiving laps for event {eventId}", eventId);
            return (false, ex);
        }
    }

    private async Task<(bool success, Exception? exception)> ArchiveEventFlagsAsync(int eventId, CancellationToken stoppingToken)
    {
        try
        {
            await using var dbContext = await tsContext.CreateDbContextAsync(stoppingToken);

            // Get all distinct sessions for this event that have flags
            var sessionIds = await dbContext.FlagLog
                .Where(l => l.EventId == eventId)
                .Select(l => l.SessionId)
                .Distinct()
                .ToListAsync(stoppingToken);

            if (sessionIds.Count == 0)
            {
                Logger.LogInformation("No flags found to archive for event {eventId}", eventId);
                return (true, null);
            }

            Logger.LogInformation("Found {count} sessions with flags to archive for event {eventId}", sessionIds.Count, eventId);

            var processedCount = 0;
            foreach (var sessionId in sessionIds)
            {
                Logger.LogInformation("Archiving flags for event {eventId}, session {sessionId}...", eventId, sessionId);
                var purgeUtilities = new PurgeUtilities(loggerFactory, tsContext);
                var flagsArchive = new FlagsArchive(loggerFactory, tsContext, archiveStorage, purgeUtilities);

                try
                {
                    var success = await flagsArchive.ArchiveFlagsAsync(eventId, sessionId, stoppingToken);

                    if (!success)
                    {
                        var failureException = new Exception(
                            $"Failed to archive flags for event {eventId}, session {sessionId}. " +
                            $"Processed {processedCount} of {sessionIds.Count} sessions successfully before failure. " +
                            $"Remaining sessions: {string.Join(", ", sessionIds.Skip(processedCount + 1))}. " +
                            $"Check FlagsArchive logs for details.");
                        Logger.LogWarning("Failed to archive flags for event {eventId}, session {sessionId}. Processed {processedCount}/{totalCount} sessions",
                            eventId, sessionId, processedCount, sessionIds.Count);
                        return (false, failureException);
                    }
                    processedCount++;
                }
                catch (Exception sessionEx)
                {
                    Logger.LogError(sessionEx, "Exception while archiving flags for event {eventId}, session {sessionId}. Processed {processedCount}/{totalCount} sessions",
                        eventId, sessionId, processedCount, sessionIds.Count);
                    return (false, sessionEx);
                }
            }

            Logger.LogInformation("Successfully archived flags for all {count} sessions in event {eventId}", sessionIds.Count, eventId);
            return (true, null);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error archiving flags for event {eventId}", eventId);
            return (false, ex);
        }
    }

    private async Task<(bool success, Exception? exception)> CleanupCarLastLapsAsync(int eventId, CancellationToken stoppingToken)
    {
        try
        {
            await using var dbContext = await tsContext.CreateDbContextAsync(stoppingToken);

            var deletedCount = await dbContext.CarLastLaps
                .Where(l => l.EventId == eventId)
                .ExecuteDeleteAsync(stoppingToken);

            Logger.LogInformation("Deleted {deletedCount} CarLastLaps records for event {eventId}", deletedCount, eventId);
            return (true, null);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error cleaning up CarLastLaps for event {eventId}", eventId);
            return (false, ex);
        }
    }

        private async Task RunSimulatedEventPurgeAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("Starting simulated event purge process...");
            try
            {
                await using var dbContext = await tsContext.CreateDbContextAsync(stoppingToken);
                var simulatedEvents = await dbContext.Events
                    .Where(e => e.IsSimulation && e.EndDate < DateTime.UtcNow.AddDays(-1))
                    .ToListAsync(stoppingToken);
                var purgeUtilities = new PurgeUtilities(loggerFactory, tsContext);
                foreach (var simEvent in simulatedEvents)
                {
                    Logger.LogInformation("Purging simulated event {eventId}...", simEvent.Id);
                    await purgeUtilities.DeleteAllEventDataAsync(simEvent.Id, stoppingToken);
                }
                await dbContext.SaveChangesAsync(stoppingToken);
                Logger.LogInformation("Simulated event purge process completed successfully.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error during simulated event purge process.");
                await SendArchiveFailureEmailAsync($"Simulated event purge failed: {ex.Message}", null, 0, ex);
            }
        }

            private async Task SendArchiveFailureEmailAsync(string failureReason, int? eventId, int retryCount, Exception? exception)
            {
                try
                {
                    var eventInfo = "N/A";
                    if (eventId.HasValue)
                    {
                        try
                        {
                            await using var dbContext = await tsContext.CreateDbContextAsync();
                            var ev = await dbContext.Events.FirstOrDefaultAsync(e => e.Id == eventId.Value);
                            if (ev != null)
                            {
                                eventInfo = $"Event ID: {eventId.Value}, Name: {ev.Name}, End Date: {ev.EndDate:yyyy-MM-dd HH:mm:ss} UTC";
                            }
                            else
                            {
                                eventInfo = $"Event ID: {eventId.Value} (Event not found in database)";
                            }
                        }
                        catch
                        {
                            eventInfo = $"Event ID: {eventId.Value} (Unable to retrieve event details)";
                        }
                    }

                            var exceptionDetails = "";
                            if (exception != null)
                            {
                                var stackTrace = string.IsNullOrWhiteSpace(exception.StackTrace) 
                                    ? "<em>Stack trace not available. This exception was created for diagnostic purposes and was not thrown. Check the exception message for details and review the EventOrchestration logs for more information.</em>" 
                                    : exception.StackTrace;

                                exceptionDetails = $@"
                    <h3>Exception Details</h3>
                    <p><strong>Exception Type:</strong> {exception.GetType().FullName}</p>
                    <p><strong>Exception Message:</strong> {exception.Message}</p>
                    <p><strong>Stack Trace:</strong></p>
                    <pre style=""background-color: #f4f4f4; padding: 10px; border: 1px solid #ddd; overflow-x: auto;"">{stackTrace}</pre>";

                                if (exception.InnerException != null)
                                {
                                    var innerStackTrace = string.IsNullOrWhiteSpace(exception.InnerException.StackTrace)
                                        ? "<em>Stack trace not available</em>"
                                        : exception.InnerException.StackTrace;

                                    exceptionDetails += $@"
                    <h4>Inner Exception</h4>
                    <p><strong>Type:</strong> {exception.InnerException.GetType().FullName}</p>
                    <p><strong>Message:</strong> {exception.InnerException.Message}</p>
                    <p><strong>Stack Trace:</strong></p>
                    <pre style=""background-color: #f4f4f4; padding: 10px; border: 1px solid #ddd; overflow-x: auto;"">{innerStackTrace}</pre>";
                                }
                            }

                    var subject = eventId.HasValue
                        ? $"Red Mist Archive Failure - Event {eventId.Value}"
                        : "Red Mist Archive Failure";

                    var body = $@"
        <html>
        <body>
            <h2>Archive Process Failure Alert</h2>
            <p><strong>Failure Reason:</strong> {failureReason}</p>
            <p><strong>Event Information:</strong> {eventInfo}</p>
            <p><strong>Timestamp:</strong> {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</p>
            {(retryCount > 0 ? $"<p><strong>Retry Attempts:</strong> {retryCount}</p>" : "")}
            {exceptionDetails}
            <p>Please investigate the logs for more detailed information.</p>
        </body>
        </html>";

                    await emailHelper.SendEmailAsync(subject, body, "support@redmist.racing", "noreply@redmist.racing");
                    Logger.LogInformation("Archive failure email sent successfully for: {failureReason}", failureReason);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to send archive failure email for: {failureReason}", failureReason);
                }
            }
    }
