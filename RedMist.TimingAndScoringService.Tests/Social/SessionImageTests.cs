using RedMist.Database.Models;
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
        var before = BunnySocialImageStore.BuildPath(SocialChannel.Facebook, 384, 26, [1, 2, 3]);
        var after = BunnySocialImageStore.BuildPath(SocialChannel.Facebook, 384, 26, [1, 2, 4]);

        Assert.AreNotEqual(before, after);
        StringAssert.StartsWith(before, "/social/facebook/event-384/session-26-");
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
            BunnySocialImageStore.BuildPath(SocialChannel.Facebook, 384, 26, [1, 2, 3]),
            BunnySocialImageStore.BuildPath(SocialChannel.Facebook, 384, 26, [1, 2, 3]));
    }

    /// <summary>Two sessions of one event must not overwrite each other.</summary>
    [TestMethod]
    public void EachSession_GetsItsOwnPath()
    {
        Assert.AreNotEqual(
            BunnySocialImageStore.BuildPath(SocialChannel.Facebook, 384, 26, [1, 2, 3]),
            BunnySocialImageStore.BuildPath(SocialChannel.Facebook, 384, 27, [1, 2, 3]));
    }

    /// <summary>
    /// Deleting a post's picture means turning its URL back into a storage path. Getting that wrong
    /// either removes nothing or removes something else, and neither announces itself.
    /// </summary>
    [TestMethod]
    public void AStoredUrl_RoundTripsBackToItsPath()
    {
        var path = BunnySocialImageStore.BuildPath(SocialChannel.Facebook, 384, 26, [1, 2, 3]);
        var url = "https://redmist-assets.b-cdn.net" + path;

        Assert.AreEqual(path, BunnySocialImageStore.TryGetPath(url, "https://redmist-assets.b-cdn.net"));
        Assert.AreEqual(path, BunnySocialImageStore.TryGetPath(url, "https://redmist-assets.b-cdn.net/"),
            "A trailing slash on the configured base is the obvious way to write one");
    }

    /// <summary>
    /// The zone holds more than post images -- organization logos live one directory over. A delete
    /// reachable from an arbitrary URL is a delete that can remove the wrong thing.
    /// </summary>
    [TestMethod]
    [DataRow("https://redmist-assets.b-cdn.net/logos/org-1.img", "an unrelated file in the same zone")]
    [DataRow("https://evil.example/social/facebook/event-384/session-26-abc.png", "a different host entirely")]
    // Traversal, using a .png so it is the path shape being refused rather than the extension.
    [DataRow("https://redmist-assets.b-cdn.net/social/../logos/org-1.png", "a traversal out of the social prefix")]
    [DataRow("https://redmist-assets.b-cdn.net/social/facebook/event-1/../../../logos/org-1.png", "a traversal from inside a valid path")]
    [DataRow("https://redmist-assets.b-cdn.net/social/facebook/event-1/..%2F..%2Flogos%2Forg-1.png", "an encoded traversal")]
    // Unicode digits: \d would accept these, and a path this store could never have written is a
    // path a delete has no business acting on.
    [DataRow("https://redmist-assets.b-cdn.net/social/facebook/event-٣٨٤/session-26-0123456789abcdef.png", "Arabic-Indic digits")]
    [DataRow("https://redmist-assets.b-cdn.net/social/facebook/event-３８４/session-26-0123456789abcdef.png", "full-width digits")]
    // A trailing newline: $ would let this through, and the newline would travel into the request.
    [DataRow("https://redmist-assets.b-cdn.net/social/facebook/event-384/session-26-0123456789abcdef.png\n", "a trailing newline")]
    [DataRow("https://redmist-assets.b-cdn.net/social/facebook/event-384/session-26-0123456789ABCDEF.png", "an uppercase hash")]
    [DataRow("https://redmist-assets.b-cdn.net/social/facebook/event-384/session-26-0123.png", "a short hash")]
    public void AUrlThisStoreDidNotWrite_IsRefused(string url, string why)
    {
        Assert.IsNull(BunnySocialImageStore.TryGetPath(url, "https://redmist-assets.b-cdn.net"), why);
    }

    /// <summary>A public helper that a delete depends on should refuse nothing, not throw at it.</summary>
    [TestMethod]
    public void ANullOrEmptyUrl_IsRefusedRatherThanThrowing()
    {
        Assert.IsNull(BunnySocialImageStore.TryGetPath(null, "https://redmist-assets.b-cdn.net"));
        Assert.IsNull(BunnySocialImageStore.TryGetPath(string.Empty, "https://redmist-assets.b-cdn.net"));
    }

    /// <summary>
    /// Two channels photographing one session produce byte-identical files, so without the channel in
    /// the path they would collide on a single object referenced by two posts -- and rejecting one
    /// post would delete the picture the other still shows.
    /// </summary>
    [TestMethod]
    public void EachChannel_GetsItsOwnPath()
    {
        Assert.AreNotEqual(
            BunnySocialImageStore.BuildPath(SocialChannel.Facebook, 384, 26, [1, 2, 3]),
            BunnySocialImageStore.BuildPath(SocialChannel.Instagram, 384, 26, [1, 2, 3]));
    }
}
