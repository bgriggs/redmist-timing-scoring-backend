using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Backend.Shared.Models;
using RedMist.Database;
using RedMist.TimingAndScoringService.EventStatus.X2;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// Coordinates the receiving of incoming timing data and sending the associated updates to UIs.
/// </summary>
public class EventAggregator : BackgroundService
{
    private const string CONSUMER_GROUP = "processor";
    private readonly int eventId;
    private readonly string streamKey;
    private readonly OrbitsDataProcessor dataProcessor;
    private readonly ILoggerFactory loggerFactory;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IMediator mediator;
    private readonly HybridCache hcache;
    private readonly IDbContextFactory<TsContext> tsContext;
    public Action<EventStatusUpdateEventArgs<List<TimingCommon.Models.EventStatus>>>? EventStatusUpdated;
    public Action<EventStatusUpdateEventArgs<List<EventEntry>>>? EventEntriesUpdated;
    public Action<EventStatusUpdateEventArgs<List<CarPosition>>>? CarPositionsUpdated;
    private ILogger Logger { get; }
    private readonly SemaphoreSlim streamCheckLock = new(1);
    private readonly SemaphoreSlim subscriptionCheckLock = new(1);


    public EventAggregator(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, IConfiguration configuration,
        IMediator mediator, HybridCache hcache, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.loggerFactory = loggerFactory;
        this.cacheMux = cacheMux;
        this.mediator = mediator;
        this.hcache = hcache;
        this.tsContext = tsContext;
        eventId = configuration.GetValue("event_id", 0);
        streamKey = string.Format(Backend.Shared.Consts.EVENT_STATUS_STREAM_KEY, eventId);
        cacheMux.ConnectionRestored += CacheMux_ConnectionRestored;

        var sessionMonitor = new SessionMonitor(eventId, tsContext, loggerFactory);
        var pitProcessor = new PitProcessor(eventId, tsContext, loggerFactory);
        var flagProcessor = new FlagProcessor(eventId, tsContext, loggerFactory);
        dataProcessor = new OrbitsDataProcessor(eventId, mediator, loggerFactory, sessionMonitor, pitProcessor, flagProcessor, cacheMux);
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("Event Aggregator starting...");
        await EnsureStream();
        await EnsureSubscriptions(stoppingToken);

        // Start a task to send a full update every so often
        _ = Task.Run(() => SendFullUpdates(stoppingToken), stoppingToken);

        // Publish reset event to get full set of data from the relay
        await mediator.Publish(new RelayResetRequest { EventId = eventId }, stoppingToken);

        // Start a task to read timing source data from this service's stream.
        // The SignalR hub is responsible for sending timing data to the stream.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cache = cacheMux.GetDatabase();
                var result = await cache.StreamReadGroupAsync(streamKey, CONSUMER_GROUP, "proc", ">", 1);
                foreach (var entry in result)
                {
                    foreach (var field in entry.Values)
                    {
                        // Process update from timing system
                        //Logger.LogTrace("Event Status Update: {e}", entry.Id);

                        // Check the message tag to determine the type of update (e.g. result monitor)
                        var tags = field.Name.ToString().Split('-');
                        if (tags.Length < 3)
                        {
                            Logger.LogWarning("Invalid event status update: {f}", field.Name);
                            continue;
                        }

                        var type = tags[0];
                        var eventId = int.Parse(tags[1]);
                        var sessionId = int.Parse(tags[2]);
                        var data = field.Value.ToString();

                        // Process the update
                        await dataProcessor.ProcessUpdate(type, data, sessionId, stoppingToken);
                    }

                    await cache.StreamAcknowledgeAsync(streamKey, CONSUMER_GROUP, entry.Id);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error reading event status stream");
                Logger.LogInformation("Throttling service for 5 secs");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                await EnsureStream();
                await EnsureSubscriptions(stoppingToken);
            }
        }
    }

    private async Task EnsureSubscriptions(CancellationToken stoppingToken = default)
    {
        await subscriptionCheckLock.WaitAsync(stoppingToken);
        try
        {
            var sub = cacheMux.GetSubscriber();
            await sub.UnsubscribeAllAsync();

            // Subscribe for full status requests such as when a new UI connects
            await sub.SubscribeAsync(new RedisChannel(Backend.Shared.Consts.SEND_FULL_STATUS, RedisChannel.PatternMode.Literal),
                async (channel, value) => await ProcessFullStatusRequest(value.ToString()), CommandFlags.FireAndForget);
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

    private async void CacheMux_ConnectionRestored(object? sender, ConnectionFailedEventArgs e)
    {
        await EnsureStream();
        await EnsureSubscriptions();

        // Publish reset event to get full set of data from the relay
        await mediator.Publish(new RelayResetRequest { EventId = eventId });
    }

    private async Task EnsureStream()
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

    #region Event Status Requests

    private async Task ProcessFullStatusRequest(string cmdJson)
    {
        var cmd = JsonSerializer.Deserialize<SendStatusCommand>(cmdJson);
        if (cmd == null)
        {
            Logger.LogWarning("Invalid command received: {cj}", cmdJson);
            return;
        }
        Logger.LogInformation("Sending full status update for event {e} to new connection {con}", cmd.EventId, cmd.ConnectionId);
        await SendEventStatusAsync(dataProcessor, cmd.ConnectionId);
    }

    private async Task SendFullUpdates(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            try
            {
                await SendEventStatusAsync(dataProcessor, string.Empty, stoppingToken);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error sending full update");
            }
        }
    }

    private async Task SendEventStatusAsync(OrbitsDataProcessor p, string connectionIdDestination, CancellationToken stoppingToken = default)
    {
        //Logger.LogTrace("Getting payload for event {e}...", p.EventId);
        var payload = await p.GetPayload(stoppingToken);
        var json = JsonSerializer.Serialize(payload);
        await mediator.Publish(new StatusNotification(p.EventId, p.SessionId, json) { ConnectionDestination = connectionIdDestination, Payload = payload, PitProcessor = p.PitProcessor }, stoppingToken);
    }

    #endregion
}