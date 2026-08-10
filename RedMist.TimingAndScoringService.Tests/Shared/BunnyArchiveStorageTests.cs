using BigMission.TestHelpers.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RedMist.Backend.Shared.Utilities;
using System.Net;
using System.Text;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// The archive layout is a contract with everything that reads archives back, and a car number comes
/// straight from a timing system, so these pin the destination paths and the sanitizing that keeps a
/// car number from turning into a directory or a URL fragment.
/// </summary>
[TestClass]
public class BunnyArchiveStorageTests
{
    private const string StorageZone = StubBunnyCdn.StorageZone;

    private static readonly Dictionary<string, string?> ArchiveSettings = new()
    {
        ["Archive:StorageZoneName"] = StorageZone,
        ["Archive:StorageAccessKey"] = "storage-key",
        ["Archive:MainReplicationRegion"] = "de",
        ["Archive:ApiAccessKey"] = "api-key",
        ["Archive:CdnId"] = "5008374",
    };

    private static IConfiguration Configuration(IDictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    /// <summary>Replaces the CDN client with one whose transport is a stub.</summary>
    private sealed class TestableArchiveStorage(IConfiguration configuration, ILoggerFactory loggerFactory, StubHttpMessageHandler transport)
        : BunnyArchiveStorage(configuration, loggerFactory, transport.AsClientFactory())
    {
        protected override BunnyCdn CreateCdnClient() => StubBunnyCdn.Create(transport);
    }

    private static BunnyArchiveStorage CreateStorage(StubHttpMessageHandler transport)
        => new TestableArchiveStorage(Configuration(ArchiveSettings), new DebugLoggerFactory(), transport);

    private static MemoryStream Payload() => new(Encoding.UTF8.GetBytes("archive contents"));

    private static List<string> UploadedPaths(StubHttpMessageHandler transport)
        => [.. transport.Requests
            .Where(r => r.Method == HttpMethod.Put)
            .Select(r => Uri.UnescapeDataString(r.RequestUri!.AbsolutePath))];

    /// <summary>
    /// One test for the whole layout: each archive kind has to keep its own directory, or a restore
    /// reads the wrong file, and the paths only ever change deliberately.
    /// </summary>
    [TestMethod]
    public async Task EveryArchiveKind_WritesToItsOwnPath()
    {
        var transport = StubHttpMessageHandler.Returning(HttpStatusCode.Created);
        var storage = CreateStorage(transport);

        Assert.IsTrue(await storage.UploadEventLogsAsync(Payload(), 42));
        Assert.IsTrue(await storage.UploadSessionLogsAsync(Payload(), 42, 7));
        Assert.IsTrue(await storage.UploadSessionLapsAsync(Payload(), 42, 7));
        Assert.IsTrue(await storage.UploadSessionCarLapsAsync(Payload(), 42, 7, "99"));
        Assert.IsTrue(await storage.UploadEventX2PassingsAsync(Payload(), 42));
        Assert.IsTrue(await storage.UploadEventX2LoopsAsync(Payload(), 42));
        Assert.IsTrue(await storage.UploadSessionFlagsAsync(Payload(), 42, 7));
        Assert.IsTrue(await storage.UploadEventCompetitorMetadataAsync(Payload(), 42));

        CollectionAssert.AreEqual(new[]
        {
            $"/{StorageZone}/event-logs/event-42.gz",
            $"/{StorageZone}/event-logs/sessions-42/session-7.gz",
            $"/{StorageZone}/event-laps/event-42-session-7-laps.gz",
            $"/{StorageZone}/event-laps/event-42-session-7-car-laps/car-99-laps.gz",
            $"/{StorageZone}/event-passings/event-42-passings.gz",
            $"/{StorageZone}/event-loops/event-42-loops.gz",
            $"/{StorageZone}/event-flags/event-42-session-7-flags.gz",
            $"/{StorageZone}/event-competitor-metadata/event-42-competitor-metadata.gz",
        }, UploadedPaths(transport));
    }

    /// <summary>
    /// Car numbers come from the timing system and are not URL safe. A '#' would start a URL fragment
    /// and truncate the object name; a '/' would silently create a directory.
    /// </summary>
    [TestMethod]
    [DataRow("99", "99")]
    [DataRow("#42", "No42")]
    [DataRow("42#", "42No")]
    [DataRow("A/B", "A_B")]
    [DataRow("#7/x", "No7_x")]
    [DataRow("car-1_2", "car-1_2")]
    [DataRow("12 A", "12 A")]
    [DataRow("?*%", "___")]
    public async Task UploadSessionCarLapsAsync_SanitizesTheCarNumberIntoThePath(string carNumber, string expected)
    {
        var transport = StubHttpMessageHandler.Returning(HttpStatusCode.Created);
        var storage = CreateStorage(transport);

        Assert.IsTrue(await storage.UploadSessionCarLapsAsync(Payload(), 42, 7, carNumber));

        Assert.AreEqual($"/{StorageZone}/event-laps/event-42-session-7-car-laps/car-{expected}-laps.gz",
            UploadedPaths(transport).Single());
    }

    /// <summary>
    /// The archive service retries on false, so a rejected upload must not be reported as archived.
    /// </summary>
    [TestMethod]
    public async Task Uploads_WhenTheCdnRejectsThem_ReportFailure()
    {
        var storage = CreateStorage(StubHttpMessageHandler.Returning(HttpStatusCode.InternalServerError));

        Assert.IsFalse(await storage.UploadEventLogsAsync(Payload(), 42));
        Assert.IsFalse(await storage.UploadSessionLogsAsync(Payload(), 42, 7));
        Assert.IsFalse(await storage.UploadSessionLapsAsync(Payload(), 42, 7));
        Assert.IsFalse(await storage.UploadSessionCarLapsAsync(Payload(), 42, 7, "99"));
        Assert.IsFalse(await storage.UploadEventX2PassingsAsync(Payload(), 42));
        Assert.IsFalse(await storage.UploadEventX2LoopsAsync(Payload(), 42));
        Assert.IsFalse(await storage.UploadSessionFlagsAsync(Payload(), 42, 7));
        Assert.IsFalse(await storage.UploadEventCompetitorMetadataAsync(Payload(), 42));
    }

    /// <summary>
    /// Missing CDN settings have to stop the service at startup; discovering them at archive time
    /// means the logs are already gone.
    /// </summary>
    [TestMethod]
    [DataRow("Archive:StorageZoneName")]
    [DataRow("Archive:StorageAccessKey")]
    [DataRow("Archive:MainReplicationRegion")]
    [DataRow("Archive:ApiAccessKey")]
    [DataRow("Archive:CdnId")]
    public void Constructor_WithAMissingSetting_Throws(string missingKey)
    {
        var settings = new Dictionary<string, string?>(ArchiveSettings);
        settings.Remove(missingKey);

        Assert.Throws<ArgumentNullException>(() => new BunnyArchiveStorage(
            Configuration(settings), new DebugLoggerFactory(), StubHttpMessageHandler.Returning(HttpStatusCode.OK).AsClientFactory()));
    }

    [TestMethod]
    public void Constructor_WithEveryStorageSetting_Succeeds()
    {
        var storage = new BunnyArchiveStorage(Configuration(ArchiveSettings), new DebugLoggerFactory(),
            StubHttpMessageHandler.Returning(HttpStatusCode.OK).AsClientFactory());

        Assert.IsInstanceOfType<IArchiveStorage>(storage);
    }
}
