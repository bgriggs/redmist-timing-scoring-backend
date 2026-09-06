using RedMist.Social.Imaging;
using RedMist.SocialCompose;

namespace RedMist.TimingAndScoringService.Tests.Social;

/// <summary>
/// The addressing decisions for results pictures: which page is photographed, and where the picture
/// is put so a social platform can fetch it. Both are strings assembled from parts, and both are
/// wrong in ways that produce a plausible-looking result rather than an error.
/// </summary>
[TestClass]
public class SessionImageTests
{
    /// <summary>
    /// The URL shape the landing UI actually routes: timing/:id/:sessionId.
    /// </summary>
    [TestMethod]
    public void TheCapturedUrl_AddressesTheSessionsTimingPage()
    {
        Assert.AreEqual(
            "https://redmist.racing/timing/384/26",
            PlaywrightSessionImageCapture.BuildUrl("https://redmist.racing", 384, 26));
    }

    /// <summary>
    /// The embed flag is what removes the site toolbar and footer at the Angular level, rather than
    /// leaving them to be hidden by injected CSS after they have already rendered.
    /// </summary>
    [TestMethod]
    public void AQueryString_IsAppendedWhenGiven()
    {
        Assert.AreEqual(
            "https://redmist.racing/timing/384/26?embed=1",
            PlaywrightSessionImageCapture.BuildUrl("https://redmist.racing", 384, 26, "embed=1"));

        Assert.AreEqual(
            "https://redmist.racing/timing/384/26?embed=1",
            PlaywrightSessionImageCapture.BuildUrl("https://redmist.racing", 384, 26, "?embed=1"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    public void AnEmptyQueryString_LeavesTheUrlBare(string? query)
    {
        Assert.AreEqual(
            "https://redmist.racing/timing/384/26",
            PlaywrightSessionImageCapture.BuildUrl("https://redmist.racing", 384, 26, query));
    }

    /// <summary>
    /// A base URL with a trailing slash is the obvious way to write one in a Helm value, and would
    /// otherwise produce a double slash that Angular's router does not match.
    /// </summary>
    [TestMethod]
    public void ATrailingSlashOnTheBaseUrl_DoesNotProduceADoubleSlash()
    {
        Assert.AreEqual(
            "https://redmist.racing/timing/384/26",
            PlaywrightSessionImageCapture.BuildUrl("https://redmist.racing/", 384, 26));
    }

    /// <summary>
    /// The stored path carries a hash of the image itself, which is what makes re-capturing safe:
    /// changed results write to a new address, so a platform that already fetched the old one cannot
    /// serve a stale picture and no CDN purge is needed.
    /// </summary>
    [TestMethod]
    public void ChangedImageContent_IsStoredAtADifferentPath()
    {
        var before = BunnySocialImageStore.BuildPath(384, 26, [1, 2, 3]);
        var after = BunnySocialImageStore.BuildPath(384, 26, [1, 2, 4]);

        Assert.AreNotEqual(before, after);
        StringAssert.StartsWith(before, "/social/event-384/session-26-");
        StringAssert.EndsWith(before, ".png");
    }

    /// <summary>
    /// The other half of that bargain: a redraft that produces the same picture must not litter the
    /// zone with a new copy every night.
    /// </summary>
    [TestMethod]
    public void IdenticalImageContent_IsStoredAtTheSamePath()
    {
        Assert.AreEqual(
            BunnySocialImageStore.BuildPath(384, 26, [1, 2, 3]),
            BunnySocialImageStore.BuildPath(384, 26, [1, 2, 3]));
    }

    /// <summary>Two sessions of one event must not overwrite each other.</summary>
    [TestMethod]
    public void EachSession_GetsItsOwnPath()
    {
        Assert.AreNotEqual(
            BunnySocialImageStore.BuildPath(384, 26, [1, 2, 3]),
            BunnySocialImageStore.BuildPath(384, 27, [1, 2, 3]));
    }
}
