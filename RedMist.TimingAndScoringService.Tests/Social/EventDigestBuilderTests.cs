using Microsoft.Extensions.Time.Testing;
using RedMist.Database.Models;
using RedMist.Social.Digest;
using RedMist.TimingCommon.Models;
using Event = RedMist.TimingCommon.Models.Configuration.Event;

namespace RedMist.TimingAndScoringService.Tests.Social;

/// <summary>
/// The digest is the only thing a post generator is allowed to draw on, so these cover what it lets
/// through and what it refuses to invent. A wrong winner here becomes a wrong public post, which is
/// why the class and podium cases are pinned in detail rather than smoke-tested.
/// </summary>
[TestClass]
public class EventDigestBuilderTests
{
    private static readonly DateTime Now = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    private static FakeTimeProvider Clock() => new(Now);

    private static Event AnEvent() => new()
    {
        Id = 341,
        Name = "The New England Enduro",
        TrackName = "Thompson Speedway",
        CourseConfiguration = "Full Course",
        StartDate = new DateTime(2026, 8, 29, 8, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2026, 8, 30, 18, 0, 0, DateTimeKind.Utc),
    };

    private static CarPosition ACar(
        string number,
        string cls,
        int classPosition,
        int laps = 100,
        string? gapToLeader = null,
        string? gapToCarAhead = null,
        string? bestTime = "1:58.402",
        int? lapsLed = null,
        int positionsGained = CarPosition.InvalidPosition,
        string driver = "",
        bool isBestTime = false,
        bool isBestTimeClass = false,
        bool isBiggestMover = false) => new()
        {
            Number = number,
            Class = cls,
            ClassPosition = classPosition,
            LastLapCompleted = laps,
            // Difference is the interval to the class LEADER; Gap is the interval to the next car up.
            // They are separate parameters here so a test can prove the digest reads the right one.
            InClassDifference = gapToLeader,
            InClassGap = gapToCarAhead,
            BestTime = bestTime,
            LapsLedInClass = lapsLed,
            InClassPositionsGained = positionsGained,
            DriverName = driver,
            IsBestTime = isBestTime,
            IsBestTimeClass = isBestTimeClass,
            IsOverallMostPositionsGained = isBiggestMover,
        };

    private static SessionResult ASession(
        int sessionId,
        string name,
        IEnumerable<CarPosition>? cars = null,
        IEnumerable<EventEntry>? entries = null,
        DateTime? start = null,
        int? leadChanges = null,
        int? yellows = null,
        bool nullState = false)
    {
        var result = new SessionResult
        {
            EventId = 341,
            SessionId = sessionId,
            Start = start ?? new DateTime(2026, 8, 29, 10, 0, 0, DateTimeKind.Utc),
        };

        if (nullState)
            return result;

        result.SessionState = new SessionState
        {
            EventId = 341,
            SessionId = sessionId,
            SessionName = name,
            CarPositions = [.. cars ?? []],
            EventEntries = [.. entries ?? []],
            LeadChanges = leadChanges,
            NumberOfYellows = yellows,
            SessionEndTime = new DateTime(2026, 8, 29, 18, 0, 0, DateTimeKind.Utc),
        };
        return result;
    }

    private static EventDigest Build(params SessionResult[] sessions) =>
        EventDigestBuilder.Build(AnEvent(), "ChampCar", sessions, Clock());

    [TestMethod]
    public void RaceSessions_AreIncluded_AndNonCompetitiveSessionsAreNot()
    {
        var cars = new[] { ACar("66", "GP1", 1) };
        var digest = Build(
            ASession(1, "Race Practice 1", cars),
            ASession(2, "Sat Qual", cars),
            ASession(3, "HPDE S3", cars),
            ASession(4, "Saturday 8 Hour", cars),
            ASession(5, "Sunday 7 Hour", cars));

        CollectionAssert.AreEqual(
            new[] { "Saturday 8 Hour", "Sunday 7 Hour" },
            digest.Sessions.Select(s => s.SessionName).ToList());
    }

    /// <summary>
    /// Excluding a run group is routine and must not raise a warning, or every weekend's digest would
    /// arrive noisy enough that the real warnings stop being read.
    /// </summary>
    [TestMethod]
    public void ExcludingPracticeSessions_DoesNotWarn()
    {
        EventEntry[] entries = [new() { Number = "66", Name = "BMM #66", Class = "GP1" }];
        var digest = Build(
            ASession(1, "Paid Practice F7", [ACar("66", "GP1", 1)], entries),
            ASession(2, "Race", [ACar("66", "GP1", 1)], entries));

        Assert.AreEqual(0, digest.Provenance.Warnings.Count,
            $"Unexpected warnings: {string.Join("; ", digest.Provenance.Warnings)}");
    }

    [TestMethod]
    public void SessionWithoutStoredState_IsSkippedWithWarning()
    {
        var digest = Build(
            ASession(7, "Race", nullState: true),
            ASession(8, "Race 2", [ACar("66", "GP1", 1)]));

        Assert.AreEqual(1, digest.Sessions.Count);
        Assert.IsTrue(digest.Provenance.Warnings.Any(w => w.Contains("Session 7") && w.Contains("no session state")));
    }

    [TestMethod]
    public void RaceSessionWithNoCarPositions_IsSkippedWithWarning()
    {
        var digest = Build(ASession(9, "Race", []));

        Assert.AreEqual(0, digest.Sessions.Count);
        Assert.IsTrue(digest.Provenance.Warnings.Any(w => w.Contains("no car positions")));
        Assert.IsTrue(digest.Provenance.Warnings.Any(w => w.Contains("No race sessions")));
    }

    [TestMethod]
    public void Podium_IsOrderedByClassPosition_AndCappedAtThree()
    {
        var digest = Build(ASession(1, "Race",
        [
            ACar("4", "GP1", 4),
            ACar("1", "GP1", 1),
            ACar("3", "GP1", 3),
            ACar("2", "GP1", 2),
        ]));

        var podium = digest.Sessions[0].Classes.Single().Podium;
        CollectionAssert.AreEqual(new[] { "1", "2", "3" }, podium.Select(f => f.CarNumber).ToList());
    }

    /// <summary>
    /// Entries counts the whole class, not the podium: copy that says "won a 14 car class" has to get
    /// the field size from somewhere, and inventing it is exactly what the digest exists to prevent.
    /// </summary>
    [TestMethod]
    public void ClassEntries_CountsEveryCarInTheClass()
    {
        var digest = Build(ASession(1, "Race",
        [
            ACar("1", "GP1", 1), ACar("2", "GP1", 2), ACar("3", "GP1", 3),
            ACar("4", "GP1", 4), ACar("5", "GP1", 5),
        ]));

        var cls = digest.Sessions[0].Classes.Single();
        Assert.AreEqual(5, cls.Entries);
        Assert.AreEqual(3, cls.Podium.Count);
    }

    [TestMethod]
    public void CarsWithoutAFinishingPosition_AreExcludedFromThePodium()
    {
        var digest = Build(ASession(1, "Race",
        [
            ACar("99", "GP1", 0),
            ACar("1", "GP1", 1),
        ]));

        var cls = digest.Sessions[0].Classes.Single();
        Assert.AreEqual("1", cls.Podium.Single().CarNumber);
        Assert.AreEqual(2, cls.Entries, "A car that did not get a position still ran in the class");
    }

    [TestMethod]
    public void ClassWhereNobodyHasAPosition_IsSkippedWithWarning()
    {
        var digest = Build(ASession(1, "Race",
        [
            ACar("1", "GP1", 1),
            ACar("50", "GP2", 0),
        ]));

        Assert.AreEqual("GP1", digest.Sessions[0].Classes.Single().ClassName);
        Assert.IsTrue(digest.Provenance.Warnings.Any(w => w.Contains("GP2") && w.Contains("no finishing positions")));
    }

    [TestMethod]
    public void TeamAndEntryName_AreJoinedFromTheEntryListByCarNumber()
    {
        var digest = Build(ASession(1, "Race",
            [ACar("66", "GP1", 1, driver: "Pat Doe")],
            [new EventEntry { Number = "66", Team = "Big Mission Motorsports", Name = "BMM #66", Class = "GP1" }]));

        var winner = digest.Sessions[0].Classes.Single().Podium[0];
        Assert.AreEqual("Big Mission Motorsports", winner.Team);
        Assert.AreEqual("BMM #66", winner.EntryName);
        Assert.AreEqual("Pat Doe", winner.LastDriverName);
    }

    [TestMethod]
    public void CarWithNoMatchingEntry_HasNullTeamRatherThanAGuess()
    {
        var digest = Build(ASession(1, "Race",
            [ACar("66", "GP1", 1)],
            [new EventEntry { Number = "12", Team = "Someone Else", Class = "GP1" }]));

        var winner = digest.Sessions[0].Classes.Single().Podium[0];
        Assert.IsNull(winner.Team);
        Assert.IsNull(winner.EntryName);
    }

    /// <summary>
    /// Across recent production events the entry name is present ~97% of the time and a team name
    /// only ~25%, so the fallback order is the difference between naming most winners and naming a
    /// quarter of them.
    /// </summary>
    [TestMethod]
    public void DisplayName_PrefersTeamThenEntryNameThenCarNumber()
    {
        EventEntry Entry(string? team, string? name) =>
            new() { Number = "66", Team = team ?? string.Empty, Name = name ?? string.Empty, Class = "GP1" };

        Finisher Winner(EventEntry entry) => EventDigestBuilder
            .Build(AnEvent(), "ChampCar", [ASession(1, "Race", [ACar("66", "GP1", 1)], [entry])], Clock())
            .Sessions[0].Classes.Single().Podium[0];

        var withTeam = Winner(Entry("Big Mission Motorsports", "BMM #66"));
        Assert.AreEqual("Big Mission Motorsports", withTeam.DisplayName);
        Assert.AreEqual(FinisherNameSource.Team, withTeam.NameSource);

        var entryOnly = Winner(Entry(null, "BMM #66"));
        Assert.AreEqual("BMM #66", entryOnly.DisplayName);
        Assert.AreEqual(FinisherNameSource.EntryName, entryOnly.NameSource);

        var neither = Winner(Entry(null, null));
        Assert.AreEqual("car #66", neither.DisplayName);
        Assert.AreEqual(FinisherNameSource.CarNumber, neither.NameSource);
    }

    [TestMethod]
    public void WinnerWithNoNameAtAll_IsWarnedAbout()
    {
        var digest = Build(ASession(1, "Race",
            [ACar("66", "GP1", 1)],
            [new EventEntry { Number = "66", Class = "GP1" }]));

        Assert.IsTrue(digest.Provenance.Warnings.Any(
            w => w.Contains("no team or entry name") && w.Contains("66")));
    }

    [TestMethod]
    public void WinnerWithAName_IsNotWarnedAbout()
    {
        var digest = Build(ASession(1, "Race",
            [ACar("66", "GP1", 1)],
            [new EventEntry { Number = "66", Name = "BMM #66", Class = "GP1" }]));

        Assert.IsFalse(digest.Provenance.Warnings.Any(w => w.Contains("no team or entry name")));
    }

    /// <summary>Duplicate numbers happen in organizer entry lists; first wins rather than throwing.</summary>
    [TestMethod]
    public void DuplicateCarNumbersInTheEntryList_DoNotThrow()
    {
        var digest = Build(ASession(1, "Race",
            [ACar("66", "GP1", 1)],
            [
                new EventEntry { Number = "66", Team = "First", Class = "GP1" },
                new EventEntry { Number = "66", Team = "Second", Class = "GP1" },
            ]));

        Assert.AreEqual("First", digest.Sessions[0].Classes.Single().Podium[0].Team);
    }

    [TestMethod]
    public void CarsWithNoClass_AreGroupedAsOverallWithWarning()
    {
        var digest = Build(ASession(1, "Race", [ACar("66", "", 1)]));

        Assert.AreEqual("Overall", digest.Sessions[0].Classes.Single().ClassName);
        Assert.IsTrue(digest.Provenance.Warnings.Any(w => w.Contains("no class")));
    }

    [TestMethod]
    public void Highlights_ComeFromThePipelineFlags()
    {
        var digest = Build(ASession(1, "Race",
        [
            ACar("1", "GP1", 1, isBestTime: true, isBestTimeClass: true),
            ACar("2", "GP2", 1, isBestTimeClass: true),
            ACar("3", "GP1", 2, isBiggestMover: true, positionsGained: 14),
        ]));

        var highlights = digest.Sessions[0].Highlights;
        Assert.AreEqual("1", highlights.FastestLapOverall?.CarNumber);
        Assert.AreEqual("3", highlights.BiggestMoverOverall?.CarNumber);
        Assert.AreEqual(14, highlights.BiggestMoverOverall?.PositionsGained);
        CollectionAssert.AreEquivalent(
            new[] { "1", "2" },
            highlights.ClassFastestLaps.Select(f => f.CarNumber).ToList());
    }

    [TestMethod]
    public void MissingHighlights_AreNullRatherThanFabricated()
    {
        var digest = Build(ASession(1, "Race", [ACar("1", "GP1", 1)]));

        var highlights = digest.Sessions[0].Highlights;
        Assert.IsNull(highlights.FastestLapOverall);
        Assert.IsNull(highlights.BiggestMoverOverall);
        Assert.AreEqual(0, highlights.ClassFastestLaps.Count);
    }

    /// <summary>
    /// PositionsGained defaults to a sentinel rather than zero. Leaking it would put "-999 positions"
    /// in front of the generator as if it were a real number.
    /// </summary>
    [TestMethod]
    public void UnsetPositionsGained_IsNullNotTheSentinel()
    {
        var digest = Build(ASession(1, "Race", [ACar("1", "GP1", 1)]));

        Assert.IsNull(digest.Sessions[0].Classes.Single().Podium[0].PositionsGained);
    }

    [TestMethod]
    [DataRow("IsPrivate")]
    [DataRow("HideName")]
    [DataRow("IsSimulation")]
    [DataRow("IsDeleted")]
    public void EventsThatMustNotBePublished_AreMarkedIneligibleWithWarning(string flag)
    {
        var evt = AnEvent();
        switch (flag)
        {
            case "IsPrivate": evt.IsPrivate = true; break;
            case "HideName": evt.HideName = true; break;
            case "IsSimulation": evt.IsSimulation = true; break;
            case "IsDeleted": evt.IsDeleted = true; break;
        }

        var digest = EventDigestBuilder.Build(evt, "ChampCar",
            [ASession(1, "Race", [ACar("1", "GP1", 1)])], Clock());

        Assert.IsFalse(digest.IsEligibleForPublication);
        Assert.IsTrue(digest.Provenance.Warnings.Any(w => w.Contains("must not be published")));
    }

    [TestMethod]
    public void AnOrdinaryEvent_IsEligible()
    {
        var digest = Build(ASession(1, "Race", [ACar("1", "GP1", 1)]));
        Assert.IsTrue(digest.IsEligibleForPublication);
    }

    /// <summary>
    /// CarPosition carries two different intervals: InClassGap is the time to the NEXT car up the
    /// order, InClassDifference is the time to the class LEADER. Reading the wrong one is right for
    /// P2 by coincidence and wrong for every finisher below it, which is invisible without this test.
    /// </summary>
    [TestMethod]
    public void GapToLeader_ComesFromTheLeaderInterval_NotTheCarAhead()
    {
        var digest = Build(ASession(1, "Race",
        [
            ACar("1", "GP1", 1),
            ACar("2", "GP1", 2, gapToLeader: "5.000", gapToCarAhead: "5.000"),
            ACar("3", "GP1", 3, gapToLeader: "11.000", gapToCarAhead: "6.000"),
        ]));

        var podium = digest.Sessions[0].Classes.Single().Podium;
        Assert.AreEqual("11.000", podium[2].GapToLeader,
            "P3 is 11.000 behind the leader and 6.000 behind P2; copy asserts the former");
    }

    /// <summary>
    /// A car that lost places must not surface a gain-shaped number. Positions are stored signed, so
    /// exposing -5 would let the validator accept "gained 5" about a car that dropped five.
    /// </summary>
    [TestMethod]
    public void PositionsLost_AreNotReportedAsPositionsGained()
    {
        var digest = Build(ASession(1, "Race", [ACar("1", "GP1", 1, positionsGained: -5)]));

        Assert.IsNull(digest.Sessions[0].Classes.Single().Podium[0].PositionsGained);
    }

    [TestMethod]
    public void UnsetLapsLed_StaysNull()
    {
        var digest = Build(ASession(1, "Race", [ACar("1", "GP1", 1)]));

        Assert.IsNull(digest.Sessions[0].Classes.Single().Podium[0].LapsLed);
    }

    /// <summary>
    /// Positions are assigned 1..N per class upstream under a stricter comparer than the grouping
    /// here, so inconsistent casing produces two cars at position 1 in one podium. Publishing two
    /// winners of the same class is exactly the failure this digest exists to prevent.
    /// </summary>
    [TestMethod]
    public void TwoCarsAtTheSamePosition_AreWarnedAbout()
    {
        var digest = Build(ASession(1, "Race",
        [
            ACar("1", "GP1", 1),
            ACar("2", "GP1", 2),
            ACar("9", "gp1 ", 1),
        ]));

        Assert.IsTrue(digest.Provenance.Warnings.Any(
            w => w.Contains("more than one car at position")),
            $"Warnings were: {string.Join("; ", digest.Provenance.Warnings)}");
    }

    [TestMethod]
    public void SessionWithNoName_IsWarnedAbout()
    {
        var digest = Build(ASession(1, "", [ACar("1", "GP1", 1)]));

        Assert.IsTrue(digest.Provenance.Warnings.Any(w => w.Contains("no name")));
    }

    [TestMethod]
    public void SourceHash_IsStableForIdenticalInput()
    {
        var first = Build(ASession(1, "Race", [ACar("1", "GP1", 1)]));
        var second = Build(ASession(1, "Race", [ACar("1", "GP1", 1)]));

        Assert.AreEqual(first.Provenance.SourceHash, second.Provenance.SourceHash);
        Assert.AreNotEqual(string.Empty, first.Provenance.SourceHash);
    }

    /// <summary>
    /// Stored results are rewritten in place when a later snapshot has more data, so the hash has to
    /// move when they do -- that is the only signal that a draft was written against stale facts.
    /// </summary>
    [TestMethod]
    public void SourceHash_ChangesWhenTheStoredResultChanges()
    {
        var before = Build(ASession(1, "Race", [ACar("1", "GP1", 1, laps: 100)]));
        var after = Build(ASession(1, "Race", [ACar("1", "GP1", 1, laps: 101)]));

        Assert.AreNotEqual(before.Provenance.SourceHash, after.Provenance.SourceHash);
    }

    /// <summary>
    /// The hash exists to reveal that the source moved under a draft that was already written. An
    /// organizer marking the event private or deleting it is exactly that, so covering only the
    /// session state would leave a withdrawn event looking unchanged.
    /// </summary>
    [TestMethod]
    [DataRow("IsPrivate")]
    [DataRow("IsDeleted")]
    [DataRow("Name")]
    public void SourceHash_ChangesWhenTheEventChanges(string field)
    {
        SessionResult Session() => ASession(1, "Race", [ACar("1", "GP1", 1)]);
        var before = EventDigestBuilder.Build(AnEvent(), "ChampCar", [Session()], Clock());

        var evt = AnEvent();
        switch (field)
        {
            case "IsPrivate": evt.IsPrivate = true; break;
            case "IsDeleted": evt.IsDeleted = true; break;
            case "Name": evt.Name = "A Different Event"; break;
        }
        var after = EventDigestBuilder.Build(evt, "ChampCar", [Session()], Clock());

        Assert.AreNotEqual(before.Provenance.SourceHash, after.Provenance.SourceHash);
    }

    [TestMethod]
    public void Provenance_CarriesTheClockAndVersion()
    {
        var digest = Build(ASession(1, "Race", [ACar("1", "GP1", 1)]));

        Assert.AreEqual(Now, digest.Provenance.ComputedAtUtc);
        Assert.AreEqual(EventDigestBuilder.CurrentDigestVersion, digest.Provenance.DigestVersion);
    }

    [TestMethod]
    public void Sessions_AreOrderedByStartTime()
    {
        var cars = new[] { ACar("1", "GP1", 1) };
        var digest = Build(
            ASession(2, "Sunday 7 Hour", cars, start: new DateTime(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc)),
            ASession(1, "Saturday 8 Hour", cars, start: new DateTime(2026, 8, 29, 10, 0, 0, DateTimeKind.Utc)));

        CollectionAssert.AreEqual(
            new[] { "Saturday 8 Hour", "Sunday 7 Hour" },
            digest.Sessions.Select(s => s.SessionName).ToList());
    }

    [TestMethod]
    public void AssertableFacts_ContainTheValuesCopyMayUse()
    {
        var digest = Build(ASession(1, "Saturday 8 Hour",
            [ACar("66", "GP1", 1, laps: 312, gapToLeader: "2 laps", bestTime: "1:58.402", lapsLed: 190)],
            [new EventEntry { Number = "66", Team = "Big Mission Motorsports", Class = "GP1" }],
            leadChanges: 7, yellows: 4));

        var facts = digest.AssertableFacts();

        foreach (var expected in new[]
        {
            "The New England Enduro", "ChampCar", "Thompson Speedway", "Full Course",
            "Saturday 8 Hour", "GP1", "66", "Big Mission Motorsports",
            "312", "2 laps", "1:58.402", "190", "7", "4",
        })
        {
            Assert.IsTrue(facts.Contains(expected), $"Expected '{expected}' among the assertable facts");
        }
    }

    /// <summary>
    /// The point of the whitelist: a number the model made up has nothing to match against. If these
    /// ever start passing, the validator built on this set silently stops catching invention.
    /// </summary>
    [TestMethod]
    [DataRow("415")]
    [DataRow("Definitely Not A Team")]
    [DataRow("1:42.001")]
    public void AssertableFacts_DoNotContainValuesThatWereNeverInTheResults(string invented)
    {
        var digest = Build(ASession(1, "Saturday 8 Hour",
            [ACar("66", "GP1", 1, laps: 312, bestTime: "1:58.402")],
            [new EventEntry { Number = "66", Team = "Big Mission Motorsports", Class = "GP1" }]));

        Assert.IsFalse(digest.AssertableFacts().Contains(invented));
    }

    [TestMethod]
    public void AssertableFacts_IgnoreCaseSoCopyMayRestyleNames()
    {
        var digest = Build(ASession(1, "Race",
            [ACar("66", "GP1", 1)],
            [new EventEntry { Number = "66", Team = "Big Mission Motorsports", Class = "GP1" }]));

        Assert.IsTrue(digest.AssertableFacts().Contains("big mission motorsports"));
    }

    [TestMethod]
    public void EventWithNoSessionsAtAll_ProducesAnEmptyDigestWithWarning()
    {
        var digest = Build();

        Assert.AreEqual(0, digest.Sessions.Count);
        Assert.AreEqual(string.Empty, digest.Provenance.SourceHash);
        Assert.IsTrue(digest.Provenance.Warnings.Any(w => w.Contains("No race sessions")));
    }
}
