using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.EventOrchestration.Models;
using RedMist.EventOrchestration.Services;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingAndScoringService.Tests.EventOrchestration.Utilities;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.EventOrchestration.Services;

/// <summary>
/// Covers the reconciliation decisions the orchestrator makes: which per-event jobs must exist,
/// when a job is recreated, what is torn down for an expired event, and how Redis is scaled.
/// The Kubernetes API itself is a <see cref="FakeKubernetes"/> stand-in.
/// </summary>
[TestClass]
public class OrchestrationServiceTests
{
    private const string Namespace = "timing-prod";

    private Mock<ILoggerFactory> loggerFactory = null!;
    private Mock<IDatabase> cache = null!;
    private Mock<ISubscriber> subscriber = null!;
    private Mock<IConnectionMultiplexer> cacheMux = null!;
    private IDbContextFactory<TsContext> dbFactory = null!;
    private FakeKubernetes k8s = null!;

    [TestInitialize]
    public void Setup()
    {
        loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        cache = new Mock<IDatabase>();
        subscriber = new Mock<ISubscriber>();
        cacheMux = new Mock<IConnectionMultiplexer>();
        cacheMux.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(cache.Object);
        cacheMux.Setup(x => x.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);

        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"OrchestrationServiceTests_{Guid.NewGuid()}")
            .Options;
        dbFactory = new TestDbContextFactory(options);

        k8s = new FakeKubernetes();
    }

    private OrchestrationService CreateService(Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? [])
            .Build();
        var eventsChecker = new Mock<EventsChecker>(cacheMux.Object, dbFactory, new FakeHybridCache());
        return new OrchestrationService(loggerFactory.Object, cacheMux.Object, dbFactory, eventsChecker.Object,
            configuration, () => k8s.Object);
    }

    private static ContainerDetails Processor(bool isService = true) =>
        new("bigmission/redmist-event-processor", "1.2.3", "{0}-evt-{1}-event-processor", isService,
            "100m", "220Mi", "350m", "800Mi");

    private async Task SeedAsync(int orgId, string shortName, string controlLogType, int eventId,
        TimingSource timingSource = TimingSource.Relay)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Organizations.Add(new Organization
        {
            Id = orgId,
            ClientId = $"client-{orgId}",
            Name = $"Org {orgId}",
            ShortName = shortName,
            ControlLogType = controlLogType
        });
        db.Events.Add(new RedMist.TimingCommon.Models.Configuration.Event
        {
            Id = eventId,
            OrganizationId = orgId,
            Name = "Test Event",
            TimingSource = timingSource
        });
        await db.SaveChangesAsync();
    }

    #region Redis replica scaling

    [TestMethod]
    public async Task EnsureRedisReplicasAsync_NoLiveEvents_ScalesDownToOneReplica()
    {
        var svc = CreateService(new Dictionary<string, string?> { ["Redis:FailoverName"] = "rmredis" });

        await svc.EnsureRedisReplicasAsync(k8s.Object, Namespace, liveEventCount: 0, CancellationToken.None);

        Assert.HasCount(1, k8s.CustomObjectPatches);
        var patch = k8s.CustomObjectPatches[0];
        Assert.AreEqual("databases.spotahome.com", patch.Group);
        Assert.AreEqual("redisfailovers", patch.Plural);
        Assert.AreEqual("rmredis", patch.Name);
        Assert.AreEqual(Namespace, patch.Namespace);
        StringAssert.Contains(PatchContent(patch), "\"replicas\":1");
    }

    [TestMethod]
    public async Task EnsureRedisReplicasAsync_LiveEventsPresent_ScalesUpToTwoReplicas()
    {
        var svc = CreateService();

        await svc.EnsureRedisReplicasAsync(k8s.Object, Namespace, liveEventCount: 3, CancellationToken.None);

        Assert.HasCount(1, k8s.CustomObjectPatches);
        StringAssert.Contains(PatchContent(k8s.CustomObjectPatches[0]), "\"replicas\":2");
        // Default failover name when nothing is configured.
        Assert.AreEqual("redis", k8s.CustomObjectPatches[0].Name);
    }

    [TestMethod]
    public async Task EnsureRedisReplicasAsync_DemandUnchanged_DoesNotPatchAgain()
    {
        var svc = CreateService();

        await svc.EnsureRedisReplicasAsync(k8s.Object, Namespace, liveEventCount: 1, CancellationToken.None);
        await svc.EnsureRedisReplicasAsync(k8s.Object, Namespace, liveEventCount: 5, CancellationToken.None);

        Assert.HasCount(1, k8s.CustomObjectPatches, "Both counts want 2 replicas, so only the first should patch");
    }

    [TestMethod]
    public async Task EnsureRedisReplicasAsync_DemandChanges_PatchesAgain()
    {
        var svc = CreateService();

        await svc.EnsureRedisReplicasAsync(k8s.Object, Namespace, liveEventCount: 1, CancellationToken.None);
        await svc.EnsureRedisReplicasAsync(k8s.Object, Namespace, liveEventCount: 0, CancellationToken.None);

        Assert.HasCount(2, k8s.CustomObjectPatches);
        StringAssert.Contains(PatchContent(k8s.CustomObjectPatches[0]), "\"replicas\":2");
        StringAssert.Contains(PatchContent(k8s.CustomObjectPatches[1]), "\"replicas\":1");
    }

    [TestMethod]
    public async Task EnsureRedisReplicasAsync_FailoverNotFound_SwallowsAndRetriesOnNextPass()
    {
        var svc = CreateService();
        k8s.PatchCustomObjectError = FakeKubernetes.NotFound();

        await svc.EnsureRedisReplicasAsync(k8s.Object, Namespace, liveEventCount: 1, CancellationToken.None);

        // A failed patch must not be remembered as the current state, otherwise the cluster would
        // stay at the wrong replica count forever.
        k8s.PatchCustomObjectError = null;
        await svc.EnsureRedisReplicasAsync(k8s.Object, Namespace, liveEventCount: 1, CancellationToken.None);

        Assert.HasCount(1, k8s.CustomObjectPatches);
        StringAssert.Contains(PatchContent(k8s.CustomObjectPatches[0]), "\"replicas\":2");
    }

    [TestMethod]
    public async Task EnsureRedisReplicasAsync_PatchThrows_SwallowsAndRetriesOnNextPass()
    {
        var svc = CreateService();
        k8s.PatchCustomObjectError = new InvalidOperationException("cluster unreachable");

        await svc.EnsureRedisReplicasAsync(k8s.Object, Namespace, liveEventCount: 0, CancellationToken.None);

        k8s.PatchCustomObjectError = null;
        await svc.EnsureRedisReplicasAsync(k8s.Object, Namespace, liveEventCount: 0, CancellationToken.None);

        Assert.HasCount(1, k8s.CustomObjectPatches, "The failed pass must not be recorded as the current state");
        StringAssert.Contains(PatchContent(k8s.CustomObjectPatches[0]), "\"replicas\":1");
    }

    [TestMethod]
    public async Task EnsureRedisReplicasAsync_UsesAMergePatch()
    {
        var svc = CreateService();

        await svc.EnsureRedisReplicasAsync(k8s.Object, Namespace, liveEventCount: 1, CancellationToken.None);

        // A strategic merge patch is not valid against a CRD; the replica change must be a merge patch.
        Assert.AreEqual(V1Patch.PatchType.MergePatch, ((V1Patch)k8s.CustomObjectPatches[0].Body).Type);
    }

    private static string PatchContent(CustomObjectPatch patch)
    {
        var v1Patch = (V1Patch)patch.Body;
        return v1Patch.Content?.ToString() ?? string.Empty;
    }

    #endregion

    #region Job lifecycle

    [TestMethod]
    public async Task EnsureJobRunningAsync_JobMissing_CreatesJobWithEventLabelsAndResources()
    {
        var svc = CreateService();

        await svc.EnsureJobRunningAsync(k8s.Object, Namespace, k8s.Jobs, "tst-evt-42-event-processor", 42,
            "Big Race", 7, "Test Org", Processor(isService: false), CancellationToken.None);

        Assert.HasCount(1, k8s.CreatedJobs);
        var job = k8s.CreatedJobs[0];
        Assert.AreEqual("tst-evt-42-event-processor", job.Metadata.Name);
        Assert.AreEqual(Namespace, job.Metadata.NamespaceProperty);
        Assert.AreEqual("42", job.Metadata.Labels["event_id"]);
        Assert.AreEqual("7", job.Metadata.Labels["organization_id"]);
        Assert.AreEqual("tst-evt-42-event-processor", job.Metadata.Labels["app"]);

        var container = job.Spec.Template.Spec.Containers[0];
        Assert.AreEqual("bigmission/redmist-event-processor:1.2.3", container.Image);
        Assert.AreEqual("100m", container.Resources.Requests["cpu"].ToString());
        Assert.AreEqual("220Mi", container.Resources.Requests["memory"].ToString());
        Assert.AreEqual("350m", container.Resources.Limits["cpu"].ToString());
        Assert.AreEqual("800Mi", container.Resources.Limits["memory"].ToString());
        Assert.AreEqual("42", container.Env.First(e => e.Name == "event_id").Value);
        Assert.AreEqual("7", container.Env.First(e => e.Name == "org_id").Value);
        Assert.AreEqual("OnFailure", job.Spec.Template.Spec.RestartPolicy);
        Assert.IsEmpty(k8s.DeletedJobs);
    }

    [TestMethod]
    public async Task EnsureJobRunningAsync_JobAlreadyRunning_LeavesItAlone()
    {
        var svc = CreateService();
        // Name casing differs; the lookup is case-insensitive so this still counts as existing.
        k8s.Jobs = JobList(NewJob("TST-EVT-42-EVENT-PROCESSOR", succeeded: 0, failed: 0));

        await svc.EnsureJobRunningAsync(k8s.Object, Namespace, k8s.Jobs, "tst-evt-42-event-processor", 42,
            "Big Race", 7, "Test Org", Processor(isService: false), CancellationToken.None);

        Assert.IsEmpty(k8s.CreatedJobs);
        Assert.IsEmpty(k8s.DeletedJobs);
    }

    [TestMethod]
    public async Task EnsureJobRunningAsync_JobSucceeded_DeletesAndRecreates()
    {
        var svc = CreateService();
        k8s.Jobs = JobList(NewJob("tst-evt-42-event-processor", succeeded: 1, failed: 0));

        await svc.EnsureJobRunningAsync(k8s.Object, Namespace, k8s.Jobs, "tst-evt-42-event-processor", 42,
            "Big Race", 7, "Test Org", Processor(isService: false), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "tst-evt-42-event-processor" }, k8s.DeletedJobs);
        Assert.HasCount(1, k8s.CreatedJobs);
        Assert.IsEmpty(k8s.DeletedServices, "Non-service containers have no service to delete");
    }

    [TestMethod]
    public async Task EnsureJobRunningAsync_JobFailed_DeletesAndRecreates()
    {
        var svc = CreateService();
        k8s.Jobs = JobList(NewJob("tst-evt-42-event-processor", succeeded: 0, failed: 3));

        await svc.EnsureJobRunningAsync(k8s.Object, Namespace, k8s.Jobs, "tst-evt-42-event-processor", 42,
            "Big Race", 7, "Test Org", Processor(isService: false), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "tst-evt-42-event-processor" }, k8s.DeletedJobs);
        Assert.HasCount(1, k8s.CreatedJobs);
    }

    [TestMethod]
    public async Task EnsureJobRunningAsync_CompletedServiceJob_DeletesAndRecreatesTheServiceToo()
    {
        var svc = CreateService();
        k8s.Jobs = JobList(NewJob("tst-evt-42-event-processor", succeeded: 1, failed: 0));

        await svc.EnsureJobRunningAsync(k8s.Object, Namespace, k8s.Jobs, "tst-evt-42-event-processor", 42,
            "Big Race", 7, "Test Org", Processor(isService: true), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "tst-evt-42-event-processor-service" }, k8s.DeletedServices);
        Assert.HasCount(1, k8s.CreatedJobs);
        Assert.HasCount(1, k8s.CreatedServices);
    }

    [TestMethod]
    public async Task EnsureJobRunningAsync_CompletedServiceJobWithNoService_StillRecreatesTheJob()
    {
        var svc = CreateService();
        k8s.Jobs = JobList(NewJob("tst-evt-42-event-processor", succeeded: 1, failed: 0));
        k8s.DeleteServiceError = FakeKubernetes.NotFound();

        await svc.EnsureJobRunningAsync(k8s.Object, Namespace, k8s.Jobs, "tst-evt-42-event-processor", 42,
            "Big Race", 7, "Test Org", Processor(isService: true), CancellationToken.None);

        Assert.HasCount(1, k8s.CreatedJobs, "A missing service must not stop the job from being recreated");
    }

    [TestMethod]
    public async Task EnsureJobRunningAsync_DeleteOfCompletedJobFails_DoesNotRecreate()
    {
        var svc = CreateService();
        k8s.Jobs = JobList(NewJob("tst-evt-42-event-processor", succeeded: 1, failed: 0));
        k8s.DeleteJobError = new InvalidOperationException("conflict");

        await svc.EnsureJobRunningAsync(k8s.Object, Namespace, k8s.Jobs, "tst-evt-42-event-processor", 42,
            "Big Race", 7, "Test Org", Processor(isService: false), CancellationToken.None);

        Assert.IsEmpty(k8s.CreatedJobs, "Recreating over a job that could not be deleted would collide");
    }

    [TestMethod]
    public async Task CreateJobAsync_ServiceContainer_CreatesClusterIpServiceSelectingTheJobPods()
    {
        var svc = CreateService();

        await svc.CreateJobAsync(k8s.Object, "tst-evt-42-event-processor", Namespace, Processor(isService: true),
            42, "Big Race", 7, "Test Org", CancellationToken.None);

        Assert.HasCount(1, k8s.CreatedServices);
        var service = k8s.CreatedServices[0];
        Assert.AreEqual("tst-evt-42-event-processor-service", service.Metadata.Name);
        Assert.AreEqual("ClusterIP", service.Spec.Type);
        Assert.AreEqual("tst-evt-42-event-processor", service.Spec.Selector["app"]);
        Assert.AreEqual(80, service.Spec.Ports[0].Port);
        Assert.AreEqual("8080", service.Spec.Ports[0].TargetPort.ToString());
        Assert.AreEqual(8080, k8s.CreatedJobs[0].Spec.Template.Spec.Containers[0].Ports[0].ContainerPort);
    }

    [TestMethod]
    public async Task CreateJobAsync_NonServiceContainer_CreatesNoServiceAndExposesNoPorts()
    {
        var svc = CreateService();

        await svc.CreateJobAsync(k8s.Object, "tst-evt-42-logger", Namespace, Processor(isService: false),
            42, "Big Race", 7, "Test Org", CancellationToken.None);

        Assert.IsEmpty(k8s.CreatedServices);
        Assert.IsNull(k8s.CreatedJobs[0].Spec.Template.Spec.Containers[0].Ports);
    }

    [TestMethod]
    public async Task CreateJobAsync_ServiceCreationFails_JobIsStillCreated()
    {
        var svc = CreateService();
        k8s.CreateServiceError = new InvalidOperationException("service already exists");

        await svc.CreateJobAsync(k8s.Object, "tst-evt-42-event-processor", Namespace, Processor(isService: true),
            42, "Big Race", 7, "Test Org", CancellationToken.None);

        Assert.HasCount(1, k8s.CreatedJobs);
    }

    #endregion

    #region Per-event job set

    [TestMethod]
    public async Task EnsureEventJobsAsync_OrganizationMissing_CreatesNothing()
    {
        var svc = CreateService();
        await SeedAsync(orgId: 7, "tst", controlLogType: "", eventId: 42);

        await svc.EnsureEventJobsAsync(new RelayConnectionEventEntry { EventId = 42, OrganizationId = 999 },
            k8s.Object, Namespace, k8s.Jobs, CancellationToken.None);

        Assert.IsEmpty(k8s.CreatedJobs);
    }

    [TestMethod]
    public async Task EnsureEventJobsAsync_EventDefinitionMissing_CreatesNothing()
    {
        var svc = CreateService();
        await SeedAsync(orgId: 7, "tst", controlLogType: "", eventId: 42);

        await svc.EnsureEventJobsAsync(new RelayConnectionEventEntry { EventId = 999, OrganizationId = 7 },
            k8s.Object, Namespace, k8s.Jobs, CancellationToken.None);

        Assert.IsEmpty(k8s.CreatedJobs);
    }

    [TestMethod]
    public async Task EnsureEventJobsAsync_NoControlLogConfigured_CreatesLoggerAndProcessorOnly()
    {
        var svc = CreateService();
        await SeedAsync(orgId: 7, "TST", controlLogType: "", eventId: 42);

        await svc.EnsureEventJobsAsync(new RelayConnectionEventEntry { EventId = 42, OrganizationId = 7 },
            k8s.Object, Namespace, k8s.Jobs, CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[] { "tst-evt-42-logger", "tst-evt-42-event-processor" },
            k8s.CreatedJobs.Select(j => j.Metadata.Name).ToArray());
    }

    [TestMethod]
    public async Task EnsureEventJobsAsync_ControlLogConfigured_AlsoCreatesControlLogJob()
    {
        var svc = CreateService();
        await SeedAsync(orgId: 7, "TST", controlLogType: "GoogleSheets", eventId: 42);

        await svc.EnsureEventJobsAsync(new RelayConnectionEventEntry { EventId = 42, OrganizationId = 7 },
            k8s.Object, Namespace, k8s.Jobs, CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[] { "tst-evt-42-control-log", "tst-evt-42-logger", "tst-evt-42-event-processor" },
            k8s.CreatedJobs.Select(j => j.Metadata.Name).ToArray());
    }

    [TestMethod]
    public async Task EnsureEventJobsAsync_ExternalTimingSourceWithImageConfigured_CreatesExternalSourceJob()
    {
        var svc = CreateService(new Dictionary<string, string?>
        {
            ["ExternalSource:Image"] = "bigmission/redmist-external",
            ["ExternalSource:Version"] = "9.9.9",
            ["ExternalSource:CpuRequest"] = "70m",
            ["ExternalSource:MemoryLimit"] = "600Mi"
        });
        await SeedAsync(orgId: 7, "TST", controlLogType: "", eventId: 42, TimingSource.External);

        await svc.EnsureEventJobsAsync(new RelayConnectionEventEntry { EventId = 42, OrganizationId = 7 },
            k8s.Object, Namespace, k8s.Jobs, CancellationToken.None);

        var external = k8s.CreatedJobs.SingleOrDefault(j => j.Metadata.Name == "tst-evt-42-external-source");
        Assert.IsNotNull(external, "External events need their source container started");
        var container = external.Spec.Template.Spec.Containers[0];
        Assert.AreEqual("bigmission/redmist-external:9.9.9", container.Image);
        Assert.AreEqual("70m", container.Resources.Requests["cpu"].ToString());
        Assert.AreEqual("600Mi", container.Resources.Limits["memory"].ToString());
        // Falls back to the documented default when only some overrides are supplied.
        Assert.AreEqual("128Mi", container.Resources.Requests["memory"].ToString());
    }

    [TestMethod]
    public async Task EnsureEventJobsAsync_ExternalTimingSourceWithNoImageConfigured_SkipsExternalSourceJob()
    {
        var svc = CreateService();
        await SeedAsync(orgId: 7, "TST", controlLogType: "", eventId: 42, TimingSource.External);

        await svc.EnsureEventJobsAsync(new RelayConnectionEventEntry { EventId = 42, OrganizationId = 7 },
            k8s.Object, Namespace, k8s.Jobs, CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[] { "tst-evt-42-logger", "tst-evt-42-event-processor" },
            k8s.CreatedJobs.Select(j => j.Metadata.Name).ToArray());
    }

    /// <summary>
    /// A configured external-source image is cluster-wide; only the event's own timing source decides
    /// whether the job is wanted. Asserted as the whole job set rather than as the absence of one name,
    /// so a regression that stopped creating any jobs at all could not pass this.
    /// </summary>
    [TestMethod]
    public async Task EnsureEventJobsAsync_RelayTimingSourceWithImageConfigured_DoesNotCreateExternalSourceJob()
    {
        var svc = CreateService(new Dictionary<string, string?> { ["ExternalSource:Image"] = "bigmission/redmist-external" });
        await SeedAsync(orgId: 7, "TST", controlLogType: "", eventId: 42);

        await svc.EnsureEventJobsAsync(new RelayConnectionEventEntry { EventId = 42, OrganizationId = 7 },
            k8s.Object, Namespace, k8s.Jobs, CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[] { "tst-evt-42-logger", "tst-evt-42-event-processor" },
            k8s.CreatedJobs.Select(j => j.Metadata.Name).ToArray());
    }

    [TestMethod]
    public async Task EnsureEventJobsAsync_JobsAlreadyRunning_CreatesNothing()
    {
        var svc = CreateService();
        await SeedAsync(orgId: 7, "TST", controlLogType: "GoogleSheets", eventId: 42);
        k8s.Jobs = JobList(
            NewJob("tst-evt-42-control-log", 0, 0),
            NewJob("tst-evt-42-logger", 0, 0),
            NewJob("tst-evt-42-event-processor", 0, 0));

        await svc.EnsureEventJobsAsync(new RelayConnectionEventEntry { EventId = 42, OrganizationId = 7 },
            k8s.Object, Namespace, k8s.Jobs, CancellationToken.None);

        Assert.IsEmpty(k8s.CreatedJobs);
    }

    #endregion

    #region Expired event teardown

    [TestMethod]
    public async Task DisposeEventAsync_DeletesTheEventsJobsAndTheirServices()
    {
        var svc = CreateService();
        // Ids that are not prefixes of one another, so this covers the intended behavior only; the
        // prefix collision is pinned separately below.
        var jobs = JobList(
            NewJob("tst-evt-42-logger", 0, 0),
            NewJob("tst-evt-42-event-processor", 0, 0),
            NewJob("tst-evt-53-logger", 0, 0));

        await svc.DisposeEventAsync(new RelayConnectionEventEntry { EventId = 42, OrganizationId = 7 },
            k8s.Object, Namespace, jobs, CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[] { "tst-evt-42-logger", "tst-evt-42-event-processor" }, k8s.DeletedJobs);
        CollectionAssert.AreEquivalent(
            new[] { "tst-evt-42-logger-service", "tst-evt-42-event-processor-service" }, k8s.DeletedServices);
        Assert.IsTrue(k8s.RequestedNamespaces.All(n => n == Namespace),
            "Teardown must stay inside the orchestrator's own namespace");
    }

    /// <summary>
    /// BUG (pinned, not fixed): a job belongs to the event being torn down if its name
    /// <c>Contains("evt-{id}")</c>, which is a substring test, not a segment one. Retiring event 4
    /// therefore also matches "tst-evt-42-..." and "tst-evt-400-...", and deletes the jobs of an event
    /// that may be mid-race. Job names are generated as "{org}-evt-{id}-{role}", so a segment-aware
    /// match ("evt-{id}-") would fix it.
    /// </summary>
    [TestMethod]
    public async Task DisposeEventAsync_EventIdIsAPrefixOfAnotherEventsId_AlsoDeletesThatOtherEventsJobs()
    {
        var svc = CreateService();
        var jobs = JobList(
            NewJob("tst-evt-4-logger", 0, 0),
            NewJob("tst-evt-42-event-processor", 0, 0),
            NewJob("tst-evt-400-logger", 0, 0));

        await svc.DisposeEventAsync(new RelayConnectionEventEntry { EventId = 4, OrganizationId = 7 },
            k8s.Object, Namespace, jobs, CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[] { "tst-evt-4-logger", "tst-evt-42-event-processor", "tst-evt-400-logger" }, k8s.DeletedJobs,
            "Only tst-evt-4-logger belongs to event 4; the other two are live events being torn down with it.");
    }

    [TestMethod]
    public async Task DisposeEventAsync_RemovesRelayHeartbeatAndPerEventCacheKeys()
    {
        var svc = CreateService();

        await svc.DisposeEventAsync(new RelayConnectionEventEntry { EventId = 42, OrganizationId = 7 },
            k8s.Object, Namespace, JobList(), CancellationToken.None);

        cache.Verify(x => x.HashDeleteAsync(
            new RedisKey(Consts.RELAY_EVENT_CONNECTIONS),
            new RedisValue(string.Format(Consts.RELAY_HEARTBEAT, 42)),
            It.IsAny<CommandFlags>()), Times.Once);
        cache.Verify(x => x.KeyDeleteAsync(
            new RedisKey(string.Format(Consts.STATUS_EVENT_CONNECTIONS, 42)), It.IsAny<CommandFlags>()), Times.Once);
        cache.Verify(x => x.KeyDeleteAsync(
            new RedisKey(string.Format(Consts.EVENT_SERVICE_STATUSES, 42)), It.IsAny<CommandFlags>()), Times.Once);
    }

    [TestMethod]
    public async Task DisposeEventAsync_JobDeleteFails_StillClearsTheCacheEntries()
    {
        var svc = CreateService();
        k8s.DeleteJobError = new InvalidOperationException("api server down");

        await svc.DisposeEventAsync(new RelayConnectionEventEntry { EventId = 42, OrganizationId = 7 },
            k8s.Object, Namespace, JobList(NewJob("tst-evt-42-logger", 0, 0)), CancellationToken.None);

        cache.Verify(x => x.KeyDeleteAsync(
            new RedisKey(string.Format(Consts.EVENT_SERVICE_STATUSES, 42)), It.IsAny<CommandFlags>()), Times.Once);
    }

    [TestMethod]
    public async Task SendPreshutdownNotificationAsync_PublishesTheExpiredEventIdsOnTheShutdownChannel()
    {
        var svc = CreateService();
        RedisChannel channel = default;
        RedisValue message = default;
        subscriber.Setup(x => x.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, RedisValue, CommandFlags>((c, m, _) => { channel = c; message = m; })
            .ReturnsAsync(1);

        await svc.SendPreshutdownNotificationAsync([42, 43]);

        // Event processors listen on this channel and parse this payload, so both are a contract.
        Assert.AreEqual(Consts.EVENT_SHUTDOWN_SIGNAL, channel.ToString());
        CollectionAssert.AreEqual(new[] { 42, 43 }, JsonSerializer.Deserialize<int[]>(message.ToString()));
    }

    [TestMethod]
    public async Task SendPreshutdownNotificationAsync_PublishFails_DoesNotPropagate()
    {
        var svc = CreateService();
        subscriber.Setup(x => x.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        await svc.SendPreshutdownNotificationAsync([42]);

        subscriber.Verify(x => x.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()),
            Times.Once);
    }

    #endregion

    #region Pod status publishing

    [TestMethod]
    public async Task PublishPodStatusesAsync_GroupsPodsByEventAndReportsEachContainer()
    {
        var svc = CreateService();
        var writes = CaptureStringSets();
        k8s.Pods = PodList(
            NewPod("42", "Running", "bigmission/redmist-event-processor:1.2.3"),
            NewPod("42", "Pending", "bigmission/redmist-event-logger:1.2.3"),
            NewPod("43", "Running", "bigmission/redmist-control-log:1.2.3"));

        await svc.PublishPodStatusesAsync(k8s.Object, Namespace, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { Namespace }, k8s.RequestedNamespaces);
        CollectionAssert.AreEqual(new[] { "event_id" }, k8s.PodListLabelSelectors,
            "Only event pods should be listed; without the selector every pod in the namespace is scanned");
        Assert.HasCount(2, writes);
        var event42 = JsonSerializer.Deserialize<List<ServiceStatus>>(writes[string.Format(Consts.EVENT_SERVICE_STATUSES, "42")])!;
        Assert.HasCount(2, event42);
        Assert.AreEqual("redmist-event-processor", event42[0].ServiceName);
        Assert.AreEqual("Running", event42[0].Status);
        Assert.AreEqual("redmist-event-logger", event42[1].ServiceName);
        Assert.AreEqual("Pending", event42[1].Status);

        var event43 = JsonSerializer.Deserialize<List<ServiceStatus>>(writes[string.Format(Consts.EVENT_SERVICE_STATUSES, "43")])!;
        Assert.HasCount(1, event43);
        Assert.AreEqual("redmist-control-log", event43[0].ServiceName);
    }

    [TestMethod]
    public async Task PublishPodStatusesAsync_PodWithNoStatus_ReportsUnknown()
    {
        var svc = CreateService();
        var writes = CaptureStringSets();
        var pod = NewPod("42", "Running", "bigmission/redmist-event-processor:1.2.3");
        pod.Status = null;
        k8s.Pods = PodList(pod);

        await svc.PublishPodStatusesAsync(k8s.Object, Namespace, CancellationToken.None);

        var statuses = JsonSerializer.Deserialize<List<ServiceStatus>>(writes[string.Format(Consts.EVENT_SERVICE_STATUSES, "42")])!;
        Assert.AreEqual("Unknown", statuses[0].Status);
    }

    [TestMethod]
    public async Task PublishPodStatusesAsync_PodWithoutEventLabel_IsIgnored()
    {
        var svc = CreateService();
        var writes = CaptureStringSets();
        var unlabeled = NewPod("42", "Running", "bigmission/redmist-event-processor:1.2.3");
        unlabeled.Metadata.Labels = new Dictionary<string, string> { ["app"] = "something-else" };
        k8s.Pods = PodList(unlabeled);

        await svc.PublishPodStatusesAsync(k8s.Object, Namespace, CancellationToken.None);

        Assert.IsEmpty(writes);
    }

    private Dictionary<string, string> CaptureStringSets()
    {
        var writes = new Dictionary<string, string>();
        cache.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(),
                It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags>(
                (key, value, _, _, _) => writes[key.ToString()] = value.ToString())
            .ReturnsAsync(true);
        return writes;
    }

    #endregion

    #region Pure helpers

    [TestMethod]
    [DataRow("bigmission/redmist-event-processor:1.2.3", "redmist-event-processor")]
    [DataRow("bigmission/redmist-event-processor", "redmist-event-processor")]
    [DataRow("redmist-event-processor:latest", "redmist-event-processor")]
    [DataRow("redmist-event-processor", "redmist-event-processor")]
    [DataRow("registry.example.com/team/redmist-control-log:2.0", "redmist-control-log")]
    public void ExtractServiceName_StripsRegistryAndTag(string image, string expected)
    {
        Assert.AreEqual(expected, OrchestrationService.ExtractServiceName(image));
    }

    [TestMethod]
    [DataRow("timing-dev", "rmkeys-dev")]
    [DataRow("timing-test", "rmkeys-test")]
    [DataRow("timing-prod", "rmkeys")]
    [DataRow("default", "rmkeys")]
    public void ResolveKeyName_PicksSecretForEnvironment(string ns, string expected)
    {
        Assert.AreEqual(expected, OrchestrationService.ResolveKeyName(ns));
    }

    #endregion

    #region Builders

    private static V1JobList JobList(params V1Job[] jobs) => new() { Items = jobs };

    private static V1Job NewJob(string name, int succeeded, int failed) => new()
    {
        Metadata = new V1ObjectMeta { Name = name },
        Status = new V1JobStatus { Succeeded = succeeded, Failed = failed }
    };

    private static V1PodList PodList(params V1Pod[] pods) => new() { Items = pods };

    private static V1Pod NewPod(string eventId, string phase, string image) => new()
    {
        Metadata = new V1ObjectMeta { Labels = new Dictionary<string, string> { ["event_id"] = eventId } },
        Spec = new V1PodSpec { Containers = [new V1Container { Name = "c", Image = image }] },
        Status = new V1PodStatus { Phase = phase }
    };

    #endregion
}
