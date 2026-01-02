using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.EventOrchestration.Models;
using StackExchange.Redis;
using System.Reflection;
using System.Text.Json;

namespace RedMist.EventOrchestration.Services;

public class OrchestrationService : BackgroundService
{
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly EventsChecker eventsChecker;

    private ILogger Logger { get; }
    private const string namespaceFile = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";
    private readonly static TimeSpan checkInterval = TimeSpan.FromMilliseconds(10000);
    private readonly static TimeSpan eventTimeout = TimeSpan.FromMinutes(10);
    private readonly ContainerDetails eventProcessorContainerDetails;
    private readonly ContainerDetails controlLogContainerDetails;
    private readonly ContainerDetails loggerContainerDetails;


    public OrchestrationService(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux,
        IDbContextFactory<TsContext> tsContext, EventsChecker eventsChecker)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.tsContext = tsContext;
        this.eventsChecker = eventsChecker;

        // Get the current assembly version for container tags
        var version = GetAssemblyVersion();
        eventProcessorContainerDetails = new(
            "bigmission/redmist-event-processor", version, "{0}-evt-{1}-event-processor", true,
            "100m", "200Mi", "350m", "750Mi");
        controlLogContainerDetails = new(
            "bigmission/redmist-control-log", version, "{0}-evt-{1}-control-log", false,
            "60m", "165Mi", "150m", "450Mi");
        loggerContainerDetails = new(
            "bigmission/redmist-event-logger", version, "{0}-evt-{1}-logger", false,
            "55m", "90Mi", "400m", "550Mi");

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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Set live events first for running outside of K8s in debug where K8s calls will fail
                var currentEvents = await eventsChecker.GetCurrentEventsAsync();
                Logger.LogDebug("Found {eventCount} current events", currentEvents.Count);

                // Update the live events in the database
                await UpdateLiveEvents(currentEvents);

                string currentNamespace = await GetCurrentNamespaceAsync(stoppingToken);
                var config = KubernetesClientConfiguration.InClusterConfig();
                using var client = new Kubernetes(config);

                // Get currently active jobs in the namespace
                var currentJobs = await GetJobsAsync(client, currentNamespace, stoppingToken);

                // Check for expired events
                var expiredEvents = currentEvents.Where(e => e.Timestamp < DateTime.UtcNow - eventTimeout).ToList();
                if (expiredEvents.Count > 0)
                {
                    Logger.LogInformation("Found {expiredCount} expired events", expiredEvents.Count);

                    // Send pre-shutdown notification to event processors to allow graceful shutdown
                    Logger.LogInformation("Sending pre-shutdown notification for expired events");
                    await SendPreshutdownNotification([.. expiredEvents.Select(e => e.EventId)]);

                    // Wait 15 seconds to allow any in-flight operations to complete, such as session finalization
                    Logger.LogInformation("Waiting 15 seconds for event processors to shut down gracefully");
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

                    Logger.LogInformation("Disposing expired events and cleaning up resources...");
                    foreach (var expired in expiredEvents)
                    {
                        Logger.LogInformation("Event {eventId} has expired, cleaning up.", expired.EventId);
                        await DisposeEventAsync(expired, client, currentNamespace, currentJobs, stoppingToken);
                    }
                }
                // Check for orphaned jobs
                var eventIds = currentEvents.Select(e => e.EventId).ToArray();
                var jobsToDelete = currentJobs.Items.Where(job => !eventIds.Any(id => job.Metadata.Name.Contains($"evt-{id}"))).ToList();
                var deleteOptions = new V1DeleteOptions { PropagationPolicy = "Foreground" };
                foreach (var job in jobsToDelete)
                {
                    Logger.LogInformation("Job {jobName} is orphaned, deleting.", job.Metadata.Name);
                    try
                    {
                        await client.DeleteNamespacedJobAsync(job.Metadata.Name, currentNamespace, body: deleteOptions, cancellationToken: stoppingToken);

                        // Also try to delete the associated service if it exists
                        var serviceName = $"{job.Metadata.Name}-service";
                        try
                        {
                            await client.DeleteNamespacedServiceAsync(serviceName, currentNamespace, body: deleteOptions, cancellationToken: stoppingToken);
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
                    await EnsureEventJobs(evt, client, currentNamespace, currentJobs, stoppingToken);
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
    private async Task<V1JobList> GetJobsAsync(Kubernetes client, string ns, CancellationToken stoppingToken)
    {
        var jobs = await client.ListNamespacedJobAsync(ns, cancellationToken: stoppingToken);
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
    private async Task UpdateLiveEvents(List<RelayConnectionEventEntry> currentEvents)
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
    private async Task SendPreshutdownNotification(List<int> eventIds)
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
    /// Cleans up expired events and their associated jobs.
    /// </summary>
    private async Task DisposeEventAsync(RelayConnectionEventEntry eventEntry, Kubernetes client, string ns, V1JobList jobs, CancellationToken stoppingToken)
    {
        var cache = cacheMux.GetDatabase();
        var hashKey = new RedisKey(Consts.RELAY_EVENT_CONNECTIONS);
        var entryKey = string.Format(Consts.RELAY_HEARTBEAT, eventEntry.EventId);

        // Remove the event entry from the cache
        await cache.HashDeleteAsync(hashKey, entryKey);

        // Remove any associated jobs and services that are running
        var eventKey = $"evt-{eventEntry.EventId}";
        var deleteOptions = new V1DeleteOptions { PropagationPolicy = "Foreground" };

        foreach (var job in jobs.Items)
        {
            if (job.Metadata.Name.Contains(eventKey, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await client.DeleteNamespacedJobAsync(job.Metadata.Name, ns, body: deleteOptions, cancellationToken: stoppingToken);

                    // Also try to delete the associated service if it exists
                    var serviceName = $"{job.Metadata.Name}-service";
                    try
                    {
                        await client.DeleteNamespacedServiceAsync(serviceName, ns, body: deleteOptions, cancellationToken: stoppingToken);
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

        // Remove the event entry from the cache that tracks connections
        await DisposeEventConnectionsAsync(eventEntry, stoppingToken);
    }

    private async Task DisposeEventConnectionsAsync(RelayConnectionEventEntry eventEntry, CancellationToken stoppingToken)
    {
        try
        {
            var cache = cacheMux.GetDatabase();
            var entryKey = string.Format(Backend.Shared.Consts.STATUS_EVENT_CONNECTIONS, eventEntry.EventId);

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
    private async Task EnsureEventJobs(RelayConnectionEventEntry evt, Kubernetes client, string ns, V1JobList jobs, CancellationToken stoppingToken)
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
            if (!jobs.Items.Any(job => job.Metadata.Name.Equals(clJobName, StringComparison.OrdinalIgnoreCase)))
            {
                Logger.LogInformation("Control log job {clJobName} does not exist for event {eventId}. Creating new job.", clJobName, evt.EventId);
                await CreateJobAsync(client, clJobName, ns, controlLogContainerDetails, evt.EventId, eventDefinition.Name, org.Id, org.Name, stoppingToken);
            }
        }

        // Check for logger job
        var loggerJobName = string.Format(loggerContainerDetails.JobFormat, org.ShortName.ToLower(), evt.EventId);
        if (!jobs.Items.Any(job => job.Metadata.Name.Equals(loggerJobName, StringComparison.OrdinalIgnoreCase)))
        {
            Logger.LogInformation("Logger job {loggerJobName} does not exist for event {eventId}. Creating new job.", loggerJobName, evt.EventId);
            await CreateJobAsync(client, loggerJobName, ns, loggerContainerDetails, evt.EventId, eventDefinition.Name, org.Id, org.Name, stoppingToken);
        }
        else
        {
            Logger.LogTrace("Logger job {loggerJobName} already exists for event {eventId}.", loggerJobName, evt.EventId);
        }

        // Check for event processor job
        var epJobName = string.Format(eventProcessorContainerDetails.JobFormat, org.ShortName.ToLower(), evt.EventId);
        if (!jobs.Items.Any(job => job.Metadata.Name.Equals(epJobName, StringComparison.OrdinalIgnoreCase)))
        {
            Logger.LogInformation("Event processor job {epJobName} does not exist for event {eventId}. Creating new job.", epJobName, evt.EventId);
            await CreateJobAsync(client, epJobName, ns, eventProcessorContainerDetails, evt.EventId, eventDefinition.Name, org.Id, org.Name, stoppingToken);
        }
        else
        {
            Logger.LogTrace("Event processor job {epJobName} already exists for event {eventId}.", epJobName, evt.EventId);
        }
    }

    private async Task CreateJobAsync(Kubernetes client, string name, string ns, ContainerDetails containerDetails, int eventId, string eventName, int organizationId, string organizationName, CancellationToken stoppingToken)
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

        await client.CreateNamespacedJobAsync(jobSpec, ns, cancellationToken: stoppingToken);

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
                await client.CreateNamespacedServiceAsync(serviceSpec, ns, cancellationToken: stoppingToken);
                Logger.LogInformation("Created service {serviceName} for job {jobName}", $"{name}-service", name);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to create service {serviceName} for job {jobName}", $"{name}-service", name);
            }
        }
    }

    private static string ResolveKeyName(string @namespace)
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
}
