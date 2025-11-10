using Microsoft.AspNetCore.SignalR;
using Prometheus;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Models;
using RedMist.ExternalDataCollection.Clients;
using RedMist.ExternalDataCollection.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarVideo;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.ExternalDataCollection.Services;

public class SentinelStatusService : BackgroundService
{
    private ILogger Logger { get; }
    private readonly int eventId;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IHubContext<StatusHub> hubContext;
    private readonly SentinelClient sentinelClient;
    private readonly TimeSpan updateInterval = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim subscriptionCheckLock = new(1);
    private List<VideoMetadata>? lastVideoMetadata;


    public SentinelStatusService(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, IConfiguration configuration,
        IHubContext<StatusHub> hubContext, SentinelClient sentinelClient)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.hubContext = hubContext;
        this.sentinelClient = sentinelClient;
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
                await EnsureCacheSubscriptionsAsync(stoppingToken);

                Logger.LogInformation("Loading last Payload...");
                var json = await cache.StringGetAsync(key);
                if (!string.IsNullOrEmpty(json) && json.HasValue)
                {
                    var payload = JsonSerializer.Deserialize<Payload>(json!);
                    if (payload != null)
                    {
                        var transponderIds = payload.CarPositions.Where(t => t.TransponderId > 0).Select(t => t.TransponderId).ToList();
                        Logger.LogInformation("Found {Count} transponder IDs in payload", transponderIds.Count);

                        Logger.LogInformation("Fetching live video streams...");
                        var streams = await sentinelClient.GetStreamsAsync();
                        Logger.LogInformation("Fetched {Count} live video streams", streams.Count);
                        var eventStreams = new List<PublicStreams>();
                        foreach (var transponderId in transponderIds)
                        {
                            var stream = streams.FirstOrDefault(s => s.TransponderId == transponderId);
                            if (stream != null)
                            {
                                eventStreams.Add(stream);
                            }
                        }
                        Logger.LogInformation("Filtered {Count} streams for transponder IDs in payload", eventStreams.Count);
                        activeCarsCounter.IncTo(eventStreams.Count);

                        //eventStreams.Add(new PublicStreams { DriverName = "test driver", TransponderId = 10908083, YouTubeUrl = "https://redmist.racing" });

                        var videoMetadata = new List<VideoMetadata>();
                        foreach (var stream in eventStreams)
                        {
                            var metadata = new VideoMetadata
                            {
                                EventId = eventId,
                                TransponderId = stream.TransponderId,
                                UseTransponderId = true,
                                SystemType = VideoSystemType.Sentinel,
                                CarNumber = payload.CarPositions.FirstOrDefault(c => c.TransponderId == stream.TransponderId)?.Number ?? string.Empty,
                                IsLive = true,
                            };

                            if (!stream.DriverName.StartsWith("PROD"))
                            {
                                metadata.DriverName = stream.DriverName;
                            }

                            if (!string.IsNullOrWhiteSpace(stream.YouTubeUrl))
                            {
                                var dest = new VideoDestination
                                {
                                    Type = VideoDestinationType.Youtube,
                                    Url = stream.YouTubeUrl
                                };
                                metadata.Destinations.Add(dest);
                            }
                            if (!string.IsNullOrWhiteSpace(stream.SvnUrl))
                            {
                                var dest = new VideoDestination
                                {
                                    Type = VideoDestinationType.DirectSrt,
                                    Url = stream.SvnUrl
                                };
                                metadata.Destinations.Add(dest);
                            }

                            videoMetadata.Add(metadata);
                        }

                        lastVideoMetadata = videoMetadata;
                        await SendVideoMetadataAsync(videoMetadata, stoppingToken);
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

    private async Task EnsureCacheSubscriptionsAsync(CancellationToken stoppingToken = default)
    {
        await subscriptionCheckLock.WaitAsync(stoppingToken);
        try
        {
            var sub = cacheMux.GetSubscriber();
            await sub.UnsubscribeAllAsync();

            // Subscribe for status requests such as when a new UI connects
            await sub.SubscribeAsync(new RedisChannel(Backend.Shared.Consts.SEND_FULL_STATUS, RedisChannel.PatternMode.Literal),
                async (channel, value) => await ProcessUiStatusRequestAsync(value.ToString()), CommandFlags.FireAndForget);
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

    private async Task SendVideoMetadataAsync(List<VideoMetadata> metadata, CancellationToken stoppingToken = default)
    {
        Logger.LogInformation("Sending video metadata for {Count} entries", metadata.Count);
        var subKey = string.Format(Consts.EVENT_SUB_V2, eventId);
        await hubContext.Clients.Group(subKey).SendAsync("ReceiveInCarVideoMetadata", metadata, stoppingToken);

        // Legacy
        await hubContext.Clients.Group(eventId.ToString()).SendAsync("ReceiveInCarVideoMetadata", metadata, stoppingToken);
    }

    private async Task ProcessUiStatusRequestAsync(string cmdJson)
    {
        var cmd = JsonSerializer.Deserialize<SendStatusCommand>(cmdJson);
        if (cmd == null)
        {
            Logger.LogWarning("Invalid command received: {cj}", cmdJson);
            return;
        }

        Logger.LogInformation("Sending UI status update for event {e} to new connection {con}", cmd.EventId, cmd.ConnectionId);
        if (lastVideoMetadata != null)
        {
            await SendVideoMetadataAsync(lastVideoMetadata, CancellationToken.None);
        }
    }
}
