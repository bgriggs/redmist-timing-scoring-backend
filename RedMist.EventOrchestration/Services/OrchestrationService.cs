using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Models;
using RedMist.Database;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.EventOrchestration.Services;

public class OrchestrationService : BackgroundService
{
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IDbContextFactory<TsContext> tsContext;

    private ILogger Logger { get; }
    private const string namespaceFile = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";
    private readonly static TimeSpan checkInterval = TimeSpan.FromMilliseconds(10000);
    private readonly static TimeSpan eventTimeout = TimeSpan.FromMinutes(10);
    private const string EVENT_PROCESSOR_JOB = "{0}-evt-{1}-event-processor";
    private readonly string eventProcessorContainer = "bigmission/redmist-timing-svc:latest";
    private const string CONTROL_LOG_JOB = "{0}-evt-{1}-control-log";
    private readonly string controlLogContainer = "bigmission/redmist-control-log-svc:latest";
    private const string LOGGER_JOB = "{0}-evt-{1}-logger";
    private readonly string loggerContainer = "bigmission/redmist-event-logger-svc:latest";


    public OrchestrationService(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.tsContext = tsContext;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string currentNamespace = await File.ReadAllTextAsync(namespaceFile, stoppingToken);
                Logger.LogTrace("Found namespace {ns}", currentNamespace);
                var config = KubernetesClientConfiguration.InClusterConfig();
                using var client = new Kubernetes(config);

                var currentEvents = await GetCurrentEventsAsync();
                Logger.LogDebug("Found {eventCount} current events", currentEvents.Count);

                // Update the live events in the database
                await UpdateLiveEvents(currentEvents);

                // Get currently active jobs in the namespace
                var currentJobs = await GetJobsAsync(client, currentNamespace, stoppingToken);

                // Check for expired events
                var expiredEvents = currentEvents.Where(e => e.Timestamp < DateTime.UtcNow - eventTimeout).ToList();
                foreach (var expired in expiredEvents)
                {
                    Logger.LogInformation("Event {eventId} has expired, cleaning up.", expired.EventId);
                    await DisposeEventAsync(expired, client, currentNamespace, currentJobs, stoppingToken);
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
    /// Based on active signalR connections of relays, get the current events for those relays.
    /// </summary>
    /// <returns></returns>
    private async Task<List<RelayConnectionEventEntry>> GetCurrentEventsAsync()
    {
        var cache = cacheMux.GetDatabase();
        var hashKey = new RedisKey(Backend.Shared.Consts.RELAY_EVENT_CONNECTIONS);

        var entries = await cache.HashGetAllAsync(hashKey);
        var result = new List<RelayConnectionEventEntry>();
        foreach (var entry in entries)
        {
            if (entry.Name.IsNullOrEmpty || entry.Value.IsNullOrEmpty) continue;

            var eventEntry = JsonSerializer.Deserialize<RelayConnectionEventEntry>(entry.Value!);
            if (eventEntry != null)
            {
                result.Add(eventEntry);
            }
        }
        return result;
    }

    /// <summary>
    /// Sets the IsLive flag on Events when they are currently active in the system.
    /// All other Events are set to false.
    /// </summary>
    private async Task UpdateLiveEvents(List<RelayConnectionEventEntry> currentEvents)
    {
        using var context = await tsContext.CreateDbContextAsync();
        var currentEventIds = currentEvents.Select(e => e.EventId).ToList();
        var idList = string.Join(",", currentEventIds);
        if (string.IsNullOrWhiteSpace(idList))
        {
            // If no events are active, set all to not live
            await context.Database.ExecuteSqlRawAsync("UPDATE Events SET IsLive = 0");
        }
        else
        {
            var sql = $@"UPDATE Events SET IsLive = CASE 
                       WHEN Id IN ({idList}) THEN 1
                       ELSE 0
                     END";

            await context.Database.ExecuteSqlRawAsync(sql);
        }
    }

    /// <summary>
    /// Cleans up expired events and their associated jobs.
    /// </summary>
    private async Task DisposeEventAsync(RelayConnectionEventEntry eventEntry, Kubernetes client, string ns, V1JobList jobs, CancellationToken stoppingToken)
    {
        var cache = cacheMux.GetDatabase();
        var hashKey = new RedisKey(Backend.Shared.Consts.RELAY_EVENT_CONNECTIONS);
        var entryKey = string.Format(Backend.Shared.Consts.RELAY_HEARTBEAT, eventEntry.EventId);

        // Remove the event entry from the cache
        await cache.HashDeleteAsync(hashKey, entryKey);

        // Remove any associated jobs that are running
        var eventKey = $"evt-{eventEntry.EventId}";
        var deleteOptions = new V1DeleteOptions { PropagationPolicy = "Foreground" };
        foreach (var job in jobs.Items)
        {
            if (job.Metadata.Name.Contains(eventKey, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await client.DeleteNamespacedJobAsync(job.Metadata.Name, ns, body: deleteOptions, cancellationToken: stoppingToken);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to delete job {jobName} in namespace {ns}", job.Metadata.Name, ns);
                }
            }
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
            var clJobName = string.Format(CONTROL_LOG_JOB, org.ShortName.ToLower(), evt.EventId);
            if (!jobs.Items.Any(job => job.Metadata.Name.Equals(clJobName, StringComparison.OrdinalIgnoreCase)))
            {
                Logger.LogInformation("Control log job {clJobName} does not exist for event {eventId}. Creating new job.", clJobName, evt.EventId);
                await CreateJob(client, clJobName, ns, controlLogContainer, evt.EventId, eventDefinition.Name, org.Id, org.Name, stoppingToken);
            }
        }

        // Check for logger job
        var loggerJobName = string.Format(LOGGER_JOB, org.ShortName.ToLower(), evt.EventId);
        if (!jobs.Items.Any(job => job.Metadata.Name.Equals(loggerJobName, StringComparison.OrdinalIgnoreCase)))
        {
            Logger.LogInformation("Logger job {loggerJobName} does not exist for event {eventId}. Creating new job.", loggerJobName, evt.EventId);
            await CreateJob(client, loggerJobName, ns, loggerContainer, evt.EventId, eventDefinition.Name, org.Id, org.Name, stoppingToken);
        }
        else
        {
            Logger.LogTrace("Logger job {loggerJobName} already exists for event {eventId}.", loggerJobName, evt.EventId);
        }

        // Check for event processor job
        var epJobName = string.Format(EVENT_PROCESSOR_JOB, org.ShortName.ToLower(), evt.EventId);
        if (!jobs.Items.Any(job => job.Metadata.Name.Equals(epJobName, StringComparison.OrdinalIgnoreCase)))
        {
            Logger.LogInformation("Event processor job {epJobName} does not exist for event {eventId}. Creating new job.", epJobName, evt.EventId);
            await CreateJob(client, epJobName, ns, eventProcessorContainer, evt.EventId, eventDefinition.Name, org.Id, org.Name, stoppingToken);
        }
        else
        {
            Logger.LogTrace("Event processor job {epJobName} already exists for event {eventId}.", epJobName, evt.EventId);
        }
    }

    private static async Task CreateJob(Kubernetes client, string name, string ns, string container, int eventId, string eventName, int organizationId, string organizationName, CancellationToken stoppingToken)
    {
        eventName = eventName.Replace(" ", "-").ToLowerInvariant();
        organizationName = organizationName.Replace(" ", "-").ToLowerInvariant();
        var labels = new Dictionary<string, string>
        {
            { "event_id", eventId.ToString() },
            { "event_name", eventName },
            { "organization_id", organizationId.ToString() },
            { "organization_name", organizationName }
        };
        var jobSpec = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = ns,
                Labels = labels
            },
            Spec = new V1JobSpec
            {
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = labels },
                    Spec = new V1PodSpec
                    {
                        Containers = [new() { Name = name, Image = container, Env =
                        [
                            new V1EnvVar { Name = "event_id", Value = eventId.ToString()  },
                            new V1EnvVar { Name = "org_id", Value = organizationId.ToString()  },
                            new V1EnvVar { Name = "ASPNETCORE_ENVIRONMENT", Value = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") },
                            new V1EnvVar { Name = "ASPNETCORE_FORWARDEDHEADERS_ENABLED", Value = Environment.GetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED") },
                            new V1EnvVar { Name = "ASPNETCORE_URLS", Value = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") },
                            new V1EnvVar { Name = "Keycloak__AuthServerUrl", Value = Environment.GetEnvironmentVariable("Keycloak__AuthServerUrl") },
                            new V1EnvVar { Name = "Keycloak__Realm", Value = Environment.GetEnvironmentVariable("Keycloak__Realm") },
                            new V1EnvVar { Name = "REDIS_SVC", Value = Environment.GetEnvironmentVariable("REDIS_SVC") },
                            new V1EnvVar { Name = "REDIS_PW", ValueFrom = new V1EnvVarSource {
                                SecretKeyRef = new V1SecretKeySelector { Name = "rmkeys", Key = "redis" }}},
                            new V1EnvVar { Name = "ConnectionStrings__Default", ValueFrom = new V1EnvVarSource {
                                SecretKeyRef = new V1SecretKeySelector { Name = "rmkeys", Key = "db" }}}
                        ] }],
                        RestartPolicy = "OnFailure"
                    }
                }
            }
        };

        await client.CreateNamespacedJobAsync(jobSpec, ns, cancellationToken: stoppingToken);
    }
}
