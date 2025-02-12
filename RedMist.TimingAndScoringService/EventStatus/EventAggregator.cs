using RedMist.TimingCommon.Models;
using StackExchange.Redis;

namespace RedMist.TimingAndScoringService.EventStatus;

public class EventAggregator : BackgroundService
{
    private readonly Dictionary<int, IDataProcessor> processors = [];
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IDataProcessorFactory dataProcessorFactory;
    public Action<EventStatusUpdateEventArgs<List<TimingCommon.Models.EventStatus>>>? EventStatusUpdated;
    public Action<EventStatusUpdateEventArgs<List<EventEntries>>>? EventEntriesUpdated;
    public Action<EventStatusUpdateEventArgs<List<CarPosition>>>? CarPositionsUpdated;
    private ILogger Logger { get; }
    private readonly string podInstance;

    public EventAggregator(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, IConfiguration configuration, IDataProcessorFactory dataProcessorFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.dataProcessorFactory = dataProcessorFactory;
        podInstance = configuration["POD_NAME"] ?? throw new ArgumentNullException("POD_NAME");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("Event Aggregator starting...");
        var cache = cacheMux.GetDatabase();
        var streamKey = string.Format(Consts.EVENT_STATUS_STREAM_KEY, podInstance);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await cache.StreamReadGroupAsync(streamKey, podInstance, podInstance, ">", 1);
                foreach (var entry in result)
                {
                    foreach( var field in entry.Values)
                    {
                        Logger.LogTrace("Event Status Update: {0}", entry.Id);

                        var tags = field.Name.ToString().Split('-');
                        if (tags.Length < 2)
                        {
                            Logger.LogWarning("Invalid event status update: {0}", field.Name);
                            continue;
                        }
                        var type = tags[0];
                        var eventId = int.Parse(tags[1]);

                        if (!processors.TryGetValue(eventId, out var processor))
                        {
                            processor = dataProcessorFactory.CreateDataProcessor(type);
                            processors[eventId] = processor;
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
}
