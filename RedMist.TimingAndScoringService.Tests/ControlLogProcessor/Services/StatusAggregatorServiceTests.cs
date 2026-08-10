using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Hubs;
using RedMist.ControlLogProcessor.Services;
using RedMist.ControlLogs;
using RedMist.Database;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using ConfigEvent = RedMist.TimingCommon.Models.Configuration.Event;
using Organization = RedMist.TimingCommon.Models.Organization;

namespace RedMist.TimingAndScoringService.Tests.ControlLogProcessor.Services;

/// <summary>
/// One full pass of the control log poll: fetch, publish to SignalR, mirror into Redis, then reap the
/// cache entries and penalty hash fields of cars that have dropped out of the log. The pass is driven by
/// starting the real background service and waiting for the cleanup step to be reached, so no test here
/// depends on elapsed time.
/// </summary>
[TestClass]
public class StatusAggregatorServiceTests
{
    private const int EventId = 42;
    private const int OrgId = 7;
    private static readonly string FullLogKey = string.Format(Consts.CONTROL_LOG, EventId);
    private static readonly string PenaltiesKey = string.Format(Consts.CONTROL_LOG_CAR_PENALTIES, EventId);
    private static string CarKey(string car) => string.Format(Consts.CONTROL_LOG_CAR, EventId, car);

    #region Harness

    private sealed class FakeControlLog(params ControlLogEntry[] entries) : IControlLog
    {
        public string Type => "fake";
        public Regex WarningPattern { get; } = new(@".*Warning.*", RegexOptions.IgnoreCase);
        public Regex LapPenaltyPattern { get; } = new(@"(\d+)\s+(Lap|Laps)", RegexOptions.IgnoreCase);
        public Regex BlackFlagPattern { get; } = new(@".*Drive Through Penalty.*", RegexOptions.IgnoreCase);

        public Task<(bool success, IEnumerable<ControlLogEntry> logs)> LoadControlLogAsync(string parameter, CancellationToken stoppingToken = default)
            => Task.FromResult((true, (IEnumerable<ControlLogEntry>)entries));

        public void Dispose() { }
    }

    private sealed class Harness
    {
        public Mock<IDatabase> Db { get; } = new();
        public Mock<IServer> Server { get; } = new();
        public Mock<IConnectionMultiplexer> Mux { get; } = new();
        public Mock<IHubContext<StatusHub>> Hub { get; } = new();

        public ConcurrentDictionary<string, string> StringsWritten { get; } = new();
        public ConcurrentQueue<string> HubGroups { get; } = new();
        public ConcurrentQueue<string> DeletedKeys { get; } = new();
        public ConcurrentQueue<(string Key, string Field)> DeletedHashFields { get; } = new();
        public ConcurrentQueue<HashEntry> PenaltyHashEntries { get; } = new();
        public TaskCompletionSource CleanupReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Harness(IEnumerable<string>? existingCarCacheKeys = null, IEnumerable<string>? existingPenaltyFields = null)
        {
            Db.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(),
                    It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()))
                .Callback((RedisKey k, RedisValue v, Expiration _, ValueCondition _, CommandFlags _) => StringsWritten[k.ToString()] = v.ToString())
                .ReturnsAsync(true);

            Db.Setup(d => d.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<HashEntry[]>(), It.IsAny<CommandFlags>()))
                .Callback((RedisKey _, HashEntry[] e, CommandFlags _) => Array.ForEach(e, PenaltyHashEntries.Enqueue))
                .Returns(Task.CompletedTask);

            Db.Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Callback((RedisKey k, CommandFlags _) => DeletedKeys.Enqueue(k.ToString()))
                .ReturnsAsync(true);

            Db.Setup(d => d.HashKeysAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Callback(() => CleanupReached.TrySetResult())
                .ReturnsAsync([.. (existingPenaltyFields ?? []).Select(f => (RedisValue)f)]);

            Db.Setup(d => d.HashDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Callback((RedisKey k, RedisValue f, CommandFlags _) => DeletedHashFields.Enqueue((k.ToString(), f.ToString())))
                .ReturnsAsync(true);

            Server.Setup(s => s.Keys(It.IsAny<int>(), It.IsAny<RedisValue>(), It.IsAny<int>(), It.IsAny<long>(),
                    It.IsAny<int>(), It.IsAny<CommandFlags>()))
                .Returns([.. (existingCarCacheKeys ?? []).Select(k => (RedisKey)k)]);

            Mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(Db.Object);
            Mux.Setup(m => m.GetEndPoints(It.IsAny<bool>())).Returns([new DnsEndPoint("localhost", 6379)]);
            Mux.Setup(m => m.GetServer(It.IsAny<EndPoint>(), It.IsAny<object>())).Returns(Server.Object);

            var proxy = new Mock<IClientProxy>();
            proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var clients = new Mock<IHubClients>();
            clients.Setup(c => c.Group(It.IsAny<string>())).Returns((string g) => { HubGroups.Enqueue(g); return proxy.Object; });
            Hub.Setup(h => h.Clients).Returns(clients.Object);
        }

        /// <summary>
        /// Runs the service until the Redis cleanup step of the first pass is reached, then stops it. The
        /// remaining work in that pass ignores the cancellation token, and StopAsync awaits the pass, so
        /// everything the pass does has happened by the time this returns.
        /// </summary>
        public async Task RunOnePassAsync(StatusAggregatorService service)
        {
            await service.StartAsync(CancellationToken.None);
            try
            {
                await CleanupReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                // Always stop, even on timeout: the service's error path retries with no delay, so leaving
                // it running would spin a core for the rest of the run.
                await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }

    private static IDbContextFactory<TsContext> Db()
    {
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var factory = new TestDbContextFactory(options);
        using var db = factory.CreateDbContext();
        db.Events.Add(new ConfigEvent { Id = EventId, OrganizationId = OrgId, Name = "Test Event" });
        db.Organizations.Add(new Organization
        {
            Id = OrgId,
            Name = "Test Org",
            ShortName = "TO",
            ClientId = "test",
            ControlLogType = "fake",
            ControlLogParams = "sheet;Tab 1",
        });
        db.SaveChanges();
        return factory;
    }

    private static StatusAggregatorService Service(Harness harness, IControlLog controlLog, int eventId = EventId)
    {
        var factory = new Mock<IControlLogFactory>();
        factory.Setup(f => f.CreateControlLog(It.IsAny<string>())).Returns(controlLog);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["event_id"] = eventId.ToString() }).Build();
        return new StatusAggregatorService(NullLoggerFactory.Instance, harness.Mux.Object, config, Db(), factory.Object, harness.Hub.Object);
    }

    private static ControlLogEntry Entry(int orderId, string car1, string note, string penaltyAction = "", string ts = "2026-06-05T21:00:00Z")
        => new() { OrderId = orderId, Car1 = car1, Note = note, PenaltyAction = penaltyAction, Timestamp = DateTime.Parse(ts).ToUniversalTime() };

    #endregion

    [TestMethod]
    public async Task OnePass_PublishesTheFullLogAndEachCarsLogToBothSignalRAndRedis()
    {
        var harness = new Harness();
        var log = new FakeControlLog(Entry(1, "15", "spun in T1"), Entry(2, "23", "contact in T4", "Warning"));
        using var service = Service(harness, log);

        await harness.RunOnePassAsync(service);

        // Full event log goes to the event-wide group and its own cache key.
        Assert.IsTrue(harness.StringsWritten.ContainsKey(FullLogKey));
        StringAssert.Contains(harness.StringsWritten[FullLogKey], "spun in T1");
        StringAssert.Contains(harness.StringsWritten[FullLogKey], "contact in T4");
        CollectionAssert.Contains(harness.HubGroups.ToList(), $"{EventId}-cl");

        // ...and each car gets its own key and its own group.
        StringAssert.Contains(harness.StringsWritten[CarKey("15")], "spun in T1");
        Assert.IsFalse(harness.StringsWritten[CarKey("15")].Contains("contact in T4"), "a car's log holds only its own entries");
        StringAssert.Contains(harness.StringsWritten[CarKey("23")], "contact in T4");
        CollectionAssert.Contains(harness.HubGroups.ToList(), $"{EventId}-15");
        CollectionAssert.Contains(harness.HubGroups.ToList(), $"{EventId}-23");
    }

    [TestMethod]
    public async Task OnePass_WritesPenaltyTalliesKeyedByCarNumber()
    {
        // Both cars already have a penalty hash field and both are still in the log, so the stale-entry
        // sweep must leave them alone.
        var harness = new Harness(existingPenaltyFields: ["15", "23"]);
        var log = new FakeControlLog(Entry(1, "15", "spun", "2 Laps"), Entry(2, "23", "contact", "Warning"));
        using var service = Service(harness, log);

        await harness.RunOnePassAsync(service);

        var penalties = harness.PenaltyHashEntries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        CollectionAssert.AreEquivalent(new[] { "15", "23" }, penalties.Keys.ToList());
        StringAssert.Contains(penalties["15"], "\"l\":2");
        StringAssert.Contains(penalties["23"], "\"w\":1");
        Assert.AreEqual(0, harness.DeletedHashFields.Count, "cars that are still in the log keep their tallies");
    }

    [TestMethod]
    public async Task OnePass_DeletesTheCacheEntriesOfCarsThatAreNoLongerInTheControlLog()
    {
        // Redis still holds a key for car 99 from an earlier event configuration; only car 15 is current.
        var harness = new Harness(existingCarCacheKeys: [CarKey("15"), CarKey("99"), "unrelated-key"]);
        using var service = Service(harness, new FakeControlLog(Entry(1, "15", "spun")));

        await harness.RunOnePassAsync(service);

        CollectionAssert.AreEqual(new[] { CarKey("99") }, harness.DeletedKeys.ToList());
    }

    [TestMethod]
    public async Task OnePass_LeavesUnrecognizedRedisKeysAlone()
    {
        var harness = new Harness(existingCarCacheKeys: ["control-log-evt-999-car-15", "some-other-service-key"]);
        using var service = Service(harness, new FakeControlLog(Entry(1, "15", "spun")));

        await harness.RunOnePassAsync(service);

        Assert.AreEqual(0, harness.DeletedKeys.Count, "keys belonging to another event must not be reaped");
    }

    [TestMethod]
    public async Task OnePass_RemovesPenaltyHashFieldsForCarsThatLeftTheControlLog()
    {
        var harness = new Harness(existingPenaltyFields: ["15", "99"]);
        using var service = Service(harness, new FakeControlLog(Entry(1, "15", "spun", "Warning")));

        await harness.RunOnePassAsync(service);

        CollectionAssert.AreEqual(new[] { (PenaltiesKey, "99") }, harness.DeletedHashFields.ToList());
    }

    [TestMethod]
    public async Task OnePass_EventWideEntriesReachTheFullLogButNotAnyCarsGroupOrTally()
    {
        // Announcement-style entries name no car and live in the "" bucket. Seed the car-less key that a
        // previous pass would have left behind so the cleanup sweep gets a chance to reap it.
        var harness = new Harness(existingCarCacheKeys: [CarKey(string.Empty)]);
        using var service = Service(harness, new FakeControlLog(Entry(1, string.Empty, "CODE 60")));

        await harness.RunOnePassAsync(service);

        StringAssert.Contains(harness.StringsWritten[FullLogKey], "CODE 60");
        CollectionAssert.DoesNotContain(harness.HubGroups.ToList(), $"{EventId}-", "no per-car SignalR group for a car-less entry");
        Assert.AreEqual(0, harness.PenaltyHashEntries.Count, "the no-car bucket is not a car and gets no penalty tally");
        // The Redis mirror, unlike the SignalR publish, does not skip the blank bucket: it writes a
        // car-less "...-car-" key that the cleanup pass then ignores forever, since it only reaps keys
        // whose extracted car number is non-blank.
        Assert.IsTrue(harness.StringsWritten.ContainsKey(CarKey(string.Empty)));
        CollectionAssert.DoesNotContain(harness.DeletedKeys.ToList(), CarKey(string.Empty), "the car-less key is never reaped");
    }

    [TestMethod]
    public async Task ExecuteAsync_EventIdNotConfigured_NeverConnectsToRedis()
    {
        var harness = new Harness();
        using var service = Service(harness, new FakeControlLog(), eventId: 0);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        harness.Mux.Verify(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()), Times.Never);
    }
}
