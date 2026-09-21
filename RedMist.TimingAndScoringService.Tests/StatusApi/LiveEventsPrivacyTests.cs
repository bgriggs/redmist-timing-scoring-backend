using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.StatusApi.Controllers;
using StackExchange.Redis;
using ConfigEvent = RedMist.TimingCommon.Models.Configuration.Event;
using Organization = RedMist.TimingCommon.Models.Organization;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

/// <summary>
/// What the public live-events list says about private events.
/// </summary>
/// <remarks>
/// <para>
/// It CARRIES the flag rather than filtering the row out, and that distinction is load-bearing in
/// two directions. The public embed hides a private event by honoring the flag; an organizer's own
/// dashboard shows their own private event by reading the same row. If this list ever started
/// filtering private events out server-side, the embed would keep working and the dashboard would
/// go quiet - with nothing failing, because a consumer cannot tell "filtered out" from "not live".
/// </para>
/// <para>
/// Pinned here rather than in a client, because a client test can only prove it parses what it was
/// given. This is the only place the behavior itself can be asserted.
/// </para>
/// </remarks>
[TestClass]
public class LiveEventsPrivacyTests
{
    private IDbContextFactory<TsContext> dbFactory = null!;
    private TsContext db = null!;

    [TestInitialize]
    public void Setup()
    {
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db = dbFactory.CreateDbContext();
    }

    [TestCleanup]
    public void Cleanup() => db.Dispose();

    private TestEventsController CreateController()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var cache = services.BuildServiceProvider().GetRequiredService<HybridCache>();

        return new TestEventsController(NullLoggerFactory.Instance, dbFactory, cache,
            new Mock<IConnectionMultiplexer>().Object, new MemoryCache(new MemoryCacheOptions()),
            new Mock<IHttpClientFactory>().Object);
    }

    private void Seed(int id, string name, bool isPrivate = false, bool hideName = false, bool isLive = true)
    {
        if (!db.Organizations.Any(o => o.Id == 7))
        {
            db.Organizations.Add(new Organization { Id = 7, ClientId = "relay-x", Name = "Org", ShortName = "O" });
        }
        db.Events.Add(new ConfigEvent
        {
            Id = id,
            OrganizationId = 7,
            Name = name,
            StartDate = new DateTime(2026, 9, 1),
            EndDate = new DateTime(2026, 9, 2),
            IsActive = true,
            IsLive = isLive,
            IsPrivate = isPrivate,
            HideName = hideName,
        });
        db.SaveChanges();
    }

    [TestMethod]
    public async Task APrivateLiveEvent_IsListedAndFlagged_NotFilteredOut()
    {
        Seed(10, "Club Test Day", isPrivate: true);

        var live = await CreateController().LoadLiveEvents();

        var evt = live.SingleOrDefault(e => e.Id == 10);
        Assert.IsNotNull(evt, "A private live event was filtered out of the list rather than flagged.");
        Assert.IsTrue(evt.IsPrivate, "The private flag was not carried, so no consumer can honor it.");
        Assert.AreEqual("Club Test Day", evt.EventName);
    }

    [TestMethod]
    public async Task APublicAndAPrivateEvent_BothAppear()
    {
        Seed(10, "Open Practice");
        Seed(11, "Club Test Day", isPrivate: true);

        var live = await CreateController().LoadLiveEvents();

        CollectionAssert.AreEquivalent(new[] { 10, 11 }, live.Select(e => e.Id).ToArray());
    }

    /// <summary>
    /// Hiding the name is done by blanking it, not by dropping the row, and it is independent of
    /// privacy. A consumer that shows the name has to handle an empty one.
    /// </summary>
    [TestMethod]
    public async Task AHiddenNameEventIsListedWithAnEmptyName()
    {
        Seed(10, "Secret Shakedown", hideName: true);

        var evt = (await CreateController().LoadLiveEvents()).Single(e => e.Id == 10);

        Assert.AreEqual(string.Empty, evt.EventName);
        Assert.IsTrue(evt.HideName);
    }

    [TestMethod]
    public async Task AnEventThatIsNotLive_IsNotListed()
    {
        Seed(10, "Finished", isLive: false);

        Assert.IsEmpty(await CreateController().LoadLiveEvents());
    }

    private sealed class TestEventsController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
        HybridCache hcache, IConnectionMultiplexer cacheMux, IMemoryCache memoryCache,
        IHttpClientFactory httpClientFactory)
        : EventsControllerBase(loggerFactory, tsContext, hcache, cacheMux, memoryCache, httpClientFactory);
}
