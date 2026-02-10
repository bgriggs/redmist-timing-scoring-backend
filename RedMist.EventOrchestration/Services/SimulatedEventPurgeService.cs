using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.EventOrchestration.Utilities;

namespace RedMist.EventOrchestration.Services;

public class SimulatedEventPurgeService : BackgroundService
{
    private readonly ILoggerFactory loggerFactory;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly EmailHelper emailHelper;

    private ILogger Logger { get; }

    public SimulatedEventPurgeService(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, EmailHelper emailHelper)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.loggerFactory = loggerFactory;
        this.tsContext = tsContext;
        this.emailHelper = emailHelper;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mountainTimeZone = TimeZoneHelper.GetMountainTimeZone();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Calculate next midnight Mountain Time
                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, mountainTimeZone);
                var nextMidnight = now.Date.AddDays(1);
                var delayUntilMidnight = nextMidnight - now;

                Logger.LogInformation("Waiting until midnight Mountain Time ({nextMidnight}) to run simulated event purge process. Current time: {now}, Delay: {delay}",
                    nextMidnight, now, delayUntilMidnight);
                await Task.Delay(delayUntilMidnight, stoppingToken);

                // Run simulated event purge
                await RunSimulatedEventPurgeAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                Logger.LogInformation("Simulated event purge service is stopping.");
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "An unexpected error occurred in the simulated event purge service main loop.");
                var emailHelper = new ArchiveEmailHelper(Logger, tsContext, this.emailHelper);
                await emailHelper.SendArchiveFailureEmailAsync($"Unexpected error in simulated event purge service main loop: {ex.Message}", null, 0, ex);
                // Wait 1 hour before trying again if there's an unexpected error
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
    }

    internal async Task RunSimulatedEventPurgeAsync(CancellationToken stoppingToken)
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
            var emailHelper = new ArchiveEmailHelper(Logger, tsContext, this.emailHelper);
            await emailHelper.SendArchiveFailureEmailAsync($"Simulated event purge failed: {ex.Message}", null, 0, ex);
        }
    }
}
