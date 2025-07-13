using RedMist.Backend.Shared.Hubs;

namespace RedMist.StatusApi.Services;

/// <summary>
/// Prints metrics to the console or a monitoring system at regular intervals.
/// </summary>
public class MetricsPublisher : BackgroundService
{
    private ILogger Logger { get; }


    public MetricsPublisher(ILoggerFactory loggerFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Logger.LogInformation("clients:{c},in-car:{ic}", StatusHub.ClientConnectionsCount.Value, StatusHub.InCarConnectionsCount.Value);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "An error occurred while publishing metrics.");
            }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
