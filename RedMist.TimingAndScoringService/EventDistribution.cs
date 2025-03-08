using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedLockNet;
using RedMist.Database;
using RedMist.Database.Models;
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
    private readonly IDbContextFactory<TsContext> tsContext;
    private CancellationToken stoppingToken;

    private ILogger Logger { get; }
    private readonly string podInstance;

    public EventDistribution(HybridCache hcache, IConnectionMultiplexer cacheMux, ILoggerFactory loggerFactory,
        IConfiguration configuration, IDistributedLockFactory lockFactory, IDbContextFactory<TsContext> tsContext)
    {
        this.hcache = hcache;
        this.cacheMux = cacheMux;
        this.lockFactory = lockFactory;
        this.tsContext = tsContext;
        Logger = loggerFactory.CreateLogger(GetType().Name);
        podInstance = configuration["POD_NAME"] ?? throw new ArgumentNullException("POD_NAME");
    }

    /// <summary>
    /// Register this new pod with the workload listing.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this.stoppingToken = stoppingToken;
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
            var workloads = await LoadPodWorkloadsAsync();
            workloads.RemoveAll(x => x.PodName == podInstance);
            workloads.Add(new EventProcessorInstance { PodName = podInstance, Events = [] });
            await db.StringSetAsync(Consts.POD_WORKLOADS, JsonSerializer.Serialize(workloads));
        }
        else
        {
            Logger.LogCritical("Unable to acquire lock to register new pod instance.");
            throw new InvalidProgramException();
        }

        // TODO: clean up old streams for pods that no longer exist
    }

    /// <summary>
    /// Get the stream that should be used for the event processing.
    /// </summary>
    /// <returns>stream key</returns>
    public async Task<string> GetStreamAsync(string eventId, CancellationToken cancellationToken = default)
    {
        var key = string.Format(Consts.EVENT_TO_POD_KEY, eventId);
        var pod = await hcache.GetOrCreateAsync(key,
            async cancel => await AllocateNewEventForProcessingAsync(key, eventId),
            cancellationToken: cancellationToken
        );
        return string.Format(Consts.EVENT_STATUS_STREAM_KEY, pod);
    }

    /// <summary>
    /// Assigns the event to a pod for processing.
    /// </summary>
    /// <returns>event lookup value</returns>
    private async Task<string> AllocateNewEventForProcessingAsync(string key, string eventId)
    {
        // Create a new processing instance
        Logger.LogInformation("Creating new processing pod for event {0}...", eventId);

        using var podLock = await lockFactory.CreateLockAsync(Consts.POD_WORKLOADS, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50));
        if (podLock.IsAcquired)
        {
            var workloads = await LoadPodWorkloadsAsync();
            var pod = await AssignWorkloadToBestPodAsync(workloads, eventId);

            Logger.LogInformation("Assigning event {0} to pod {1}...", eventId, pod);
            var db = cacheMux.GetDatabase();
            await db.StringSetAsync(key, pod);
            return pod;
        }

        Logger.LogError("Unable to acquire lock {0} to assign new event {1}", Consts.POD_WORKLOADS, eventId);
        return string.Empty;
    }

    private async Task<List<EventProcessorInstance>> LoadPodWorkloadsAsync()
    {
        var db = cacheMux.GetDatabase();
        var pwJson = await db.StringGetAsync(Consts.POD_WORKLOADS);
        if (pwJson.IsNullOrEmpty)
        {
            return [];
        }
        return JsonSerializer.Deserialize<List<EventProcessorInstance>>(pwJson.ToString()) ?? [];
    }

    private async Task<string> AssignWorkloadToBestPodAsync(List<EventProcessorInstance> workloads, string eventId)
    {
        // For now, order by least number of events
        var inst = workloads.OrderBy(x => x.Events.Count).First();
        inst.Events.Add(eventId);

        var db = cacheMux.GetDatabase();
        await db.StringSetAsync(Consts.POD_WORKLOADS, JsonSerializer.Serialize(workloads));
        return inst.PodName;
    }

    public async Task<int> GetOrganizationId(string clientId)
    {
        var key = string.Format(Consts.CLIENT_ID, clientId);
        return await hcache.GetOrCreateAsync(key,
            async cancel => await LoadOrganizationId(clientId),
            cancellationToken: stoppingToken);
    }

    private async Task<int> LoadOrganizationId(string clientId)
    {
        using var db = await tsContext.CreateDbContextAsync(stoppingToken);
        var org = await db.Organizations.FirstOrDefaultAsync(x => x.ClientId == clientId, stoppingToken);
        return org?.Id ?? 0;
    }

    public async Task<int> GetEventId(int orgId, int eventReference)
    {
        var key = string.Format(Consts.EVENT_REF_ID, orgId, eventReference);
        return await hcache.GetOrCreateAsync(key,
            async cancel => await LoadEventId(orgId, eventReference),
            cancellationToken: stoppingToken);
    }

    private async Task<int> LoadEventId(int orgId, int eventReference)
    {
        using var db = await tsContext.CreateDbContextAsync(stoppingToken);
        var ev = await db.Events.FirstOrDefaultAsync(x => x.OrganizationId == orgId && x.EventReferenceId == eventReference, stoppingToken);
        return ev?.Id ?? 0;
    }

    public async Task<int> SaveOrUpdateEvent(int orgId, int eventReference, string name)
    {
        using var db = await tsContext.CreateDbContextAsync(stoppingToken);
        var ev = await db.Events.FirstOrDefaultAsync(x => x.OrganizationId == orgId && x.EventReferenceId == eventReference, stoppingToken);
        if (ev == null)
        {
            ev = new Event
            {
                OrganizationId = orgId,
                EventReferenceId = eventReference,
                Name = name,
                StartDate = DateTime.UtcNow,
            };
            db.Events.Add(ev);
        }
        else
        {
            ev.Name = name;
            ev.StartDate = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(stoppingToken);
        return ev.Id;
    }
}