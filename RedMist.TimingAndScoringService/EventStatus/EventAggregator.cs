using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.ControlLogs;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Diagnostics;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// Coordinates the receiving of incoming timing data and sending the associated updates to UIs.
/// </summary>
public class EventAggregator : BackgroundService
{
    private readonly Dictionary<int, IDataProcessor> processors = [];
    private readonly Dictionary<int, ControlLogCache> controlLogCaches = [];
    private readonly ILoggerFactory loggerFactory;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IDataProcessorFactory dataProcessorFactory;
    private readonly IMediator mediator;
    private readonly HybridCache hcache;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly IControlLogFactory controlLogFactory;
    public Action<EventStatusUpdateEventArgs<List<TimingCommon.Models.EventStatus>>>? EventStatusUpdated;
    public Action<EventStatusUpdateEventArgs<List<EventEntry>>>? EventEntriesUpdated;
    public Action<EventStatusUpdateEventArgs<List<CarPosition>>>? CarPositionsUpdated;
    private ILogger Logger { get; }
    private readonly string podInstance;

    public EventAggregator(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, IConfiguration configuration,
        IDataProcessorFactory dataProcessorFactory, IMediator mediator, HybridCache hcache,
        IDbContextFactory<TsContext> tsContext, IControlLogFactory controlLogFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.loggerFactory = loggerFactory;
        this.cacheMux = cacheMux;
        this.dataProcessorFactory = dataProcessorFactory;
        this.mediator = mediator;
        this.hcache = hcache;
        this.tsContext = tsContext;
        this.controlLogFactory = controlLogFactory;
        podInstance = configuration["POD_NAME"] ?? throw new ArgumentNullException("POD_NAME");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("Event Aggregator starting...");
        var cache = cacheMux.GetDatabase();
        var streamKey = string.Format(Consts.EVENT_STATUS_STREAM_KEY, podInstance);

        var sub = cacheMux.GetSubscriber();

        // Subscribe for full status requests such as when a new UI connects
        await sub.SubscribeAsync(new RedisChannel(Consts.SEND_FULL_STATUS, RedisChannel.PatternMode.Literal),
            async (channel, value) => await ProcessFullStatusRequest(value.ToString()),
            CommandFlags.FireAndForget);

        // Subscribe to control log requests such as when UI details opens for a car
        await sub.SubscribeAsync(new RedisChannel(Consts.SEND_CONTROL_LOG, RedisChannel.PatternMode.Literal),
            async (channel, value) => await ProcessControlLogRequest(value.ToString(), stoppingToken),
            CommandFlags.FireAndForget);

        // Start a task to send a full update every so often
        _ = Task.Run(() => SendFullUpdates(stoppingToken), stoppingToken);
        // Start a task to send a control log updates every so often
        _ = Task.Run(() => SendControlLogUpdates(stoppingToken), stoppingToken);

        // Start a task to read timing source data from this service's stream.
        // The SignalR hub is responsible for sending timing data to the stream.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await cache.StreamReadGroupAsync(streamKey, podInstance, podInstance, ">", 1);
                foreach (var entry in result)
                {
                    foreach (var field in entry.Values)
                    {
                        // Process update from timing system
                        Logger.LogTrace("Event Status Update: {0}", entry.Id);

                        // Check the message tag to determine the type of update (e.g. result monitor)
                        var tags = field.Name.ToString().Split('-');
                        if (tags.Length < 2)
                        {
                            Logger.LogWarning("Invalid event status update: {0}", field.Name);
                            continue;
                        }

                        var type = tags[0];
                        var eventId = int.Parse(tags[1]);

                        IDataProcessor? processor;
                        lock (processors)
                        {
                            if (!processors.TryGetValue(eventId, out processor))
                            {
                                processor = dataProcessorFactory.CreateDataProcessor(type, eventId);
                                processors[eventId] = processor;

                                // Create a control log cache for the event
                                var controlLogCache = new ControlLogCache(eventId, loggerFactory, tsContext, controlLogFactory);
                                controlLogCaches[eventId] = controlLogCache;
                            }
                        }

                        var data = field.Value.ToString();
                        _ = Task.Run(() => LogStatusData(eventId, data, stoppingToken), stoppingToken);
                        await processor.ProcessUpdate(data, stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error reading event status stream");
            }
        }
    }

    private async Task ProcessFullStatusRequest(string cmdJson)
    {
        var cmd = JsonSerializer.Deserialize<SendStatusCommand>(cmdJson);
        if (cmd == null)
        {
            Logger.LogWarning("Invalid command received: {0}", cmdJson);
            return;
        }

        IDataProcessor? p;
        lock (processors)
        {
            processors.TryGetValue(cmd.EventId, out p);
        }

        if (p != null)
        {
            Logger.LogInformation("Sending full status update for event {0} to new connection {1}", cmd.EventId, cmd.ConnectionId);
            await SendEventStatus(p, cmd.ConnectionId);
        }
    }

    private async Task SendFullUpdates(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            Logger.LogInformation("Sending full update...");
            var sw = Stopwatch.StartNew();
            IDataProcessor[] ps;
            lock (processors)
            {
                ps = [.. processors.Values];
            }
            try
            {
                foreach (var p in ps)
                {
                    await SendEventStatus(p, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error sending full update");
            }
            Logger.LogInformation("Full update sent in {0}ms", sw.ElapsedMilliseconds);
        }
    }

    private async Task SendEventStatus(IDataProcessor p, CancellationToken stoppingToken = default)
    {
        await SendEventStatus(p, string.Empty, stoppingToken);
    }

    private async Task SendEventStatus(IDataProcessor p, string connectionIdDestination, CancellationToken stoppingToken = default)
    {
        Logger.LogDebug("Getting payload for event {0}...", p.EventId);
        var payload = await p.GetPayload(stoppingToken);
        var json = JsonSerializer.Serialize(payload);
        await mediator.Publish(new StatusNotification(p.EventId, json) { Payload = payload }, stoppingToken);
    }


    #region Control Log

    private async Task ProcessControlLogRequest(string cmdJson, CancellationToken stoppingToken = default)
    {
        var cmd = JsonSerializer.Deserialize<SendControlLogCommand>(cmdJson);
        if (cmd == null)
        {
            Logger.LogWarning("Invalid command received: {0}", cmdJson);
            return;
        }

        if (controlLogCaches.TryGetValue(cmd.EventId, out var controlLog))
        {
            var entries = await controlLog.GetCarControlEntries([cmd.CarNumber.ToLower()]);
            Logger.LogInformation("Sending control logs for event {0} car {1} to new connection {1}", cmd.EventId, cmd.CarNumber, cmd.ConnectionId);
            var ccl = new CarControlLogs { CarNumber = cmd.CarNumber, ControlLogEntries = [.. entries.SelectMany(s => s.Value)] };
            await mediator.Publish(new ControlLogNotification(cmd.EventId, ccl) { ConnectionDestination = cmd.ConnectionId }, stoppingToken);
        }
    }

    private async Task SendControlLogUpdates(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            Logger.LogInformation("Requesting control log update...");
            var sw = Stopwatch.StartNew();

            try
            {
                foreach (var controlLogs in controlLogCaches.ToDictionary())
                {
                    var changedCars = await controlLogs.Value.RequestControlLogChanges(stoppingToken);
                    var entries = await controlLogs.Value.GetCarControlEntries([.. changedCars]);
                    foreach (var e in entries)
                    {
                        var ccl = new CarControlLogs { CarNumber = e.Key, ControlLogEntries = e.Value };
                        var notificaiton = new ControlLogNotification(controlLogs.Key, ccl) { CarNumber = e.Key };
                        _ = mediator.Publish(notificaiton, stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error sending full update");
            }
            Logger.LogDebug("Full update sent in {0}ms", sw.ElapsedMilliseconds);
        }
    }

    #endregion

    #region Event Data Logging

    private async Task LogStatusData(int eventId, string data, CancellationToken stoppingToken)
    {
        try
        {
            var isEnabled = await IsLoggingEnabled(eventId, stoppingToken);
            if (isEnabled)
            {
                using var db = await tsContext.CreateDbContextAsync(stoppingToken);
                var log = new EventStatusLog
                {
                    EventId = eventId,
                    Timestamp = DateTime.UtcNow,
                    Data = data,
                };
                db.EventStatusLogs.Add(log);
                await db.SaveChangesAsync(stoppingToken);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error logging status data for event {0}", eventId);
        }
    }

    private async Task<bool> IsLoggingEnabled(int eventId, CancellationToken stoppingToken)
    {
        var key = string.Format(Consts.LOG_EVENT_DATA, eventId);
        return await hcache.GetOrCreateAsync(key,
            async cancel => await LoadIsEventLoggingEnabled(eventId, stoppingToken),
            cancellationToken: stoppingToken);
    }

    private async Task<bool> LoadIsEventLoggingEnabled(int eventId, CancellationToken stoppingToken)
    {
        using var db = await tsContext.CreateDbContextAsync(stoppingToken);
        var isEnabled = await db.Events.Where(x => x.Id == eventId).Select(x => x.EnableSourceDataLogging).FirstOrDefaultAsync(stoppingToken);
        return isEnabled;
    }

    #endregion
}