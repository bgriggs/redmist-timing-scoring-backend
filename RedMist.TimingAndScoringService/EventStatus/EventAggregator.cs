using MediatR;
using Microsoft.AspNetCore.SignalR;
using RedMist.TimingAndScoringService.Hubs;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Diagnostics;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.EventStatus;

public class EventAggregator : BackgroundService
{
    private readonly Dictionary<int, IDataProcessor> processors = [];
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IDataProcessorFactory dataProcessorFactory;
    private readonly IMediator mediator;
    public Action<EventStatusUpdateEventArgs<List<TimingCommon.Models.EventStatus>>>? EventStatusUpdated;
    public Action<EventStatusUpdateEventArgs<List<EventEntry>>>? EventEntriesUpdated;
    public Action<EventStatusUpdateEventArgs<List<CarPosition>>>? CarPositionsUpdated;
    private ILogger Logger { get; }
    private readonly string podInstance;

    public EventAggregator(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, IConfiguration configuration,
        IDataProcessorFactory dataProcessorFactory, IMediator mediator)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.dataProcessorFactory = dataProcessorFactory;
        this.mediator = mediator;
        podInstance = configuration["POD_NAME"] ?? throw new ArgumentNullException("POD_NAME");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("Event Aggregator starting...");
        var cache = cacheMux.GetDatabase();
        var streamKey = string.Format(Consts.EVENT_STATUS_STREAM_KEY, podInstance);

        var sub = cacheMux.GetSubscriber();
        await sub.SubscribeAsync(new RedisChannel(Consts.SEND_FULL_STATUS, RedisChannel.PatternMode.Literal),
            async (channel, value) => await ProcessFullStatusRequest(value.ToString()),
            CommandFlags.FireAndForget);

        _ = Task.Run(() => SendFullUpdates(stoppingToken), stoppingToken);

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
                            }
                        }

                        await processor.ProcessUpdate(field.Value.ToString(), stoppingToken);
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
        await mediator.Publish(new StatusNotification(p.EventId, json), stoppingToken);
    }
}