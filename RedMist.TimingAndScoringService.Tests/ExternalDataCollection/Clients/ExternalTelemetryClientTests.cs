using MessagePack;
using Microsoft.Extensions.Configuration;
using RedMist.ExternalDataCollection.Clients;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarVideo;
using RestSharp;
using RestSharp.Authenticators;
using System.Net;

namespace RedMist.TimingAndScoringService.Tests.ExternalDataCollection.Clients;

/// <summary>
/// Pins the wire contract of the external telemetry calls: endpoint, verb, MessagePack body and how
/// an unhappy server is reported back to the poll loop.
/// </summary>
[TestClass]
public class ExternalTelemetryClientTests
{
    /// <summary>Stands in for the Keycloak authenticator so no token round trip is attempted.</summary>
    private sealed class NoOpAuthenticator : IAuthenticator
    {
        public ValueTask Authenticate(IRestClient client, RestRequest request, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private static readonly string[] RequiredSettings =
    [
        "Server:ExtTelemUrl",
        "Keycloak:AuthServerUrl",
        "Keycloak:Realm",
        "Keycloak:ClientId",
        "Keycloak:ClientSecret"
    ];

    private static IConfiguration Config(string? omitKey = null)
    {
        var values = new Dictionary<string, string?>
        {
            { "Server:ExtTelemUrl", "https://exttelem.test" },
            { "Keycloak:AuthServerUrl", "https://keycloak.test" },
            { "Keycloak:Realm", "test-realm" },
            { "Keycloak:ClientId", "test-client" },
            { "Keycloak:ClientSecret", "test-secret" }
        };
        if (omitKey != null)
        {
            values.Remove(omitKey);
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ExternalTelemetryClient CreateClient(StubHttpMessageHandler handler) =>
        new(Config(), new NoOpAuthenticator(), handler);

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void Constructor_MissingRequiredSetting_FailsFast(int settingIndex)
    {
        var missing = RequiredSettings[settingIndex];
        Assert.ThrowsExactly<ArgumentException>(() => new ExternalTelemetryClient(Config(omitKey: missing)), missing);
    }

    [TestMethod]
    public async Task UpdateDriversAsync_PostsMessagePackBodyToUpdateDrivers()
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.OK);
        var client = CreateClient(handler);
        List<DriverInfo> drivers =
        [
            new() { TransponderId = 1329228, DriverName = "Alice Smith", EventId = 7, CarNumber = "42" },
            new() { TransponderId = 222, DriverName = string.Empty }
        ];

        var success = await client.UpdateDriversAsync(drivers);

        Assert.IsTrue(success);
        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("https://exttelem.test/UpdateDrivers", request.Uri!.GetLeftPart(UriPartial.Path));
        Assert.AreEqual("application/x-msgpack", request.ContentType);

        // The receiver decodes MessagePack, so a switch to any other encoding would break it silently.
        var roundTripped = MessagePackSerializer.Deserialize<List<DriverInfo>>(request.Body);
        Assert.HasCount(2, roundTripped);
        Assert.AreEqual(1329228u, roundTripped[0].TransponderId);
        Assert.AreEqual("Alice Smith", roundTripped[0].DriverName);
        Assert.AreEqual("42", roundTripped[0].CarNumber);
        Assert.AreEqual(string.Empty, roundTripped[1].DriverName, "Cleared drivers must survive the round trip");
    }

    [TestMethod]
    public async Task UpdateCarVideosAsync_PostsMessagePackBodyToUpdateCarVideos()
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.OK);
        var client = CreateClient(handler);
        List<VideoMetadata> videos =
        [
            new()
            {
                TransponderId = 1329228,
                UseTransponderId = true,
                SystemType = VideoSystemType.Sentinel,
                IsLive = true,
                Destinations = [new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtu.be/abc" }]
            }
        ];

        var success = await client.UpdateCarVideosAsync(videos);

        Assert.IsTrue(success);
        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("https://exttelem.test/UpdateCarVideos", request.Uri!.GetLeftPart(UriPartial.Path));
        Assert.AreEqual("application/x-msgpack", request.ContentType);

        var roundTripped = MessagePackSerializer.Deserialize<List<VideoMetadata>>(request.Body).Single();
        Assert.AreEqual(1329228u, roundTripped.TransponderId);
        Assert.IsTrue(roundTripped.UseTransponderId);
        Assert.AreEqual(VideoSystemType.Sentinel, roundTripped.SystemType);
        Assert.IsTrue(roundTripped.IsLive);
        Assert.AreEqual("https://youtu.be/abc", roundTripped.Destinations.Single().Url);
    }

    [TestMethod]
    public async Task UpdateDriversAsync_ServerRejectsUpdate_ReturnsFalse()
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        Assert.IsFalse(await client.UpdateDriversAsync([new DriverInfo { TransponderId = 111 }]));
    }

    [TestMethod]
    public async Task UpdateCarVideosAsync_ServerRejectsUpdate_ReturnsFalse()
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.Unauthorized);
        var client = CreateClient(handler);

        Assert.IsFalse(await client.UpdateCarVideosAsync([new VideoMetadata { TransponderId = 111 }]));
    }

    [TestMethod]
    public async Task UpdateCarVideosAsync_TransportFailure_ReturnsFalseInsteadOfThrowing()
    {
        // The poll loop treats a false return as a lost publish; an exception here would abort the
        // whole poll before the driver update is sent.
        var handler = StubHttpMessageHandler.Throws(new HttpRequestException("connection refused"));
        var client = CreateClient(handler);

        Assert.IsFalse(await client.UpdateCarVideosAsync([new VideoMetadata { TransponderId = 111 }]));
    }

    [TestMethod]
    public async Task UpdateDriversAsync_EmptyList_StillPostsAnEmptyPayload()
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.OK);
        var client = CreateClient(handler);

        Assert.IsTrue(await client.UpdateDriversAsync([]));
        Assert.IsEmpty(MessagePackSerializer.Deserialize<List<DriverInfo>>(handler.Requests.Single().Body));
    }
}
