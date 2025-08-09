using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Models;
using RedMist.ControlLogs;
using RedMist.Database;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.ControlLogProcessor.Services;

public class StatusAggregatorService : BackgroundService
{
    private readonly ILoggerFactory loggerFactory;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IConfiguration configuration;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly IControlLogFactory controlLogFactory;
    private readonly IHubContext<StatusHub> hubContext;

    private ILogger Logger { get; }
    private ControlLogCache? controlLogCache;
    private int? eventId;
    private readonly SemaphoreSlim subscriptionCheckLock = new(1);


    public StatusAggregatorService(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, IConfiguration configuration, IDbContextFactory<TsContext> tsContext, IControlLogFactory controlLogFactory, IHubContext<StatusHub> hubContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.loggerFactory = loggerFactory;
        this.cacheMux = cacheMux;
        this.configuration = configuration;
        this.tsContext = tsContext;
        this.controlLogFactory = controlLogFactory;
        this.hubContext = hubContext;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("StatusAggregatorService starting...");
        eventId = configuration.GetValue<int>("event_id");
        if (eventId == null || eventId <= 0)
        {
            Logger.LogError("Event ID is not set or invalid. Cannot start StatusAggregatorService.");
            return;
        }

        var requestCounter = Metrics.CreateCounter("controllog_requests", "Total control log requests to organization source");
        var failureCounter = Metrics.CreateCounter("controllog_failures", "Total control log failures");
        var entriesCounter = Metrics.CreateCounter("controllog_entries_total", "Total control log entries");

        controlLogCache = new ControlLogCache(eventId.Value, loggerFactory, tsContext, controlLogFactory);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Logger.LogInformation("Updating control log entries");
                requestCounter.Inc();
                await RequestAndSendControlLogUpdatesAsync(stoppingToken);
                var entries = await controlLogCache.GetControlEntries();
                entriesCounter.IncTo(entries.Count);
                Logger.LogInformation("Total entries: {count}, requests: {r}, failures: {f}", entries.Count, requestCounter.Value, failureCounter.Value);
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "An error occurred in StatusAggregatorService.");
                failureCounter.Inc();
                //Logger.LogInformation("Throttling service for 10 secs");
                //await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
        Logger.LogInformation("StatusAggregatorService stopped.");
    }

    #region Control Log

    private async Task RequestAndSendControlLogUpdatesAsync(CancellationToken stoppingToken)
    {
        if (controlLogCache == null || eventId == null)
        {
            Logger.LogWarning("ControlLogCache is not initialized. Cannot process control log request.");
            return;
        }

        // Single car update
        var changedCars = await controlLogCache.RequestControlLogChanges(stoppingToken);
        var entries = await controlLogCache.GetCarControlEntries([.. changedCars]);
        foreach (var e in entries)
        {
            if (!string.IsNullOrWhiteSpace(e.Key))
            {
                var ccl = new CarControlLogs { CarNumber = e.Key, ControlLogEntries = e.Value };
                await SendAsync(string.Empty, e.Key, ccl, stoppingToken);
            }
        }

        // Full log update
        var fullLog = await controlLogCache.GetControlEntries();
        var fullCcl = new CarControlLogs { CarNumber = string.Empty, ControlLogEntries = fullLog };
        await SendAsync(eventId.ToString()!, string.Empty, fullCcl, stoppingToken);

        var cache = cacheMux.GetDatabase();

        // Update the cache with the latest full control log
        Logger.LogInformation("Updating cache with latest control log...");
        var logCacheKey = string.Format(Consts.CONTROL_LOG, eventId);
        var logCacheValue = JsonSerializer.Serialize(fullCcl);
        await cache.StringSetAsync(logCacheKey, logCacheValue);

        // Update the cache with car control logs
        Logger.LogInformation("Updating cache with car control logs...");
        var carEntriesLookup = await controlLogCache.GetCarControlEntries();
        foreach (var carEntry in carEntriesLookup)
        {
            var carLogEntryKey = string.Format(Consts.CONTROL_LOG_CAR, eventId, carEntry.Key);
            var carLogCacheValue = JsonSerializer.Serialize(new CarControlLogs { CarNumber = carEntry.Key, ControlLogEntries = carEntry.Value });
            await cache.StringSetAsync(carLogEntryKey, carLogCacheValue);
        }

        // Update the cache with car penalties
        Logger.LogInformation("Updating cache with car penalties...");
        var carLogCacheKey = string.Format(Consts.CONTROL_LOG_CAR_PENALTIES, eventId);
        var carPenaltyEntries = new List<HashEntry>();
        var carPenalties = await controlLogCache.GetPenaltiesAsync(stoppingToken);
        foreach (var carPenalty in carPenalties)
        {
            var penaltyJson = JsonSerializer.Serialize(new CarPenality(carPenalty.Value.warnings, carPenalty.Value.laps));
            carPenaltyEntries.Add(new HashEntry(carPenalty.Key, penaltyJson));
        }
        await cache.HashSetAsync(carLogCacheKey, [.. carPenaltyEntries], CommandFlags.FireAndForget);

        // Clean up inactive car cache entries
        await CleanupInactiveCarCacheEntriesAsync(carEntriesLookup.Keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToHashSet(), stoppingToken);

        Logger.LogInformation("Finished updating cache");
    }

    /// <summary>
    /// Removes Redis cache entries for cars that are no longer active in the control log
    /// </summary>
    /// <param name="activeCarNumbers">Set of car numbers that are currently active</param>
    /// <param name="stoppingToken">Cancellation token</param>
    private async Task CleanupInactiveCarCacheEntriesAsync(HashSet<string> activeCarNumbers, CancellationToken stoppingToken)
    {
        if (eventId == null)
        {
            return;
        }

        try
        {
            var cache = cacheMux.GetDatabase();
            
            // Get existing car cache keys to identify what needs to be removed
            var existingCarKeys = new HashSet<string>();
            var pattern = string.Format(Consts.CONTROL_LOG_CAR, eventId, "*");
            var server = cacheMux.GetServer(cacheMux.GetEndPoints().First());
            var keys = server.Keys(pattern: pattern);
            
            foreach (var key in keys)
            {
                // Extract car number from the key pattern: control-log-evt-{eventId}-car-{carNumber}
                var keyStr = key.ToString();
                var lastDashIndex = keyStr.LastIndexOf('-');
                if (lastDashIndex > 0 && lastDashIndex < keyStr.Length - 1)
                {
                    var carNumber = keyStr.Substring(lastDashIndex + 1);
                    existingCarKeys.Add(carNumber);
                }
            }

            // Find cars that are no longer active
            var removedCars = existingCarKeys.Except(activeCarNumbers);
            if (removedCars.Any())
            {
                Logger.LogInformation("Removing cache entries for inactive cars: {removedCars}", string.Join(", ", removedCars));
                
                foreach (var removedCar in removedCars)
                {
                    // Remove individual car control log cache entry
                    var carLogEntryKey = string.Format(Consts.CONTROL_LOG_CAR, eventId, removedCar);
                    await cache.KeyDeleteAsync(carLogEntryKey);
                    Logger.LogDebug("Removed cache entry for car {carNumber}: {cacheKey}", removedCar, carLogEntryKey);
                    
                    // Remove penalty entry from hash set
                    var carLogCacheKey = string.Format(Consts.CONTROL_LOG_CAR_PENALTIES, eventId);
                    await cache.HashDeleteAsync(carLogCacheKey, removedCar);
                    Logger.LogDebug("Removed penalty entry for car {carNumber} from {cacheKey}", removedCar, carLogCacheKey);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error occurred while cleaning up inactive car cache entries");
        }
    }

    #endregion

    public async Task SendAsync(string connectionDestination, string carNumber, CarControlLogs logs, CancellationToken cancellationToken)
    {
        if (eventId == null || eventId <= 0)
        {
            Logger.LogError("Event ID is not set or invalid. Cannot send logs.");
            return;
        }
        try
        {
            if (!string.IsNullOrEmpty(connectionDestination))
            {
                Logger.LogTrace("ControlLogNotification: full list to client {g}", connectionDestination);
                await hubContext.Clients.Client(connectionDestination).SendAsync("ReceiveControlLog", logs, cancellationToken);
            }
            else if (!string.IsNullOrEmpty(carNumber))
            {
                string grpKey = $"{eventId}-{carNumber}";
                Logger.LogTrace("ControlLogNotification: car {c} group {g}", carNumber, grpKey);
                await hubContext.Clients.Group(grpKey).SendAsync("ReceiveControlLog", logs, cancellationToken);
            }
            else
            {
                string grpKey = $"{eventId}-cl";
                Logger.LogTrace("ControlLogNotification: full subscriptions of event {c} group {g}", eventId, grpKey);
                await hubContext.Clients.Group(grpKey).SendAsync("ReceiveControlLog", logs, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error sending control log update to clients.");
        }
    }
}
