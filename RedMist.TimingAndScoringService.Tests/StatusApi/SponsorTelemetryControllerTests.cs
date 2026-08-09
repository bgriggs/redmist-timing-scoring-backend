using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.StatusApi.Controllers.V1;
using RedMist.StatusApi.Services;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

[TestClass]
public class SponsorTelemetryControllerTests
{
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger> _mockLogger = null!;
    private SponsorTelemetryQueue _queue = null!;
    private IDbContextFactory<TsContext> _dbContextFactory = null!;
    private TsContext _dbContext = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger>();
        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);

        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContextFactory = new TestDbContextFactory(options);
        _dbContext = _dbContextFactory.CreateDbContext();
        _queue = new SponsorTelemetryQueue(_mockLoggerFactory.Object, _dbContextFactory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext?.Dispose();
    }

    private SponsorTelemetryController CreateController(HybridCache? hybridCache = null)
    {
        hybridCache ??= new FakeHybridCache();
        return new SponsorTelemetryController(_mockLoggerFactory.Object, _queue, _dbContextFactory, hybridCache);
    }

    /// <summary>
    /// Always invokes the factory directly, allowing tests to exercise the real query and filtering logic
    /// without requiring a running cache infrastructure.
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

    #region Save Telemetry Tests

    [TestMethod]
    public void SaveImpression_ReturnsOk()
    {
        var result = CreateController().SaveImpression("source1", "img1", "event1");
        Assert.IsInstanceOfType<OkResult>(result);
    }

    [TestMethod]
    public void SaveViewableImpression_ReturnsOk()
    {
        var result = CreateController().SaveViewableImpression("source1", "img1", "event1");
        Assert.IsInstanceOfType<OkResult>(result);
    }

    [TestMethod]
    public void SaveClickThrough_ReturnsOk()
    {
        var result = CreateController().SaveClickThrough("source1", "img1", "event1");
        Assert.IsInstanceOfType<OkResult>(result);
    }

    [TestMethod]
    public void SaveEngagementDuration_ReturnsOk()
    {
        var result = CreateController().SaveEngagementDuration("source1", "img1", 5000, "event1");
        Assert.IsInstanceOfType<OkResult>(result);
    }

    #endregion

    #region GetSponsorsAsync Tests

    [TestMethod]
    public async Task GetSponsorsAsync_ActiveSubscription_ReturnsSponsor()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await _dbContext.Sponsors.AddAsync(new Sponsor
        {
            Name = "Active Sponsor",
            ImageUrl = "https://example.com/img.png",
            TargetUrl = "https://example.com",
            SubscriptionStart = today.AddDays(-10),
            SubscriptionEnd = today.AddDays(10)
        });
        await _dbContext.SaveChangesAsync();

        var result = await CreateController(new PassThroughHybridCache()).GetSponsorsAsync();

        var okResult = (OkObjectResult)result.Result!;
        var sponsors = (List<SponsorInfo>)okResult.Value!;
        Assert.AreEqual(1, sponsors.Count);
        Assert.AreEqual("Active Sponsor", sponsors[0].Name);
    }

    [TestMethod]
    public async Task GetSponsorsAsync_NullEndDate_ReturnsSponsor()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await _dbContext.Sponsors.AddAsync(new Sponsor
        {
            Name = "Open-Ended Sponsor",
            ImageUrl = "https://example.com/img.png",
            TargetUrl = "https://example.com",
            SubscriptionStart = today.AddDays(-30),
            SubscriptionEnd = null
        });
        await _dbContext.SaveChangesAsync();

        var result = await CreateController(new PassThroughHybridCache()).GetSponsorsAsync();

        var okResult = (OkObjectResult)result.Result!;
        var sponsors = (List<SponsorInfo>)okResult.Value!;
        Assert.AreEqual(1, sponsors.Count);
        Assert.AreEqual("Open-Ended Sponsor", sponsors[0].Name);
    }

    [TestMethod]
    public async Task GetSponsorsAsync_ExpiredSubscription_ExcludesSponsor()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await _dbContext.Sponsors.AddAsync(new Sponsor
        {
            Name = "Expired Sponsor",
            ImageUrl = "https://example.com/img.png",
            TargetUrl = "https://example.com",
            SubscriptionStart = today.AddDays(-30),
            SubscriptionEnd = today.AddDays(-1)
        });
        await _dbContext.SaveChangesAsync();

        var result = await CreateController(new PassThroughHybridCache()).GetSponsorsAsync();

        var okResult = (OkObjectResult)result.Result!;
        var sponsors = (List<SponsorInfo>)okResult.Value!;
        Assert.AreEqual(0, sponsors.Count);
    }

    [TestMethod]
    public async Task GetSponsorsAsync_FutureSubscription_ExcludesSponsor()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await _dbContext.Sponsors.AddAsync(new Sponsor
        {
            Name = "Future Sponsor",
            ImageUrl = "https://example.com/img.png",
            TargetUrl = "https://example.com",
            SubscriptionStart = today.AddDays(5),
            SubscriptionEnd = today.AddDays(30)
        });
        await _dbContext.SaveChangesAsync();

        var result = await CreateController(new PassThroughHybridCache()).GetSponsorsAsync();

        var okResult = (OkObjectResult)result.Result!;
        var sponsors = (List<SponsorInfo>)okResult.Value!;
        Assert.AreEqual(0, sponsors.Count);
    }

    [TestMethod]
    public async Task GetSponsorsAsync_MixedSubscriptions_ReturnsOnlyActiveSponsors()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await _dbContext.Sponsors.AddRangeAsync(
            new Sponsor { Name = "Active", ImageUrl = "https://a.com/1.png", TargetUrl = "https://a.com", SubscriptionStart = today.AddDays(-5), SubscriptionEnd = today.AddDays(5) },
            new Sponsor { Name = "Open-Ended", ImageUrl = "https://a.com/2.png", TargetUrl = "https://b.com", SubscriptionStart = today.AddDays(-1), SubscriptionEnd = null },
            new Sponsor { Name = "Expired", ImageUrl = "https://a.com/3.png", TargetUrl = "https://c.com", SubscriptionStart = today.AddDays(-10), SubscriptionEnd = today.AddDays(-1) },
            new Sponsor { Name = "Future", ImageUrl = "https://a.com/4.png", TargetUrl = "https://d.com", SubscriptionStart = today.AddDays(5), SubscriptionEnd = today.AddDays(20) }
        );
        await _dbContext.SaveChangesAsync();

        var result = await CreateController(new PassThroughHybridCache()).GetSponsorsAsync();

        var okResult = (OkObjectResult)result.Result!;
        var sponsors = (List<SponsorInfo>)okResult.Value!;
        Assert.AreEqual(2, sponsors.Count);
        CollectionAssert.AreEquivalent(
            new[] { "Active", "Open-Ended" },
            sponsors.Select(s => s.Name).ToArray());
    }

    [TestMethod]
    public async Task GetSponsorsAsync_MapsRequiredFields()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await _dbContext.Sponsors.AddAsync(new Sponsor
        {
            Name = "Mapped Sponsor",
            ImageUrl = "https://example.com/img.png",
            TargetUrl = "https://example.com/target",
            AltText = "Sponsor alt text",
            DisplayDurationMs = 3000,
            DisplayPriority = 5,
            ContactName = "John Doe",
            ContactEmail = "john@example.com",
            SubscriptionStart = today.AddDays(-1),
            SubscriptionEnd = null
        });
        await _dbContext.SaveChangesAsync();

        var result = await CreateController(new PassThroughHybridCache()).GetSponsorsAsync();

        var okResult = (OkObjectResult)result.Result!;
        var sponsor = ((List<SponsorInfo>)okResult.Value!).Single();
        Assert.AreEqual("Mapped Sponsor", sponsor.Name);
        Assert.AreEqual("https://example.com/img.png", sponsor.ImageUrl);
        Assert.AreEqual("https://example.com/target", sponsor.TargetUrl);
        Assert.AreEqual("Sponsor alt text", sponsor.AltText);
        Assert.AreEqual(3000, sponsor.DisplayDurationMs);
        Assert.AreEqual(5, sponsor.DisplayPriority);
    }

    #endregion

    #region Sponsor Exclusion Tests

    /// <summary>
    /// Adds three active sponsors (ids assigned by the in-memory store) and an event owned by
    /// <paramref name="organizationId"/>, returning the sponsor ids in insertion order.
    /// </summary>
    private async Task<(int eventId, int[] sponsorIds)> SeedSponsorsAndEventAsync(int organizationId)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var sponsors = new[]
        {
            new Sponsor { Name = "ChampCar", ImageUrl = "https://a.com/1.png", TargetUrl = "https://a.com", SubscriptionStart = today.AddDays(-5), SubscriptionEnd = null },
            new Sponsor { Name = "Lucky Dog", ImageUrl = "https://a.com/2.png", TargetUrl = "https://b.com", SubscriptionStart = today.AddDays(-5), SubscriptionEnd = null },
            new Sponsor { Name = "WRL", ImageUrl = "https://a.com/3.png", TargetUrl = "https://c.com", SubscriptionStart = today.AddDays(-5), SubscriptionEnd = null },
        };
        await _dbContext.Sponsors.AddRangeAsync(sponsors);

        var evt = new RedMist.TimingCommon.Models.Configuration.Event { OrganizationId = organizationId, Name = "Test Event" };
        await _dbContext.Events.AddAsync(evt);
        await _dbContext.SaveChangesAsync();

        return (evt.Id, sponsors.Select(s => s.Id).ToArray());
    }

    private static List<SponsorInfo> Sponsors(ActionResult<List<SponsorInfo>> result)
        => (List<SponsorInfo>)((OkObjectResult)result.Result!).Value!;

    [TestMethod]
    public async Task GetSponsorsAsync_EventOrgHasExclusions_RemovesExcludedSponsors()
    {
        var (eventId, sponsorIds) = await SeedSponsorsAndEventAsync(organizationId: 5);
        await _dbContext.SponsorExclusions.AddRangeAsync(
            new SponsorExclusion { OrganizationId = 5, SponsorId = sponsorIds[1] },
            new SponsorExclusion { OrganizationId = 5, SponsorId = sponsorIds[2] });
        await _dbContext.SaveChangesAsync();

        var result = await CreateController(new PassThroughHybridCache()).GetSponsorsAsync(eventId);

        var sponsors = Sponsors(result);
        Assert.AreEqual(1, sponsors.Count);
        Assert.AreEqual("ChampCar", sponsors[0].Name);
    }

    [TestMethod]
    public async Task GetSponsorsAsync_NoEventId_IgnoresExclusions()
    {
        var (_, sponsorIds) = await SeedSponsorsAndEventAsync(organizationId: 5);
        await _dbContext.SponsorExclusions.AddAsync(new SponsorExclusion { OrganizationId = 5, SponsorId = sponsorIds[1] });
        await _dbContext.SaveChangesAsync();

        var result = await CreateController(new PassThroughHybridCache()).GetSponsorsAsync();

        Assert.AreEqual(3, Sponsors(result).Count);
    }

    [TestMethod]
    public async Task GetSponsorsAsync_ExclusionsBelongToAnotherOrg_ReturnsAllSponsors()
    {
        var (eventId, sponsorIds) = await SeedSponsorsAndEventAsync(organizationId: 5);
        await _dbContext.SponsorExclusions.AddAsync(new SponsorExclusion { OrganizationId = 6, SponsorId = sponsorIds[1] });
        await _dbContext.SaveChangesAsync();

        var result = await CreateController(new PassThroughHybridCache()).GetSponsorsAsync(eventId);

        Assert.AreEqual(3, Sponsors(result).Count);
    }

    [TestMethod]
    public async Task GetSponsorsAsync_UnknownEventId_ReturnsAllSponsors()
    {
        var (eventId, sponsorIds) = await SeedSponsorsAndEventAsync(organizationId: 5);
        await _dbContext.SponsorExclusions.AddAsync(new SponsorExclusion { OrganizationId = 5, SponsorId = sponsorIds[1] });
        await _dbContext.SaveChangesAsync();

        var result = await CreateController(new PassThroughHybridCache()).GetSponsorsAsync(eventId + 1000);

        Assert.AreEqual(3, Sponsors(result).Count);
    }

    [TestMethod]
    public async Task GetSponsorsAsync_ExcludedSponsorIsAlreadyExpired_StillReturnsRemainingSponsors()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var (eventId, sponsorIds) = await SeedSponsorsAndEventAsync(organizationId: 5);
        var expired = new Sponsor
        {
            Name = "Expired",
            ImageUrl = "https://a.com/4.png",
            TargetUrl = "https://d.com",
            SubscriptionStart = today.AddDays(-30),
            SubscriptionEnd = today.AddDays(-1)
        };
        await _dbContext.Sponsors.AddAsync(expired);
        await _dbContext.SaveChangesAsync();
        await _dbContext.SponsorExclusions.AddRangeAsync(
            new SponsorExclusion { OrganizationId = 5, SponsorId = expired.Id },
            new SponsorExclusion { OrganizationId = 5, SponsorId = sponsorIds[2] });
        await _dbContext.SaveChangesAsync();

        var result = await CreateController(new PassThroughHybridCache()).GetSponsorsAsync(eventId);

        CollectionAssert.AreEquivalent(
            new[] { "ChampCar", "Lucky Dog" },
            Sponsors(result).Select(s => s.Name).ToArray());
    }

    /// <summary>
    /// Exclusions must be cached per event, not globally: two events belonging to different
    /// organizations, served by one cache, each get their own organization's filtering.
    /// </summary>
    [TestMethod]
    public async Task GetSponsorsAsync_TwoEventsDifferentOrgs_EachGetsItsOwnExclusions()
    {
        var (champCarEventId, sponsorIds) = await SeedSponsorsAndEventAsync(organizationId: 5);
        var luckyDogEvent = new RedMist.TimingCommon.Models.Configuration.Event { OrganizationId = 6, Name = "Other Event" };
        await _dbContext.Events.AddAsync(luckyDogEvent);
        await _dbContext.SponsorExclusions.AddRangeAsync(
            new SponsorExclusion { OrganizationId = 5, SponsorId = sponsorIds[1] },
            new SponsorExclusion { OrganizationId = 6, SponsorId = sponsorIds[0] });
        await _dbContext.SaveChangesAsync();

        var controller = CreateController(new FakeHybridCache());
        var champCar = await controller.GetSponsorsAsync(champCarEventId);
        var luckyDog = await controller.GetSponsorsAsync(luckyDogEvent.Id);

        CollectionAssert.AreEquivalent(new[] { "ChampCar", "WRL" }, Sponsors(champCar).Select(s => s.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { "Lucky Dog", "WRL" }, Sponsors(luckyDog).Select(s => s.Name).ToArray());
    }

    /// <summary>
    /// Filtering must not mutate the cached sponsor list: an event with exclusions is served first,
    /// then a request with no event must still see every sponsor.
    /// </summary>
    [TestMethod]
    public async Task GetSponsorsAsync_FilteringDoesNotMutateCachedList()
    {
        var (eventId, sponsorIds) = await SeedSponsorsAndEventAsync(organizationId: 5);
        await _dbContext.SponsorExclusions.AddAsync(new SponsorExclusion { OrganizationId = 5, SponsorId = sponsorIds[1] });
        await _dbContext.SaveChangesAsync();

        var controller = CreateController(new FakeHybridCache());
        var filtered = await controller.GetSponsorsAsync(eventId);
        var unfiltered = await controller.GetSponsorsAsync();

        Assert.AreEqual(2, Sponsors(filtered).Count);
        Assert.AreEqual(3, Sponsors(unfiltered).Count);
    }

    #endregion
}
