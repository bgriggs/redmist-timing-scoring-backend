using RedMist.Backend.Shared.Utilities;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// The client type derived here is what the relay's connected-client telemetry is grouped by, and it
/// is derived from an untrusted claim, so unknown and missing values have to land somewhere sane.
/// </summary>
[TestClass]
public class ClientTypeHelperTests
{
    [TestMethod]
    [DataRow("redmist-ios-ui", "iOS")]
    [DataRow("redmist-android-ui", "Android")]
    [DataRow("redmist-browser-ui", "Web")]
    [DataRow("api-partner-feed", "API")]
    [DataRow("API-PARTNER-FEED", "API")]
    [DataRow("Api-Partner-Feed", "API")]
    [DataRow("something-else", "Web")]
    [DataRow("", "Web")]
    [DataRow(null, "Web")]
    public void ResolveClientType(string? clientId, string expected)
    {
        Assert.AreEqual(expected, ClientTypeHelper.ResolveClientType(clientId));
    }

    /// <summary>
    /// The api- test is a prefix match, not an equality match, so a client id that merely contains
    /// it is not an API client.
    /// </summary>
    [TestMethod]
    public void ResolveClientType_OnlyTreatsTheApiPrefixAsAnApiClient()
    {
        Assert.AreEqual("Web", ClientTypeHelper.ResolveClientType("partner-api-feed"));
    }
}
