using BigMission.TestHelpers.Testing;
using Microsoft.Extensions.Time.Testing;
using RedMist.Database.Models;
using RedMist.Social.Digest;
using RedMist.Social.Generation;
using RedMist.TimingCommon.Models;
using Event = RedMist.TimingCommon.Models.Configuration.Event;

namespace RedMist.TimingAndScoringService.Tests.Social;

/// <summary>
/// The composer decides what reaches a reviewer, so these cover the two ways that goes wrong: copy
/// with invented numbers being accepted, and a usable draft being thrown away. A generator stub
/// stands in for the model, since none of this logic depends on a real one.
/// </summary>
[TestClass]
public class PostComposerTests
{
    private static readonly DateTime Now = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Returns canned replies in order, and records what it was asked, so a test can assert on the
    /// correction turn rather than just on the number of calls.
    /// </summary>
    private sealed class StubGenerator(params string[] replies) : ICopyGenerator
    {
        private int callCount;

        public List<CopyRequest> Requests { get; } = [];

        public bool TruncateFirstReply { get; init; }

        public string ModelName { get; init; } = "claude-sonnet-5-test";

        public string? StopReasonName { get; init; }

        public Task<GeneratedCopy> GenerateAsync(CopyRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var index = callCount++;

            // The last reply repeats, so a test can supply one bad reply and exhaust the attempts.
            var text = replies[Math.Min(index, replies.Length - 1)];
            return Task.FromResult(new GeneratedCopy(
                text, ModelName, TruncateFirstReply && index == 0, StopReasonName));
        }
    }

    private static Event AnEvent() => new()
    {
        Id = 341,
        Name = "The New England Enduro",
        TrackName = "Thompson Speedway",
        StartDate = new DateTime(2026, 6, 13, 8, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2026, 6, 14, 18, 0, 0, DateTimeKind.Utc),
    };

    private static EventDigest ADigest(Event? evt = null)
    {
        var result = new SessionResult
        {
            EventId = 341,
            SessionId = 1,
            Start = new DateTime(2026, 6, 13, 10, 0, 0, DateTimeKind.Utc),
            SessionState = new SessionState
            {
                EventId = 341,
                SessionId = 1,
                SessionName = "Saturday Race",
                CarPositions =
                [
                    new() { Number = "66", Class = "GP1", ClassPosition = 1, LastLapCompleted = 214, BestTime = "1:58.402" },
                    new() { Number = "12", Class = "GP1", ClassPosition = 2, LastLapCompleted = 213, BestTime = "1:59.100" },
                ],
                EventEntries =
                [
                    new() { Number = "66", Name = "Bad Decision Racing", Class = "GP1" },
                    new() { Number = "12", Name = "Slow And Steady", Class = "GP1" },
                ],
            },
        };

        return EventDigestBuilder.Build(evt ?? AnEvent(), "ChampCar", [result], new FakeTimeProvider(Now));
    }

    private static ComposeOptions Options(int maxAttempts = 3, int maxCharacters = 1200) =>
        new("claude-sonnet-5", MaxTokens: 1024, MaxAttempts: maxAttempts, MaxCharacters: maxCharacters);

    private static PostComposer Composer(ICopyGenerator generator) =>
        new(generator, new DebugLoggerFactory());

    private static SocialPrompt APrompt() => new()
    {
        Version = 4,
        Kind = SocialPostKind.EventResults,
        Channel = SocialChannel.Facebook,
        SystemPrompt = "Write a short post.",
        VoiceGuide = "Warm and plain.",
        IsActive = true,
    };

    /// <summary>Copy drawn entirely from the digest costs one call and reaches review unflagged.</summary>
    [TestMethod]
    public async Task CopyThatMatchesTheDigest_IsAcceptedOnTheFirstAttempt()
    {
        var generator = new StubGenerator("Bad Decision Racing took GP1 at Thompson Speedway with 214 laps.");
        var composed = await Composer(generator).ComposeAsync(ADigest(), APrompt(), Options(), CancellationToken.None);

        Assert.AreEqual(1, composed.Attempts);
        Assert.IsFalse(composed.HasUnverifiedClaims);
        Assert.AreEqual(1, generator.Requests.Count);
    }

    /// <summary>
    /// The whole reason for a second attempt: an invented number has to be named back to the model,
    /// because re-sending an identical request leaves it to chance whether the number goes away.
    /// </summary>
    [TestMethod]
    public async Task AnInventedNumber_IsSentBackToTheModelByName()
    {
        var generator = new StubGenerator(
            "Bad Decision Racing won by 47 seconds.",
            "Bad Decision Racing took GP1 with 214 laps.");

        var composed = await Composer(generator).ComposeAsync(ADigest(), APrompt(), Options(), CancellationToken.None);

        Assert.AreEqual(2, composed.Attempts);
        Assert.IsFalse(composed.HasUnverifiedClaims);
        Assert.AreEqual("Bad Decision Racing took GP1 with 214 laps.", composed.Text);

        // The second call carries the rejected draft and a correction naming the offending number.
        var correction = generator.Requests[1].Messages;
        Assert.AreEqual(3, correction.Count, "Expected the original turn, the rejected draft and a correction");
        Assert.AreEqual(CopyRole.Assistant, correction[1].Role);
        Assert.AreEqual("Bad Decision Racing won by 47 seconds.", correction[1].Text);
        StringAssert.Contains(correction[2].Text, "47");
    }

    /// <summary>
    /// A draft that never validates is still handed over. Discarding it would leave the event with no
    /// post and a person with nothing to correct, which is worse than a draft carrying a loud flag.
    /// </summary>
    [TestMethod]
    public async Task CopyThatNeverValidates_IsStillReturnedButFlagged()
    {
        var generator = new StubGenerator("Bad Decision Racing won by 47 seconds.");
        var composed = await Composer(generator).ComposeAsync(ADigest(), APrompt(), Options(maxAttempts: 3), CancellationToken.None);

        Assert.AreEqual(3, composed.Attempts);
        Assert.IsTrue(composed.HasUnverifiedClaims);
        Assert.AreEqual("Bad Decision Racing won by 47 seconds.", composed.Text);
        Assert.IsTrue(composed.Warnings.Any(w => w.StartsWith("UNRESOLVED", StringComparison.Ordinal) && w.Contains("47")),
            $"Expected an unresolved warning naming 47. Got: {string.Join(" | ", composed.Warnings)}");
    }

    /// <summary>
    /// A reply cut off at the token ceiling ends mid-sentence. Publishing a fragment is worse than a
    /// short post, so it is a failure rather than something to accept quietly.
    /// </summary>
    [TestMethod]
    public async Task TruncatedCopy_IsRejectedAndRegenerated()
    {
        var generator = new StubGenerator(
            "Bad Decision Racing took GP1 at Thompson Speedway and then the",
            "Bad Decision Racing took GP1 with 214 laps.")
        {
            TruncateFirstReply = true,
        };

        var composed = await Composer(generator).ComposeAsync(ADigest(), APrompt(), Options(), CancellationToken.None);

        Assert.AreEqual(2, composed.Attempts);
        Assert.IsFalse(composed.HasUnverifiedClaims);
        StringAssert.Contains(generator.Requests[1].Messages[2].Text, "cut off");
    }

    [TestMethod]
    public async Task OverlongCopy_IsRejected()
    {
        var longCopy = new string('a', 200);
        var generator = new StubGenerator(longCopy, "Bad Decision Racing took GP1 with 214 laps.");

        var composed = await Composer(generator).ComposeAsync(
            ADigest(), APrompt(), Options(maxCharacters: 100), CancellationToken.None);

        Assert.AreEqual(2, composed.Attempts);
        StringAssert.Contains(generator.Requests[1].Messages[2].Text, "200 characters");
    }

    [TestMethod]
    public async Task AnEmptyReply_IsRejected()
    {
        var generator = new StubGenerator("   ", "Bad Decision Racing took GP1 with 214 laps.");
        var composed = await Composer(generator).ComposeAsync(ADigest(), APrompt(), Options(), CancellationToken.None);

        Assert.AreEqual(2, composed.Attempts);
        Assert.IsFalse(composed.HasUnverifiedClaims);
    }

    /// <summary>
    /// A private, hidden, simulated or deleted event must never reach a generator. The job filters
    /// them out, so this is the backstop that turns a filtering mistake into a crash rather than a
    /// draft of something that was never meant to be public.
    /// </summary>
    [TestMethod]
    public async Task AnIneligibleEvent_IsRefusedBeforeAnyGeneratorCall()
    {
        var hidden = AnEvent();
        hidden.IsPrivate = true;
        var generator = new StubGenerator("anything");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            Composer(generator).ComposeAsync(ADigest(hidden), APrompt(), Options(), CancellationToken.None));

        Assert.AreEqual(0, generator.Requests.Count, "The model must not be called for an ineligible event");
    }

    /// <summary>
    /// What the digest could not establish is context a reviewer needs before judging the copy, so it
    /// travels with the draft rather than staying in the job's logs.
    /// </summary>
    [TestMethod]
    public async Task DigestWarnings_ReachTheReviewer()
    {
        var evt = AnEvent();
        var orphan = new SessionResult { EventId = 341, SessionId = 9, Start = Now };
        var digest = EventDigestBuilder.Build(evt, "ChampCar", [orphan, ADigestSourceSession()], new FakeTimeProvider(Now));

        var generator = new StubGenerator("Bad Decision Racing took GP1 with 214 laps.");
        var composed = await Composer(generator).ComposeAsync(digest, APrompt(), Options(), CancellationToken.None);

        Assert.IsTrue(composed.Warnings.Any(w => w.StartsWith("Digest:", StringComparison.Ordinal) && w.Contains("no session state")),
            $"Expected a digest warning. Got: {string.Join(" | ", composed.Warnings)}");
    }

    /// <summary>
    /// A link is the one injection payload worth crafting for a public page, and it is the one thing
    /// the fact checks structurally cannot catch: a hostile entry name is *in* the digest, so the name
    /// check passes it and the numeric check never looks at it. Flagged for review rather than
    /// rejected, so a team genuinely named after a domain does not burn every attempt.
    /// </summary>
    [TestMethod]
    [DataRow("Bad Decision Racing took GP1. More at evil-example.com")]
    [DataRow("Bad Decision Racing took GP1. https://evil.example/x")]
    [DataRow("Bad Decision Racing took GP1. Follow @notarealaccount")]
    public async Task CopyContainingALinkOrHandle_IsFlaggedForCloseReview(string reply)
    {
        var generator = new StubGenerator(reply);
        var composed = await Composer(generator).ComposeAsync(ADigest(), APrompt(), Options(), CancellationToken.None);

        Assert.AreEqual(1, generator.Requests.Count, "A link is a review warning, not a reason to regenerate");
        Assert.IsTrue(composed.Warnings.Any(w => w.StartsWith("REVIEW CLOSELY", StringComparison.Ordinal)),
            $"Expected a link warning. Got: {string.Join(" | ", composed.Warnings)}");
    }

    [TestMethod]
    public async Task OrdinaryCopy_IsNotFlaggedAsContainingALink()
    {
        var generator = new StubGenerator("Bad Decision Racing took GP1 at Thompson Speedway with 214 laps.");
        var composed = await Composer(generator).ComposeAsync(ADigest(), APrompt(), Options(), CancellationToken.None);

        Assert.IsFalse(composed.Warnings.Any(w => w.StartsWith("REVIEW CLOSELY", StringComparison.Ordinal)),
            $"Unexpected link warning: {string.Join(" | ", composed.Warnings)}");
    }

    /// <summary>
    /// An empty reply has several causes that look identical from here. A refusal in particular would
    /// otherwise leave a blank draft in the review queue with nothing to explain it.
    /// </summary>
    [TestMethod]
    public async Task AnEmptyReply_CarriesTheStopReasonIntoTheWarnings()
    {
        var generator = new StubGenerator(string.Empty) { StopReasonName = "refusal" };
        var composed = await Composer(generator).ComposeAsync(ADigest(), APrompt(), Options(), CancellationToken.None);

        Assert.IsTrue(composed.HasUnverifiedClaims);
        Assert.IsTrue(composed.Warnings.Any(w => w.Contains("refusal", StringComparison.Ordinal)),
            $"Expected the stop reason in the warnings. Got: {string.Join(" | ", composed.Warnings)}");
    }

    /// <summary>The model actually used is carried through, so a change in output can be traced to it.</summary>
    [TestMethod]
    public async Task TheServingModel_IsRecordedRatherThanTheRequestedOne()
    {
        var generator = new StubGenerator("Bad Decision Racing took GP1 with 214 laps.")
        {
            ModelName = "claude-sonnet-5-20260514",
        };

        var composed = await Composer(generator).ComposeAsync(ADigest(), APrompt(), Options(), CancellationToken.None);

        Assert.AreEqual("claude-sonnet-5-20260514", composed.Model);
    }

    /// <summary>The stored prompt and the non-negotiable rules both reach the model on every attempt.</summary>
    [TestMethod]
    public async Task EveryAttempt_CarriesTheStoredPromptAndTheHardRules()
    {
        var generator = new StubGenerator("Bad Decision Racing won by 47 seconds.");
        await Composer(generator).ComposeAsync(ADigest(), APrompt(), Options(maxAttempts: 2), CancellationToken.None);

        foreach (var request in generator.Requests)
        {
            StringAssert.Contains(request.SystemPrompt, "Write a short post.");
            StringAssert.Contains(request.SystemPrompt, "Assert nothing that is not in the data.");
        }
    }

    private static SessionResult ADigestSourceSession() => new()
    {
        EventId = 341,
        SessionId = 1,
        Start = new DateTime(2026, 6, 13, 10, 0, 0, DateTimeKind.Utc),
        SessionState = new SessionState
        {
            EventId = 341,
            SessionId = 1,
            SessionName = "Saturday Race",
            CarPositions = [new() { Number = "66", Class = "GP1", ClassPosition = 1, LastLapCompleted = 214 }],
            EventEntries = [new() { Number = "66", Name = "Bad Decision Racing", Class = "GP1" }],
        },
    };
}
