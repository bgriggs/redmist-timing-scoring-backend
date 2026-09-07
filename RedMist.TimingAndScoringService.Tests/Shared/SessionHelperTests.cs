using RedMist.Backend.Shared.Utilities;

namespace RedMist.EventProcessor.Tests.Shared;

/// <summary>
/// Session names are free text typed by the organizer, and this classifier is the only thing
/// separating a competitive session from a run group. Today its one consumer is the timing viewer's
/// default sort mode (fastest-lap vs position); nothing gates results on it. The cases below are
/// drawn from names that actually occur in production rather than invented ones.
/// </summary>
[TestClass]
public class SessionHelperTests
{
    [TestMethod]
    [DataRow("Practice")]
    [DataRow("Team Practice")]
    [DataRow("Race Practice 1")]
    [DataRow("Paid Practice F7")]
    [DataRow("Qualifying")]
    [DataRow("Sat Qual")]
    [DataRow("GTO/GTU Qualifying")]
    [DataRow("GP1/GP2/GP3 Qualifying")]
    public void PracticeAndQualifying_AreNonCompetitive(string sessionName)
    {
        Assert.IsTrue(SessionHelper.IsPracticeOrQualifyingSession(sessionName));
    }

    [TestMethod]
    [DataRow("HPDE S3")]
    [DataRow("HPDE Session 4")]
    [DataRow("HPDE 1")]
    [DataRow("HPDE - Untimed - HPDE S6")]
    [DataRow("School")]
    [DataRow("Intro & Advanced Schools")]
    [DataRow("Test Day")]
    [DataRow("Testing")]
    [DataRow("Autobahn Friday Test")]
    [DataRow("Radical Cup Test")]
    [DataRow("Timing Equipment Test")]
    [DataRow("Schools & Testing")]
    [DataRow("Setup")]
    [DataRow("Thursday set-up/Test")]
    [DataRow("Max Track Time")]
    [DataRow("MaxTrackTime")]
    [DataRow("Max Track Time Tuesday")]
    [DataRow("Friday Track Day")]
    public void DriverEducationSchoolsAndTestDays_AreNonCompetitive(string sessionName)
    {
        Assert.IsTrue(SessionHelper.IsPracticeOrQualifyingSession(sessionName));
    }

    /// <summary>
    /// The tab and double-space rows are the reason matching splits on all whitespace rather than on
    /// a single space character; without them a "simplification" to Split(' ') passes every other test.
    /// </summary>
    [TestMethod]
    [DataRow("DE")]
    [DataRow("DE Group 1")]
    [DataRow("Sat DE")]
    [DataRow("Sat DE Run 2")]
    [DataRow("Sat\tDE")]
    [DataRow("Sat  DE")]
    public void StandaloneDe_IsNonCompetitive(string sessionName)
    {
        Assert.IsTrue(SessionHelper.IsPracticeOrQualifyingSession(sessionName));
    }

    /// <summary>
    /// "DE" counts only as an exact, case-sensitive, whitespace-delimited word. Matched loosely it
    /// swallows any name containing "de" -- "Sebring Under the Stars" is a real 14 hour race, and
    /// Thunderhill races would go the same way. Getting this wrong silently drops races from results
    /// rather than failing loudly, so the narrow rule is deliberate: a session named "DE-2" or
    /// "de group" is missed, which costs one correction, while a swallowed race costs a wrong post.
    /// </summary>
    [TestMethod]
    [DataRow("Sebring Under the Stars 14 hr Fri")]
    [DataRow("Thunderhill 8 Hour")]
    [DataRow("Under the Lights 500")]
    [DataRow("Independence Day Sprint")]
    [DataRow("Sunday de session")]
    [DataRow("DE-2")]
    [DataRow("Sat De Run")]
    [DataRow("Sat dE Run")]
    public void DeThatIsNotAStandaloneWord_DoesNotMatch(string sessionName)
    {
        Assert.IsFalse(SessionHelper.IsPracticeOrQualifyingSession(sessionName));
    }

    [TestMethod]
    [DataRow("Race")]
    [DataRow("Saturday 8 Hour")]
    [DataRow("Sunday 7 Hour")]
    [DataRow("Sat 7Hr")]
    [DataRow("8 Hour")]
    [DataRow("The New England Enduro")]
    [DataRow("Cookie Cutter Classic")]
    [DataRow("The Fast Parts Grand Prix at Mid-Ohio")]
    [DataRow("EC Harris Hill Spring Double")]
    public void Races_AreCompetitive(string sessionName)
    {
        Assert.IsFalse(SessionHelper.IsPracticeOrQualifyingSession(sessionName));
    }

    /// <summary>
    /// Documents the limit of name matching rather than asserting desired behavior. All of these are
    /// real production session names that are not races, but nothing in the name says so: "Friday" is
    /// a test day only by convention, "New run" says nothing at all, and "Practdice" is a typo. Any
    /// consumer that needs certainty must confirm the session list with a human rather than trusting
    /// a false result here.
    /// </summary>
    [TestMethod]
    [DataRow("G2 Motorsports Friday")]
    [DataRow("Autobahn Friday")]
    [DataRow("Daytona Friday")]
    [DataRow("New run")]
    [DataRow("Lime Rock Practdice")]
    [DataRow("Warm-up")]
    [DataRow("Registration and Tech Inspection (near the Diner)")]
    public void NonRacesThatNameMatchingCannotDetect_AreStillTreatedAsCompetitive(string sessionName)
    {
        Assert.IsFalse(SessionHelper.IsPracticeOrQualifyingSession(sessionName));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(null)]
    [DataRow("   ")]
    [DataRow("\t")]
    public void MissingOrBlankName_IsNotClassifiedAsNonCompetitive(string? sessionName)
    {
        Assert.IsFalse(SessionHelper.IsPracticeOrQualifyingSession(sessionName!));
    }

    /// <summary>Substring terms ignore case; only the whole-word terms are case sensitive.</summary>
    [TestMethod]
    [DataRow("hpde s3")]
    [DataRow("TEST")]
    [DataRow("sChOoL")]
    [DataRow("paid practice f7")]
    public void SubstringTerms_MatchCaseInsensitively(string sessionName)
    {
        Assert.IsTrue(SessionHelper.IsPracticeOrQualifyingSession(sessionName));
    }
}
