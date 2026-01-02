using Microsoft.AspNetCore.SignalR;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Services;
using RedMist.EventProcessor.EventStatus.X2;
using RedMist.EventProcessor.Models;
using StackExchange.Redis;

namespace RedMist.EventProcessor.EventStatus;

/// <summary>
/// Coordinates the receiving of incoming timing data and sending the associated updates to UIs.
/// </summary>
public class EventAggregatorService : BackgroundService
{
    private const string CONSUMER_GROUP = "processor";
    private readonly int eventId;
    private readonly string streamKey;
    private readonly string serviceName;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IMediator mediator;
    private readonly IHubContext<StatusHub> hubContext;

    private ILogger Logger { get; }
    private readonly SemaphoreSlim streamCheckLock = new(1);
    private readonly SemaphoreSlim subscriptionCheckLock = new(1);
    private readonly SemaphoreSlim payloadSerializationLock = new(1);
    private readonly SessionStateProcessingPipeline processingPipeline;
    private readonly SessionContext sessionContext;
    private readonly PitProcessor pitProcessorV2;


    public EventAggregatorService(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, 
        IConfiguration configuration, IMediator mediator, IHubContext<StatusHub> hubContext, 
        SessionStateProcessingPipeline processingPipeline, SessionContext sessionContext, PitProcessor pitProcessorV2)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.mediator = mediator;
        this.hubContext = hubContext;
        this.processingPipeline = processingPipeline;
        this.sessionContext = sessionContext;
        this.pitProcessorV2 = pitProcessorV2;
        eventId = configuration.GetValue("event_id", 0);
        streamKey = string.Format(Backend.Shared.Consts.EVENT_STATUS_STREAM_KEY, eventId);
        serviceName = configuration["job_name"] ?? string.Empty;
        if (string.IsNullOrEmpty(serviceName))
        {
            // If job_name is not set such as in debug, try to use the host name and port from applicationUrl
            var applicationUrl = configuration["ASPNETCORE_URLS"];
            if (!string.IsNullOrEmpty(applicationUrl) && Uri.TryCreate(applicationUrl, UriKind.Absolute, out var uri))
            {
                if (uri.Host == "0.0.0.0")
                    serviceName = $"localhost:{uri.Port}";
                else
                    serviceName = $"{uri.Host}:{uri.Port}";
            }
        }
        else
        {
            serviceName += "-service";
        }
        if (string.IsNullOrEmpty(serviceName))
            throw new ArgumentException("Service name could not be determined. Set job_name in configuration.");

        cacheMux.ConnectionRestored += CacheMux_ConnectionRestored;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("Event Aggregator starting...");
        await EnsureStreamAsync();
        await RegisterEndpointAsync();

        sessionContext.CancellationToken = stoppingToken;

        // Initialize PitProcessorV2 with the current event
        await pitProcessorV2.Initialize(eventId);

        // Publish reset event to get full set of data from the relay
        await mediator.Publish(new RelayResetRequest { EventId = eventId, ForceTimingDataReset = false }, stoppingToken);

        // Start a task to read timing source data from this service's stream.
        // The SignalR hub is responsible for sending timing data to the stream.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cache = cacheMux.GetDatabase();
                var result = await cache.StreamReadGroupAsync(streamKey, CONSUMER_GROUP, "proc", ">", count: 5);
                if (result.Length == 0)
                {
                    // No messages available, wait before next poll
                    await Task.Delay(TimeSpan.FromMilliseconds(30), stoppingToken);
                }
                else
                {
                    foreach (var entry in result)
                    {
                        foreach (var field in entry.Values)
                        {
                            // Check the message tag to determine the type of update (e.g. result monitor)
                            var tags = field.Name.ToString().Split('-');
                            if (tags.Length < 3)
                            {
                                Logger.LogWarning("Invalid event status update: {f}", field.Name);
                                continue;
                            }

                            var type = tags[0];
                            //var eventId = int.Parse(tags[1]);
                            var sessionId = int.Parse(tags[2]);
                            var data = field.Value.ToString();

                            // Create timing message and send to processing pipeline
                            var timingMessage = new TimingMessage(type, data, sessionId, DateTime.UtcNow);

                            // Post message to the new processing pipeline
                            await processingPipeline.PostAsync(timingMessage);
                        }

                        await cache.StreamAcknowledgeAsync(streamKey, CONSUMER_GROUP, entry.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error reading event status stream");
                Logger.LogInformation("Throttling service for 5 secs");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                await EnsureStreamAsync();
            }
        }
    }


    private async void CacheMux_ConnectionRestored(object? sender, ConnectionFailedEventArgs e)
    {
        await EnsureStreamAsync();

        // Publish reset event to get full set of data from the relay
        await mediator.Publish(new RelayResetRequest { EventId = eventId });
    }

    private async Task EnsureStreamAsync()
    {
        // Lock to avoid race condition between checking for the stream and creating it
        await streamCheckLock.WaitAsync();
        try
        {
            var cache = cacheMux.GetDatabase();
            if (!await cache.KeyExistsAsync(streamKey) || (await cache.StreamGroupInfoAsync(streamKey)).All(x => x.Name != CONSUMER_GROUP))
            {
                Logger.LogInformation("Creating new stream and consumer group {cg}", CONSUMER_GROUP);
                await cache.StreamCreateConsumerGroupAsync(streamKey, CONSUMER_GROUP, createStream: true);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error checking stream");
        }
        finally
        {
            streamCheckLock.Release();
        }
    }

    private async Task RegisterEndpointAsync()
    {
        var cache = cacheMux.GetDatabase();
        var key = string.Format(Backend.Shared.Consts.EVENT_SERVICE_ENDPOINT, eventId);
        await cache.StringSetAsync(key, serviceName, TimeSpan.FromDays(7), When.Always);
        Logger.LogInformation("Registered service endpoint {s}", serviceName);
    }
}