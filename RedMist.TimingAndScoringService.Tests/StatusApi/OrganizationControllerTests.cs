using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.StatusApi.Controllers;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

/// <summary>
/// Covers OrganizationControllerBase: the relay-only filter on the organization list, the logo
/// fallback chain, and the magic-number sniffing that decides the Content-Type of the logo response.
/// </summary>
[TestClass]
public class OrganizationControllerTests
{
    private TsContext _db = null!;
    private IDbContextFactory<TsContext> _dbFactory = null!;
    private FakeHybridCache _cache = null!;
    private TestOrganizationController _controller = null!;

    /// <summary>Widens access to the protected static MIME sniffing helper.</summary>
    private sealed class TestOrganizationController : RedMist.StatusApi.Controllers.V1.OrganizationController
    {
        public TestOrganizationController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
            Microsoft.Extensions.Caching.Hybrid.HybridCache hcache)
            : base(loggerFactory, tsContext, hcache) { }

        public static string MimeTypeOf(byte[] bytes) => GetImageMimeType(bytes);
    }

    [TestInitialize]
    public void Setup()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbFactory = new TestDbContextFactory(options);
        _db = _dbFactory.CreateDbContext();
        _cache = new FakeHybridCache();

        _controller = new TestOrganizationController(loggerFactory.Object, _dbFactory, _cache)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [TestCleanup]
    public void Cleanup() => _db.Dispose();

    /// <summary>Seeds an organization with its own copy of the logo bytes, so nothing is shared across workers.</summary>
    private void AddOrganization(int id, string name, string clientId, byte[]? logo = null, string? website = null) =>
        _db.Organizations.Add(new Organization
        {
            Id = id,
            Name = name,
            ShortName = "TST",
            ClientId = clientId,
            Logo = logo is null ? null : [.. logo],
            Website = website,
        });

    // File signatures the controller sniffs. Read-only; callers copy before seeding.
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
    private static readonly byte[] GifBytes = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61];
    private static readonly byte[] BmpBytes = [0x42, 0x4D, 0x36, 0x00, 0x00, 0x00];

    #region GetOrganizations

    /// <summary>
    /// The list is the relay app's organization picker, so only organizations with a provisioned
    /// relay client belong in it - internal and service accounts must not leak into a public response.
    /// </summary>
    [TestMethod]
    public async Task GetOrganizations_ReturnsOnlyOrganizationsWithARelayClientId()
    {
        AddOrganization(1, "ChampCar", "relay-champcar", website: "https://champcar.example");
        AddOrganization(2, "Lucky Dog", "relay-luckydog");
        AddOrganization(3, "Internal Tools", "svc-internal");
        AddOrganization(4, "Legacy", "champcar-relay");
        await _db.SaveChangesAsync();

        var result = await _controller.GetOrganizations();

        var orgs = (List<OrganizationSummary>)((OkObjectResult)result.Result!).Value!;
        CollectionAssert.AreEquivalent(new[] { "ChampCar", "Lucky Dog" }, orgs.Select(o => o.Name).ToArray());
        Assert.AreEqual("https://champcar.example", orgs.Single(o => o.Name == "ChampCar").Website);
    }

    [TestMethod]
    public async Task GetOrganizations_SecondCall_ServedFromCache()
    {
        AddOrganization(1, "ChampCar", "relay-champcar");
        await _db.SaveChangesAsync();

        var first = (List<OrganizationSummary>)((OkObjectResult)(await _controller.GetOrganizations()).Result!).Value!;
        AddOrganization(2, "Added Later", "relay-later");
        await _db.SaveChangesAsync();
        var second = (List<OrganizationSummary>)((OkObjectResult)(await _controller.GetOrganizations()).Result!).Value!;

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(1, second.Count);
        Assert.AreEqual(1, _cache.FactoryInvocations.Count(k => k == "org-list"));
    }

    #endregion

    #region GetOrganizationIcon

    [TestMethod]
    public async Task GetOrganizationIcon_OrganizationWithLogo_ReturnsItWithSniffedContentType()
    {
        AddOrganization(1, "ChampCar", "relay-champcar", logo: PngBytes);
        await _db.SaveChangesAsync();

        var result = await _controller.GetOrganizationIcon(1);

        var file = result as FileContentResult;
        Assert.IsNotNull(file);
        Assert.AreEqual("image/png", file.ContentType);
        CollectionAssert.AreEqual(PngBytes, file.FileContents);
    }

    /// <summary>
    /// An organization that has not uploaded a logo gets the platform default, so listings never show
    /// a broken image.
    /// </summary>
    [TestMethod]
    public async Task GetOrganizationIcon_OrganizationWithoutLogo_FallsBackToTheDefaultImage()
    {
        AddOrganization(1, "No Logo", "relay-nologo", logo: null);
        _db.DefaultOrgImages.Add(new DefaultOrgImage { ImageData = [.. JpegBytes] });
        await _db.SaveChangesAsync();

        var result = await _controller.GetOrganizationIcon(1);

        var file = result as FileContentResult;
        Assert.IsNotNull(file);
        Assert.AreEqual("image/jpeg", file.ContentType);
        CollectionAssert.AreEqual(JpegBytes, file.FileContents);
    }

    /// <summary>
    /// An unknown organization also takes the default-image path rather than 404ing, because the
    /// lookup returns an empty array rather than null.
    /// </summary>
    [TestMethod]
    public async Task GetOrganizationIcon_UnknownOrganization_FallsBackToTheDefaultImage()
    {
        _db.DefaultOrgImages.Add(new DefaultOrgImage { ImageData = [.. GifBytes] });
        await _db.SaveChangesAsync();

        var result = await _controller.GetOrganizationIcon(999);

        var file = result as FileContentResult;
        Assert.IsNotNull(file);
        Assert.AreEqual("image/gif", file.ContentType);
    }

    /// <summary>
    /// With neither a logo nor a default image the endpoint still answers 200 with a zero-byte body,
    /// not 404: the loader returns an empty array, and the null check guarding NotFound never fires.
    /// </summary>
    [TestMethod]
    public async Task GetOrganizationIcon_NoLogoAndNoDefaultImage_ReturnsEmptyOctetStreamRatherThanNotFound()
    {
        AddOrganization(1, "No Logo", "relay-nologo", logo: null);
        await _db.SaveChangesAsync();

        var result = await _controller.GetOrganizationIcon(1);

        var file = result as FileContentResult;
        Assert.IsNotNull(file);
        Assert.AreEqual(0, file.FileContents.Length);
        Assert.AreEqual("application/octet-stream", file.ContentType);
    }

    /// <summary>Two organizations served by one process must not share a cached icon.</summary>
    [TestMethod]
    public async Task GetOrganizationIcon_CachesPerOrganization()
    {
        AddOrganization(1, "Png Org", "relay-a", logo: PngBytes);
        AddOrganization(2, "Bmp Org", "relay-b", logo: BmpBytes);
        await _db.SaveChangesAsync();

        var first = (FileContentResult)await _controller.GetOrganizationIcon(1);
        var second = (FileContentResult)await _controller.GetOrganizationIcon(2);

        Assert.AreEqual("image/png", first.ContentType);
        Assert.AreEqual("image/bmp", second.ContentType);
        Assert.Contains("org-icon-1", _cache.FactoryInvocations);
        Assert.Contains("org-icon-2", _cache.FactoryInvocations);
    }

    #endregion

    #region GetImageMimeType

    // The four recognized signatures (PNG, JPEG, GIF, BMP) are each asserted end-to-end through
    // GetOrganizationIcon above, so only the fallbacks are exercised directly here.

    [TestMethod]
    public void GetImageMimeType_UnrecognizedSignature_FallsBackToOctetStream() =>
        Assert.AreEqual("application/octet-stream", TestOrganizationController.MimeTypeOf([0x00, 0x01, 0x02, 0x03]));

    /// <summary>Anything under four bytes must not be indexed into; it short-circuits to the fallback.</summary>
    [TestMethod]
    public void GetImageMimeType_TooShortToSniff_FallsBackToOctetStream()
    {
        Assert.AreEqual("application/octet-stream", TestOrganizationController.MimeTypeOf([]));
        Assert.AreEqual("application/octet-stream", TestOrganizationController.MimeTypeOf([0xFF, 0xD8, 0xFF]));
    }

    #endregion
}
