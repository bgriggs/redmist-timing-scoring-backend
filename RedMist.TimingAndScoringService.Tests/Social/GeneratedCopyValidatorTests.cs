using Microsoft.Extensions.Time.Testing;
using RedMist.Database.Models;
using RedMist.Social.Digest;
using RedMist.Social.Validation;
using RedMist.TimingCommon.Models;
using Event = RedMist.TimingCommon.Models.Configuration.Event;

namespace RedMist.TimingAndScoringService.Tests.Social;

/// <summary>
/// This is the last thing standing between a hallucinated race result and a public page, so the
/// cases below are written as the failures they are meant to catch rather than as API coverage.
/// A number the generator invented has nothing in the digest to match, which is the whole mechanism.
/// </summary>
[TestClass]
public class GeneratedCopyValidatorTests
{
    /// <summary>
    /// One fixed digest all the copy is checked against: car 66 won GP1 from a 14 car field, 312 laps,
    /// best lap 1:58.402, two laps clear, seven lead changes, four cautions.
    /// </summary>
    private static EventDigest Digest()
    {
        var cars = new List<CarPosition>();
        cars.Add(new CarPosition
        {
            Number = "66",
            Class = "GP1",
            ClassPosition = 1,
            LastLapCompleted = 312,
            BestTime = "1:58.402",
            LapsLedInClass = 190,
            InClassPositionsGained = 5,
        });
        for (int i = 2; i <= 14; i++)
        {
            cars.Add(new CarPosition
            {
                Number = $"{i}00",
                Class = "GP1",
                ClassPosition = i,
                LastLapCompleted = 300,
                // The leader has no interval of its own; "2 laps clear" is the runner-up's deficit,
                // and it needs to be a real fact rather than passing because 2 is also a position.
                InClassDifference = i == 2 ? "2 laps" : null,
            });
        }

        var result = new SessionResult
        {
            EventId = 341,
            SessionId = 1,
            Start = new DateTime(2026, 6, 29, 10, 0, 0, DateTimeKind.Utc),
            SessionState = new SessionState
            {
                EventId = 341,
                SessionId = 1,
                SessionName = "Saturday 8 Hour",
                CarPositions = cars,
                EventEntries = [new EventEntry { Number = "66", Team = "Big Mission Motorsports", Class = "GP1" }],
                LeadChanges = 7,
                NumberOfYellows = 4,
            },
        };

        var evt = new Event
        {
            Id = 341,
            Name = "The New England Enduro",
            TrackName = "Thompson Speedway",
            StartDate = new DateTime(2026, 6, 29, 8, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 6, 30, 18, 0, 0, DateTimeKind.Utc),
        };

        return EventDigestBuilder.Build(evt, "ChampCar", [result],
            new FakeTimeProvider(new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc)));
    }

    private static CopyValidationResult Validate(string copy) =>
        GeneratedCopyValidator.Validate(copy, Digest());

    [TestMethod]
    public void CopyDrawnEntirelyFromTheDigest_Passes()
    {
        var result = Validate(
            "Big Mission Motorsports took the GP1 win at Thompson Speedway, completing 312 laps " +
            "to finish 2 laps clear of the field. Car 66 led 190 laps and set a best of 1:58.402.");

        Assert.IsFalse(result.HasInventedNumbers,
            $"Unexpected: {string.Join(", ", result.UnverifiedNumbers)}");
    }

    [TestMethod]
    [DataRow("The team completed 415 laps.", "415")]
    [DataRow("A best lap of 1:42.001 sealed it.", "1:42.001")]
    [DataRow("Car 99 took the win.", "99")]
    [DataRow("They finished 37 seconds ahead.", "37")]
    public void ANumberThatIsNotInTheDigest_IsFlagged(string copy, string expected)
    {
        var result = Validate(copy);

        Assert.IsTrue(result.HasInventedNumbers);
        CollectionAssert.Contains(result.UnverifiedNumbers.ToList(), expected);
    }

    /// <summary>
    /// A generator will often spell small numbers out. Without word-to-digit mapping, "seven lead
    /// changes" would be rejected even though the digest says exactly seven -- which would make the
    /// validator fire constantly on correct copy and get switched off.
    /// </summary>
    [TestMethod]
    [DataRow("There were seven lead changes.")]
    [DataRow("Four cautions slowed the race.")]
    public void SpelledOutNumbersThatMatchTheDigest_Pass(string copy)
    {
        var result = Validate(copy);

        Assert.IsFalse(result.HasInventedNumbers,
            $"Unexpected: {string.Join(", ", result.UnverifiedNumbers)}");
    }

    [TestMethod]
    public void SpelledOutNumbersThatContradictTheDigest_AreFlagged()
    {
        var result = Validate("There were nineteen lead changes.");

        Assert.IsTrue(result.HasInventedNumbers);
        CollectionAssert.Contains(result.UnverifiedNumbers.ToList(), "nineteen");
    }

    [TestMethod]
    public void LapTimesAreComparedWhole_NotDigitByDigit()
    {
        // Every digit of 1:58.401 appears somewhere in the digest; only the whole token differs.
        var result = Validate("A best lap of 1:58.401.");

        Assert.IsTrue(result.HasInventedNumbers, "A lap time must not pass because its digits appear elsewhere");
        CollectionAssert.Contains(result.UnverifiedNumbers.ToList(), "1:58.401");
    }

    /// <summary>
    /// A comma groups thousands only when three digits follow it. Treating every comma as a separator
    /// to strip turns the comma-joined list "1,2,3" into the invented number 123, which would make the
    /// validator reject an ordinary podium listing.
    /// </summary>
    [TestMethod]
    public void CommaJoinedNumbers_AreReadSeparately_NotConcatenated()
    {
        var result = Validate("The podium was 1,2,3.");

        Assert.IsFalse(result.HasInventedNumbers,
            $"Unexpected: {string.Join(", ", result.UnverifiedNumbers)}");
    }

    [TestMethod]
    public void AThousandsSeparatedNumberNotInTheDigest_IsStillFlagged()
    {
        var result = Validate("They covered 1,312 laps in total.");

        Assert.IsTrue(result.HasInventedNumbers);
        CollectionAssert.Contains(result.UnverifiedNumbers.ToList(), "1,312");
    }

    [TestMethod]
    public void OrdinalsAndTrailingPunctuation_DoNotCauseFalsePositives()
    {
        var result = Validate("They finished 1st. The gap was 2 laps.");

        Assert.IsFalse(result.HasInventedNumbers,
            $"Unexpected: {string.Join(", ", result.UnverifiedNumbers)}");
    }

    [TestMethod]
    public void EachInventedNumber_IsReportedOnce()
    {
        var result = Validate("They ran 415 laps. Yes, 415 laps. All 415 of them.");

        Assert.AreEqual(1, result.UnverifiedNumbers.Count);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    public void EmptyCopy_IsNotTreatedAsInvention(string? copy)
    {
        var result = GeneratedCopyValidator.Validate(copy!, Digest());

        Assert.IsFalse(result.HasInventedNumbers);
        Assert.AreEqual(0, result.PossibleUnverifiedNames.Count);
    }

    [TestMethod]
    public void CopyWithNoNumbersAtAll_Passes()
    {
        var result = Validate("What a race at Thompson Speedway. Congratulations to the winners!");

        Assert.IsFalse(result.HasInventedNumbers);
    }

    [TestMethod]
    public void AFabricatedTeamName_IsSurfacedAsAdvisory()
    {
        var result = Validate("The win went to Phantom Racing Collective in GP1.");

        CollectionAssert.Contains(result.PossibleUnverifiedNames.ToList(), "Phantom Racing Collective");
        Assert.IsFalse(result.HasInventedNumbers, "Name findings must not be reported as invented numbers");
    }

    [TestMethod]
    public void ARealTeamNameFromTheDigest_IsNotSurfaced()
    {
        var result = Validate("The win went to Big Mission Motorsports in GP1.");

        Assert.AreEqual(0, result.PossibleUnverifiedNames.Count,
            $"Unexpected: {string.Join(", ", result.PossibleUnverifiedNames)}");
    }

    /// <summary>
    /// Name detection is advisory precisely because it cannot tell a fabricated team from ordinary
    /// capitalized prose. This pins that limitation rather than pretending it is solved: if these
    /// ever stop being reported, the heuristic has been changed and its noise level with it.
    /// </summary>
    [TestMethod]
    public void OrdinaryCapitalizedProse_IsAlsoSurfaced_WhichIsWhyNameFindingsAreAdvisory()
    {
        var result = Validate("A great day at Victory Lane for everyone.");

        CollectionAssert.Contains(result.PossibleUnverifiedNames.ToList(), "Victory Lane");
        Assert.IsFalse(result.HasInventedNumbers);
    }

    [TestMethod]
    public void NameFindings_NeverBlockOnTheirOwn()
    {
        var result = Validate("Congratulations to Phantom Racing Collective on 312 laps.");

        Assert.IsFalse(result.HasInventedNumbers,
            "Only numeric invention is disqualifying; names are for a human to glance at");
        Assert.AreNotEqual(0, result.PossibleUnverifiedNames.Count);
    }

    [TestMethod]
    public void TheEventYear_CountsAsAKnownFact()
    {
        var result = Validate("A fitting end to the 2026 season opener.");

        Assert.IsFalse(result.HasInventedNumbers);
    }

    [TestMethod]
    [DataRow("The race ran on June 29 and 30.")]
    [DataRow("Held 6/29/2026 at Thompson Speedway.")]
    public void DatingTheRace_DoesNotTripTheValidator(string copy)
    {
        var result = Validate(copy);

        Assert.IsFalse(result.HasInventedNumbers,
            $"Steady false rejections are how a gate like this gets switched off: {string.Join(", ", result.UnverifiedNumbers)}");
    }

    /// <summary>
    /// Digits inside names must not authorize numbers. "GP1" and "Saturday 8 Hour" would otherwise put
    /// 1 and 8 into the numeric whitelist, and small integers are most of what a results post asserts.
    /// </summary>
    [TestMethod]
    public void DigitsInsideNames_DoNotAuthorizeNumericClaims()
    {
        var result = Validate("Only 8 cars were still running at the end.");

        Assert.IsTrue(result.HasInventedNumbers,
            "The 8 in the session name 'Saturday 8 Hour' must not verify a car count");
        CollectionAssert.Contains(result.UnverifiedNumbers.ToList(), "8");
    }

    [TestMethod]
    [DataRow("There were twenty-one lead changes.", "twenty")]
    [DataRow("The winner completed one hundred laps.", "hundred")]
    [DataRow("A dozen cautions slowed the race.", "dozen")]
    public void QuantityWordsTheDictionaryCannotResolve_AreFlagged(string copy, string expected)
    {
        var result = Validate(copy);

        Assert.IsTrue(result.HasInventedNumbers,
            $"'{copy}' should not pass silently");
        CollectionAssert.Contains(result.UnverifiedNumbers.ToList(), expected);
    }

    [TestMethod]
    public void AnOrdinalBeyondFifth_IsStillChecked()
    {
        var result = Validate("Big Mission Motorsports finished ninth in GP1.");

        Assert.IsTrue(result.HasInventedNumbers, "They won; ninth is not in the digest");
    }

    [TestMethod]
    public void ANameOpeningASentence_IsStillSurfaced()
    {
        var result = Validate("Phantom Collective won GP1. It was close.");

        CollectionAssert.Contains(result.PossibleUnverifiedNames.ToList(), "Phantom Collective");
    }

    /// <summary>
    /// The limitation that matters most, pinned so nobody reads a clean result as "this is accurate".
    /// Every number below is genuinely in the digest; they are simply attached to the wrong entrant.
    /// Recombination is not detectable by whitelist comparison, which is why human review is the gate
    /// and this validator is only the floor.
    /// </summary>
    [TestMethod]
    [DataRow("Rival Racing took the GP1 win with 312 laps and a best of 1:58.402.")]
    [DataRow("Big Mission Motorsports finished 2nd in GP1.")]
    public void MisattributedButRealNumbers_PassUndetected(string copy)
    {
        var result = Validate(copy);

        Assert.IsFalse(result.HasInventedNumbers,
            "Documents a known limit rather than desired behavior: if this starts failing, the " +
            "validator has gained association-awareness and the review guidance should be revisited");
    }
}
