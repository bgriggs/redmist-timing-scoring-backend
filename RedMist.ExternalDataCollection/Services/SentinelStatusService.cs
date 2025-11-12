using Microsoft.AspNetCore.SignalR;
using Prometheus;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Models;
using RedMist.Backend.Shared.Utilities;
using RedMist.ExternalDataCollection.Clients;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarVideo;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.ExternalDataCollection.Services;

public class SentinelStatusService : BackgroundService
{
    private ILogger Logger { get; }
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IHubContext<StatusHub> hubContext;
    private readonly SentinelClient sentinelClient;
    private readonly EventsChecker eventsChecker;
    private readonly ExternalTelemetryClient externalTelemetryClient;
    private readonly IHttpClientFactory httpClientFactory;
    /// <summary>
    /// Interval for checking for updates from Sentinel endpoint. This is accessible
    /// for unit testing.
    /// </summary>
    private TimeSpan UpdateInterval { get; set; } = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim subscriptionCheckLock = new(1);
    private List<VideoMetadata>? lastVideoMetadata;
    private List<DriverInfo>? lastDriverInfo;
    private CancellationToken stoppingToken = CancellationToken.None;


    public SentinelStatusService(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux,
        IHubContext<StatusHub> hubContext, SentinelClient sentinelClient, EventsChecker eventsChecker,
        ExternalTelemetryClient externalTelemetryClient, IHttpClientFactory httpClientFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.hubContext = hubContext;
        this.sentinelClient = sentinelClient;
        this.eventsChecker = eventsChecker;
        this.externalTelemetryClient = externalTelemetryClient;
        this.httpClientFactory = httpClientFactory;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("SentinelStatusService starting...");
        this.stoppingToken = stoppingToken;

        var requestCounter = Metrics.CreateCounter("sentinel_requests", "Total sentinel video requests");
        var failureCounter = Metrics.CreateCounter("sentinel_failures", "Total sentinel video request failures");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cache = cacheMux.GetDatabase();
                await EnsureCacheSubscriptionsAsync(stoppingToken);

                var currentEvents = await eventsChecker.GetCurrentEventsAsync();
                Logger.LogInformation("Found {e} current events", currentEvents.Count);
                if (currentEvents.Count == 0)
                {
                    await Task.Delay(UpdateInterval, stoppingToken);
                    continue;
                }

                var streams = await sentinelClient.GetStreamsAsync();
                Logger.LogInformation("Fetched {Count} live Sentinel video streams", streams.Count);

                //streams.Add(new PublicStreams { DriverName = "test driver",TransponderIdStr = "1329228", YouTubeUrl = "https://redmist.racing" });

                var videoMetadata = new List<VideoMetadata>();
                var driverInfo = new List<DriverInfo>();
                foreach (var stream in streams)
                {
                    var metadata = new VideoMetadata
                    {
                        TransponderId = stream.TransponderId,
                        UseTransponderId = true,
                        SystemType = VideoSystemType.Sentinel,
                        IsLive = true,
                    };

                    if (!stream.DriverName.StartsWith("PROD"))
                    {
                        metadata.DriverName = stream.DriverName;
                        var di = new DriverInfo { DriverName = stream.DriverName, TransponderId = stream.TransponderId };
                        driverInfo.Add(di);
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
                        // Check that the video is active before adding as often it is provided but is 404
                        bool success = await CheckUrlSuccessAsync(stream.SvnUrl);
                        if (success)
                        {
                            var dest = new VideoDestination
                            {
                                Type = VideoDestinationType.DirectSrt,
                                Url = stream.SvnUrl
                            };
                            metadata.Destinations.Add(dest);
                        }
                    }

                    videoMetadata.Add(metadata);
                }

                var eventIds = currentEvents.Select(e => e.EventId).Distinct().ToList();
                await SendVideoMetadataAsync(videoMetadata, eventIds, stoppingToken);
                await SendVideoMetadataAsync(videoMetadata, stoppingToken);
                await SendDriverInfoAsync(driverInfo, stoppingToken);

                lastVideoMetadata = videoMetadata;
                lastDriverInfo = driverInfo;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error processing Sentinel video status update");
                failureCounter.Inc();
            }
            finally
            {
                requestCounter.Inc();
                Logger.LogInformation("Total Sentinel: requests: {r}, failures: {f}", requestCounter.Value, failureCounter.Value);
            }
            await Task.Delay(UpdateInterval, stoppingToken);
        }
    }

    private async Task SendDriverInfoAsync(List<DriverInfo> driverInfo, CancellationToken stoppingToken = default)
    {
        // Find any drivers that have been removed since last time
        var removedDrivers = new List<DriverInfo>();
        if (lastDriverInfo != null)
        {
            foreach (var lastDriver in lastDriverInfo)
            {
                if (!driverInfo.Any(d => d.TransponderId == lastDriver.TransponderId))
                {
                    var emptyDriver = new DriverInfo { TransponderId = lastDriver.TransponderId };
                    removedDrivers.Add(emptyDriver);
                }
            }
        }

        Logger.LogDebug("Sending {d} drivers, removed {r}", driverInfo.Count, removedDrivers.Count);
        await externalTelemetryClient.UpdateDriversAsync([.. driverInfo, .. removedDrivers], stoppingToken);
    }

    private async Task SendVideoMetadataAsync(List<VideoMetadata> metadata, CancellationToken stoppingToken = default)
    {
        // Find any video streams that have been removed since last time
        var removedStreams = new List<VideoMetadata>();
        if (lastVideoMetadata != null)
        {
            foreach (var lastStream in lastVideoMetadata)
            {
                if (!metadata.Any(d => d.TransponderId == lastStream.TransponderId))
                {
                    var emptyStream = new VideoMetadata { TransponderId = lastStream.TransponderId };
                    removedStreams.Add(emptyStream);
                }
            }
        }

        Logger.LogDebug("Sending {v} video streams, removed {r}", metadata.Count, removedStreams.Count);
        await externalTelemetryClient.UpdateCarVideosAsync([.. metadata, .. removedStreams], stoppingToken);
    }

    private async Task EnsureCacheSubscriptionsAsync(CancellationToken stoppingToken = default)
    {
        await subscriptionCheckLock.WaitAsync(stoppingToken);
        try
        {
            var sub = cacheMux.GetSubscriber();
            await sub.UnsubscribeAllAsync();

            // Subscribe for status requests such as when a new UI connects
            await sub.SubscribeAsync(new RedisChannel(Consts.SEND_FULL_STATUS, RedisChannel.PatternMode.Literal),
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

    [Obsolete("This will be removed in the future. Use other SendVideoMetadataAsync.")]
    private async Task SendVideoMetadataAsync(List<VideoMetadata> metadata, List<int> currentEvents, CancellationToken stoppingToken = default)
    {
        Logger.LogInformation("Sending video metadata for {Count} entries", metadata.Count);
        foreach (var evtId in currentEvents)
        {
            var subKey = string.Format(Consts.EVENT_SUB_V2, evtId);
            await hubContext.Clients.Group(subKey).SendAsync("ReceiveInCarVideoMetadata", metadata, stoppingToken);

            // Legacy
            await hubContext.Clients.Group(evtId.ToString()).SendAsync("ReceiveInCarVideoMetadata", metadata, stoppingToken);
        }
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
            await hubContext.Clients.Client(cmd.ConnectionId)
                .SendAsync("ReceiveInCarVideoMetadata", lastVideoMetadata, stoppingToken);
        }
    }

    private async Task<bool> CheckUrlSuccessAsync(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return false;
        
        using HttpClient client = httpClientFactory.CreateClient();
        try
        {
            var response = await client.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
