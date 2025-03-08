using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using RedMist.Database;
using RedMist.TimingAndScoringService.Models;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests;

[TestClass]
public sealed class EventDistributionTests
{
    private IConfiguration configuration;
    private readonly DebugLoggerFactory lf;
    private readonly Mock<IDbContextFactory<TsContext>> dbContextFactoryMock;

    public EventDistributionTests()
    {
        var configValues = new Dictionary<string, string?> { { "POD_NAME", "test" } };
        configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();
        lf = new DebugLoggerFactory();
        dbContextFactoryMock = new Mock<IDbContextFactory<TsContext>>();
    }

    [TestMethod]
    public async Task Start_InitializePodWorkload_Single()
    {
        var hcache = new HybridCacheShell();
        var mux = new RedisConnectionMultiplexerShell();
        var disLock = new DistributedLockFactoryShell();
        var eventDist = new EventDistribution(hcache, mux, lf, configuration, disLock, dbContextFactoryMock.Object);

        await eventDist.StartAsync(default);
        var cache = mux.GetDatabase();
        Assert.IsTrue(cache.KeyExists(Consts.POD_WORKLOADS));
    }

    [TestMethod]
    public async Task Start_InitializePodWorkload_MultiplePods()
    {
        var hcache = new HybridCacheShell();
        var mux = new RedisConnectionMultiplexerShell();
        var disLock = new DistributedLockFactoryShell();
        var eventDist = new EventDistribution(hcache, mux, lf, configuration, disLock, dbContextFactoryMock.Object);

        await eventDist.StartAsync(default);

        var configValues = new Dictionary<string, string?> { { "POD_NAME", "test2" } };
        configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
        eventDist = new EventDistribution(hcache, mux, lf, configuration, disLock, dbContextFactoryMock.Object);
        await eventDist.StartAsync(default);

        var cache = mux.GetDatabase();
        var pwJson = await cache.StringGetAsync(Consts.POD_WORKLOADS);
        var pw = JsonSerializer.Deserialize<List<EventProcessorInstance>>(pwJson!);
        Assert.AreEqual(2, pw!.Count);
    }

    [TestMethod]
    public async Task GetStream_Single()
    {
        var hcache = new HybridCacheShell();
        var mux = new RedisConnectionMultiplexerShell();
        var disLock = new DistributedLockFactoryShell();
        var eventDist = new EventDistribution(hcache, mux, lf, configuration, disLock, dbContextFactoryMock.Object);

        await eventDist.StartAsync(default);
        var stream = await eventDist.GetStreamAsync("test-event", default);

        var expectedKey = string.Format(Consts.EVENT_STATUS_STREAM_KEY, configuration["POD_NAME"]);
        Assert.AreEqual(expectedKey, stream);

        var cache = mux.GetDatabase();
        var pwJson = await cache.StringGetAsync(Consts.POD_WORKLOADS);
        var pw = JsonSerializer.Deserialize<List<EventProcessorInstance>>(pwJson!);
        Assert.AreEqual("test-event", pw![0].Events[0]);
    }

    [TestMethod]
    public async Task GetStream_MultiplePods_BalanceEvenly()
    {
        var hcache = new HybridCacheShell();
        var mux = new RedisConnectionMultiplexerShell();
        var disLock = new DistributedLockFactoryShell();
        var eventDist = new EventDistribution(hcache, mux, lf, configuration, disLock, dbContextFactoryMock.Object);

        await eventDist.StartAsync(default);

        var configValues = new Dictionary<string, string?> { { "POD_NAME", "test2" } };
        configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
        eventDist = new EventDistribution(hcache, mux, lf, configuration, disLock, dbContextFactoryMock.Object);
        await eventDist.StartAsync(default);

        await eventDist.GetStreamAsync("test-event1", default);
        await eventDist.GetStreamAsync("test-event2", default);

        var cache = mux.GetDatabase();
        var pwJson = await cache.StringGetAsync(Consts.POD_WORKLOADS);
        var pw = JsonSerializer.Deserialize<List<EventProcessorInstance>>(pwJson!);
        Assert.AreEqual(1, pw![0].Events.Count);
        Assert.AreEqual(1, pw![1].Events.Count);

        await eventDist.GetStreamAsync("test-event3", default);
        await eventDist.GetStreamAsync("test-event4", default);

        pwJson = await cache.StringGetAsync(Consts.POD_WORKLOADS);
        pw = JsonSerializer.Deserialize<List<EventProcessorInstance>>(pwJson!);
        Assert.AreEqual(2, pw![0].Events.Count);
        Assert.AreEqual(2, pw![1].Events.Count);
    }

    [TestMethod]
    [ExpectedException(typeof(InvalidOperationException))]
    public async Task GetStream_None_Error()
    {
        var hcache = new HybridCacheShell();
        var mux = new RedisConnectionMultiplexerShell();
        var disLock = new DistributedLockFactoryShell();
        var eventDist = new EventDistribution(hcache, mux, lf, configuration, disLock, dbContextFactoryMock.Object);

        await eventDist.GetStreamAsync("test-event", default);
    }
}
