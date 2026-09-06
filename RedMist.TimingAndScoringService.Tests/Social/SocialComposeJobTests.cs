using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.Social.Digest;
using RedMist.Social.Generation;
using RedMist.Social.Imaging;
using RedMist.SocialCompose;
using RedMist.TimingCommon.Models;
using Event = RedMist.TimingCommon.Models.Configuration.Event;

namespace RedMist.TimingAndScoringService.Tests.Social;

/// <summary>
/// Covers the two decisions the job makes that a person cannot undo: which events it draws on, and
/// when it is allowed to overwrite a draft somebody may already have read.
/// </summary>
[TestClass]
public class SocialComposeJobTests
{
    private static readonly DateTime Now = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    private sealed class StubGenerator(string reply = "Bad Decision Racing took GP1 with 214 laps.") : ICopyGenerator
    {
        public int Calls { get; private set; }

        public Task<GeneratedCopy> GenerateAsync(CopyRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new GeneratedCopy(reply, "claude-sonnet-5-test", false));
        }
    }

    private static SocialComposeSettings Settings(
        int settleHours = 24,
        int lookbackDays = 14,
        int maxEvents = 10,
        bool images = false,
        int maxImages = 2) => new()
        {
            Model = "claude-sonnet-5",
            MaxTokens = 1024,
            MaxAttempts = 3,
            MaxCharacters = 1200,
            Channel = SocialChannel.Facebook,
            SettlePeriod = TimeSpan.FromHours(settleHours),
            LookbackWindow = TimeSpan.FromDays(lookbackDays),
            MaxEventsPerRun = maxEvents,
            ImagesEnabled = images,
            MaxImagesPerPost = maxImages,
            ImageCapture = new SessionImageCaptureOptions(
                "https://redmist.racing", "embed=1", 900, 1100, 2, ".car-row-container",
                ClipSelector: "", HideSelectors: [], BlockedUrlSubstrings: [],
                TimeSpan.FromSeconds(45), TimeSpan.Zero),
        };

    private static TestDbContextFactory Db()
    {
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new TestDbContextFactory(options);
    }

    private static SocialComposeJob Job(
        TestDbContextFactory db,
        SocialComposeSettings? settings = null,
        ICopyGenerator? generator = null,
        ISessionImageCapture? capture = null,
        ISocialImageStore? store = null)
    {
        var loggerFactory = new DebugLoggerFactory();
        var composer = new PostComposer(generator ?? new StubGenerator(), loggerFactory);
        return new SocialComposeJob(
            loggerFactory,
            db,
            composer,
            settings ?? Settings(),
            Mock.Of<IHostApplicationLifetime>(),
            capture,
            store,
            new FakeTimeProvider(Now));
    }

    /// <summary>Returns a fixed image, or fails, without launching anything.</summary>
    /// <param name="failure">Thrown instead of returning an image.</param>
    /// <param name="failOnSessionId">
    /// Restricts <paramref name="failure"/> to one session, so a partial set can be exercised. Zero
    /// means every session fails.
    /// </param>
    private sealed class StubCapture(Exception? failure = null, int failOnSessionId = 0) : ISessionImageCapture
    {
        public List<SessionImageRequest> Requests { get; } = [];

        public StubCapture(int failOnSessionId)
            : this(new SessionImageCaptureException("the page never rendered"), failOnSessionId) { }

        public Task<CapturedImage> CaptureAsync(SessionImageRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (failure is not null && (failOnSessionId == 0 || request.SessionId == failOnSessionId))
                throw failure;

            // Distinct bytes per session, so the store path differs and a test can tell them apart.
            return Task.FromResult(new CapturedImage([1, 2, (byte)request.SessionId], 1800, 2200));
        }
    }

    private sealed class StubStore : ISocialImageStore
    {
        public Task<string> StoreAsync(int eventId, int sessionId, CapturedImage image, CancellationToken cancellationToken) =>
            Task.FromResult($"https://cdn.example/social/event-{eventId}/session-{sessionId}.png");
    }

    private static Event AnEvent(int id, DateTime endDate) => new()
    {
        Id = id,
        OrganizationId = 1,
        Name = $"Event {id}",
        TrackName = "Thompson Speedway",
        StartDate = endDate.AddDays(-1),
        EndDate = endDate,
    };

    /// <summary>An event with one race session stored, so a full run has something to draft from.</summary>
    private static Event AnEventWithResults(TsContext context, int id, DateTime endDate)
    {
        context.SessionResults.Add(new SessionResult
        {
            EventId = id,
            SessionId = 1,
            Start = endDate.AddHours(-4),
            SessionState = new SessionState
            {
                EventId = id,
                SessionId = 1,
                SessionName = "Saturday Race",
                CarPositions = [new() { Number = "66", Class = "GP1", ClassPosition = 1, LastLapCompleted = 214 }],
                EventEntries = [new() { Number = "66", Name = "Bad Decision Racing", Class = "GP1" }],
            },
        });

        return AnEvent(id, endDate);
    }

    private static EventDigest ADigest(string sessionName = "Saturday Race")
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
                SessionName = sessionName,
                CarPositions = [new() { Number = "66", Class = "GP1", ClassPosition = 1, LastLapCompleted = 214 }],
                EventEntries = [new() { Number = "66", Name = "Bad Decision Racing", Class = "GP1" }],
            },
        };

        return EventDigestBuilder.Build(
            new Event
            {
                Id = 341,
                Name = "The New England Enduro",
                TrackName = "Thompson Speedway",
                StartDate = new DateTime(2026, 6, 13, 8, 0, 0, DateTimeKind.Utc),
                EndDate = new DateTime(2026, 6, 14, 18, 0, 0, DateTimeKind.Utc),
            },
            "ChampCar",
            [result],
            new FakeTimeProvider(Now));
    }

    private static SocialPost APost(SocialPostState state, string? sourceHash, string? generated = "a draft") => new()
    {
        Kind = SocialPostKind.EventResults,
        EventId = 341,
        Channel = SocialChannel.Facebook,
        IdempotencyKey = "EventResults:341:Facebook",
        State = state,
        DigestSourceHash = sourceHash,
        GeneratedText = generated,
        CreatedUtc = Now,
        ScheduledUtc = Now,
    };

    // ---- Which events are drawn on -------------------------------------------------------------

    /// <summary>
    /// An EndDate is a date, so it lands at midnight. Without the settle period the job would draft
    /// from a race that was still running when the day ticked over.
    /// </summary>
    [TestMethod]
    public async Task AnEventThatEndedTooRecently_IsNotDrafted()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Events.Add(AnEvent(1, Now.AddHours(-2)));
            seed.Events.Add(AnEvent(2, Now.AddHours(-48)));
            await seed.SaveChangesAsync();
        }

        await using var context = db.CreateDbContext();
        var found = await Job(db).LoadCandidateEventsAsync(context, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { 2 }, found.Select(e => e.Id).ToList());
    }

    [TestMethod]
    public async Task AnEventOlderThanTheLookbackWindow_IsNotDrafted()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Events.Add(AnEvent(1, Now.AddDays(-3)));
            seed.Events.Add(AnEvent(2, Now.AddDays(-40)));
            await seed.SaveChangesAsync();
        }

        await using var context = db.CreateDbContext();
        var found = await Job(db).LoadCandidateEventsAsync(context, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { 1 }, found.Select(e => e.Id).ToList());
    }

    /// <summary>
    /// These are the events that must never be written about. The digest refuses them too, but they
    /// are excluded here so they are never loaded, never digested and never counted against the
    /// per-run ceiling.
    /// </summary>
    [TestMethod]
    public async Task PrivateHiddenSimulatedDeletedAndLiveEvents_AreNeverCandidates()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            var ended = Now.AddDays(-3);
            var pub = AnEvent(1, ended);

            var priv = AnEvent(2, ended);
            priv.IsPrivate = true;

            var hidden = AnEvent(3, ended);
            hidden.HideName = true;

            var sim = AnEvent(4, ended);
            sim.IsSimulation = true;

            var deleted = AnEvent(5, ended);
            deleted.IsDeleted = true;

            var live = AnEvent(6, ended);
            live.IsLive = true;

            seed.Events.AddRange(pub, priv, hidden, sim, deleted, live);
            await seed.SaveChangesAsync();
        }

        await using var context = db.CreateDbContext();
        var found = await Job(db).LoadCandidateEventsAsync(context, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { 1 }, found.Select(e => e.Id).ToList());
    }

    /// <summary>
    /// Oldest first, because the ceiling can defer an event to the next run and the ones nearest to
    /// leaving the lookback window have the least time left to be drafted in.
    /// </summary>
    [TestMethod]
    public async Task Candidates_AreOrderedOldestFirst()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Events.Add(AnEvent(1, Now.AddDays(-2)));
            seed.Events.Add(AnEvent(2, Now.AddDays(-9)));
            seed.Events.Add(AnEvent(3, Now.AddDays(-5)));
            await seed.SaveChangesAsync();
        }

        await using var context = db.CreateDbContext();
        var found = await Job(db).LoadCandidateEventsAsync(context, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { 2, 3, 1 }, found.Select(e => e.Id).ToList());
    }

    /// <summary>
    /// Every draft costs API calls, so an unexpected burst -- a bulk import, a batch of corrected end
    /// dates -- must cost one run's ceiling rather than an open-ended bill.
    /// </summary>
    [TestMethod]
    public async Task TheRunCeiling_BoundsHowManyDraftsAreWritten()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            for (var i = 1; i <= 6; i++)
                seed.Events.Add(AnEventWithResults(seed, i, Now.AddDays(-i - 1)));
            await seed.SaveChangesAsync();
        }

        await Job(db, Settings(maxEvents: 2)).RunAsync(CancellationToken.None);

        await using var check = db.CreateDbContext();
        Assert.AreEqual(2, await check.SocialPosts.CountAsync());
    }

    /// <summary>
    /// The ceiling has to count drafts written, not events looked at. Counting candidates would let
    /// already-drafted events eat the budget, and since they leave the lookback window last, anything
    /// behind them would age out without ever being written about.
    /// </summary>
    [TestMethod]
    public async Task AlreadyDraftedEvents_DoNotConsumeTheRunCeiling()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            for (var i = 1; i <= 4; i++)
                seed.Events.Add(AnEventWithResults(seed, i, Now.AddDays(-i - 1)));
            await seed.SaveChangesAsync();
        }

        // Two runs at a ceiling of two. If the ceiling counted candidates, the second run would spend
        // both slots re-examining events 4 and 3 and never reach 2 and 1.
        await Job(db, Settings(maxEvents: 2)).RunAsync(CancellationToken.None);
        await Job(db, Settings(maxEvents: 2)).RunAsync(CancellationToken.None);

        await using var check = db.CreateDbContext();
        CollectionAssert.AreEquivalent(
            new[] { 1, 2, 3, 4 },
            await check.SocialPosts.Select(p => p.EventId!.Value).ToListAsync());
    }

    // ---- When an existing draft may be overwritten ---------------------------------------------

    /// <summary>
    /// The costly one. Session results are rewritten in place, so redrafting has to exist; but on
    /// every other night the source is identical, and regenerating would spend a call to replace a
    /// reviewer's draft with differently-worded copy saying the same thing.
    /// </summary>
    [TestMethod]
    public void ADraftFromUnchangedResults_IsLeftAlone()
    {
        var digest = ADigest();
        var existing = APost(SocialPostState.PendingReview, digest.Provenance.SourceHash);

        var reason = SocialComposeJob.WhyNotRecompose(existing, digest);

        Assert.IsNotNull(reason);
        StringAssert.Contains(reason, "have not changed");
    }

    [TestMethod]
    public void ADraftWhoseResultsWereRewritten_IsRedrafted()
    {
        var existing = APost(SocialPostState.PendingReview, "a-hash-from-an-earlier-snapshot");

        Assert.IsNull(SocialComposeJob.WhyNotRecompose(existing, ADigest()));
    }

    /// <summary>
    /// A row can carry a matching hash and no copy when a previous run recorded a generation failure.
    /// That is exactly the case redrafting exists for, so the hash alone must not stop it.
    /// </summary>
    [TestMethod]
    public void AFailedAttemptWithNoCopy_IsRetriedEvenWhenTheResultsAreUnchanged()
    {
        var digest = ADigest();
        var existing = APost(SocialPostState.Failed, digest.Provenance.SourceHash, generated: null);

        Assert.IsNull(SocialComposeJob.WhyNotRecompose(existing, digest));
    }

    /// <summary>
    /// The way a post gets stranded. A draft is written, the results are then rewritten, the redraft
    /// hits an API outage -- and the failed row is left holding copy describing the superseded
    /// results. If the hash comparison applied to Failed, this row would never be picked up again: it
    /// would sit outside the review queue forever, carrying text a reviewer could still approve.
    /// </summary>
    [TestMethod]
    public void AFailedRedraftStillHoldingSupersededCopy_IsRetried()
    {
        var digest = ADigest();
        var stranded = APost(SocialPostState.Failed, digest.Provenance.SourceHash, generated: "copy about the old results");
        stranded.Error = "Anthropic API returned 503 Service Unavailable";

        Assert.IsNull(SocialComposeJob.WhyNotRecompose(stranded, digest),
            "A failed post must always be retried, whatever its hash says");
    }

    /// <summary>
    /// Overwriting a human edit would silently discard work, and the reviewer would have no way to
    /// tell it had happened.
    /// </summary>
    [TestMethod]
    public void ADraftEditedByHand_IsNeverOverwritten()
    {
        var existing = APost(SocialPostState.PendingReview, "an-older-hash");
        existing.EditedText = "what a person actually wants published";

        var reason = SocialComposeJob.WhyNotRecompose(existing, ADigest());

        Assert.IsNotNull(reason);
        StringAssert.Contains(reason, "edited by hand");
    }

    /// <summary>
    /// Past review, a post is either somebody's decision or already public. Redrafting an approved one
    /// would publish copy nobody approved; redrafting a published one would orphan its remote id; and
    /// redrafting a rejected one would put back something a person deliberately turned down.
    /// </summary>
    [TestMethod]
    [DataRow(SocialPostState.Approved)]
    [DataRow(SocialPostState.Publishing)]
    [DataRow(SocialPostState.Published)]
    [DataRow(SocialPostState.Rejected)]
    public void APostPastReview_IsNeverRedrafted(SocialPostState state)
    {
        var reason = SocialComposeJob.WhyNotRecompose(APost(state, "an-older-hash"), ADigest());

        Assert.IsNotNull(reason, $"{state} must not be overwritten");
        StringAssert.Contains(reason, state.ToString());
    }

    [TestMethod]
    [DataRow(SocialPostState.Draft)]
    [DataRow(SocialPostState.PendingReview)]
    [DataRow(SocialPostState.Failed)]
    public void APostStillBeforeReview_MayBeRedrafted(SocialPostState state)
    {
        Assert.IsNull(SocialComposeJob.WhyNotRecompose(APost(state, "an-older-hash"), ADigest()));
    }

    // ---- Prompt seeding ------------------------------------------------------------------------

    /// <summary>
    /// The first run has no prompt to work from. Seeding writes a real, versioned row so the database
    /// stays the single answer to "what were we telling the model" from that point on.
    /// </summary>
    [TestMethod]
    public async Task TheFirstRun_SeedsAPromptItCanThenUse()
    {
        var db = Db();
        await using var context = db.CreateDbContext();

        var prompt = await Job(db).GetOrSeedPromptAsync(context, CancellationToken.None);

        Assert.AreEqual(1, prompt.Version);
        Assert.IsTrue(prompt.IsActive);
        Assert.AreEqual(SocialPostKind.EventResults, prompt.Kind);
        Assert.AreEqual(SocialChannel.Facebook, prompt.Channel);
        Assert.IsFalse(string.IsNullOrWhiteSpace(prompt.SystemPrompt));

        await using var reread = db.CreateDbContext();
        Assert.AreEqual(1, await reread.SocialPrompts.CountAsync(), "The seed must be persisted, not just returned");
    }

    [TestMethod]
    public async Task AnExistingPrompt_IsUsedRatherThanReseeded()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.SocialPrompts.Add(new SocialPrompt
            {
                Version = 7,
                Kind = SocialPostKind.EventResults,
                Channel = SocialChannel.Facebook,
                SystemPrompt = "The tuned prompt.",
                IsActive = true,
                CreatedUtc = Now,
            });
            await seed.SaveChangesAsync();
        }

        await using var context = db.CreateDbContext();
        var prompt = await Job(db).GetOrSeedPromptAsync(context, CancellationToken.None);

        Assert.AreEqual(7, prompt.Version);
        Assert.AreEqual("The tuned prompt.", prompt.SystemPrompt);
    }

    /// <summary>
    /// Prompts that all exist but are inactive is a state somebody chose. Re-seeding would undo that
    /// decision, and -- because the seed is always version 1 -- would collide with the existing
    /// version 1 on the unique (Kind, Channel, Version) index and fail naming the wrong cause.
    /// </summary>
    [TestMethod]
    public async Task AllPromptsInactive_StopsTheRunRatherThanReseedingOverTheDecision()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.SocialPrompts.Add(new SocialPrompt
            {
                Version = 1,
                Kind = SocialPostKind.EventResults,
                Channel = SocialChannel.Facebook,
                SystemPrompt = "A deliberately parked prompt.",
                IsActive = false,
                CreatedUtc = Now,
            });
            await seed.SaveChangesAsync();
        }

        await using var context = db.CreateDbContext();
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => Job(db).GetOrSeedPromptAsync(context, CancellationToken.None));

        StringAssert.Contains(ex.Message, "none is active");

        await using var check = db.CreateDbContext();
        Assert.AreEqual(1, await check.SocialPrompts.CountAsync(), "Nothing should have been seeded");
    }

    // ---- A full run ----------------------------------------------------------------------------

    /// <summary>
    /// End to end: what a run actually leaves in the table. Everything else here tests one decision in
    /// isolation, so this is the only cover for the create path, what Apply writes, and the fact that
    /// a draft lands in review rather than anywhere closer to being published.
    /// </summary>
    [TestMethod]
    public async Task ARun_LeavesADraftWaitingForReview()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            seed.Events.Add(AnEventWithResults(seed, 341, Now.AddDays(-3)));
            await seed.SaveChangesAsync();
        }

        await Job(db).RunAsync(CancellationToken.None);

        await using var check = db.CreateDbContext();
        var post = await check.SocialPosts.SingleAsync();

        Assert.AreEqual(SocialPostState.PendingReview, post.State, "A run must never produce anything closer to published");
        Assert.AreEqual(341, post.EventId);
        Assert.AreEqual("EventResults:341:Facebook", post.IdempotencyKey);
        Assert.AreEqual("Bad Decision Racing took GP1 with 214 laps.", post.GeneratedText);
        Assert.AreEqual("claude-sonnet-5-test", post.Model);
        Assert.AreEqual(1, post.PromptVersion);
        Assert.AreEqual(1, post.GenerationAttempts);
        Assert.IsFalse(post.HasUnverifiedClaims);
        Assert.IsFalse(string.IsNullOrWhiteSpace(post.DigestSourceHash));
        Assert.IsFalse(string.IsNullOrWhiteSpace(post.DigestJson));
        Assert.IsNull(post.EditedText);
        Assert.IsNull(post.PublishedUtc);
        Assert.IsNull(post.ApprovedBy);
    }

    /// <summary>A second run over unchanged results must not spend a call or churn the draft.</summary>
    [TestMethod]
    public async Task ASecondRunOverUnchangedResults_CostsNothing()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            seed.Events.Add(AnEventWithResults(seed, 341, Now.AddDays(-3)));
            await seed.SaveChangesAsync();
        }

        var generator = new StubGenerator();
        await Job(db, generator: generator).RunAsync(CancellationToken.None);
        await Job(db, generator: generator).RunAsync(CancellationToken.None);

        Assert.AreEqual(1, generator.Calls, "The second run should not have called the model");

        await using var check = db.CreateDbContext();
        Assert.AreEqual(1, await check.SocialPosts.CountAsync());
    }

    /// <summary>
    /// A generation failure has to leave evidence. Without a row the event looks like it was never
    /// considered, and the only trace is a log line in a pod that has already exited.
    /// </summary>
    [TestMethod]
    public async Task AGenerationFailure_IsRecordedAsAFailedPost()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            seed.Events.Add(AnEventWithResults(seed, 341, Now.AddDays(-3)));
            await seed.SaveChangesAsync();
        }

        await Job(db, generator: new ThrowingGenerator("Anthropic API returned 503")).RunAsync(CancellationToken.None);

        await using var check = db.CreateDbContext();
        var post = await check.SocialPosts.SingleAsync();

        Assert.AreEqual(SocialPostState.Failed, post.State);
        StringAssert.Contains(post.Error, "503");
        Assert.IsNull(post.GeneratedText);
    }

    /// <summary>
    /// A run must survive one event failing. The remaining events have their own results and a
    /// reviewer expecting posts about them.
    /// </summary>
    [TestMethod]
    public async Task OneEventFailing_DoesNotCostTheRestOfTheRun()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            seed.Events.Add(AnEventWithResults(seed, 1, Now.AddDays(-4)));
            seed.Events.Add(AnEventWithResults(seed, 2, Now.AddDays(-3)));
            seed.Events.Add(AnEventWithResults(seed, 3, Now.AddDays(-2)));
            await seed.SaveChangesAsync();
        }

        await Job(db, generator: new ThrowingGenerator("boom", failOnCall: 2)).RunAsync(CancellationToken.None);

        await using var check = db.CreateDbContext();
        var posts = await check.SocialPosts.OrderBy(p => p.EventId).ToListAsync();

        Assert.AreEqual(3, posts.Count, "Every event should have been attempted");
        CollectionAssert.AreEqual(
            new[] { SocialPostState.PendingReview, SocialPostState.Failed, SocialPostState.PendingReview },
            posts.Select(p => p.State).ToList());
    }

    /// <summary>
    /// A client-side request timeout arrives as an OperationCanceledException. Treating that as host
    /// shutdown would abort the whole run at the first slow call, leaving no row for the event that
    /// timed out and none for any event behind it.
    /// </summary>
    [TestMethod]
    public async Task ARequestTimeout_IsTreatedAsAFailureRatherThanShutdown()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            seed.Events.Add(AnEventWithResults(seed, 1, Now.AddDays(-4)));
            seed.Events.Add(AnEventWithResults(seed, 2, Now.AddDays(-3)));
            await seed.SaveChangesAsync();
        }

        var timeout = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout",
            new TimeoutException());

        await Job(db, generator: new ThrowingGenerator(timeout, failOnCall: 1)).RunAsync(CancellationToken.None);

        await using var check = db.CreateDbContext();
        var posts = await check.SocialPosts.OrderBy(p => p.EventId).ToListAsync();

        Assert.AreEqual(2, posts.Count, "The event behind the timeout must still be drafted");
        Assert.AreEqual(SocialPostState.Failed, posts[0].State);
        Assert.AreEqual(SocialPostState.PendingReview, posts[1].State);
    }

    /// <summary>
    /// Copy that never validates still reaches a reviewer, but the flag it carries has to be queryable
    /// rather than buried in free text, or the review screen cannot tell it from a clean draft.
    /// </summary>
    [TestMethod]
    public async Task ADraftWithInventedNumbers_IsStoredFlagged()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            seed.Events.Add(AnEventWithResults(seed, 341, Now.AddDays(-3)));
            await seed.SaveChangesAsync();
        }

        await Job(db, generator: new StubGenerator("Bad Decision Racing won by 47 seconds.")).RunAsync(CancellationToken.None);

        await using var check = db.CreateDbContext();
        var post = await check.SocialPosts.SingleAsync();

        Assert.AreEqual(SocialPostState.PendingReview, post.State);
        Assert.IsTrue(post.HasUnverifiedClaims);
        Assert.AreEqual(3, post.GenerationAttempts);
        StringAssert.Contains(post.ValidationWarnings, "47");
    }

    // ---- Results pictures ----------------------------------------------------------------------

    [TestMethod]
    public async Task CapturedImages_AreAttachedToTheDraft()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            seed.Events.Add(AnEventWithResults(seed, 341, Now.AddDays(-3)));
            await seed.SaveChangesAsync();
        }

        var capture = new StubCapture();
        await Job(db, Settings(images: true), capture: capture, store: new StubStore()).RunAsync(CancellationToken.None);

        await using var check = db.CreateDbContext();
        var post = await check.SocialPosts.SingleAsync();

        CollectionAssert.AreEqual(
            new[] { "https://cdn.example/social/event-341/session-1.png" }, post.ImageRefs);
        Assert.AreEqual(1, capture.Requests.Count);
        Assert.AreEqual(341, capture.Requests[0].EventId);
        Assert.AreEqual(1, capture.Requests[0].SessionId);
    }

    /// <summary>
    /// The important one. A browser that times out, a page that changed shape or a CDN that refuses
    /// an upload must not cost the event its post -- the copy stands on its own, and the reviewer
    /// needs to be told the picture is missing rather than left to notice.
    /// </summary>
    [TestMethod]
    public async Task AFailedCapture_LeavesTheDraftIntactAndSaysSo()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            seed.Events.Add(AnEventWithResults(seed, 341, Now.AddDays(-3)));
            await seed.SaveChangesAsync();
        }

        var capture = new StubCapture(new SessionImageCaptureException("Timed out waiting for '.car-row-container'"));
        await Job(db, Settings(images: true), capture: capture, store: new StubStore()).RunAsync(CancellationToken.None);

        await using var check = db.CreateDbContext();
        var post = await check.SocialPosts.SingleAsync();

        Assert.AreEqual(SocialPostState.PendingReview, post.State, "The post must survive a failed capture");
        Assert.AreEqual("Bad Decision Racing took GP1 with 214 laps.", post.GeneratedText);
        Assert.AreEqual(0, post.ImageRefs.Count);
        StringAssert.Contains(post.ValidationWarnings, "NO IMAGE");
    }

    /// <summary>Images off means no capture attempted at all, not a capture that is thrown away.</summary>
    [TestMethod]
    public async Task WithImagesDisabled_NoCaptureIsAttempted()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            seed.Events.Add(AnEventWithResults(seed, 341, Now.AddDays(-3)));
            await seed.SaveChangesAsync();
        }

        var capture = new StubCapture();
        await Job(db, Settings(images: false), capture: capture, store: new StubStore()).RunAsync(CancellationToken.None);

        Assert.AreEqual(0, capture.Requests.Count);

        await using var check = db.CreateDbContext();
        Assert.AreEqual(0, (await check.SocialPosts.SingleAsync()).ImageRefs.Count);
    }

    /// <summary>
    /// The production shape when images are off: nothing is registered, so the job is handed nulls
    /// rather than an unused stub. Pinned because it is a resolution failure rather than a wrong
    /// picture -- the run would not start at all.
    /// </summary>
    [TestMethod]
    public async Task WithNoCaptureRegisteredAtAll_TheRunStillDrafts()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            seed.Events.Add(AnEventWithResults(seed, 341, Now.AddDays(-3)));
            await seed.SaveChangesAsync();
        }

        await Job(db, Settings(images: true), capture: null, store: null).RunAsync(CancellationToken.None);

        await using var check = db.CreateDbContext();
        var post = await check.SocialPosts.SingleAsync();

        Assert.AreEqual(SocialPostState.PendingReview, post.State);
        Assert.AreEqual(0, post.ImageRefs.Count);
    }

    /// <summary>
    /// One session failing must not discard the picture of the one that worked. A post carrying the
    /// Saturday race and a note about Sunday is worth far more than a post carrying neither.
    /// </summary>
    [TestMethod]
    public async Task WhenOneSessionFails_TheOtherPictureIsStillKept()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            var evt = AnEventWithResults(seed, 341, Now.AddDays(-3));
            AddRaceSession(seed, 341, sessionId: 2, "Sunday Race");
            seed.Events.Add(evt);
            await seed.SaveChangesAsync();
        }

        var capture = new StubCapture(failOnSessionId: 2);
        await Job(db, Settings(images: true), capture: capture, store: new StubStore()).RunAsync(CancellationToken.None);

        await using var check = db.CreateDbContext();
        var post = await check.SocialPosts.SingleAsync();

        CollectionAssert.AreEqual(
            new[] { "https://cdn.example/social/event-341/session-1.png" }, post.ImageRefs);
        StringAssert.Contains(post.ValidationWarnings, "Sunday Race");
        Assert.AreEqual(SocialPostState.PendingReview, post.State);
    }

    /// <summary>
    /// A capture deadline surfaces as an OperationCanceledException even though nothing was
    /// cancelled. This codebase has been bitten by that exact confusion before, and reading it as
    /// shutdown here would abort the run and leave the events behind it undrafted.
    /// </summary>
    [TestMethod]
    public async Task ACaptureTimeout_IsNotMistakenForShutdown()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            seed.Events.Add(AnEventWithResults(seed, 1, Now.AddDays(-4)));
            seed.Events.Add(AnEventWithResults(seed, 2, Now.AddDays(-3)));
            await seed.SaveChangesAsync();
        }

        var capture = new StubCapture(new OperationCanceledException("the capture deadline elapsed"));
        await Job(db, Settings(images: true), capture: capture, store: new StubStore()).RunAsync(CancellationToken.None);

        await using var check = db.CreateDbContext();
        var posts = await check.SocialPosts.OrderBy(p => p.EventId).ToListAsync();

        Assert.AreEqual(2, posts.Count, "The event behind the timeout must still be drafted");
        Assert.IsTrue(posts.All(p => p.State == SocialPostState.PendingReview));
        Assert.IsTrue(posts.All(p => p.ValidationWarnings!.Contains("NO IMAGE", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A weekend with more races than the limit must produce a post carrying the first few pictures
    /// and a note, not an album and not a silent truncation.
    /// </summary>
    [TestMethod]
    public async Task MoreSessionsThanTheImageLimit_ArePicturedUpToItAndNoted()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            var evt = AnEventWithResults(seed, 341, Now.AddDays(-3));
            AddRaceSession(seed, 341, sessionId: 2, "Sunday Race");
            AddRaceSession(seed, 341, sessionId: 3, "Sunday Sprint");
            seed.Events.Add(evt);
            await seed.SaveChangesAsync();
        }

        var capture = new StubCapture();
        await Job(db, Settings(images: true, maxImages: 2), capture: capture, store: new StubStore())
            .RunAsync(CancellationToken.None);

        Assert.AreEqual(2, capture.Requests.Count);

        await using var check = db.CreateDbContext();
        var post = await check.SocialPosts.SingleAsync();

        Assert.AreEqual(2, post.ImageRefs.Count);
        StringAssert.Contains(post.ValidationWarnings, "first 2 of 3 race sessions");
    }

    private static void AddRaceSession(TsContext context, int eventId, int sessionId, string name)
    {
        context.SessionResults.Add(new SessionResult
        {
            EventId = eventId,
            SessionId = sessionId,
            Start = Now.AddDays(-3).AddHours(sessionId),
            SessionState = new SessionState
            {
                EventId = eventId,
                SessionId = sessionId,
                SessionName = name,
                CarPositions = [new() { Number = "66", Class = "GP1", ClassPosition = 1, LastLapCompleted = 100 }],
                EventEntries = [new() { Number = "66", Name = "Bad Decision Racing", Class = "GP1" }],
            },
        });
    }

    /// <summary>A generator that fails on a given call, to exercise the per-event failure paths.</summary>
    private sealed class ThrowingGenerator(Exception failure, int failOnCall = 0) : ICopyGenerator
    {
        private int callCount;

        public ThrowingGenerator(string message, int failOnCall = 0)
            : this(new InvalidOperationException(message), failOnCall) { }

        public Task<GeneratedCopy> GenerateAsync(CopyRequest request, CancellationToken cancellationToken)
        {
            callCount++;
            if (failOnCall == 0 || callCount == failOnCall)
                throw failure;

            return Task.FromResult(new GeneratedCopy(
                "Bad Decision Racing took GP1 with 214 laps.", "claude-sonnet-5-test", false));
        }
    }
}
