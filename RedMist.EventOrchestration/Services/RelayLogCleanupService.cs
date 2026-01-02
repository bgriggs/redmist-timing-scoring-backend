using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.EventOrchestration.Utilities;

namespace RedMist.EventOrchestration.Services;

public class RelayLogCleanupService : BackgroundService
{
    private readonly IDbContextFactory<TsContext> tsContext;
    private ILogger Logger { get; }

    public RelayLogCleanupService(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mountainTimeZone = TimeZoneHelper.GetMountainTimeZone();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, mountainTimeZone);
                var nextRunTime = CalculateNextRunTime(now);

                var delay = nextRunTime - now;
                if (delay.TotalMilliseconds > 0)
                {
                    Logger.LogInformation("Next RelayLog cleanup scheduled for {nextRunTime} Mountain Time. Current time: {now}",
                        nextRunTime, now);
                    await Task.Delay(delay, stoppingToken);
                }

                if (stoppingToken.IsCancellationRequested)
                    break;

                await CleanupOldRelayLogsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in RelayLogCleanupService");
                // Wait 1 hour before retrying on error
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
    }

    private DateTime CalculateNextRunTime(DateTime currentMountainTime)
    {
        var targetTime = new TimeSpan(12, 0, 0); // 12:00 PM
        var currentDate = currentMountainTime.Date;
        var currentDayOfWeek = currentMountainTime.DayOfWeek;

        // If it's already past 12 PM today and today is a valid day, schedule for next valid day
        if (currentMountainTime.TimeOfDay >= targetTime)
        {
            currentDate = currentDate.AddDays(1);
            currentDayOfWeek = currentDate.DayOfWeek;
        }

        // Find the next valid day (Tuesday, Wednesday, or Thursday)
        while (currentDayOfWeek != DayOfWeek.Tuesday &&
               currentDayOfWeek != DayOfWeek.Wednesday &&
               currentDayOfWeek != DayOfWeek.Thursday)
        {
            currentDate = currentDate.AddDays(1);
            currentDayOfWeek = currentDate.DayOfWeek;
        }

        return currentDate.Add(targetTime);
    }

    private async Task CleanupOldRelayLogsAsync(CancellationToken stoppingToken)
    {
        try
        {
            Logger.LogInformation("Starting RelayLog cleanup process");

            using var context = await tsContext.CreateDbContextAsync(stoppingToken);
            
            var cutoffDate = DateTime.UtcNow.AddDays(-30);
            Logger.LogInformation("Deleting RelayLogs older than {cutoffDate}", cutoffDate);

            var deletedCount = await context.RelayLogs
                .Where(r => r.Timestamp < cutoffDate)
                .ExecuteDeleteAsync(stoppingToken);

            Logger.LogInformation("RelayLog cleanup completed. Deleted {deletedCount} records", deletedCount);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during RelayLog cleanup");
            throw;
        }
    }
}
