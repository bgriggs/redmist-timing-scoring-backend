using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models.Configuration;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.Shared;

[TestClass]
public class EventsCheckerTests
{
    private Mock<IConnectionMultiplexer> _mockConnectionMux = null!;
    private Mock<IDatabase> _mockDatabase = null!;
    private IDbContextFactory<TsContext> _dbContextFactory = null!;
    private HybridCache _hybridCache = null!;
    private string _testDatabaseName = null!;

    /// <summary>
    /// Always invokes the factory directly, bypassing cache infrastructure.
    /// </summary>
    private sealed class PassThroughHybridCache : HybridCache
    {
        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key, TState state, Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null, IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
            => factory(state, cancellationToken);

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public override ValueTask RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public override ValueTask SetAsync<T>(string key, T value, HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    [TestInitialize]
    public void Setup()
    {
        _mockConnectionMux = new Mock<IConnectionMultiplexer>();
        _mockDatabase = new Mock<IDatabase>();
        _mockConnectionMux.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_mockDatabase.Object);

        _testDatabaseName = $"EventsCheckerTests_{Guid.NewGuid()}";
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(_testDatabaseName)
            .Options;
        _dbContextFactory = new TestDbContextFactory(options);

        _hybridCache = new PassThroughHybridCache();
    }

    private EventsChecker CreateChecker() => new(_mockConnectionMux.Object, _dbContextFactory, _hybridCache);

    private async Task SeedEventAsync(int eventId, bool isArchived, DateTime endDate)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.Events.Add(new Event
        {
            Id = eventId,
            OrganizationId = 1,
            Name = $"Test Event {eventId}",
            IsArchived = isArchived,
            EndDate = endDate,
            StartDate = endDate.AddHours(-8),
            TrackName = "Test Track",
            CourseConfiguration = "Full",
            Distance = "2.5mi",
            EventUrl = "",
        });
        await db.SaveChangesAsync();
    }

    private void SetupRedisEntries(params RelayConnectionEventEntry[] eventEntries)
    {
        var hashEntries = eventEntries.Select(e =>
            new HashEntry(e.ConnectionId, JsonSerializer.Serialize(e))).ToArray();

        _mockDatabase
            .Setup(x => x.HashGetAllAsync(It.Is<RedisKey>(k => k == Consts.RELAY_EVENT_CONNECTIONS), It.IsAny<CommandFlags>()))
            .ReturnsAsync(hashEntries);
    }

    #region Empty / No Entries

    [TestMethod]
    public async Task GetCurrentEventsAsync_NoRedisEntries_ReturnsEmptyList()
    {
        _mockDatabase
            .Setup(x => x.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync([]);

        var checker = CreateChecker();
        var result = await checker.GetCurrentEventsAsync();

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public async Task GetCurrentEventsAsync_NullAndEmptyHashValues_AreSkipped()
    {
        var hashEntries = new[]
        {
            new HashEntry(RedisValue.Null, "value"),
            new HashEntry("key", RedisValue.Null),
            new HashEntry("", "value2"),
            new HashEntry("key2", ""),
        };
        _mockDatabase
            .Setup(x => x.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(hashEntries);

        var checker = CreateChecker();
        var result = await checker.GetCurrentEventsAsync();

        Assert.IsEmpty(result);
    }

    #endregion

    #region Active Event Filtering

    [TestMethod]
    public async Task GetCurrentEventsAsync_ActiveEvent_IsIncluded()
    {
        var eventId = 10;
        await SeedEventAsync(eventId, isArchived: false, endDate: DateTime.UtcNow.AddHours(-1));

        SetupRedisEntries(new RelayConnectionEventEntry { ConnectionId = "conn1", EventId = eventId, OrganizationId = 1 });

        var checker = CreateChecker();
        var result = await checker.GetCurrentEventsAsync();

        Assert.HasCount(1, result);
        Assert.AreEqual(eventId, result[0].EventId);
    }

    [TestMethod]
    public async Task GetCurrentEventsAsync_ArchivedEvent_IsExcluded()
    {
        var eventId = 20;
        await SeedEventAsync(eventId, isArchived: true, endDate: DateTime.UtcNow.AddHours(-1));

        SetupRedisEntries(new RelayConnectionEventEntry { ConnectionId = "conn1", EventId = eventId, OrganizationId = 1 });

        var checker = CreateChecker();
        var result = await checker.GetCurrentEventsAsync();

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public async Task GetCurrentEventsAsync_EndDateOlderThan24Hours_IsExcluded()
    {
        var eventId = 30;
        await SeedEventAsync(eventId, isArchived: false, endDate: DateTime.UtcNow.AddHours(-25));

        SetupRedisEntries(new RelayConnectionEventEntry { ConnectionId = "conn1", EventId = eventId, OrganizationId = 1 });

        var checker = CreateChecker();
        var result = await checker.GetCurrentEventsAsync();

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public async Task GetCurrentEventsAsync_EndDateExactly24HoursAgo_IsIncluded()
    {
        var eventId = 31;
        await SeedEventAsync(eventId, isArchived: false, endDate: DateTime.UtcNow.AddHours(-23));

        SetupRedisEntries(new RelayConnectionEventEntry { ConnectionId = "conn1", EventId = eventId, OrganizationId = 1 });

        var checker = CreateChecker();
        var result = await checker.GetCurrentEventsAsync();

        Assert.HasCount(1, result);
    }

    [TestMethod]
    public async Task GetCurrentEventsAsync_ArchivedAndExpiredEvent_IsExcluded()
    {
        var eventId = 40;
        await SeedEventAsync(eventId, isArchived: true, endDate: DateTime.UtcNow.AddHours(-48));

        SetupRedisEntries(new RelayConnectionEventEntry { ConnectionId = "conn1", EventId = eventId, OrganizationId = 1 });

        var checker = CreateChecker();
        var result = await checker.GetCurrentEventsAsync();

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public async Task GetCurrentEventsAsync_EventNotInDatabase_IsExcluded()
    {
        var eventId = 999;
        SetupRedisEntries(new RelayConnectionEventEntry { ConnectionId = "conn1", EventId = eventId, OrganizationId = 1 });

        var checker = CreateChecker();
        var result = await checker.GetCurrentEventsAsync();

        Assert.IsEmpty(result);
    }

    #endregion

    #region Multiple Events

    [TestMethod]
    public async Task GetCurrentEventsAsync_MixedEvents_ReturnsOnlyActive()
    {
        var activeEventId = 100;
        var archivedEventId = 101;
        var expiredEventId = 102;

        await SeedEventAsync(activeEventId, isArchived: false, endDate: DateTime.UtcNow.AddHours(-1));
        await SeedEventAsync(archivedEventId, isArchived: true, endDate: DateTime.UtcNow.AddHours(-1));
        await SeedEventAsync(expiredEventId, isArchived: false, endDate: DateTime.UtcNow.AddHours(-48));

        SetupRedisEntries(
            new RelayConnectionEventEntry { ConnectionId = "conn1", EventId = activeEventId, OrganizationId = 1 },
            new RelayConnectionEventEntry { ConnectionId = "conn2", EventId = archivedEventId, OrganizationId = 1 },
            new RelayConnectionEventEntry { ConnectionId = "conn3", EventId = expiredEventId, OrganizationId = 1 }
        );

        var checker = CreateChecker();
        var result = await checker.GetCurrentEventsAsync();

        Assert.HasCount(1, result);
        Assert.AreEqual(activeEventId, result[0].EventId);
    }

    [TestMethod]
    public async Task GetCurrentEventsAsync_MultipleActiveEvents_ReturnsAll()
    {
        var eventId1 = 200;
        var eventId2 = 201;

        await SeedEventAsync(eventId1, isArchived: false, endDate: DateTime.UtcNow.AddHours(-2));
        await SeedEventAsync(eventId2, isArchived: false, endDate: DateTime.UtcNow.AddHours(-10));

        SetupRedisEntries(
            new RelayConnectionEventEntry { ConnectionId = "conn1", EventId = eventId1, OrganizationId = 1 },
            new RelayConnectionEventEntry { ConnectionId = "conn2", EventId = eventId2, OrganizationId = 1 }
        );

        var checker = CreateChecker();
        var result = await checker.GetCurrentEventsAsync();

        Assert.HasCount(2, result);
        CollectionAssert.AreEquivalent(new[] { eventId1, eventId2 }, result.Select(r => r.EventId).ToList());
    }

    #endregion

    #region EventId Validation

    [TestMethod]
    public async Task GetCurrentEventsAsync_EventIdZero_IsSkipped()
    {
        SetupRedisEntries(new RelayConnectionEventEntry { ConnectionId = "conn1", EventId = 0, OrganizationId = 1 });

        var checker = CreateChecker();
        var result = await checker.GetCurrentEventsAsync();

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public async Task GetCurrentEventsAsync_NegativeEventId_IsSkipped()
    {
        SetupRedisEntries(new RelayConnectionEventEntry { ConnectionId = "conn1", EventId = -1, OrganizationId = 1 });

        var checker = CreateChecker();
        var result = await checker.GetCurrentEventsAsync();

        Assert.IsEmpty(result);
    }

    #endregion

    #region Future EndDate

    [TestMethod]
    public async Task GetCurrentEventsAsync_FutureEndDate_IsIncluded()
    {
        var eventId = 300;
        await SeedEventAsync(eventId, isArchived: false, endDate: DateTime.UtcNow.AddHours(5));

        SetupRedisEntries(new RelayConnectionEventEntry { ConnectionId = "conn1", EventId = eventId, OrganizationId = 1 });

        var checker = CreateChecker();
        var result = await checker.GetCurrentEventsAsync();

        Assert.HasCount(1, result);
        Assert.AreEqual(eventId, result[0].EventId);
    }

    #endregion
}
