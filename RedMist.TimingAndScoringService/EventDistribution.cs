using Microsoft.Extensions.Caching.Hybrid;
using RedLockNet;
using RedMist.TimingAndScoringService.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.TimingAndScoringService;

/// <summary>
/// Responsible for distributing events to the appropriate pod for processing.
/// </summary>
public class EventDistribution : BackgroundService
{
    private readonly HybridCache hcache;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IDistributedLockFactory lockFactory;

    private ILogger Logger { get; }
    private readonly string podInstance;

    public EventDistribution(HybridCache hcache, IConnectionMultiplexer cacheMux, ILoggerFactory loggerFactory, 
        IConfiguration configuration, IDistributedLockFactory lockFactory)
    {
        this.hcache = hcache;
        this.cacheMux = cacheMux;
        this.lockFactory = lockFactory;
        Logger = loggerFactory.CreateLogger(GetType().Name);
        podInstance = configuration["POD_NAME"] ?? throw new ArgumentNullException("POD_NAME");
    }

    /// <summary>
    /// Register this new pod with the workload listing.
    /// </summary>
    /// <param name="stoppingToken"></param>
    /// <returns></returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var streamKey = string.Format(Consts.EVENT_STATUS_STREAM_KEY, podInstance);

        // Create the redis stream and consumer group if they don't exist
        var db = cacheMux.GetDatabase();
        if (!await db.KeyExistsAsync(streamKey) || (await db.StreamGroupInfoAsync(streamKey)).All(x => x.Name != podInstance))
        {
            Logger.LogInformation("Creating new stream and consumer group for pod: {0}", podInstance);
            await db.StreamCreateConsumerGroupAsync(streamKey, podInstance, createStream: true);
        }

        // Register new Pod instance
        using var podLock = await lockFactory.CreateLockAsync(Consts.POD_WORKLOADS, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200));
        if (podLock.IsAcquired)
        {
            var workloads = await LoadPodWorkloads();
            workloads.Add(new EventProcessorInstance { PodName = podInstance, Events = [] });
            await db.StringSetAsync(Consts.POD_WORKLOADS, JsonSerializer.Serialize(workloads));
        }
        else
        {
            Logger.LogCritical("Unable to acquire lock to register new pod instance.");
            throw new InvalidProgramException();
        }

        // TODO: clean up old streams
    }

    /// <summary>
    /// Get the stream that should be used for the event processing.
    /// </summary>
    /// <returns>stream key</returns>
    public async Task<string> GetStream(string eventId, CancellationToken cancellationToken)
    {
        var key = string.Format(Consts.EVENT_TO_POD_KEY, eventId);
        var pod = await hcache.GetOrCreateAsync(key,
            async cancel => await AllocateNewEventForProcessing(key, eventId),
            cancellationToken: cancellationToken
        );
        return string.Format(Consts.EVENT_STATUS_STREAM_KEY, pod);
    }

    /// <summary>
    /// Assigns the event to a pod for processing.
    /// </summary>
    /// <returns>event lookup value</returns>
    private async Task<string> AllocateNewEventForProcessing(string key, string eventId)
    {
        // Create a new processing instance
        Logger.LogInformation("Creating new processing pod for event {0}...", eventId);

        using var podLock = await lockFactory.CreateLockAsync(Consts.POD_WORKLOADS, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50));
        if (podLock.IsAcquired)
        {
            var workloads = await LoadPodWorkloads();
            var pod = await AssignWorkloadToBestPod(workloads, eventId);

            Logger.LogInformation("Assigning event {0} to pod {1}...", eventId, pod);
            var db = cacheMux.GetDatabase();
            await db.StringSetAsync(key, pod);
            return pod;
        }

        Logger.LogError("Unable to acquire lock {0} to assign new event {1}", Consts.POD_WORKLOADS, eventId);
        return string.Empty;
    }

    private async Task<List<EventProcessorInstance>> LoadPodWorkloads()
    {
        var db = cacheMux.GetDatabase();
        var pwJson = await db.StringGetAsync(Consts.POD_WORKLOADS);
        if (pwJson.IsNullOrEmpty)
        {
            return [];
        }
        return JsonSerializer.Deserialize<List<EventProcessorInstance>>(pwJson!) ?? [];
    }

    private async Task<string> AssignWorkloadToBestPod(List<EventProcessorInstance> workloads, string eventId)
    {
        // For now, order by least number of events
        var inst = workloads.OrderBy(x => x.Events.Count).First();
        inst.Events.Add(eventId);

        var db = cacheMux.GetDatabase();
        await db.StringSetAsync(Consts.POD_WORKLOADS, JsonSerializer.Serialize(workloads));
        return inst.PodName;
    }
}
