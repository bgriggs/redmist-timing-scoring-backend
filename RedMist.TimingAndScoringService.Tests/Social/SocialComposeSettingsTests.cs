using Microsoft.Extensions.Configuration;
using RedMist.Database.Models;
using RedMist.SocialCompose;

namespace RedMist.TimingAndScoringService.Tests.Social;

/// <summary>
/// These values decide what the job spends and what it publishes about, and they arrive as strings
/// from a Helm chart. A typo has to stop the job rather than quietly change its behavior.
/// </summary>
[TestClass]
public class SocialComposeSettingsTests
{
    /// <summary>Any site, so that tests about the other settings do not have to name one.</summary>
    private const string SomeSite = "https://example.test";

    /// <summary>
    /// Supplies <c>Social:Images:BaseUrl</c> unless the caller names it. That setting has no default
    /// -- see <see cref="AMissingImagesBaseUrl_IsRefused"/> -- so without this every test here would
    /// fail on it instead of on what it is about.
    /// </summary>
    private static IConfiguration Config(params (string Key, string Value)[] values)
    {
        var dictionary = values.ToDictionary(v => v.Key, v => (string?)v.Value);
        if (!dictionary.ContainsKey("Social:Images:BaseUrl"))
            dictionary["Social:Images:BaseUrl"] = SomeSite;

        return new ConfigurationBuilder().AddInMemoryCollection(dictionary).Build();
    }

    /// <summary>
    /// The site the pictures are taken from has to be named, because the event ids handed to it come
    /// from whichever database the job reads. A default would be production's site, which is wrong in
    /// every other environment -- and wrong silently: an id that does not resolve there produces a
    /// timeout and a pictureless draft, and one that does produces a picture of the wrong race.
    /// </summary>
    [TestMethod]
    public void AMissingImagesBaseUrl_IsRefused()
    {
        var withoutBaseUrl = new ConfigurationBuilder().Build();

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => SocialComposeSettings.FromConfiguration(withoutBaseUrl));

        StringAssert.Contains(ex.Message, "Social:Images:BaseUrl",
            "The message has to name the setting; that is the whole point of failing at startup.");
    }

    /// <summary>Blank is how an unset Helm value arrives, so it must be refused like a missing one.</summary>
    [TestMethod]
    public void ABlankImagesBaseUrl_IsRefused()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => SocialComposeSettings.FromConfiguration(Config(("Social:Images:BaseUrl", ""))));
    }

    /// <summary>
    /// Defaults are chosen so that being wrong means a post is missed, never that an unwanted one is
    /// drafted: a short lookback, a settle period long enough that a race has finished, a low ceiling.
    /// </summary>
    [TestMethod]
    public void DefaultsAreConservative()
    {
        var settings = SocialComposeSettings.FromConfiguration(Config());

        Assert.AreEqual(SocialChannel.Facebook, settings.Channel);
        Assert.AreEqual(TimeSpan.FromHours(24), settings.SettlePeriod);
        Assert.AreEqual(TimeSpan.FromDays(14), settings.LookbackWindow);
        Assert.AreEqual(10, settings.MaxEventsPerRun);
        Assert.AreEqual(3, settings.MaxAttempts);
    }

    [TestMethod]
    public void ConfiguredValues_AreUsed()
    {
        var settings = SocialComposeSettings.FromConfiguration(Config(
            ("Social:Model", "claude-opus-5"),
            ("Social:SettleHours", "6"),
            ("Social:LookbackDays", "3"),
            ("Social:MaxEventsPerRun", "2"),
            ("Social:Channel", "Instagram")));

        Assert.AreEqual("claude-opus-5", settings.Model);
        Assert.AreEqual(TimeSpan.FromHours(6), settings.SettlePeriod);
        Assert.AreEqual(TimeSpan.FromDays(3), settings.LookbackWindow);
        Assert.AreEqual(2, settings.MaxEventsPerRun);
        Assert.AreEqual(SocialChannel.Instagram, settings.Channel);
    }

    /// <summary>
    /// Falling back to the default here would be worse than crashing: the job would run happily on a
    /// window nobody chose, and the only symptom would be posts that never appear.
    /// </summary>
    [TestMethod]
    public void AnUnparsableNumber_StopsTheJob()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => SocialComposeSettings.FromConfiguration(Config(("Social:LookbackDays", "fourteen"))));

        StringAssert.Contains(ex.Message, "Social:LookbackDays");
    }

    /// <summary>
    /// Each of these fails obscurely rather than loudly when out of range: zero attempts leaves the
    /// composer with no candidate to return and every event dies on a null reference, zero tokens
    /// makes the API reject every call, and a negative lookback inverts the window so the job reports
    /// "nothing to draft" forever.
    /// </summary>
    [TestMethod]
    [DataRow("Social:MaxAttempts", "0")]
    [DataRow("Social:MaxTokens", "0")]
    [DataRow("Social:MaxEventsPerRun", "0")]
    [DataRow("Social:LookbackDays", "-1")]
    [DataRow("Social:SettleHours", "-1")]
    [DataRow("Social:MaxCharacters", "-1")]
    public void AnOutOfRangeNumber_StopsTheJobAtStartup(string key, string value)
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => SocialComposeSettings.FromConfiguration(Config((key, value))));

        StringAssert.Contains(ex.Message, key);
        StringAssert.Contains(ex.Message, "at least");
    }

    /// <summary>Zero characters means "no length limit", so it has to stay a legal value.</summary>
    [TestMethod]
    public void ZeroMaxCharacters_MeansNoLimitRatherThanAnError()
    {
        Assert.AreEqual(0, SocialComposeSettings.FromConfiguration(Config(("Social:MaxCharacters", "0"))).MaxCharacters);
    }

    [TestMethod]
    public void AnUnknownChannel_StopsTheJob()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => SocialComposeSettings.FromConfiguration(Config(("Social:Channel", "Bluesky"))));

        StringAssert.Contains(ex.Message, "Facebook");
    }

    /// <summary>An unset Helm value arrives as an empty string, which must read as "not configured".</summary>
    [TestMethod]
    public void AnEmptyValue_FallsBackToTheDefault()
    {
        var settings = SocialComposeSettings.FromConfiguration(Config(
            ("Social:Model", "  "),
            ("Social:LookbackDays", "")));

        Assert.AreEqual("claude-sonnet-5", settings.Model);
        Assert.AreEqual(TimeSpan.FromDays(14), settings.LookbackWindow);
    }

    [TestMethod]
    public void ComposeOptions_CarryTheConfiguredLimits()
    {
        var options = SocialComposeSettings.FromConfiguration(Config(
            ("Social:MaxTokens", "512"),
            ("Social:MaxAttempts", "2"),
            ("Social:MaxCharacters", "800"))).ToComposeOptions();

        Assert.AreEqual(512, options.MaxTokens);
        Assert.AreEqual(2, options.MaxAttempts);
        Assert.AreEqual(800, options.MaxCharacters);
    }
}
