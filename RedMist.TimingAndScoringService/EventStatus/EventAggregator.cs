﻿using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Models;
using RedMist.Database;
using RedMist.TimingAndScoringService.EventStatus.InCarDriverMode;
using RedMist.TimingAndScoringService.EventStatus.X2;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.IO.Compression;
using System.Text;
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
    private readonly IHubContext<StatusHub> hubContext;
    private static readonly TimeSpan fullSendInterval = TimeSpan.FromMilliseconds(5000);

    public Action<EventStatusUpdateEventArgs<List<TimingCommon.Models.EventStatus>>>? EventStatusUpdated;
    public Action<EventStatusUpdateEventArgs<List<EventEntry>>>? EventEntriesUpdated;
    public Action<EventStatusUpdateEventArgs<List<CarPosition>>>? CarPositionsUpdated;
    private ILogger Logger { get; }
    private readonly SemaphoreSlim streamCheckLock = new(1);
    private readonly SemaphoreSlim subscriptionCheckLock = new(1);
    private readonly SemaphoreSlim payloadSerializationLock = new(1);
    private DateTime? lastFullStatusTimestamp;
    private string? lastFullStatusData;
    private DateTime? lastPayloadChangedTimestamp;


    public EventAggregator(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, IConfiguration configuration,
        IMediator mediator, HybridCache hcache, IDbContextFactory<TsContext> tsContext, IHubContext<StatusHub> hubContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.loggerFactory = loggerFactory;
        this.cacheMux = cacheMux;
        this.mediator = mediator;
        this.hcache = hcache;
        this.tsContext = tsContext;
        this.hubContext = hubContext;
        eventId = configuration.GetValue("event_id", 0);
        streamKey = string.Format(Backend.Shared.Consts.EVENT_STATUS_STREAM_KEY, eventId);
        cacheMux.ConnectionRestored += CacheMux_ConnectionRestored;

        var sessionMonitor = new SessionMonitor(eventId, tsContext, loggerFactory);
        var pitProcessor = new PitProcessor(eventId, tsContext, loggerFactory);
        var flagProcessor = new FlagProcessor(eventId, tsContext, loggerFactory);
        var driverModeDataProcessor = new DriverModeProcessor(eventId, hubContext, loggerFactory, hcache, tsContext, cacheMux);
        dataProcessor = new OrbitsDataProcessor(eventId, mediator, loggerFactory, sessionMonitor, pitProcessor, flagProcessor, cacheMux, tsContext, driverModeDataProcessor);
        dataProcessor.PayloadChanged += DataProcessor_PayloadChanged;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("Event Aggregator starting...");
        await EnsureStream();
        await EnsureCacheSubscriptions(stoppingToken);

        // Start a task to send a full update every so often
        _ = Task.Run(() => SendFullUpdates(stoppingToken), stoppingToken);

        // Publish reset event to get full set of data from the relay
        await mediator.Publish(new RelayResetRequest { EventId = eventId, ForceTimingDataReset = true }, stoppingToken);

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
                await EnsureCacheSubscriptions(stoppingToken);
            }
        }
    }

    private async Task EnsureCacheSubscriptions(CancellationToken stoppingToken = default)
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
        await EnsureCacheSubscriptions();

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

    private void DataProcessor_PayloadChanged(Payload obj)
    {
        lastPayloadChangedTimestamp = DateTime.UtcNow;
    }

    private async Task ProcessFullStatusRequest(string cmdJson)
    {
        var cmd = JsonSerializer.Deserialize<SendStatusCommand>(cmdJson);
        if (cmd == null)
        {
            Logger.LogWarning("Invalid command received: {cj}", cmdJson);
            return;
        }

        Logger.LogInformation("Sending full status update for event {e} to new connection {con}", cmd.EventId, cmd.ConnectionId);
        var payload = await GetEventStatusWithRefreshAsync(dataProcessor);
        await SendEventStatusAsync(cmd.ConnectionId, payload);
    }

    private async Task SendFullUpdates(CancellationToken stoppingToken)
    {
        await Task.Delay(fullSendInterval, stoppingToken);

        var connKey = string.Format(Backend.Shared.Consts.STATUS_EVENT_CONNECTIONS, eventId);
        var maxInterval = TimeSpan.FromMilliseconds(50);
        var minInterval = TimeSpan.FromMilliseconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            var sendStart = DateTime.UtcNow;
            try
            {
                // Stagger the sending of full status updates to all connections
                var cache = cacheMux.GetDatabase();
                var connectionEntries = await cache.HashGetAllAsync(connKey);
                if (connectionEntries?.Length > 0)
                {
                    var connectionIds = connectionEntries.Select(x => x.Name.ToString()).ToArray();
                    var interval = TimeSpan.FromMilliseconds(fullSendInterval.TotalMilliseconds / connectionEntries.Length);
                    if (interval > maxInterval)
                        interval = maxInterval;
                    else if (interval < minInterval)
                        interval = minInterval;

                    Logger.LogTrace("Sending full status to {c} connections with interval {i}", connectionEntries.Length, interval.TotalMilliseconds);
                    foreach (var connectionId in connectionIds)
                    {
                        var payload = await GetEventStatusWithRefreshAsync(dataProcessor, stoppingToken);
                        await SendEventStatusAsync(connectionId, payload, stoppingToken);

                        await Task.Delay(interval, stoppingToken);
                        if (stoppingToken.IsCancellationRequested)
                            return;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error sending full update");
            }
            finally
            {
                var elapsed = DateTime.UtcNow - sendStart;
                if (elapsed < fullSendInterval)
                {
                    await Task.Delay(fullSendInterval - elapsed, stoppingToken);
                }
            }

            // Save off the last payload to the cache for quick access by other services
            await UpdateCachedPayload(dataProcessor, stoppingToken);
        }
    }

    private async Task SendEventStatusAsync(string connectionIdDestination, string payload, CancellationToken stoppingToken = default)
    {
        //Logger.LogTrace("Getting payload for event {e}...", p.EventId);
        await hubContext.Clients.Client(connectionIdDestination).SendAsync("ReceiveMessage", payload, stoppingToken);
        //await mediator.Publish(new StatusNotification(p.EventId, p.SessionId, b64) { ConnectionDestination = connectionIdDestination, Payload = payload, PitProcessor = p.PitProcessor }, stoppingToken);
    }

    /// <summary>
    /// Get compressed event status data.
    /// </summary>
    /// <param name="p"></param>
    private async Task<string> GetEventStatusWithRefreshAsync(OrbitsDataProcessor p, CancellationToken stoppingToken = default)
    {
        // See if the last payload is still the latest. When there is a newer change, update the payload.
        if (lastFullStatusTimestamp.HasValue && (lastPayloadChangedTimestamp <= lastFullStatusTimestamp.Value) && lastFullStatusData != null)
        {
            return lastFullStatusData;
        }

        await payloadSerializationLock.WaitAsync(stoppingToken);
        try
        {
            var payload = await p.GetPayload(stoppingToken);
            lastFullStatusTimestamp = DateTime.UtcNow;

            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            {
                gzip.Write(bytes, 0, bytes.Length);
            }
            var b64 = Convert.ToBase64String(output.ToArray());

            lastFullStatusData = b64;
            return lastFullStatusData;
        }
        finally
        {
            payloadSerializationLock.Release();
        }
    }


    private async Task UpdateCachedPayload(OrbitsDataProcessor p, CancellationToken stoppingToken)
    {
        try
        {
            var cache = cacheMux.GetDatabase();
            var payload = await p.GetPayload(stoppingToken);
            var json = JsonSerializer.Serialize(payload);

            Logger.LogTrace("Caching payload...");
            var key = string.Format(Backend.Shared.Consts.EVENT_PAYLOAD, eventId);
            await cache.StringSetAsync(key, json, TimeSpan.FromMinutes(1), When.Always);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error updating cached payload");
        }
    }

    #endregion
}