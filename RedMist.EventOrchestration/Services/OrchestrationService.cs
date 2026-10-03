using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventOrchestration.Models;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Reflection;
using System.Text.Json;

namespace RedMist.EventOrchestration.Services;

public class OrchestrationService : BackgroundService
{
    #region Metrics

    private static readonly Gauge LiveEventsCount = Metrics.CreateGauge(Consts.LIVE_EVENTS_COUNT_KEY, "Number of currently live events");

    #endregion

    private readonly IConnectionMultiplexer cacheMux;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly EventsChecker eventsChecker;
    private readonly string redisFailoverName;

    private ILogger Logger { get; }
    private const string namespaceFile = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";
    private readonly static TimeSpan checkInterval = TimeSpan.FromMilliseconds(10000);
    private readonly static TimeSpan eventTimeout = TimeSpan.FromMinutes(10);
    private readonly ContainerDetails eventProcessorContainerDetails;
    private readonly ContainerDetails controlLogContainerDetails;
    private readonly ContainerDetails loggerContainerDetails;
    // Source container for events whose timing data comes from an external source.
    // Image is supplied via configuration (helm values) so no source-specific name lives here.
    private readonly ContainerDetails? externalSourceContainerDetails;
    // Creates the Kubernetes client used for each reconciliation pass. Defaults to the in-cluster
    // client; tests supply a stand-in so no cluster is required.
    private readonly Func<IKubernetes> kubernetesFactory;
    // Track last known Redis replica count to avoid unnecessary patches
    private int lastRedisReplicas = -1;


    public OrchestrationService(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux,
        IDbContextFactory<TsContext> tsContext, EventsChecker eventsChecker, IConfiguration configuration,
        Func<IKubernetes>? kubernetesFactory = null)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.tsContext = tsContext;
        this.eventsChecker = eventsChecker;
        this.kubernetesFactory = kubernetesFactory ?? (() => new Kubernetes(KubernetesClientConfiguration.InClusterConfig()));
        redisFailoverName = configuration["Redis:FailoverName"] ?? "redis";

        // Get the current assembly version for container tags
        var version = GetAssemblyVersion();
        eventProcessorContainerDetails = new(
            "bigmission/redmist-event-processor", version, "{0}-evt-{1}-event-processor", true,
            "100m", "220Mi", "350m", "800Mi");
        controlLogContainerDetails = new(
            "bigmission/redmist-control-log", version, "{0}-evt-{1}-control-log", false,
            "60m", "310Mi", "150m", "550Mi");
        loggerContainerDetails = new(
            "bigmission/redmist-event-logger", version, "{0}-evt-{1}-logger", false,
            "55m", "90Mi", "400m", "550Mi");

        // Optional external-source container; only configured deployments can run external events.
        var externalImage = configuration["ExternalSource:Image"];
        if (!string.IsNullOrWhiteSpace(externalImage))
        {
            externalSourceContainerDetails = new(
                externalImage, configuration["ExternalSource:Version"] ?? version,
                "{0}-evt-{1}-external-source", false,
                configuration["ExternalSource:CpuRequest"] ?? "60m",
                configuration["ExternalSource:MemoryRequest"] ?? "128Mi",
                configuration["ExternalSource:CpuLimit"] ?? "300m",
                configuration["ExternalSource:MemoryLimit"] ?? "512Mi");
        }

        Logger.LogInformation("OrchestrationService initialized with version {version}", version);
    }

    /// <summary>
    /// Gets the current assembly version for use in container tags.
    /// </summary>
    /// <returns>The assembly version string</returns>
    private string GetAssemblyVersion()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            if (version == null)
            {
                Logger.LogWarning("Assembly version is null, falling back to 'latest' tag for container images");
                return "latest";
            }
            return version.ToString();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to retrieve assembly version, falling back to 'latest' tag for container images");
            return "latest";
        }
    }

    /// <summary>
    /// Gets the current Kubernetes namespace the pod is running in.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The current namespace name</returns>
    private async Task<string> GetCurrentNamespaceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(namespaceFile))
            {
                Logger.LogWarning("Namespace file {namespaceFile} does not exist, falling back to 'default' namespace", namespaceFile);
                return "default";
            }

            var currentNamespace = await File.ReadAllTextAsync(namespaceFile, cancellationToken);
            currentNamespace = currentNamespace.Trim();

            if (string.IsNullOrWhiteSpace(currentNamespace))
            {
                Logger.LogWarning("Namespace file {namespaceFile} is empty, falling back to 'default' namespace", namespaceFile);
                return "default";
            }

            Logger.LogTrace("Found namespace {namespace}", currentNamespace);
            return currentNamespace;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to read namespace from {namespaceFile}, falling back to 'default' namespace", namespaceFile);
            return "default";
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Start pod status polling in the background
        _ = PollPodStatusesAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Set live events first for running outside of K8s in debug where K8s calls will fail
                var currentEvents = await eventsChecker.GetCurrentEventsAsync();
                Logger.LogDebug("Found {eventCount} current events", currentEvents.Count);
                LiveEventsCount.Set(currentEvents.Count);

                // Update the live events in the database
                await UpdateLiveEventsAsync(currentEvents);

                string currentNamespace = await GetCurrentNamespaceAsync(stoppingToken);
                using var client = kubernetesFactory();

                // Get currently active jobs in the namespace
                var currentJobs = await GetJobsAsync(client, currentNamespace, stoppingToken);

                // Scale Redis replicas based on whether any live event is active
                await EnsureRedisReplicasAsync(client, currentNamespace, currentEvents.Count, stoppingToken);

                // Check for expired events
                var expiredEvents = currentEvents.Where(e => e.Timestamp < DateTime.UtcNow - eventTimeout).ToList();
                if (expiredEvents.Count > 0)
                {
                    Logger.LogInformation("Found {expiredCount} expired events", expiredEvents.Count);

                    // Send pre-shutdown notification to event processors to allow graceful shutdown
                    Logger.LogInformation("Sending pre-shutdown notification for expired events");
                    await SendPreshutdownNotificationAsync([.. expiredEvents.Select(e => e.EventId)]);

                    // Wait 15 seconds to allow any in-flight operations to complete, such as session finalization
                    Logger.LogInformation("Waiting 15 seconds for event processors to shut down gracefully");
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

                    Logger.LogInformation("Disposing expired events and cleaning up resources...");
                    foreach (var expired in expiredEvents)
                    {
                        Logger.LogInformation("Event {eventId} has expired, cleaning up.", expired.EventId);
                        await DisposeEventAsync(expired, client, currentNamespace, currentJobs, stoppingToken);
                    }

                    // Wait 15 seconds to allow jobs to stop
                    Logger.LogInformation("Waiting 15 seconds for event processor jobs to shut down");
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

                    // Remove expired redis streams
                    var cache = cacheMux.GetDatabase();
                    foreach (var expired in expiredEvents)
                    {
                        Logger.LogInformation("Cleanup redis streams for expired event {eventId}", expired.EventId);
                        var eventStatusStreamKey = string.Format(Consts.EVENT_STATUS_STREAM_KEY, expired.EventId);
                        await cache.KeyDeleteAsync(eventStatusStreamKey);
                        var eventLogsStreamKey = string.Format(Consts.EVENT_PROCESSOR_LOGGING_STREAM_KEY, expired.EventId);
                        await cache.KeyDeleteAsync(eventLogsStreamKey);
                        var externalLogsStreamKey = string.Format(Consts.EVENT_EXTERNAL_LOG_STREAM_KEY, expired.EventId);
                        await cache.KeyDeleteAsync(externalLogsStreamKey);
                        var viewershipStreamKey = string.Format(Consts.EVENT_VIEWERSHIP_STREAM_KEY, expired.EventId);
                        await cache.KeyDeleteAsync(viewershipStreamKey);
                    }
                }
                // Check for orphaned jobs
                var eventIds = currentEvents.Select(e => e.EventId).ToArray();
                var jobsToDelete = currentJobs.Items.Where(job => IsOrphanedJob(job.Metadata.Name, eventIds)).ToList();
                var deleteOptions = new V1DeleteOptions { PropagationPolicy = "Foreground" };
                foreach (var job in jobsToDelete)
                {
                    Logger.LogInformation("Job {jobName} is orphaned, deleting.", job.Metadata.Name);
                    try
                    {
                        await client.BatchV1.DeleteNamespacedJobAsync(job.Metadata.Name, currentNamespace, body: deleteOptions, cancellationToken: stoppingToken);

                        // Also try to delete the associated service if it exists
                        var serviceName = $"{job.Metadata.Name}-service";
                        try
                        {
                            await client.CoreV1.DeleteNamespacedServiceAsync(serviceName, currentNamespace, body: deleteOptions, cancellationToken: stoppingToken);
                            Logger.LogInformation("Deleted orphaned service {serviceName}", serviceName);
                        }
                        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            // Service doesn't exist, which is fine
                            Logger.LogTrace("Orphaned service {serviceName} not found (may not have been a service job)", serviceName);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "Failed to delete orphaned service {serviceName} in namespace {ns}", serviceName, currentNamespace);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Failed to delete orphaned job {jobName} in namespace {ns}", job.Metadata.Name, currentNamespace);
                    }
                }

                // Get events that need jobs created
                var activeEvents = currentEvents.Where(e => !expiredEvents.Contains(e)).ToList();
                using var db = await tsContext.CreateDbContextAsync(stoppingToken);
                foreach (var evt in activeEvents)
                {
                    await EnsureEventJobsAsync(evt, client, currentNamespace, currentJobs, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "An error occurred in the orchestration service.");
            }

            await Task.Delay(checkInterval, stoppingToken);
        }
    }

    /// <summary>
    /// Gets the jobs active in kubernetes for the current namespace.
    /// </summary>
    private async Task<V1JobList> GetJobsAsync(IKubernetes client, string ns, CancellationToken stoppingToken)
    {
        var jobs = await client.BatchV1.ListNamespacedJobAsync(ns, labelSelector: "event_id", cancellationToken: stoppingToken);
        Logger.LogDebug("Found {jobCount} jobs in namespace {ns}", jobs.Items.Count, ns);
        foreach (var job in jobs.Items)
        {
            Logger.LogTrace("Found job {jobName} in namespace {ns}", job.Metadata.Name, ns);
        }

        return jobs;
    }

    /// <summary>
    /// Sets the IsLive flag on Events when they are currently active in the system.
    /// All other Events are set to false.
    /// </summary>
    private async Task UpdateLiveEventsAsync(List<RelayConnectionEventEntry> currentEvents)
    {
        using var context = await tsContext.CreateDbContextAsync();
        var currentEventIds = currentEvents.Select(e => e.EventId).ToList();

        if (currentEventIds.Count == 0)
        {
            // If no events are active, set all to not live
            await context.Database.ExecuteSqlRawAsync("UPDATE \"Events\" SET \"IsLive\" = false");
        }
        else
        {
            // Use ANY operator with array parameter for PostgreSQL
            await context.Database.ExecuteSqlRawAsync(
                @"UPDATE ""Events"" SET ""IsLive"" = CASE 
                WHEN ""Id"" = ANY(@p0) THEN true
                ELSE false
                END",
                currentEventIds.ToArray());
        }
    }

    /// <summary>
    /// Sends notification to event processors that the event is shutting down.
    /// </summary>
    /// <param name="eventIds">List of event IDs that are shutting down.</param>
    internal async Task SendPreshutdownNotificationAsync(List<int> eventIds)
    {
        try
        {
            var sub = cacheMux.GetSubscriber();
            var message = JsonSerializer.Serialize(eventIds);
            await sub.PublishAsync(new RedisChannel(Consts.EVENT_SHUTDOWN_SIGNAL, RedisChannel.PatternMode.Literal), message);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send pre-shutdown notification for events {eventIds}", string.Join(", ", eventIds));
        }
    }

    /// <summary>
    /// The fragment of a job name that identifies the event it belongs to.
    /// </summary>
    /// <remarks>
    /// The trailing hyphen is load-bearing. Job names are built as <c>{org}-evt-{eventId}-{role}</c>,
    /// so matching on <c>evt-{id}</c> alone makes every id a prefix match for the ids that extend it —
    /// tearing down event 4 would delete the running jobs of events 40-49 and 400+ mid-race.
    /// </remarks>
    internal static string EventJobKey(int eventId) => $"evt-{eventId}-";

    /// <summary>
    /// True when a job does not belong to any of the events that are currently live, and so should be reaped.
    /// </summary>
    /// <remarks>
    /// This is the more destructive of the two matching sites — it deletes jobs whose event is not in the
    /// live set at all — so it must use the same segment-aware key as teardown. Matching on a bare
    /// "evt-{id}" substring here kept orphans alive whenever a live event's id extended theirs.
    /// </remarks>
    internal static bool IsOrphanedJob(string jobName, IReadOnlyCollection<int> liveEventIds) =>
        !liveEventIds.Any(id => jobName.Contains(EventJobKey(id)));

    /// <summary>
    /// Cleans up expired events and their associated jobs.
    /// </summary>
    internal async Task DisposeEventAsync(RelayConnectionEventEntry eventEntry, IKubernetes client, string ns, V1JobList jobs, CancellationToken stoppingToken)
    {
        var cache = cacheMux.GetDatabase();
        var hashKey = new RedisKey(Consts.RELAY_EVENT_CONNECTIONS);
        var entryKey = string.Format(Consts.RELAY_HEARTBEAT, eventEntry.EventId);

        // Remove the event entry from the cache
        await cache.HashDeleteAsync(hashKey, entryKey);

        // Remove any associated jobs and services that are running
        var eventKey = EventJobKey(eventEntry.EventId);
        var deleteOptions = new V1DeleteOptions { PropagationPolicy = "Foreground" };

        foreach (var job in jobs.Items)
        {
            if (job.Metadata.Name.Contains(eventKey, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await client.BatchV1.DeleteNamespacedJobAsync(job.Metadata.Name, ns, body: deleteOptions, cancellationToken: stoppingToken);

                    // Also try to delete the associated service if it exists
                    var serviceName = $"{job.Metadata.Name}-service";
                    try
                    {
                        await client.CoreV1.DeleteNamespacedServiceAsync(serviceName, ns, body: deleteOptions, cancellationToken: stoppingToken);
                        Logger.LogInformation("Deleted service {serviceName} for expired event {eventId}", serviceName, eventEntry.EventId);
                    }
                    catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // Service doesn't exist, which is fine
                        Logger.LogTrace("Service {serviceName} not found (may not have been a service job)", serviceName);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Failed to delete service {serviceName} for expired event {eventId}", serviceName, eventEntry.EventId);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to delete job {jobName} in namespace {ns}", job.Metadata.Name, ns);
                }
            }
        }

        // Close any viewer sessions the logger did not get to, before the connection hash they are
        // reconciled against goes away. The other order leaves a window in which a still-running
        // logger reads an empty hash and closes them as absent, which is the same end time under a
        // reason that says something different about why the event's numbers stop where they do.
        await CloseOpenViewerSessionsAsync(eventEntry, stoppingToken);

        // Remove the event entry from the cache that tracks connections
        await DisposeEventConnectionsAsync(eventEntry, stoppingToken);

        // Remove the service statuses from cache
        var serviceStatusKey = string.Format(Consts.EVENT_SERVICE_STATUSES, eventEntry.EventId);
        await cache.KeyDeleteAsync(serviceStatusKey, CommandFlags.FireAndForget);

        // Remove the relay message totals. The per-minute hashes are left to expire on their own:
        // there are up to a hundred and twenty of them, each gone within two hours of its last write.
        var relayMessageCountsKey = string.Format(Consts.RELAY_MESSAGE_COUNTS, eventEntry.EventId);
        await cache.KeyDeleteAsync(relayMessageCountsKey, CommandFlags.FireAndForget);
    }

    /// <summary>
    /// Ends any viewer session still open for an event being torn down.
    /// </summary>
    /// <remarks>
    /// The logger pod closes its own sessions when it sees the shutdown signal, and normally gets
    /// there first. This is the backstop for when it does not - it was never scheduled, it had
    /// already crashed, or it was killed before the signal reached it - because a session left open
    /// is never closed by anything afterwards and grows the event's viewer-minutes without bound.
    /// Idempotent against the logger's own close: only rows that still have no end time are touched.
    /// </remarks>
    private async Task CloseOpenViewerSessionsAsync(RelayConnectionEventEntry eventEntry, CancellationToken stoppingToken)
    {
        try
        {
            using var db = await tsContext.CreateDbContextAsync(stoppingToken);
            var open = await db.EventViewerSessions
                .Where(s => s.EventId == eventEntry.EventId && s.EndUtc == null)
                .ToListAsync(stoppingToken);
            if (open.Count == 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            foreach (var session in open)
            {
                session.EndUtc = now < session.StartUtc ? session.StartUtc : now;
                session.EndReason = ViewerSessionEndReason.EventTeardown;
            }

            await db.SaveChangesAsync(stoppingToken);
            Logger.LogInformation("Closed {n} open viewer sessions for expired event {eventId}", open.Count, eventEntry.EventId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to close open viewer sessions for event {eventId}", eventEntry.EventId);
        }
    }

    private async Task DisposeEventConnectionsAsync(RelayConnectionEventEntry eventEntry, CancellationToken stoppingToken)
    {
        try
        {
            var cache = cacheMux.GetDatabase();
            var entryKey = string.Format(Consts.STATUS_EVENT_CONNECTIONS, eventEntry.EventId);

            // Remove the event entry from the cache
            await cache.KeyDeleteAsync(entryKey, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete event connection entry {entryKey}", eventEntry.EventId);
        }
    }

    /// <summary>
    /// Make sure the necessary jobs for an event are created.
    /// </summary>
    internal async Task EnsureEventJobsAsync(RelayConnectionEventEntry evt, IKubernetes client, string ns, V1JobList jobs, CancellationToken stoppingToken)
    {
        using var db = await tsContext.CreateDbContextAsync(stoppingToken);
        var org = await db.Organizations.FirstOrDefaultAsync(o => o.Id == evt.OrganizationId, cancellationToken: stoppingToken);
        if (org == null)
        {
            Logger.LogWarning("Organization {orgId} not found for event {eventId}.", evt.OrganizationId, evt.EventId);
            return;
        }
        var eventDefinition = await db.Events.FirstOrDefaultAsync(e => e.Id == evt.EventId, cancellationToken: stoppingToken);
        if (eventDefinition == null)
        {
            Logger.LogWarning("Event definition for event {eventId} not found.", evt.EventId);
            return;
        }

        // Check control log processing if enabled
        if (!string.IsNullOrWhiteSpace(org.ControlLogType))
        {
            var clJobName = string.Format(controlLogContainerDetails.JobFormat, org.ShortName.ToLower(), evt.EventId);
            await EnsureJobRunningAsync(client, ns, jobs, clJobName, evt.EventId, eventDefinition.Name, org.Id, org.Name, controlLogContainerDetails, stoppingToken);
        }

        // Check for logger job
        var loggerJobName = string.Format(loggerContainerDetails.JobFormat, org.ShortName.ToLower(), evt.EventId);
        await EnsureJobRunningAsync(client, ns, jobs, loggerJobName, evt.EventId, eventDefinition.Name, org.Id, org.Name, loggerContainerDetails, stoppingToken);

        // Check for event processor job
        var epJobName = string.Format(eventProcessorContainerDetails.JobFormat, org.ShortName.ToLower(), evt.EventId);
        await EnsureJobRunningAsync(client, ns, jobs, epJobName, evt.EventId, eventDefinition.Name, org.Id, org.Name, eventProcessorContainerDetails, stoppingToken);

        // Events whose data comes from an external source need that source's container started too.
        if (eventDefinition.TimingSource == TimingSource.External)
        {
            if (externalSourceContainerDetails is null)
            {
                Logger.LogWarning("Event {eventId} uses an external timing source but no external source image is configured.", evt.EventId);
            }
            else
            {
                var extJobName = string.Format(externalSourceContainerDetails.JobFormat, org.ShortName.ToLower(), evt.EventId);
                await EnsureJobRunningAsync(client, ns, jobs, extJobName, evt.EventId, eventDefinition.Name, org.Id, org.Name, externalSourceContainerDetails, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Ensures a job is running. If it's completed or failed, it will be deleted and recreated.
    /// </summary>
    internal async Task EnsureJobRunningAsync(IKubernetes client, string ns, V1JobList jobs, string jobName, int eventId, string eventName, int organizationId, string organizationName, ContainerDetails containerDetails, CancellationToken stoppingToken)
    {
        var existingJob = jobs.Items.FirstOrDefault(job => job.Metadata.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase));

        if (existingJob == null)
        {
            Logger.LogInformation("Job {jobName} does not exist for event {eventId}. Creating new job.", jobName, eventId);
            await CreateJobAsync(client, jobName, ns, containerDetails, eventId, eventName, organizationId, organizationName, stoppingToken);
            return;
        }

        // Check if the job has completed (either successfully or failed)
        var isCompleted = (existingJob.Status?.Succeeded ?? 0) > 0 || (existingJob.Status?.Failed ?? 0) > 0;

        if (isCompleted)
        {
            Logger.LogInformation("Job {jobName} for event {eventId} has completed. Deleting and recreating.", jobName, eventId);

            var deleteOptions = new V1DeleteOptions { PropagationPolicy = "Foreground" };
            try
            {
                await client.BatchV1.DeleteNamespacedJobAsync(jobName, ns, body: deleteOptions, cancellationToken: stoppingToken);

                // If this was a service job, also delete the service
                if (containerDetails.IsService)
                {
                    var serviceName = $"{jobName}-service";
                    try
                    {
                        await client.CoreV1.DeleteNamespacedServiceAsync(serviceName, ns, body: deleteOptions, cancellationToken: stoppingToken);
                        Logger.LogInformation("Deleted service {serviceName} for completed job {jobName}", serviceName, jobName);
                    }
                    catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        Logger.LogTrace("Service {serviceName} not found (may have already been deleted)", serviceName);
                    }
                }

                // Wait a moment for the deletion to complete
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

                // Recreate the job
                Logger.LogInformation("Recreating job {jobName} for event {eventId}", jobName, eventId);
                await CreateJobAsync(client, jobName, ns, containerDetails, eventId, eventName, organizationId, organizationName, stoppingToken);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to restart completed job {jobName} for event {eventId}", jobName, eventId);
            }
        }
        else
        {
            Logger.LogTrace("Job {jobName} is already running for event {eventId}.", jobName, eventId);
        }
    }

    internal async Task CreateJobAsync(IKubernetes client, string name, string ns, ContainerDetails containerDetails, int eventId, string eventName, int organizationId, string organizationName, CancellationToken stoppingToken)
    {
        eventName = eventName.Replace(" ", "-").ToLowerInvariant();
        organizationName = organizationName.Replace(" ", "-").ToLowerInvariant();
        var k8sNamespace = await GetCurrentNamespaceAsync(stoppingToken);
        var secretKeyName = ResolveKeyName(k8sNamespace);

        var labels = new Dictionary<string, string>
        {
            { "event_id", eventId.ToString() },
            { "organization_id", organizationId.ToString() },
            { "app", name } // Add app label for service selector
        };

        var podSpec = new V1PodSpec
        {
            Containers = [new() {
                Name = name,
                Image = containerDetails.ImageName,
                Resources = new V1ResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity>
                    {
                        { "cpu", new ResourceQuantity(containerDetails.CpuRequest) },
                        { "memory", new ResourceQuantity(containerDetails.MemoryRequest) }
                    },
                    Limits = new Dictionary<string, ResourceQuantity>
                    {
                        { "cpu", new ResourceQuantity(containerDetails.CpuLimit) },
                        { "memory", new ResourceQuantity(containerDetails.MemoryLimit) }
                    }
                },
                Env = [
                    new V1EnvVar { Name = "event_id", Value = eventId.ToString() },
                    new V1EnvVar { Name = "org_id", Value = organizationId.ToString() },
                    new V1EnvVar { Name = "job_name", Value = name },
                    new V1EnvVar { Name = "job_namespace", Value = ns },
                    new V1EnvVar { Name = "ASPNETCORE_ENVIRONMENT", Value = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") },
                    new V1EnvVar { Name = "ASPNETCORE_FORWARDEDHEADERS_ENABLED", Value = Environment.GetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED") },
                    new V1EnvVar { Name = "ASPNETCORE_URLS", Value = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") },
                    new V1EnvVar { Name = "Keycloak__AuthServerUrl", Value = Environment.GetEnvironmentVariable("Keycloak__AuthServerUrl") },
                    new V1EnvVar { Name = "Keycloak__Realm", Value = Environment.GetEnvironmentVariable("Keycloak__Realm") },
                    new V1EnvVar { Name = "SentinelApiUrl", Value = Environment.GetEnvironmentVariable("SentinelApiUrl") },
                    new V1EnvVar { Name = "REDIS_SVC", Value = Environment.GetEnvironmentVariable("REDIS_SVC") },
                    new V1EnvVar { Name = "REDIS_PW", ValueFrom = new V1EnvVarSource {
                        SecretKeyRef = new V1SecretKeySelector { Name = secretKeyName, Key = "redis" }}},
                    new V1EnvVar { Name = "ConnectionStrings__Default", ValueFrom = new V1EnvVarSource {
                        SecretKeyRef = new V1SecretKeySelector { Name = secretKeyName, Key = "db" }}}
                ]
            }],
            RestartPolicy = "OnFailure"
        };

        // Add ports if this is a service
        if (containerDetails.IsService)
        {
            podSpec.Containers[0].Ports = [
                new V1ContainerPort { ContainerPort = 8080, Name = "http" },
                //new V1ContainerPort { ContainerPort = 8443, Name = "https" }
            ];
        }

        var jobSpec = new V1Job
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns, Labels = labels },
            Spec = new V1JobSpec
            {
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = labels },
                    Spec = podSpec
                }
            }
        };

        await client.BatchV1.CreateNamespacedJobAsync(jobSpec, ns, cancellationToken: stoppingToken);

        // Create a Service if this container should be exposed as a service
        if (containerDetails.IsService)
        {
            var serviceSpec = new V1Service
            {
                Metadata = new V1ObjectMeta
                {
                    Name = $"{name}-service",
                    NamespaceProperty = ns,
                    Labels = labels
                },
                Spec = new V1ServiceSpec
                {
                    Selector = new Dictionary<string, string> { { "app", name } },
                    Ports = [
                        new V1ServicePort { Name = "http", Port = 80, TargetPort = 8080 },
                        //new V1ServicePort { Name = "https", Port = 443, TargetPort = 8443 }
                    ],
                    Type = "ClusterIP"
                }
            };

            try
            {
                await client.CoreV1.CreateNamespacedServiceAsync(serviceSpec, ns, cancellationToken: stoppingToken);
                Logger.LogInformation("Created service {serviceName} for job {jobName}", $"{name}-service", name);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to create service {serviceName} for job {jobName}", $"{name}-service", name);
            }
        }
    }

    internal static string ResolveKeyName(string @namespace)
    {
        if (@namespace.Contains("-dev"))
        {
            return "rmkeys-dev";
        }
        else if (@namespace.Contains("-test"))
        {
            return "rmkeys-test";
        }
        else // Prod
        {
            return "rmkeys"; // Default key name
        }
    }

    /// <summary>
    /// Polls Kubernetes pod statuses every second and saves per-event service statuses to Redis.
    /// Each entry has a 1-minute TTL as a safeguard in case cleanup is missed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every <see cref="SitePodHealthEveryNthPoll"/>th pass also publishes the site-wide pod health,
    /// with the same client but in a try of its own so that neither report can stop the other: the
    /// relay's per-event statuses are what its operator watches during a session, and must not depend
    /// on a whole-namespace list that only the site operations page reads.
    /// </para>
    /// <para>
    /// A failed site pod health report is logged at warning, on the first failure and then every
    /// <see cref="SitePodHealthWarnEveryNthFailure"/>th in a run, so that a page stuck on "pod data
    /// unavailable" has a reason in the logs without one line every five seconds.
    /// </para>
    /// </remarks>
    private async Task PollPodStatusesAsync(CancellationToken stoppingToken)
    {
        var pass = 0;
        var sitePodHealthFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            IKubernetes? client = null;
            try
            {
                string currentNamespace = await GetCurrentNamespaceAsync(stoppingToken);
                client = kubernetesFactory();

                try
                {
                    await PublishPodStatusesAsync(client, currentNamespace, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    Logger.LogDebug(ex, "Error polling pod statuses");
                }

                if (pass++ % SitePodHealthEveryNthPoll == 0)
                {
                    try
                    {
                        await PublishSitePodHealthAsync(client, currentNamespace, DateTime.UtcNow, stoppingToken);
                        if (sitePodHealthFailures > 0)
                        {
                            Logger.LogInformation("Site pod health publishing recovered after {failures} failure(s)", sitePodHealthFailures);
                        }
                        sitePodHealthFailures = 0;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        if (sitePodHealthFailures++ % SitePodHealthWarnEveryNthFailure == 0)
                        {
                            Logger.LogWarning(ex, "Error publishing site pod health ({failures} consecutive failure(s))", sitePodHealthFailures);
                        }
                        else
                        {
                            Logger.LogDebug(ex, "Error publishing site pod health");
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                Logger.LogDebug(ex, "Error creating Kubernetes client for pod polling");
            }
            finally
            {
                client?.Dispose();
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    /// <summary>
    /// How many consecutive site pod health failures pass between warnings: about once a minute at
    /// one report every five seconds.
    /// </summary>
    private const int SitePodHealthWarnEveryNthFailure = 12;

    /// <summary>
    /// How many one-second pod polls pass between site-wide pod health reports.
    /// </summary>
    /// <remarks>
    /// Five seconds rather than every second, because this list is of the whole namespace rather than
    /// just the event pods, and is read by a page that refreshes every fifteen. Comfortably inside the
    /// key's one-minute expiry, so a missed report or two never makes the page say the data is gone.
    /// </remarks>
    internal const int SitePodHealthEveryNthPoll = 5;

    /// <summary>
    /// How old a job must be before having no pod counts as missing: several report intervals, so the
    /// job controller has had time to create its pod.
    /// </summary>
    internal static readonly TimeSpan MissingJobGracePeriod = TimeSpan.FromSeconds(30);

    /// <summary>How long a site pod health report survives without being rewritten.</summary>
    private static readonly TimeSpan sitePodHealthTtl = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Lists every pod in the namespace and the event jobs, and publishes their health to
    /// <see cref="Consts.SITE_POD_HEALTH"/> for the site operations page.
    /// </summary>
    /// <remarks>
    /// No label selector on the pod list, on purpose: the shared services - status, relay, event
    /// management, this orchestrator, Redis - carry no event label, and they are half of what the page
    /// is for. The orchestration role already grants list on pods across its namespace. The jobs are
    /// listed again rather than borrowed from the reconcile loop, which runs on a ten-second cycle of
    /// its own and would hand this a list up to ten seconds old. Jobs are listed before pods, so a job
    /// that is already listed has the longest possible time for its pod to appear in the pod list.
    /// </remarks>
    internal async Task PublishSitePodHealthAsync(IKubernetes client, string ns, DateTime asOfUtc, CancellationToken stoppingToken)
    {
        var jobs = await client.BatchV1.ListNamespacedJobAsync(ns, labelSelector: "event_id", cancellationToken: stoppingToken);
        var pods = await client.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: stoppingToken);

        var health = BuildSitePodHealth(pods.Items ?? [], jobs.Items ?? [], asOfUtc);

        var cache = cacheMux.GetDatabase();
        var json = JsonSerializer.Serialize(health, SitePodHealth.JsonOptions);
        await cache.StringSetAsync(Consts.SITE_POD_HEALTH, json, sitePodHealthTtl);
    }

    /// <summary>
    /// Builds the site pod health report from a pod list and the event jobs.
    /// </summary>
    /// <remarks>
    /// A job counts as having a pod when any pod is owned by it, or carries its name in one of the
    /// labels Kubernetes and this orchestrator put on a job's pods. Several are checked because each
    /// can be missing: the owner reference on an orphaned pod, <c>batch.kubernetes.io/job-name</c> on
    /// older clusters, and <c>app</c> on a pod this orchestrator did not template.
    /// A job being deleted, or created less than <see cref="MissingJobGracePeriod"/> ago, is never
    /// reported, because it has no pod for a reason that is not a fault.
    /// </remarks>
    internal static SitePodHealth BuildSitePodHealth(IEnumerable<V1Pod> pods, IEnumerable<V1Job> jobs, DateTime asOfUtc)
    {
        var podList = pods.Where(p => p?.Metadata != null).ToList();
        var health = new SitePodHealth
        {
            AsOfUtc = UtcTimestamp.Normalize(asOfUtc),
            Pods = [.. podList.Select(MapPodHealth)
                .OrderBy(p => p.EventId ?? int.MaxValue)
                .ThenBy(p => p.PodName, StringComparer.Ordinal)],
        };

        var podJobNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pod in podList)
        {
            foreach (var owner in pod.Metadata.OwnerReferences ?? [])
            {
                if (string.Equals(owner.Kind, "Job", StringComparison.Ordinal) && !string.IsNullOrEmpty(owner.Name))
                {
                    podJobNames.Add(owner.Name);
                }
            }
            foreach (var label in new[] { "job-name", "batch.kubernetes.io/job-name", "app" })
            {
                if (pod.Metadata.Labels?.TryGetValue(label, out var name) == true && !string.IsNullOrEmpty(name))
                {
                    podJobNames.Add(name);
                }
            }
        }

        foreach (var job in jobs)
        {
            var jobName = job?.Metadata?.Name;
            if (string.IsNullOrEmpty(jobName) || podJobNames.Contains(jobName))
            {
                continue;
            }
            // A job being torn down (foreground deletion removes its pods before the job) or created
            // moments ago (the job controller has not made its pod yet, or it was created between the
            // job and pod lists) has no pod for a reason that is not a fault.
            if (job!.Metadata.DeletionTimestamp != null)
            {
                continue;
            }
            if (job.Metadata.CreationTimestamp is { } created
                && UtcTimestamp.Normalize(asOfUtc) - UtcTimestamp.Normalize(created) < MissingJobGracePeriod)
            {
                continue;
            }
            if (!TryParseLabel(job.Metadata.Labels, "event_id", out var eventId))
            {
                continue;
            }

            var key = EventJobKey(eventId);
            var at = jobName.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            var role = at >= 0 && at + key.Length < jobName.Length ? jobName[(at + key.Length)..] : null;
            health.MissingJobs.Add(new MissingJob { JobName = jobName, EventId = eventId, Role = role });
        }

        health.MissingJobs.Sort((a, b) => a.EventId != b.EventId
            ? a.EventId.CompareTo(b.EventId)
            : string.CompareOrdinal(a.JobName, b.JobName));
        return health;
    }

    /// <summary>
    /// Reduces one pod to what tells an operator whether it is healthy.
    /// </summary>
    /// <remarks>
    /// Everything here comes from the pod list response the orchestrator is already permitted to
    /// read; nothing needs a get, a watch or the logs. Every field is read defensively, because a pod
    /// moments old has a spec and little else.
    /// </remarks>
    internal static PodHealth MapPodHealth(V1Pod pod)
    {
        var labels = pod.Metadata?.Labels;
        var statuses = pod.Status?.ContainerStatuses ?? [];
        var totalContainers = pod.Spec?.Containers?.Count ?? 0;
        var readyContainers = statuses.Count(s => s.Ready);

        var firstImage = pod.Spec?.Containers?.FirstOrDefault()?.Image;

        // The previous run's reason where a container has one, since that is why it restarted; failing
        // that, the reason a container that is not running now stopped.
        var terminated = statuses.Select(s => s.LastState?.Terminated).FirstOrDefault(t => t != null)
            ?? statuses.Select(s => s.State?.Terminated).FirstOrDefault(t => t != null);

        return new PodHealth
        {
            PodName = pod.Metadata?.Name ?? string.Empty,
            Namespace = pod.Metadata?.NamespaceProperty,
            EventId = TryParseLabel(labels, "event_id", out var eventId) ? eventId : null,
            OrganizationId = TryParseLabel(labels, "organization_id", out var orgId) ? orgId : null,
            AppName = LabelOrNull(labels, "app")
                ?? LabelOrNull(labels, "app.kubernetes.io/name")
                ?? pod.Metadata?.OwnerReferences?.FirstOrDefault()?.Name,
            ServiceName = string.IsNullOrEmpty(firstImage) ? null : ExtractServiceName(firstImage),
            Phase = string.IsNullOrEmpty(pod.Status?.Phase) ? "Unknown" : pod.Status.Phase,
            Ready = totalContainers > 0 && readyContainers >= totalContainers,
            ReadyContainers = readyContainers,
            TotalContainers = totalContainers,
            RestartCount = statuses.Sum(s => s.RestartCount),
            WaitingReason = statuses.Concat(pod.Status?.InitContainerStatuses ?? [])
                .Select(s => s.State?.Waiting?.Reason)
                .FirstOrDefault(r => !string.IsNullOrEmpty(r)),
            LastTerminatedReason = terminated?.Reason,
            LastTerminatedUtc = terminated?.FinishedAt is { } finished ? UtcTimestamp.Normalize(finished) : null,
            StartedUtc = pod.Status?.StartTime is { } started ? UtcTimestamp.Normalize(started) : null,
            NodeName = pod.Spec?.NodeName,
            Deleting = pod.Metadata?.DeletionTimestamp != null,
        };
    }

    private static string? LabelOrNull(IDictionary<string, string>? labels, string name) =>
        labels != null && labels.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    private static bool TryParseLabel(IDictionary<string, string>? labels, string name, out int value)
    {
        value = 0;
        return labels != null && labels.TryGetValue(name, out var raw) && int.TryParse(raw, out value);
    }

    /// <summary>
    /// Reads the event pods in the namespace and publishes the per-event service statuses to Redis.
    /// </summary>
    internal async Task PublishPodStatusesAsync(IKubernetes client, string ns, CancellationToken stoppingToken)
    {
        var pods = await client.CoreV1.ListNamespacedPodAsync(ns, labelSelector: "event_id", cancellationToken: stoppingToken);

        var eventPodGroups = pods.Items
            .Where(p => p.Metadata?.Labels != null && p.Metadata.Labels.ContainsKey("event_id"))
            .GroupBy(p => p.Metadata.Labels["event_id"]);

        var cache = cacheMux.GetDatabase();

        foreach (var group in eventPodGroups)
        {
            var statuses = new List<ServiceStatus>();

            foreach (var pod in group)
            {
                foreach (var container in pod.Spec.Containers)
                {
                    var serviceName = ExtractServiceName(container.Image);
                    var podPhase = pod.Status?.Phase ?? "Unknown";

                    statuses.Add(new ServiceStatus
                    {
                        ServiceName = serviceName,
                        Status = podPhase
                    });
                }
            }

            var key = string.Format(Consts.EVENT_SERVICE_STATUSES, group.Key);
            var json = JsonSerializer.Serialize(statuses);
            await cache.StringSetAsync(key, json, TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>
    /// Extracts the service name from a container image string.
    /// For example, "bigmission/redmist-event-processor:1.0" returns "redmist-event-processor".
    /// </summary>
    internal static string ExtractServiceName(string image)
    {
        var nameWithTag = image.Contains('/') ? image[(image.LastIndexOf('/') + 1)..] : image;
        var name = nameWithTag.Contains(':') ? nameWithTag[..nameWithTag.IndexOf(':')] : nameWithTag;
        return name;
    }

    /// <summary>
    /// Scales the RedisFailover CR replica count to match demand:
    /// 1 replica when idle, 2 replicas while any live event is active.
    /// Patches are skipped when the desired count already matches the last known state.
    /// </summary>
    internal async Task EnsureRedisReplicasAsync(IKubernetes client, string ns, int liveEventCount, CancellationToken stoppingToken)
    {
        var desiredReplicas = liveEventCount > 0 ? 2 : 1;
        if (desiredReplicas == lastRedisReplicas)
        {
            return;
        }

        try
        {
            var patchBody = new { spec = new { redis = new { replicas = desiredReplicas } } };
            var patch = new V1Patch(JsonSerializer.Serialize(patchBody), V1Patch.PatchType.MergePatch);
            await client.CustomObjects.PatchNamespacedCustomObjectAsync(
                patch,
                "databases.spotahome.com", "v1", ns, "redisfailovers", redisFailoverName,
                cancellationToken: stoppingToken);

            Logger.LogInformation("Redis replicas scaled to {desired} (live events: {count})", desiredReplicas, liveEventCount);
            lastRedisReplicas = desiredReplicas;
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Logger.LogWarning("RedisFailover '{name}' not found in namespace {ns} — Redis scaling skipped", redisFailoverName, ns);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to scale Redis replicas in namespace {ns}", ns);
        }
    }
}
