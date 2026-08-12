using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventManagement.Controllers;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using StackExchange.Redis;
using System.Security.Claims;
using ConfigEvent = RedMist.TimingCommon.Models.Configuration.Event;
using Organization = RedMist.TimingCommon.Models.Organization;

namespace RedMist.TimingAndScoringService.Tests.EventManagement.Controllers;

[TestClass]
public class EventControllerBaseTests
{
    private const string ClientId = "test-client-id";
    private const string OtherClientId = "other-client-id";
    private const int OrgId = 1;
    private const int OtherOrgId = 2;

    private Mock<IConnectionMultiplexer> _mockCacheMux = null!;
    private Mock<IDatabase> _mockDatabase = null!;
    private IDbContextFactory<TsContext> _dbContextFactory = null!;
    private TsContext _dbContext = null!;
    private TestEventController _controller = null!;

    [TestInitialize]
    public void Setup()
    {
        var mockLoggerFactory = new Mock<ILoggerFactory>();
        mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        _mockDatabase = new Mock<IDatabase>();
        _mockDatabase.Setup(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                It.IsAny<RedisValue?>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue("1-1"));
        _mockDatabase.Setup(x => x.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        _mockCacheMux = new Mock<IConnectionMultiplexer>();
        _mockCacheMux.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_mockDatabase.Object);

        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContextFactory = new TestDbContextFactory(options);
        _dbContext = _dbContextFactory.CreateDbContext();

        _controller = new TestEventController(mockLoggerFactory.Object, _dbContextFactory, _mockCacheMux.Object);
        SetUser(ClientId);
    }

    [TestCleanup]
    public void Cleanup() => _dbContext?.Dispose();

    private void SetUser(string? clientId)
    {
        var claims = clientId == null ? new List<Claim>() : [new Claim("client_id", clientId)];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuthType"));
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
    }

    private async Task SeedOrganizationsAsync()
    {
        _dbContext.Organizations.Add(new Organization { Id = OrgId, ClientId = ClientId, Name = "Mine", ShortName = "M" });
        _dbContext.Organizations.Add(new Organization { Id = OtherOrgId, ClientId = OtherClientId, Name = "Theirs", ShortName = "T" });
        await _dbContext.SaveChangesAsync();
    }

    private static ConfigEvent NewEvent(int id, int orgId, string name, DateTime start, bool isActive = false, bool isDeleted = false)
        => new()
        {
            Id = id,
            OrganizationId = orgId,
            Name = name,
            StartDate = start,
            EndDate = start.AddDays(1),
            IsActive = isActive,
            IsDeleted = isDeleted,
        };

    private void VerifyPublishCount(int eventId, Times times)
    {
        _mockDatabase.Verify(x => x.StreamAddAsync(
            It.Is<RedisKey>(k => k == string.Format(Consts.EVENT_STATUS_STREAM_KEY, eventId)),
            It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(), It.IsAny<long?>(),
            It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()), times);
    }

    #region LoadEventSummaries

    [TestMethod]
    public async Task LoadEventSummaries_ReturnsOnlyOwnOrgNonDeletedEvents_NewestFirst()
    {
        await SeedOrganizationsAsync();
        var newest = NewEvent(11, OrgId, "New", new DateTime(2026, 6, 1), isActive: true);
        newest.IsSimulation = true;
        newest.IsArchived = true;
        _dbContext.Events.AddRange(
            NewEvent(10, OrgId, "Old", new DateTime(2026, 1, 1)),
            newest,
            NewEvent(12, OrgId, "Deleted", new DateTime(2026, 12, 1), isDeleted: true),
            NewEvent(13, OtherOrgId, "Other org", new DateTime(2026, 11, 1)));
        await _dbContext.SaveChangesAsync();

        var result = await _controller.LoadEventSummaries();

        CollectionAssert.AreEqual(new[] { 11, 10 }, result.Select(r => r.Id).ToArray());
        var summary = result[0];
        Assert.AreEqual("New", summary.Name);
        Assert.AreEqual(new DateTime(2026, 6, 1), summary.StartDate);
        Assert.AreEqual(new DateTime(2026, 6, 2), summary.EndDate);
        Assert.IsTrue(summary.IsActive);
        Assert.IsTrue(summary.IsSimulation);
        Assert.IsTrue(summary.IsArchived);
    }

    [TestMethod]
    public async Task LoadEventSummaries_MissingClientIdClaim_ReturnsNoEvents()
    {
        await SeedOrganizationsAsync();
        _dbContext.Events.Add(NewEvent(10, OrgId, "Mine", new DateTime(2026, 1, 1)));
        await _dbContext.SaveChangesAsync();
        SetUser(null);

        var result = await _controller.LoadEventSummaries();

        Assert.AreEqual(0, result.Count);
    }

    #endregion

    #region LoadEvent

    [TestMethod]
    public async Task LoadEvent_OwnEvent_ReturnsEvent()
    {
        await SeedOrganizationsAsync();
        _dbContext.Events.Add(NewEvent(10, OrgId, "Mine", new DateTime(2026, 1, 1)));
        await _dbContext.SaveChangesAsync();

        var result = await _controller.LoadEvent(10);

        Assert.IsNotNull(result);
        Assert.AreEqual("Mine", result.Name);
    }

    [TestMethod]
    public async Task LoadEvent_EventOwnedByAnotherOrganization_ReturnsNull()
    {
        await SeedOrganizationsAsync();
        _dbContext.Events.Add(NewEvent(20, OtherOrgId, "Theirs", new DateTime(2026, 1, 1)));
        await _dbContext.SaveChangesAsync();

        Assert.IsNull(await _controller.LoadEvent(20));
    }

    [TestMethod]
    public async Task LoadEvent_DeletedEvent_ReturnsNull()
    {
        await SeedOrganizationsAsync();
        _dbContext.Events.Add(NewEvent(10, OrgId, "Mine", new DateTime(2026, 1, 1), isDeleted: true));
        await _dbContext.SaveChangesAsync();

        Assert.IsNull(await _controller.LoadEvent(10));
    }

    #endregion

    #region SaveNewEvent

    [TestMethod]
    [DataRow((string?)null)]
    [DataRow("")]
    [DataRow("abcd")]
    [DataRow("12a4")]
    [DataRow("12345678")]
    [DataRow("-1")]
    [DataRow("1.5")]
    public async Task SaveNewEvent_PrivateWithInvalidAccessCode_ReturnsBadRequest(string? accessCode)
    {
        await SeedOrganizationsAsync();

        var result = await _controller.SaveNewEvent(new ConfigEvent { Name = "E", IsPrivate = true, AccessCode = accessCode });

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.AreEqual(0, _dbContext.Events.Count());
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("1234567")]
    public async Task SaveNewEvent_PrivateWithValidAccessCode_PersistsCode(string accessCode)
    {
        await SeedOrganizationsAsync();

        var result = await _controller.SaveNewEvent(new ConfigEvent { Name = "E", IsPrivate = true, AccessCode = accessCode });

        Assert.IsNull(result.Result);
        var saved = _dbContext.Events.AsNoTracking().Single();
        Assert.IsTrue(saved.IsPrivate);
        Assert.AreEqual(accessCode, saved.AccessCode);
    }

    [TestMethod]
    public async Task SaveNewEvent_OrganizationNotFoundForClient_ReturnsNotFound()
    {
        await SeedOrganizationsAsync();
        SetUser("unknown-client");

        var result = await _controller.SaveNewEvent(new ConfigEvent { Name = "E" });

        var notFound = result.Result as NotFoundObjectResult;
        Assert.IsNotNull(notFound);
        Assert.AreEqual("org", notFound.Value);
        Assert.AreEqual(0, _dbContext.Events.Count());
    }

    [TestMethod]
    public async Task SaveNewEvent_AssignsCallersOrganization_IgnoringSuppliedOrganizationId()
    {
        await SeedOrganizationsAsync();

        var result = await _controller.SaveNewEvent(new ConfigEvent { Name = "E", OrganizationId = OtherOrgId });

        Assert.IsNull(result.Result);
        var saved = _dbContext.Events.AsNoTracking().Single();
        Assert.AreEqual(OrgId, saved.OrganizationId);
        Assert.AreEqual(saved.Id, result.Value);
        VerifyPublishCount(saved.Id, Times.Once());
    }

    [TestMethod]
    public async Task SaveNewEvent_RelayClient_IsNotSimulationAndLogsSourceData()
    {
        await SeedOrganizationsAsync();

        await _controller.SaveNewEvent(new ConfigEvent { Name = "E", IsSimulation = true, EnableSourceDataLogging = false });

        var saved = _dbContext.Events.AsNoTracking().Single();
        Assert.IsFalse(saved.IsSimulation, "A non-api client must not be flagged as a simulation.");
        Assert.IsTrue(saved.EnableSourceDataLogging);
    }

    [TestMethod]
    public async Task SaveNewEvent_ApiClient_ForcesSimulationAndDisablesSourceDataLogging()
    {
        _dbContext.Organizations.Add(new Organization { Id = OrgId, ClientId = "api-test", Name = "Mine", ShortName = "M" });
        await _dbContext.SaveChangesAsync();
        SetUser("api-test");

        await _controller.SaveNewEvent(new ConfigEvent { Name = "E", IsSimulation = false, EnableSourceDataLogging = true });

        var saved = _dbContext.Events.AsNoTracking().Single();
        Assert.IsTrue(saved.IsSimulation);
        Assert.IsFalse(saved.EnableSourceDataLogging);
    }

    [TestMethod]
    public async Task SaveNewEvent_NotPrivate_DropsSuppliedAccessCode()
    {
        await SeedOrganizationsAsync();

        await _controller.SaveNewEvent(new ConfigEvent { Name = "E", IsPrivate = false, AccessCode = "1234" });

        Assert.IsNull(_dbContext.Events.AsNoTracking().Single().AccessCode);
    }

    [TestMethod]
    public async Task SaveNewEvent_ExternalTimingSource_KeepsExternalConfig()
    {
        await SeedOrganizationsAsync();

        await _controller.SaveNewEvent(new ConfigEvent { Name = "E", TimingSource = TimingSource.External, ExternalConfig = "{\"url\":\"x\"}" });

        var saved = _dbContext.Events.AsNoTracking().Single();
        Assert.AreEqual(TimingSource.External, saved.TimingSource);
        Assert.AreEqual("{\"url\":\"x\"}", saved.ExternalConfig);
    }

    [TestMethod]
    public async Task SaveNewEvent_NonExternalTimingSource_DropsExternalConfig()
    {
        await SeedOrganizationsAsync();

        await _controller.SaveNewEvent(new ConfigEvent { Name = "E", TimingSource = TimingSource.Relay, ExternalConfig = "{\"url\":\"x\"}" });

        Assert.IsNull(_dbContext.Events.AsNoTracking().Single().ExternalConfig);
    }

    #endregion

    #region UpdateEvent

    [TestMethod]
    public async Task UpdateEvent_PrivateWithInvalidAccessCode_ReturnsBadRequestWithoutSaving()
    {
        await SeedOrganizationsAsync();
        _dbContext.Events.Add(NewEvent(10, OrgId, "Original", new DateTime(2026, 1, 1)));
        await _dbContext.SaveChangesAsync();

        var result = await _controller.UpdateEvent(new ConfigEvent { Id = 10, Name = "Changed", IsPrivate = true, AccessCode = "nope" });

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        Assert.AreEqual("Original", _dbContext.Events.AsNoTracking().Single().Name);
        VerifyPublishCount(10, Times.Never());
    }

    [TestMethod]
    public async Task UpdateEvent_OrganizationNotFoundForClient_ReturnsNotFound()
    {
        await SeedOrganizationsAsync();
        SetUser("unknown-client");

        var result = await _controller.UpdateEvent(new ConfigEvent { Id = 10, Name = "Changed" });

        Assert.AreEqual("org", (result as NotFoundObjectResult)?.Value);
    }

    [TestMethod]
    public async Task UpdateEvent_EventOwnedByAnotherOrganization_LeavesItUnchanged()
    {
        await SeedOrganizationsAsync();
        _dbContext.Events.Add(NewEvent(20, OtherOrgId, "Theirs", new DateTime(2026, 1, 1)));
        await _dbContext.SaveChangesAsync();

        var result = await _controller.UpdateEvent(new ConfigEvent { Id = 20, Name = "Hijacked", OrganizationId = OrgId });

        Assert.IsInstanceOfType<OkResult>(result);
        var stored = _dbContext.Events.AsNoTracking().Single();
        Assert.AreEqual("Theirs", stored.Name);
        Assert.AreEqual(OtherOrgId, stored.OrganizationId);
        VerifyPublishCount(20, Times.Never());
    }

    [TestMethod]
    public async Task UpdateEvent_OwnEvent_UpdatesFieldsAndPublishes()
    {
        await SeedOrganizationsAsync();
        _dbContext.Events.Add(NewEvent(10, OrgId, "Original", new DateTime(2026, 1, 1)));
        await _dbContext.SaveChangesAsync();

        var update = new ConfigEvent
        {
            Id = 10,
            Name = "Updated",
            StartDate = new DateTime(2026, 5, 1),
            EndDate = new DateTime(2026, 5, 3),
            IsActive = true,
            EventUrl = "https://example.test",
            TrackName = "Track",
            CourseConfiguration = "Full",
            Distance = "2.5",
            IsArchived = true,
            HideName = true,
            EnableSourceDataLogging = true,
            Schedule = new EventSchedule { Name = "Weekend", Entries = [new EventScheduleEntry { Name = "Practice", DayOfEvent = new DateTime(2026, 5, 1) }] },
            Broadcast = new BroadcasterConfig { CompanyName = "Broadcaster", Url = "https://stream.test" },
            LoopsMetadata = [new LoopMetadata { Id = 7u, Name = "S/F" }],
        };

        var result = await _controller.UpdateEvent(update);

        Assert.IsInstanceOfType<OkResult>(result);
        var stored = _dbContext.Events.AsNoTracking().Single();
        Assert.AreEqual("Updated", stored.Name);
        Assert.AreEqual(new DateTime(2026, 5, 1), stored.StartDate);
        Assert.AreEqual(new DateTime(2026, 5, 3), stored.EndDate);
        Assert.IsTrue(stored.IsActive);
        Assert.AreEqual("https://example.test", stored.EventUrl);
        Assert.AreEqual("Track", stored.TrackName);
        Assert.AreEqual("Full", stored.CourseConfiguration);
        Assert.AreEqual("2.5", stored.Distance);
        Assert.IsTrue(stored.IsArchived);
        Assert.IsTrue(stored.HideName);
        Assert.AreEqual(OrgId, stored.OrganizationId);
        Assert.AreEqual("Weekend", stored.Schedule.Name);
        Assert.AreEqual("Practice", stored.Schedule.Entries.Single().Name);
        Assert.AreEqual("Broadcaster", stored.Broadcast.CompanyName);
        Assert.AreEqual("https://stream.test", stored.Broadcast.Url);
        Assert.AreEqual("S/F", stored.LoopsMetadata.Single().Name);
        VerifyPublishCount(10, Times.Once());
    }

    [TestMethod]
    public async Task UpdateEvent_TurningOffPrivate_ClearsStoredAccessCode()
    {
        await SeedOrganizationsAsync();
        var stored = NewEvent(10, OrgId, "E", new DateTime(2026, 1, 1));
        stored.IsPrivate = true;
        stored.AccessCode = "1234";
        _dbContext.Events.Add(stored);
        await _dbContext.SaveChangesAsync();

        await _controller.UpdateEvent(new ConfigEvent { Id = 10, Name = "E", IsPrivate = false, AccessCode = "1234" });

        var updated = _dbContext.Events.AsNoTracking().Single();
        Assert.IsFalse(updated.IsPrivate);
        Assert.IsNull(updated.AccessCode);
    }

    [TestMethod]
    public async Task UpdateEvent_ApiClient_ForcesSimulationEvenWhenRequestSaysOtherwise()
    {
        _dbContext.Organizations.Add(new Organization { Id = OrgId, ClientId = "api-client", Name = "Mine", ShortName = "M" });
        _dbContext.Events.Add(NewEvent(10, OrgId, "E", new DateTime(2026, 1, 1)));
        await _dbContext.SaveChangesAsync();
        SetUser("api-client");

        await _controller.UpdateEvent(new ConfigEvent { Id = 10, Name = "E", IsSimulation = false });

        Assert.IsTrue(_dbContext.Events.AsNoTracking().Single().IsSimulation);
    }

    [TestMethod]
    public async Task UpdateEvent_SwitchingAwayFromExternalSource_ClearsExternalConfig()
    {
        await SeedOrganizationsAsync();
        var stored = NewEvent(10, OrgId, "E", new DateTime(2026, 1, 1));
        stored.TimingSource = TimingSource.External;
        stored.ExternalConfig = "{\"url\":\"x\"}";
        _dbContext.Events.Add(stored);
        await _dbContext.SaveChangesAsync();

        await _controller.UpdateEvent(new ConfigEvent { Id = 10, Name = "E", TimingSource = TimingSource.Relay, ExternalConfig = "{\"url\":\"x\"}" });

        var updated = _dbContext.Events.AsNoTracking().Single();
        Assert.AreEqual(TimingSource.Relay, updated.TimingSource);
        Assert.IsNull(updated.ExternalConfig);
    }

    #endregion

    #region UpdateEventStatusActive

    [TestMethod]
    public async Task UpdateEventStatusActive_OrganizationNotFoundForClient_ReturnsNotFound()
    {
        await SeedOrganizationsAsync();
        SetUser("unknown-client");

        var result = await _controller.UpdateEventStatusActive(10);

        Assert.AreEqual("org", (result as NotFoundObjectResult)?.Value);
        VerifyPublishCount(10, Times.Never());
    }

    [TestMethod]
    public async Task UpdateEventStatusActive_EventOwnedByAnotherOrganization_MakesNoChanges()
    {
        await SeedOrganizationsAsync();
        _dbContext.Events.Add(NewEvent(20, OtherOrgId, "Theirs", new DateTime(2026, 1, 1)));
        await _dbContext.SaveChangesAsync();

        var result = await _controller.UpdateEventStatusActive(20);

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.IsFalse(_dbContext.Events.AsNoTracking().Single().IsActive);
        VerifyPublishCount(20, Times.Never());
    }

    #endregion

    #region DeleteEvent

    [TestMethod]
    public async Task DeleteEvent_OrganizationNotFoundForClient_ReturnsNotFoundOrg()
    {
        await SeedOrganizationsAsync();
        SetUser("unknown-client");

        var result = await _controller.DeleteEvent(10);

        Assert.AreEqual("org", (result as NotFoundObjectResult)?.Value);
    }

    [TestMethod]
    public async Task DeleteEvent_EventOwnedByAnotherOrganization_ReturnsNotFoundAndLeavesItAlone()
    {
        await SeedOrganizationsAsync();
        _dbContext.Events.Add(NewEvent(20, OtherOrgId, "Theirs", new DateTime(2026, 1, 1)));
        await _dbContext.SaveChangesAsync();

        var result = await _controller.DeleteEvent(20);

        Assert.AreEqual("event", (result as NotFoundObjectResult)?.Value);
        Assert.IsFalse(_dbContext.Events.AsNoTracking().Single().IsDeleted);
        VerifyPublishCount(20, Times.Never());
    }

    [TestMethod]
    public async Task DeleteEvent_InactiveEvent_SoftDeletesAndPublishesWithoutReassigningActive()
    {
        await SeedOrganizationsAsync();
        _dbContext.Events.AddRange(
            NewEvent(10, OrgId, "Target", new DateTime(2026, 1, 1)),
            NewEvent(11, OrgId, "Other", new DateTime(2026, 2, 1)));
        await _dbContext.SaveChangesAsync();
        _controller.InterceptActiveEventUpdate = true;

        var result = await _controller.DeleteEvent(10);

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.IsTrue(_dbContext.Events.AsNoTracking().Single(e => e.Id == 10).IsDeleted);
        Assert.AreEqual(0, _controller.ActiveEventReassignments.Count);
        VerifyPublishCount(10, Times.Once());
    }

    [TestMethod]
    public async Task DeleteEvent_ActiveEvent_ReassignsActiveToNewestRemainingEventInSameOrganization()
    {
        await SeedOrganizationsAsync();
        _dbContext.Events.AddRange(
            NewEvent(10, OrgId, "Target", new DateTime(2026, 3, 1), isActive: true),
            NewEvent(11, OrgId, "Older", new DateTime(2026, 1, 1)),
            NewEvent(12, OrgId, "Newest remaining", new DateTime(2026, 2, 1)),
            NewEvent(13, OrgId, "Deleted but newer", new DateTime(2026, 12, 1), isDeleted: true),
            NewEvent(20, OtherOrgId, "Other org newer", new DateTime(2026, 12, 1)));
        await _dbContext.SaveChangesAsync();
        _controller.InterceptActiveEventUpdate = true;

        var result = await _controller.DeleteEvent(10);

        Assert.IsInstanceOfType<OkResult>(result);
        CollectionAssert.AreEqual(new[] { 12 }, _controller.ActiveEventReassignments.ToArray());
    }

    [TestMethod]
    public async Task DeleteEvent_ActiveEventWithNoRemainingEvents_DoesNotReassign()
    {
        await SeedOrganizationsAsync();
        _dbContext.Events.Add(NewEvent(10, OrgId, "Only", new DateTime(2026, 1, 1), isActive: true));
        await _dbContext.SaveChangesAsync();
        _controller.InterceptActiveEventUpdate = true;

        var result = await _controller.DeleteEvent(10);

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.AreEqual(0, _controller.ActiveEventReassignments.Count);
    }

    #endregion

    #region PublishEventConfigurationChangedAsync

    [TestMethod]
    public async Task PublishEventConfigurationChanged_WritesStreamEntryAndDropsCachedAccessRecord()
    {
        await _controller.PublishConfigurationChangedAsync(42);

        _mockDatabase.Verify(x => x.StreamAddAsync(
            It.Is<RedisKey>(k => k == "evt-st-42"),
            It.Is<RedisValue>(f => f == $"{Consts.EVENT_CONFIGURATION_CHANGED}-42-999999"),
            It.Is<RedisValue>(v => v == "42"),
            It.IsAny<RedisValue?>(),
            It.Is<long?>(l => l == Consts.EVENT_STATUS_STREAM_MAX_LENGTH),
            It.Is<bool>(a => a),
            It.IsAny<long?>(),
            It.IsAny<StreamTrimMode>(),
            It.IsAny<CommandFlags>()), Times.Once);
        _mockDatabase.Verify(x => x.KeyDeleteAsync(
            It.Is<RedisKey>(k => k == "evt-42-access"), CommandFlags.FireAndForget), Times.Once);
    }

    [TestMethod]
    public async Task PublishEventConfigurationChanged_TransientRedisFailure_SucceedsOnRetry()
    {
        _mockDatabase.SetupSequence(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                It.IsAny<RedisValue?>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()))
            .Throws(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "transient"))
            .ReturnsAsync(new RedisValue("1-1"));

        await _controller.PublishConfigurationChangedAsync(42);

        _mockDatabase.Verify(x => x.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    [TestMethod]
    public async Task PublishEventConfigurationChanged_PersistentRedisFailure_RethrowsAfterThreeAttempts()
    {
        _mockDatabase.Setup(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                It.IsAny<RedisValue?>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()))
            .Throws(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        await Assert.ThrowsExactlyAsync<RedisConnectionException>(() => _controller.PublishConfigurationChangedAsync(42));

        _mockDatabase.Verify(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue?>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()), Times.Exactly(3));
        _mockDatabase.Verify(x => x.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    [TestMethod]
    public async Task PublishEventConfigurationChanged_NonRedisFailure_RethrowsWithoutRetrying()
    {
        _mockDatabase.Setup(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                It.IsAny<RedisValue?>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()))
            .Throws(new InvalidOperationException("boom"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => _controller.PublishConfigurationChangedAsync(42));

        _mockDatabase.Verify(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue?>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    #endregion

    #region Event status logs

    /// <summary>
    /// Events 10 and 11 belong to the caller's organization and event 12 belongs to another one, so the
    /// eventId/sessionId filtering is exercised alongside the absence of any organization scoping.
    /// </summary>
    private async Task SeedLogsAsync()
    {
        await SeedOrganizationsAsync();
        var start = new DateTime(2026, 4, 1);
        _dbContext.Events.Add(NewEvent(10, OrgId, "Mine A", start));
        _dbContext.Events.Add(NewEvent(11, OrgId, "Mine B", start));
        _dbContext.Events.Add(NewEvent(12, OtherOrgId, "Theirs", start));

        var baseTime = new DateTime(2026, 4, 1, 12, 0, 0);
        _dbContext.EventStatusLogs.AddRange(
            new EventStatusLog { Id = 1, EventId = 10, SessionId = 100, Timestamp = baseTime, Data = "a" },
            new EventStatusLog { Id = 2, EventId = 10, SessionId = 100, Timestamp = baseTime, Data = "b" },
            new EventStatusLog { Id = 3, EventId = 10, SessionId = 101, Timestamp = baseTime.AddMinutes(1), Data = "c" },
            new EventStatusLog { Id = 4, EventId = 11, SessionId = 100, Timestamp = baseTime.AddMinutes(2), Data = "d" },
            new EventStatusLog { Id = 5, EventId = 12, SessionId = 100, Timestamp = baseTime.AddMinutes(3), Data = "e" });
        await _dbContext.SaveChangesAsync();
    }

    private async Task<List<EventStatusLog>> ReadLogsAsync(int eventId, int? sessionId, int skip = 0, int take = 1000)
    {
        var logs = new List<EventStatusLog>();
        await foreach (var log in _controller.LoadEventLogsAsync(eventId, sessionId, skip, take))
            logs.Add(log);
        return logs;
    }

    [TestMethod]
    public async Task LoadEventLogsAsync_ScopedToSession_OrdersNewestFirstThenByIdDescending()
    {
        await SeedLogsAsync();

        var logs = await ReadLogsAsync(10, 100);

        CollectionAssert.AreEqual(new[] { 2L, 1L }, logs.Select(l => l.Id).ToArray());
    }

    [TestMethod]
    public async Task LoadEventLogsAsync_NoSessionFilter_ReturnsAllSessionsForEventOnly()
    {
        await SeedLogsAsync();

        var logs = await ReadLogsAsync(10, null);

        CollectionAssert.AreEqual(new[] { 3L, 2L, 1L }, logs.Select(l => l.Id).ToArray());
    }

    [TestMethod]
    public async Task LoadEventLogsAsync_SkipAndTake_PageThroughOrderedResults()
    {
        await SeedLogsAsync();

        var logs = await ReadLogsAsync(10, null, skip: 1, take: 1);

        CollectionAssert.AreEqual(new[] { 2L }, logs.Select(l => l.Id).ToArray());
    }

    [TestMethod]
    public async Task LoadEventLogsCountAsync_ScopedToSession_CountsOnlyThatSession()
    {
        await SeedLogsAsync();

        Assert.AreEqual(2, await _controller.LoadEventLogsCountAsync(10, 100));
    }

    [TestMethod]
    public async Task LoadEventLogsCountAsync_NoSessionFilter_CountsAllSessionsForEvent()
    {
        await SeedLogsAsync();

        // Event 11 also has a log row, so this proves the eventId filter is applied.
        Assert.AreEqual(3, await _controller.LoadEventLogsCountAsync(10, null));
        Assert.AreEqual(1, await _controller.LoadEventLogsCountAsync(11, null));
    }

    /// <summary>
    /// Unlike every other endpoint on this controller, these two are keyed on event id alone. The relay's
    /// simulation tab downloads another organization's real event data to replay it on a dev or test
    /// relay, so scoping these to the caller's own organization breaks simulation.
    /// </summary>
    [TestMethod]
    public async Task LoadEventLogsAsync_EventBelongsToAnotherOrganization_StillReturnsTheLogs()
    {
        await SeedLogsAsync();

        var logs = await ReadLogsAsync(12, null);

        CollectionAssert.AreEqual(new[] { 5L }, logs.Select(l => l.Id).ToArray());
        Assert.AreEqual(1, await _controller.LoadEventLogsCountAsync(12, null));
    }

    /// <summary>
    /// The claim is what would scope these endpoints by organization, so its absence must not empty the
    /// result either - that would be the same scoping back again, keyed on a missing claim rather than a
    /// non-matching one, and the relay would be back to downloading nothing.
    /// </summary>
    [TestMethod]
    public async Task LoadEventLogsAsync_WithoutAClientIdClaim_StillReturnsTheLogs()
    {
        await SeedLogsAsync();
        SetUser(null);

        var logs = await ReadLogsAsync(10, null);

        CollectionAssert.AreEqual(new[] { 3L, 2L, 1L }, logs.Select(l => l.Id).ToArray());
        Assert.AreEqual(3, await _controller.LoadEventLogsCountAsync(10, null));
    }

    #endregion

    /// <summary>
    /// Concrete controller used for testing. Exposes the protected publish helper and can stand in
    /// for <see cref="EventControllerBase.UpdateEventStatusActive"/> so the active-event reassignment
    /// performed by DeleteEvent can be observed without executing raw SQL.
    /// </summary>
    private sealed class TestEventController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
        IConnectionMultiplexer cacheMux) : EventControllerBase(loggerFactory, tsContext, cacheMux)
    {
        public bool InterceptActiveEventUpdate { get; set; }
        public List<int> ActiveEventReassignments { get; } = [];

        public Task PublishConfigurationChangedAsync(int eventId) => PublishEventConfigurationChangedAsync(eventId);

        public override Task<IActionResult> UpdateEventStatusActive(int eventId)
        {
            if (InterceptActiveEventUpdate)
            {
                ActiveEventReassignments.Add(eventId);
                return Task.FromResult<IActionResult>(new OkResult());
            }
            return base.UpdateEventStatusActive(eventId);
        }
    }
}
