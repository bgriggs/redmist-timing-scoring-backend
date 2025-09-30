﻿using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Models;
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

    private ILogger Logger { get; }
    private const string namespaceFile = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";
    private readonly static TimeSpan checkInterval = TimeSpan.FromMilliseconds(10000);
    private readonly static TimeSpan eventTimeout = TimeSpan.FromMinutes(10);
    private readonly ContainerDetails eventProcessorContainerDetails;
    private readonly ContainerDetails controlLogContainerDetails;
    private readonly ContainerDetails loggerContainerDetails;
    private readonly ContainerDetails sentinelVideoContainerDetails;


    public OrchestrationService(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.tsContext = tsContext;

        // Get the current assembly version for container tags
        var version = GetAssemblyVersion();
        eventProcessorContainerDetails = new("bigmission/redmist-timing-svc", version, "{0}-evt-{1}-event-processor", true);
        controlLogContainerDetails = new("bigmission/redmist-control-log-svc", version, "{0}-evt-{1}-control-log");
        loggerContainerDetails = new("bigmission/redmist-event-logger-svc", version, "{0}-evt-{1}-logger");
        sentinelVideoContainerDetails = new("bigmission/redmist-sentinel-video-svc", version, "{0}-evt-{1}-sentinel-video");

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
                var currentEvents = await GetCurrentEventsAsync();
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
            if (eventEntry != null && eventEntry.EventId > 0)
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
                await CreateJob(client, clJobName, ns, controlLogContainerDetails.ImageName, evt.EventId, eventDefinition.Name, org.Id, org.Name, controlLogContainerDetails.IsService, stoppingToken);
            }
        }

        // Check for logger job
        var loggerJobName = string.Format(loggerContainerDetails.JobFormat, org.ShortName.ToLower(), evt.EventId);
        if (!jobs.Items.Any(job => job.Metadata.Name.Equals(loggerJobName, StringComparison.OrdinalIgnoreCase)))
        {
            Logger.LogInformation("Logger job {loggerJobName} does not exist for event {eventId}. Creating new job.", loggerJobName, evt.EventId);
            await CreateJob(client, loggerJobName, ns, loggerContainerDetails.ImageName, evt.EventId, eventDefinition.Name, org.Id, org.Name, loggerContainerDetails.IsService, stoppingToken);
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
            await CreateJob(client, epJobName, ns, eventProcessorContainerDetails.ImageName, evt.EventId, eventDefinition.Name, org.Id, org.Name, eventProcessorContainerDetails.IsService, stoppingToken);
        }
        else
        {
            Logger.LogTrace("Event processor job {epJobName} already exists for event {eventId}.", epJobName, evt.EventId);
        }

        // Check for sentinel video job
        var svJobName = string.Format(sentinelVideoContainerDetails.JobFormat, org.ShortName.ToLower(), evt.EventId);
        if (!jobs.Items.Any(job => job.Metadata.Name.Equals(svJobName, StringComparison.OrdinalIgnoreCase)))
        {
            Logger.LogInformation("Sentinel video job {svJobName} does not exist for event {eventId}. Creating new job.", svJobName, evt.EventId);
            await CreateJob(client, svJobName, ns, sentinelVideoContainerDetails.ImageName, evt.EventId, eventDefinition.Name, org.Id, org.Name, sentinelVideoContainerDetails.IsService, stoppingToken);
        }
        else
        {
            Logger.LogTrace("Sentinel video job {svJobName} already exists for event {eventId}.", svJobName, evt.EventId);
        }
    }

    private async Task CreateJob(Kubernetes client, string name, string ns, string container, int eventId, string eventName, int organizationId, string organizationName, bool isService, CancellationToken stoppingToken)
    {
        eventName = eventName.Replace(" ", "-").ToLowerInvariant();
        organizationName = organizationName.Replace(" ", "-").ToLowerInvariant();
        var k8sNamespace = await GetCurrentNamespaceAsync(stoppingToken);
        var secretKeyName = ResolveKeyName(k8sNamespace);

        var labels = new Dictionary<string, string>
        {
            { "event_id", eventId.ToString() },
            //{ "event_name", eventName },
            { "organization_id", organizationId.ToString() },
            //{ "organization_name", organizationName },
            { "app", name } // Add app label for service selector
        };

        var podSpec = new V1PodSpec
        {
            Containers = [new() {
                Name = name,
                Image = container,
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
        if (isService)
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
        if (isService)
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
