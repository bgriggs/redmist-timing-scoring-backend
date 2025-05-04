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
        cacheMux.ConnectionRestored += CacheMux_ConnectionRestored;
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

        await EnsureSubscriptions(stoppingToken);
        controlLogCache = new ControlLogCache(eventId.Value, loggerFactory, tsContext, controlLogFactory);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Logger.LogInformation("Updating control log entries");
                requestCounter.Inc();
                await RequestAndSendControlLogUpdates(stoppingToken);
                var entries = await controlLogCache.GetControlEntries();
                entriesCounter.IncTo(entries.Count);
                Logger.LogInformation("Total entries: {count}, requests: {r}, failures: {f}", entries.Count, requestCounter.Value, failureCounter.Value);
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "An error occurred in StatusAggregatorService.");
                failureCounter.Inc();
                Logger.LogInformation("Throttling service for 10 secs");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                await EnsureSubscriptions(stoppingToken);
            }
        }
        Logger.LogInformation("StatusAggregatorService stopped.");
    }

    private async void CacheMux_ConnectionRestored(object? sender, ConnectionFailedEventArgs e)
    {
        await EnsureSubscriptions();
    }

    private async Task EnsureSubscriptions(CancellationToken stoppingToken = default)
    {
        await subscriptionCheckLock.WaitAsync(stoppingToken);
        try
        {
            var sub = cacheMux.GetSubscriber();
            await sub.UnsubscribeAllAsync();

            // Subscribe to control log requests such as when UI details opens for a car
            await sub.SubscribeAsync(new RedisChannel(Consts.SEND_CONTROL_LOG, RedisChannel.PatternMode.Literal),
                async (channel, value) => await ProcessControlLogRequest(value.ToString(), stoppingToken), CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error ensuring subscriptions");
        }
        finally
        {
            subscriptionCheckLock.Release();
        }
    }

    #region Control Log

    private async Task ProcessControlLogRequest(string cmdJson, CancellationToken stoppingToken = default)
    {
        if (controlLogCache == null)
        {
            Logger.LogWarning("ControlLogCache is not initialized. Cannot process control log request.");
            return;
        }

        var cmd = JsonSerializer.Deserialize<SendControlLogCommand>(cmdJson);
        if (cmd == null)
        {
            Logger.LogWarning("Invalid command received: {cj}", cmdJson);
            return;
        }

        CarControlLogs ccl;
        if (string.IsNullOrEmpty(cmd.CarNumber))
        {
            var entries = await controlLogCache.GetControlEntries();
            ccl = new CarControlLogs { CarNumber = string.Empty, ControlLogEntries = entries };
        }
        else // Specific car
        {
            var entries = await controlLogCache.GetCarControlEntries([cmd.CarNumber.ToLower()]);
            ccl = new CarControlLogs { CarNumber = cmd.CarNumber, ControlLogEntries = [.. entries.SelectMany(s => s.Value)] };
        }
        Logger.LogInformation("Sending control logs for event {e} car {c} to new connection {con}", cmd.EventId, cmd.CarNumber, cmd.ConnectionId);
        await SendAsync(cmd.ConnectionId, string.Empty, ccl, stoppingToken);
    }

    private async Task RequestAndSendControlLogUpdates(CancellationToken stoppingToken)
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


        // Update the cache with the latest full control log
        Logger.LogInformation("Updating cache with latest control log...");
        var cache = cacheMux.GetDatabase();
        var logCacheKey = string.Format(Consts.CONTROL_LOG, eventId);
        var logCacheValue = JsonSerializer.Serialize(fullCcl);
        await cache.StringSetAsync(logCacheKey, logCacheValue, TimeSpan.FromMinutes(5), When.Always, CommandFlags.FireAndForget);

        var carLogCacheKey = string.Format(Consts.CONTROL_LOG_CAR_PENALTIES, eventId);
        var carPenaltyEntries = new List<HashEntry>();
        var carPenalties = await controlLogCache.GetPenaltiesAsync(stoppingToken);
        foreach (var carPenalty in carPenalties)
        {
            var penaltyJson = JsonSerializer.Serialize(new CarPenality(carPenalty.Value.warnings, carPenalty.Value.laps));
            carPenaltyEntries.Add(new HashEntry(carPenalty.Key, penaltyJson));
        }
        await cache.HashSetAsync(carLogCacheKey, [.. carPenaltyEntries], CommandFlags.FireAndForget);

        Logger.LogInformation("Finished updating cache");
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
