using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.Database;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.StatusApi.Controllers.V1;
using RedMist.TimingCommon.Models.InCarVideo;
using StackExchange.Redis;
using System.Security.Claims;
using System.Text.Json;
using DriverInfo = RedMist.TimingCommon.Models.DriverInfo;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

/// <summary>
/// Covers the parts of ExternalTelemetryController that decide who is allowed to overwrite whom.
/// The relay is the authoritative name source during an event, and third-party telemetry partners
/// post to the same keys, so the priority rules are what stop a partner blanking the field.
/// Also covers the in-car video endpoint end to end.
/// </summary>
[TestClass]
public class ExternalTelemetryVideoAndPriorityTests
{
    private Mock<ILogger> _logger = null!;
    private Mock<IConnectionMultiplexer> _mux = null!;
    private Mock<IDatabase> _redis = null!;
    private FakeHybridCache _hybridCache = null!;
    private TsContext _db = null!;
    private IDbContextFactory<TsContext> _dbFactory = null!;
    private ExternalTelemetryController _controller = null!;

    [TestInitialize]
    public void Setup()
    {
        _logger = new Mock<ILogger>();
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_logger.Object);

        _mux = new Mock<IConnectionMultiplexer>();
        _redis = new Mock<IDatabase>();
        _mux.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(() => _redis.Object);
        _hybridCache = new FakeHybridCache();

        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbFactory = new TestDbContextFactory(options);
        _db = _dbFactory.CreateDbContext();

        var eventsController = new RedMist.StatusApi.Controllers.V2.EventsController(
            loggerFactory.Object, _dbFactory, _hybridCache, _mux.Object,
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            new Mock<IHttpClientFactory>().Object);

        _controller = new ExternalTelemetryController(loggerFactory.Object, _mux.Object, eventsController, _dbFactory, _hybridCache);

        _redis.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        _redis.Setup(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue?>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<long?>(),
            It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue("stream-id"));
    }

    [TestCleanup]
    public void Cleanup() => _db.Dispose();

    private void SetUser(string clientId, params string[] roles)
    {
        var identity = new ClaimsIdentity([new Claim("azp", clientId)], "TestAuthType");
        foreach (var role in roles)
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private void SetExistingRedisValue(string key, string? json) =>
        _redis.Setup(x => x.StringGetAsync((RedisKey)key, It.IsAny<CommandFlags>()))
            .ReturnsAsync(json is null ? RedisValue.Null : new RedisValue(json));

    /// <summary>Verifies against the maxLength/trim-mode overload the controller actually binds to.</summary>
    private void VerifyStreamAdd(string field, Times times) =>
        _redis.Verify(x => x.StreamAddAsync(It.IsAny<RedisKey>(), (RedisValue)field, It.IsAny<RedisValue>(),
            It.IsAny<RedisValue?>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<long?>(),
            It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()), times);

    #region Driver update priority rules

    /// <summary>
    /// A relay posts the whole field at once. When one car's name was set by a higher priority
    /// third-party source and the relay has no name for it, that single car must be rejected while
    /// every other car in the same batch is still written - otherwise one protected car would stall
    /// updates for the entire field.
    /// </summary>
    [TestMethod]
    public async Task UpdateDrivers_RelayBlankingAThirdPartyName_Returns423ButStillWritesTheRestOfTheBatch()
    {
        SetUser("relay-test");
        var protectedKey = string.Format(Consts.EVENT_DRIVER_KEY, 1, "42");
        SetExistingRedisValue(protectedKey, JsonSerializer.Serialize(new DriverInfoSource(
            new DriverInfo { CarNumber = "42", EventId = 1, DriverName = "Third Party Name" },
            "ext-telem-partner", DateTime.UtcNow)));

        var result = await _controller.UpdateDriversAsync(
        [
            new DriverInfo { CarNumber = "42", EventId = 1, DriverName = string.Empty },
            new DriverInfo { CarNumber = "43", EventId = 1, DriverName = "Relay Name" },
        ]);

        var status = result as ObjectResult;
        Assert.IsNotNull(status);
        Assert.AreEqual(StatusCodes.Status423Locked, status.StatusCode);

        // Car 42 must be left alone...
        _redis.Verify(x => x.StringSetAsync((RedisKey)protectedKey, It.IsAny<RedisValue>(),
            It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()), Times.Never);
        // ...but car 43 must still have been written.
        _redis.Verify(x => x.StringSetAsync((RedisKey)string.Format(Consts.EVENT_DRIVER_KEY, 1, "43"),
            It.Is<RedisValue>(v => v.ToString().Contains("Relay Name")),
            It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    /// <summary>The relay may blank its own previous value; only a foreign source's name is protected.</summary>
    [TestMethod]
    public async Task UpdateDrivers_RelayBlankingItsOwnPreviousName_IsAllowed()
    {
        SetUser("relay-test");
        var key = string.Format(Consts.EVENT_DRIVER_KEY, 1, "42");
        SetExistingRedisValue(key, JsonSerializer.Serialize(new DriverInfoSource(
            new DriverInfo { CarNumber = "42", EventId = 1, DriverName = "Old Relay Name" },
            "relay-test", DateTime.UtcNow)));

        var result = await _controller.UpdateDriversAsync([new DriverInfo { CarNumber = "42", EventId = 1, DriverName = string.Empty }]);

        Assert.IsInstanceOfType<OkResult>(result);
        _redis.Verify(x => x.StringSetAsync((RedisKey)key, It.IsAny<RedisValue>(),
            It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    /// <summary>
    /// A third-party source must never overwrite a relay-set record, whatever it sends. The record is
    /// refreshed to keep its TTL alive but no change is published.
    /// </summary>
    [TestMethod]
    public async Task UpdateDrivers_ThirdPartyOverExistingRelayRecord_Returns423AndPublishesNothing()
    {
        SetUser("ext-telem-partner", "ext-telem");
        SetExistingRedisValue(string.Format(Consts.EVENT_DRIVER_KEY, 1, "42"), JsonSerializer.Serialize(
            new DriverInfoSource(new DriverInfo { CarNumber = "42", EventId = 1, DriverName = "Relay Name" },
                "relay-test", DateTime.UtcNow)));

        var result = await _controller.UpdateDriversAsync(
            [new DriverInfo { CarNumber = "42", EventId = 1, DriverName = "Partner Name" }]);

        var status = result as ObjectResult;
        Assert.IsNotNull(status);
        Assert.AreEqual(StatusCodes.Status423Locked, status.StatusCode);
        VerifyStreamAdd(string.Format(Consts.EVENT_DRIVER_CHANGE_STREAM_FIELD, 1), Times.Never());
    }

    /// <summary>A third-party source may update another third-party source's record.</summary>
    [TestMethod]
    public async Task UpdateDrivers_ThirdPartyOverAnotherThirdPartyRecord_PublishesTheChange()
    {
        SetUser("ext-telem-partner", "ext-telem");
        SetExistingRedisValue(string.Format(Consts.EVENT_DRIVER_KEY, 1, "42"), JsonSerializer.Serialize(
            new DriverInfoSource(new DriverInfo { CarNumber = "42", EventId = 1, DriverName = "Old Partner Name" },
                "ext-telem-other", DateTime.UtcNow)));

        var result = await _controller.UpdateDriversAsync(
            [new DriverInfo { CarNumber = "42", EventId = 1, DriverName = "New Partner Name" }]);

        Assert.IsInstanceOfType<OkResult>(result);
        VerifyStreamAdd(string.Format(Consts.EVENT_DRIVER_CHANGE_STREAM_FIELD, 1), Times.Once());
    }

    /// <summary>Unreadable cached JSON is treated as a change so a corrupt entry self-heals on the next post.</summary>
    [TestMethod]
    public async Task UpdateDrivers_CorruptCachedRecord_IsTreatedAsAChangeAndRewritten()
    {
        SetUser("relay-test");
        SetExistingRedisValue(string.Format(Consts.EVENT_DRIVER_KEY, 1, "42"), "{ not valid json");

        var result = await _controller.UpdateDriversAsync(
            [new DriverInfo { CarNumber = "42", EventId = 1, DriverName = "Relay Name" }]);

        Assert.IsInstanceOfType<OkResult>(result);
        VerifyStreamAdd(string.Format(Consts.EVENT_DRIVER_CHANGE_STREAM_FIELD, 1), Times.Once());
    }

    /// <summary>A cached record that deserializes to null is also treated as a change and rewritten.</summary>
    [TestMethod]
    public async Task UpdateDrivers_CachedRecordIsJsonNull_IsTreatedAsAChange()
    {
        SetUser("relay-test");
        SetExistingRedisValue(string.Format(Consts.EVENT_DRIVER_KEY, 1, "42"), "null");

        var result = await _controller.UpdateDriversAsync(
            [new DriverInfo { CarNumber = "42", EventId = 1, DriverName = "Relay Name" }]);

        Assert.IsInstanceOfType<OkResult>(result);
        VerifyStreamAdd(string.Format(Consts.EVENT_DRIVER_CHANGE_STREAM_FIELD, 1), Times.Once());
    }

    /// <summary>
    /// The centrally stored name overrides an unchanged record for third-party sources too, not just
    /// for the relay - both branches carry their own copy of the check.
    /// </summary>
    [TestMethod]
    public async Task UpdateDrivers_ThirdPartyUnchangedButDifferentCentralName_PublishesTheCentralName()
    {
        SetUser("ext-telem-partner", "ext-telem");
        _db.DriverInfo.Add(new RedMist.Database.Models.DriverInfo { FlagtronicsId = 777, Name = "Central Name" });
        await _db.SaveChangesAsync();

        var incoming = new DriverInfo { CarNumber = "42", EventId = 1, DriverId = "777", DriverName = "Partner Name" };
        SetExistingRedisValue(string.Format(Consts.EVENT_DRIVER_KEY, 1, "42"),
            JsonSerializer.Serialize(new DriverInfoSource(incoming, "ext-telem-other", DateTime.UtcNow)));

        var result = await _controller.UpdateDriversAsync([incoming]);

        Assert.IsInstanceOfType<OkResult>(result);
        _redis.Verify(x => x.StringSetAsync(It.IsAny<RedisKey>(),
            It.Is<RedisValue>(v => v.ToString().Contains("Central Name")),
            It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()), Times.Once);
        VerifyStreamAdd(string.Format(Consts.EVENT_DRIVER_CHANGE_STREAM_FIELD, 1), Times.Once());
    }

    /// <summary>
    /// 0xFFFFFFFF is Flagtronics' "name is still being validated" sentinel; looking it up centrally
    /// would key thousands of unrelated cars to one bogus driver record.
    /// </summary>
    [TestMethod]
    public async Task UpdateDrivers_ValidatingSentinelDriverId_SkipsTheCentralNameLookup()
    {
        SetUser("relay-test");
        _db.DriverInfo.Add(new RedMist.Database.Models.DriverInfo { FlagtronicsId = 4294967295, Name = "Sentinel" });
        await _db.SaveChangesAsync();

        var result = await _controller.UpdateDriversAsync(
            [new DriverInfo { CarNumber = "42", EventId = 1, DriverId = "4294967295", DriverName = "Source Name" }]);

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.DoesNotContain("driver-flagtronics-4294967295", _hybridCache.FactoryInvocations);
        _redis.Verify(x => x.StringSetAsync(It.IsAny<RedisKey>(),
            It.Is<RedisValue>(v => v.ToString().Contains("Source Name")),
            It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    /// <summary>Driver id 0 means "unknown driver" and must not be looked up either.</summary>
    [TestMethod]
    public async Task UpdateDrivers_ZeroDriverId_SkipsTheCentralNameLookup()
    {
        SetUser("relay-test");

        var result = await _controller.UpdateDriversAsync(
            [new DriverInfo { CarNumber = "42", EventId = 1, DriverId = "0", DriverName = "Source Name" }]);

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.DoesNotContain("driver-flagtronics-0", _hybridCache.FactoryInvocations);
    }

    /// <summary>
    /// When the incoming record already matches the cache but the centrally stored name differs, the
    /// central name must still be pushed out - otherwise a stale per-event name sticks for the event.
    /// </summary>
    [TestMethod]
    public async Task UpdateDrivers_UnchangedRecordButDifferentCentralName_PublishesTheCentralName()
    {
        SetUser("relay-test");
        _db.DriverInfo.Add(new RedMist.Database.Models.DriverInfo { FlagtronicsId = 555, Name = "Central Name" });
        await _db.SaveChangesAsync();

        var incoming = new DriverInfo { CarNumber = "42", EventId = 1, DriverId = "555", DriverName = "Source Name" };
        SetExistingRedisValue(string.Format(Consts.EVENT_DRIVER_KEY, 1, "42"),
            JsonSerializer.Serialize(new DriverInfoSource(incoming, "relay-test", DateTime.UtcNow)));

        var result = await _controller.UpdateDriversAsync([incoming]);

        Assert.IsInstanceOfType<OkResult>(result);
        _redis.Verify(x => x.StringSetAsync(It.IsAny<RedisKey>(),
            It.Is<RedisValue>(v => v.ToString().Contains("Central Name")),
            It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()), Times.Once);
        VerifyStreamAdd(string.Format(Consts.EVENT_DRIVER_CHANGE_STREAM_FIELD, 1), Times.Once());
    }

    /// <summary>
    /// A transponder-keyed update has no event, so the change has to fan out to every live event's
    /// status stream.
    /// </summary>
    [TestMethod]
    public async Task UpdateDrivers_TransponderKeyed_FansOutToEveryLiveEvent()
    {
        SetUser("relay-test");
        _db.Organizations.Add(new RedMist.TimingCommon.Models.Organization { Id = 1, Name = "Org", ShortName = "O", ClientId = "relay-1" });
        _db.Events.Add(new RedMist.TimingCommon.Models.Configuration.Event { Name = "Live A", OrganizationId = 1, IsLive = true, IsActive = true });
        _db.Events.Add(new RedMist.TimingCommon.Models.Configuration.Event { Name = "Live B", OrganizationId = 1, IsLive = true, IsActive = true });
        _db.Events.Add(new RedMist.TimingCommon.Models.Configuration.Event { Name = "Not Live", OrganizationId = 1, IsLive = false, IsActive = true });
        await _db.SaveChangesAsync();

        var result = await _controller.UpdateDriversAsync(
            [new DriverInfo { TransponderId = 987654, DriverName = "Transponder Driver" }]);

        Assert.IsInstanceOfType<OkResult>(result);
        VerifyStreamAdd(Consts.DRIVER_CHANGE_TRANSPONDER_FIELD, Times.Exactly(2));
    }

    #endregion

    #region UpdateCarVideosAsync

    private static VideoMetadata Video(int eventId = 1, string carNumber = "42", uint transponderId = 0,
        bool isLive = true, string driverName = "Driver") => new()
        {
            EventId = eventId,
            CarNumber = carNumber,
            TransponderId = transponderId,
            IsLive = isLive,
            DriverName = driverName,
            SystemType = VideoSystemType.MyRacesLive,
        };

    /// <summary>Video metadata is not a relay feed: only clients holding the ext-telem role may post it.</summary>
    [TestMethod]
    public async Task UpdateCarVideos_WithoutExtTelemRole_IsForbidden()
    {
        SetUser("relay-test");

        var result = await _controller.UpdateCarVideosAsync([Video()]);

        Assert.IsInstanceOfType<ForbidResult>(result);
    }

    [TestMethod]
    public async Task UpdateCarVideos_EmptyList_ReturnsBadRequest()
    {
        SetUser("partner", "ext-telem");

        var result = await _controller.UpdateCarVideosAsync([]);

        var bad = result as BadRequestObjectResult;
        Assert.IsNotNull(bad);
        Assert.AreEqual("No video data found", bad.Value);
    }

    [TestMethod]
    public async Task UpdateCarVideos_NullList_ReturnsBadRequest()
    {
        SetUser("partner", "ext-telem");

        var result = await _controller.UpdateCarVideosAsync(null!);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
    }

    [TestMethod]
    public async Task UpdateCarVideos_EmptyClientId_ReturnsBadRequest()
    {
        SetUser(string.Empty, "ext-telem");

        var result = await _controller.UpdateCarVideosAsync([Video()]);

        var bad = result as BadRequestObjectResult;
        Assert.IsNotNull(bad);
        Assert.AreEqual("No client ID found", bad.Value);
    }

    [TestMethod]
    public async Task UpdateCarVideos_NewVideo_IsCachedAndPublishedToTheEventStream()
    {
        SetUser("partner", "ext-telem");

        var result = await _controller.UpdateCarVideosAsync([Video(eventId: 1, carNumber: "42", transponderId: 999)]);

        Assert.IsInstanceOfType<OkResult>(result);
        _redis.Verify(x => x.StringSetAsync((RedisKey)string.Format(Consts.EVENT_VIDEO_KEY, 1, "42", 999u),
            It.Is<RedisValue>(v => v.ToString().Contains("Driver")),
            It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()), Times.Once);
        VerifyStreamAdd(string.Format(Consts.EVENT_VIDEO_CHANGE_STREAM_FIELD, 1), Times.Once());
    }

    /// <summary>
    /// Partners re-post the same payload on a timer; republishing every one would flood the event
    /// status stream and evict live timing messages from its capped length.
    /// </summary>
    [TestMethod]
    public async Task UpdateCarVideos_IdenticalToTheCachedValue_RefreshesTtlWithoutRepublishing()
    {
        SetUser("partner", "ext-telem");
        var video = Video();
        SetExistingRedisValue(string.Format(Consts.EVENT_VIDEO_KEY, 1, "42", 0u), JsonSerializer.Serialize(video));

        var result = await _controller.UpdateCarVideosAsync([video]);

        Assert.IsInstanceOfType<OkResult>(result);
        _redis.Verify(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()), Times.Once);
        VerifyStreamAdd(string.Format(Consts.EVENT_VIDEO_CHANGE_STREAM_FIELD, 1), Times.Never());
    }

    [TestMethod]
    public async Task UpdateCarVideos_ChangedFromTheCachedValue_IsRepublished()
    {
        SetUser("partner", "ext-telem");
        SetExistingRedisValue(string.Format(Consts.EVENT_VIDEO_KEY, 1, "42", 0u),
            JsonSerializer.Serialize(Video(driverName: "Old Driver")));

        var result = await _controller.UpdateCarVideosAsync([Video(driverName: "New Driver")]);

        Assert.IsInstanceOfType<OkResult>(result);
        VerifyStreamAdd(string.Format(Consts.EVENT_VIDEO_CHANGE_STREAM_FIELD, 1), Times.Once());
    }

    /// <summary>A video posted with no event id is broadcast to every live event under the wildcard field.</summary>
    [TestMethod]
    public async Task UpdateCarVideos_WithoutAnEventId_BroadcastsToEveryLiveEvent()
    {
        SetUser("partner", "ext-telem");
        _db.Organizations.Add(new RedMist.TimingCommon.Models.Organization { Id = 1, Name = "Org", ShortName = "O", ClientId = "relay-1" });
        _db.Events.Add(new RedMist.TimingCommon.Models.Configuration.Event { Name = "Live A", OrganizationId = 1, IsLive = true, IsActive = true });
        _db.Events.Add(new RedMist.TimingCommon.Models.Configuration.Event { Name = "Live B", OrganizationId = 1, IsLive = true, IsActive = true });
        _db.Events.Add(new RedMist.TimingCommon.Models.Configuration.Event { Name = "Archived", OrganizationId = 1, IsLive = false, IsActive = true });
        await _db.SaveChangesAsync();

        var result = await _controller.UpdateCarVideosAsync([Video(eventId: 0, transponderId: 12345)]);

        Assert.IsInstanceOfType<OkResult>(result);
        VerifyStreamAdd(string.Format(Consts.EVENT_VIDEO_CHANGE_STREAM_FIELD, 999999), Times.Exactly(2));
    }

    /// <summary>
    /// Unlike the driver endpoint, a failure here aborts the whole request with a 500 rather than
    /// skipping the offending item.
    /// </summary>
    [TestMethod]
    public async Task UpdateCarVideos_CacheFailure_Returns500()
    {
        SetUser("partner", "ext-telem");
        _redis.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        var result = await _controller.UpdateCarVideosAsync([Video()]);

        var status = result as ObjectResult;
        Assert.IsNotNull(status);
        Assert.AreEqual(StatusCodes.Status500InternalServerError, status.StatusCode);
    }

    #endregion
}
