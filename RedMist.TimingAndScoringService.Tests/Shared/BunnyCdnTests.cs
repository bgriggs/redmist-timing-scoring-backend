using RedMist.Backend.Shared.Utilities;
using System.Net;
using System.Text;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// Covers the CDN client's request shaping and, more importantly, its failure handling: every upload
/// here returns a bool that the archive pipeline branches on, so an error that is swallowed as a
/// success loses an event's logs silently.
/// </summary>
[TestClass]
public class BunnyCdnTests
{
    private const string StorageZone = StubBunnyCdn.StorageZone;

    private static BunnyCdn CreateCdn(StubHttpMessageHandler? storageTransport = null, IHttpClientFactory? apiClientFactory = null)
        => StubBunnyCdn.Create(storageTransport, apiClientFactory);

    private static MemoryStream Payload() => new(Encoding.UTF8.GetBytes("archive contents"));

    #region Cache purge

    [TestMethod]
    public async Task PurgeCacheAsync_PostsToThePullZoneWithTheAccountApiKey()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK);
        using var cdn = CreateCdn(apiClientFactory: handler.AsClientFactory());

        Assert.IsTrue(await cdn.PurgeCacheAsync("5008374"));

        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("https://api.bunny.net/pullzone/5008374/purgeCache", request.RequestUri!.ToString());
        Assert.AreEqual("api-key", request.Headers.GetValues("AccessKey").Single());
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.NotFound)]
    [DataRow(HttpStatusCode.InternalServerError)]
    public async Task PurgeCacheAsync_WhenTheApiRefuses_ReportsFailure(HttpStatusCode status)
    {
        using var cdn = CreateCdn(apiClientFactory: StubHttpMessageHandler.Returning(status).AsClientFactory());

        Assert.IsFalse(await cdn.PurgeCacheAsync("5008374"));
    }

    #endregion

    #region Uploads

    [TestMethod]
    public async Task UploadAsync_PutsTheStreamAtTheRequestedPath()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.Created);
        using var cdn = CreateCdn(storageTransport: handler);
        using var stream = Payload();

        Assert.IsTrue(await cdn.UploadAsync(stream, $"/{StorageZone}/event-logs/event-42.gz"));

        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Put, request.Method);
        Assert.EndsWith($"{StorageZone}/event-logs/event-42.gz", request.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// The archive service treats false as "retry later"; an upload the storage zone rejected must
    /// therefore not come back as a success.
    /// </summary>
    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.NotFound)]
    [DataRow(HttpStatusCode.InternalServerError)]
    public async Task UploadAsync_WhenStorageRejectsTheUpload_ReportsFailure(HttpStatusCode status)
    {
        using var cdn = CreateCdn(storageTransport: StubHttpMessageHandler.Returning(status));
        using var stream = Payload();

        Assert.IsFalse(await cdn.UploadAsync(stream, $"/{StorageZone}/event-logs/event-42.gz"));
    }

    /// <summary>
    /// Bunny paths have to start with the storage zone name; this is why every caller builds paths as
    /// "/{zone}/...". Getting it wrong fails before anything is sent rather than writing to the wrong
    /// place.
    /// </summary>
    [TestMethod]
    public async Task UploadAsync_WithAPathOutsideTheStorageZone_FailsWithoutSendingAnything()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.Created);
        using var cdn = CreateCdn(storageTransport: handler);
        using var stream = Payload();

        Assert.IsFalse(await cdn.UploadAsync(stream, "/some-other-zone/event-logs/event-42.gz"));
        Assert.IsEmpty(handler.Requests);
    }

    #endregion

    #region Destination cleanup

    private const string ListingJson = """
        [
          { "Guid": "1", "StorageZoneName": "redmist-test-zone", "Path": "/redmist-test-zone/logos/", "ObjectName": "org-1.img", "Length": 10, "IsDirectory": false },
          { "Guid": "2", "StorageZoneName": "redmist-test-zone", "Path": "/redmist-test-zone/logos/", "ObjectName": "nested", "Length": 0, "IsDirectory": true }
        ]
        """;

    [TestMethod]
    public async Task CleanDestinationAsync_DeletesEveryListedObjectAndMarksDirectoriesWithATrailingSlash()
    {
        var handler = new StubHttpMessageHandler(request => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ListingJson) }
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var cdn = CreateCdn(storageTransport: handler);

        Assert.AreEqual(0, await cdn.CleanDestinationAsync(2, $"/{StorageZone}/logos/"));

        var deleted = handler.Requests
            .Where(r => r.Method == HttpMethod.Delete)
            .Select(r => r.RequestUri!.AbsolutePath)
            .ToList();
        Assert.HasCount(2, deleted);
        Assert.Contains($"/{StorageZone}/logos/org-1.img", deleted);
        Assert.Contains($"/{StorageZone}/logos/nested/", deleted,
            "a directory delete without the trailing slash is treated as a file and does nothing");
    }

    /// <summary>
    /// A delete the storage zone refuses is reported as a warning rather than failing the clean; the
    /// caller's contract is "the destination has been swept", not "every object existed".
    /// </summary>
    [TestMethod]
    public async Task CleanDestinationAsync_WhenAnObjectCannotBeDeleted_StillCompletes()
    {
        var handler = new StubHttpMessageHandler(request => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ListingJson) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var cdn = CreateCdn(storageTransport: handler);

        Assert.AreEqual(0, await cdn.CleanDestinationAsync(2, $"/{StorageZone}/logos/"));
        Assert.HasCount(2, handler.Requests.Where(r => r.Method == HttpMethod.Delete).ToList());
    }

    [TestMethod]
    public async Task CleanDestinationAsync_WhenTheListingFails_ReportsAnError()
    {
        using var cdn = CreateCdn(storageTransport: StubHttpMessageHandler.Returning(HttpStatusCode.Unauthorized));

        Assert.AreEqual(2, await cdn.CleanDestinationAsync(2, $"/{StorageZone}/logos/"));
    }

    [TestMethod]
    public async Task CleanDestinationAsync_WithNothingToDelete_Succeeds()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        using var cdn = CreateCdn(storageTransport: handler);

        Assert.AreEqual(0, await cdn.CleanDestinationAsync(2, $"/{StorageZone}/logos/"));
        Assert.IsFalse(handler.Requests.Any(r => r.Method == HttpMethod.Delete));
    }

    #endregion
}
