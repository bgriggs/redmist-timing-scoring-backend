using Microsoft.Extensions.Configuration;
using RedMist.ExternalDataCollection.Clients;
using System.Net;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.ExternalDataCollection.Clients;

/// <summary>
/// Exercises the Sentinel HTTP wrapper against a stubbed transport: request shape, the mapping of the
/// feed's JSON field names onto <c>PublicStreams</c>, and what an unhappy upstream does to the caller.
/// </summary>
[TestClass]
public class SentinelClientTests
{
    private const string TwoStreamsJson = """
        [
          {
            "youtube_public_url": "https://youtu.be/abc",
            "svnPublicURL": "srt://svn.test/live/1",
            "transponderNumber": "1329228",
            "driverName": "Alice Smith"
          },
          {
            "youtube_public_url": "",
            "svnPublicURL": "",
            "transponderNumber": "not-a-number",
            "driverName": "PROD 1"
          }
        ]
        """;

    private static IConfiguration Config(string? sentinelUrl = "https://sentinel.test") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "SentinelApiUrl", sentinelUrl } })
            .Build();

    [TestMethod]
    public void Constructor_MissingSentinelApiUrl_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new SentinelClient(Config(null)));
    }

    [TestMethod]
    public async Task GetStreamsAsync_RequestsPublicStreamsEndpointWithGet()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, "[]");
        var client = new SentinelClient(Config(), handler);

        await client.GetStreamsAsync();

        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual("https://sentinel.test/getPublicStreams", request.Uri!.GetLeftPart(UriPartial.Path));
    }

    [TestMethod]
    public async Task GetStreamsAsync_MapsFeedFieldNamesOntoPublicStreams()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TwoStreamsJson);
        var client = new SentinelClient(Config(), handler);

        var streams = await client.GetStreamsAsync();

        Assert.HasCount(2, streams);
        Assert.AreEqual("https://youtu.be/abc", streams[0].YouTubeUrl);
        Assert.AreEqual("srt://svn.test/live/1", streams[0].SvnUrl);
        Assert.AreEqual("Alice Smith", streams[0].DriverName);
        Assert.AreEqual("1329228", streams[0].TransponderIdStr);
        Assert.AreEqual(1329228u, streams[0].TransponderId);
        // A transponder number the feed cannot supply numerically silently becomes 0.
        Assert.AreEqual(0u, streams[1].TransponderId);
    }

    [TestMethod]
    public async Task GetStreamsAsync_NullBody_ReturnsEmptyListRatherThanNull()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, "null");
        var client = new SentinelClient(Config(), handler);

        Assert.IsEmpty(await client.GetStreamsAsync());
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    [DataRow(HttpStatusCode.Unauthorized)]
    public async Task GetStreamsAsync_NonSuccessStatus_ThrowsInsteadOfReportingNoStreams(HttpStatusCode status)
    {
        // Critical: a failing upstream must not look like "no cars are streaming". The poll loop treats
        // an empty stream list as "everything ended" and clears every car's video and driver info.
        var handler = StubHttpMessageHandler.Json(status, "null");
        var client = new SentinelClient(Config(), handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStreamsAsync());
    }

    [TestMethod]
    public async Task GetStreamsAsync_MalformedBody_Throws()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, "{ this is not json");
        var client = new SentinelClient(Config(), handler);

        await Assert.ThrowsAsync<JsonException>(() => client.GetStreamsAsync());
    }

    [TestMethod]
    public async Task GetStreamsAsync_TransportFailure_Throws()
    {
        var handler = StubHttpMessageHandler.Throws(new HttpRequestException("connection refused"));
        var client = new SentinelClient(Config(), handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStreamsAsync());
    }
}
