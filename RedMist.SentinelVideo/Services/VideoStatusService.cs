using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using RedMist.Backend.Shared.Hubs;
using RedMist.Database;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.SentinelVideo.Services;

public class VideoStatusService : BackgroundService
{
    private ILogger Logger { get; }
    private readonly int eventId;
    private readonly ILoggerFactory loggerFactory;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly IHubContext<StatusHub> hubContext;
    private readonly TimeSpan updateInterval = TimeSpan.FromSeconds(120);


    public VideoStatusService(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, IConfiguration configuration,
        IDbContextFactory<TsContext> tsContext, IHubContext<StatusHub> hubContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.loggerFactory = loggerFactory;
        this.cacheMux = cacheMux;
        this.tsContext = tsContext;
        this.hubContext = hubContext;
        eventId = configuration.GetValue("event_id", 0);
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("VideoStatusService starting...");
        if (eventId <= 0)
        {
            Logger.LogError("Event ID is not set or invalid. Cannot start VideoStatusService.");
            return;
        }

        var requestCounter = Metrics.CreateCounter("sentinel_requests", "Total sentinel video requests");
        var failureCounter = Metrics.CreateCounter("sentinel_failures", "Total sentinel video request failures");
        var activeCarsCounter = Metrics.CreateCounter("sentinel_cars_total", "Total cars with active Sentinel");

        while (!stoppingToken.IsCancellationRequested)
        {
            
            try
            {
                var cache = cacheMux.GetDatabase();
                var key = string.Format(Backend.Shared.Consts.EVENT_PAYLOAD, eventId);

                Logger.LogInformation("Loading last Payload...");
                var json = await cache.StringGetAsync(key);
                if (!string.IsNullOrEmpty(json) && json.HasValue)
                {
                    var payload = JsonSerializer.Deserialize<Payload>(json!);
                    if (payload != null)
                    {
                        var transponderIds = payload.CarPositions.Select(t => t.TransponderId).ToList();
                        Logger.LogInformation("Found {Count} transponder IDs in payload", transponderIds.Count);

                        Logger.LogInformation("Fetching video status for transponder IDs...");
                        // Todo: Fetch the latest video status for each transponder ID
                        //activeCarsCounter.IncTo();
                        //await hubContext.Clients.Group(eventId.ToString()).SendAsync("ReceiveInCarVideoMetadata", json, stoppingToken);
                    }
                    else
                    {
                        Logger.LogWarning("Payload deserialization returned null for key {Key}", key);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error processing video status update for event {EventId}", eventId);
                failureCounter.Inc();
            }
            finally
            {
                requestCounter.Inc();
                Logger.LogInformation("Total Sentinel: {count}, requests: {r}, failures: {f}", activeCarsCounter.Value, requestCounter.Value, failureCounter.Value);
            }
            await Task.Delay(updateInterval, stoppingToken);
        }
    }
}
