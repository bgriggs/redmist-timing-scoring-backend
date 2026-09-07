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
            store is null ? null : new SocialImageCleanup(store, loggerFactory),
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
        /// <summary>Every URL this store was asked to delete, so a test can assert on cleanup.</summary>
        public List<string> Deleted { get; } = [];

        public Task<string> StoreAsync(
            SocialChannel channel, int eventId, int sessionId, CapturedImage image, CancellationToken cancellationToken) =>
            Task.FromResult($"https://cdn.example/social/{channel}/event-{eventId}/session-{sessionId}.png");

        public Task<bool> DeleteAsync(string url, CancellationToken cancellationToken)
        {
            Deleted.Add(url);
            return Task.FromResult(true);
        }
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
            new[] { "https://cdn.example/social/Facebook/event-341/session-1.png" }, post.ImageRefs);
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
            new[] { "https://cdn.example/social/Facebook/event-341/session-1.png" }, post.ImageRefs);
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
        StringAssert.Contains(post.ValidationWarnings, "first 2 of 3 races");
    }

    /// <summary>
    /// The feed can produce two sessions for one race under the same name -- the duplicate usually
    /// carrying session id 0 -- and taking the first N sessions in order then photographs that race
    /// twice and the next one not at all. Seen on a real event: two pictures of Saturday, none of
    /// Sunday, in a post whose own copy described both, because the digest reads results and had
    /// every session regardless.
    /// </summary>
    [TestMethod]
    public async Task ASessionRepeatedUnderTheSameName_IsNotPicturedTwice()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            // Session 1 is "Saturday Race"; session 0 repeats it, and Sunday is the race that would
            // otherwise lose its slot.
            var evt = AnEventWithResults(seed, 341, Now.AddDays(-3));
            AddRaceSession(seed, 341, sessionId: 0, "Saturday Race", start: Now.AddDays(-3).AddHours(-4).AddMinutes(11));
            AddRaceSession(seed, 341, sessionId: 2, "Sunday Race");
            seed.Events.Add(evt);
            await seed.SaveChangesAsync();
        }

        var capture = new StubCapture();
        await Job(db, Settings(images: true, maxImages: 2), capture: capture, store: new StubStore())
            .RunAsync(CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[] { "Saturday Race", "Sunday Race" },
            capture.Requests.Select(r => r.SessionName).ToList(),
            "Both races should be pictured once, rather than Saturday twice.");

        // The id decides which page is photographed, so asserting names alone would pass however
        // badly the row was chosen.
        CollectionAssert.AreEquivalent(
            new[] { 1, 2 },
            capture.Requests.Select(r => r.SessionId).ToList(),
            "Saturday should be photographed from its real session, not the spare row.");

        await using var check = db.CreateDbContext();
        var post = await check.SocialPosts.SingleAsync();

        Assert.AreEqual(2, post.ImageRefs.Count);
        StringAssert.Contains(post.ValidationWarnings, "repeats the name 'Saturday Race'",
            "A repeated session is worth naming: it is the visible end of a data problem.");
    }

    /// <summary>
    /// The spare row carries session id 0 and can share the real row's start. Sessions reach the
    /// digest ordered by start then id, so on a tie the id-0 row comes first -- and picking whichever
    /// came first would photograph /timing/{event}/0 and drop the real session as the duplicate. That
    /// turns a redundant picture into a wrong one, which is worse than the bug.
    ///
    /// The spare is given the LARGER field here on purpose. On the event this was written for both
    /// rows held all 52 cars, so size cannot be what identifies the spare; if size outranked the id
    /// then one extra car in the spare would hand it the race.
    /// </summary>
    [TestMethod]
    public async Task WhereTwoRowsShareARace_TheRealSessionIdIsPicturedEvenIfTheSpareIsFuller()
    {
        var db = Db();
        var sameStart = Now.AddDays(-3).AddHours(-4);
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            var evt = AnEvent(341, Now.AddDays(-3));
            AddRaceSession(seed, 341, sessionId: 0, "Saturday Race", start: sameStart, cars: 9);
            AddRaceSession(seed, 341, sessionId: 67, "Saturday Race", start: sameStart, cars: 8);
            seed.Events.Add(evt);
            await seed.SaveChangesAsync();
        }

        var capture = new StubCapture();
        await Job(db, Settings(images: true, maxImages: 2), capture: capture, store: new StubStore())
            .RunAsync(CancellationToken.None);

        Assert.AreEqual(1, capture.Requests.Count, "One race, one picture.");
        Assert.AreEqual(67, capture.Requests[0].SessionId,
            "The picture is fetched by session id, so the spare row's page would be photographed.");
    }

    /// <summary>Where both rows carry a real id, size is what is left to tell them apart.</summary>
    [TestMethod]
    public async Task WhereBothRowsCarryARealId_TheFullerOneIsPictured()
    {
        var db = Db();
        var sameStart = Now.AddDays(-3).AddHours(-4);
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            AddRaceSession(seed, 341, sessionId: 66, "Saturday Race", start: sameStart, cars: 3);
            AddRaceSession(seed, 341, sessionId: 67, "Saturday Race", start: sameStart, cars: 9);
            seed.Events.Add(AnEvent(341, Now.AddDays(-3)));
            await seed.SaveChangesAsync();
        }

        var capture = new StubCapture();
        await Job(db, Settings(images: true, maxImages: 2), capture: capture, store: new StubStore())
            .RunAsync(CancellationToken.None);

        Assert.AreEqual(67, capture.Requests.Single().SessionId);
    }

    /// <summary>
    /// Rows for one race differ in casing and spacing without meaning anything by it.
    /// </summary>
    [TestMethod]
    public async Task NamesDifferingOnlyInCaseOrSpacing_AreTheSameRace()
    {
        var db = Db();
        var start = Now.AddDays(-3).AddHours(-4);
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            AddRaceSession(seed, 341, sessionId: 0, "saturday  8   hour", start: start, cars: 9);
            AddRaceSession(seed, 341, sessionId: 67, "Saturday 8 Hour", start: start.AddMinutes(11), cars: 9);
            seed.Events.Add(AnEvent(341, Now.AddDays(-3)));
            await seed.SaveChangesAsync();
        }

        var capture = new StubCapture();
        await Job(db, Settings(images: true, maxImages: 2), capture: capture, store: new StubStore())
            .RunAsync(CancellationToken.None);

        Assert.AreEqual(67, capture.Requests.Single().SessionId);
    }

    /// <summary>Where the rows are the same size, a real session id still beats the spare's zero.</summary>
    [TestMethod]
    public async Task WhereTwoRowsAreIndistinguishableBySize_TheRealSessionIdWins()
    {
        var db = Db();
        var sameStart = Now.AddDays(-3).AddHours(-4);
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            AddRaceSession(seed, 341, sessionId: 0, "Saturday Race", start: sameStart, cars: 4);
            AddRaceSession(seed, 341, sessionId: 67, "Saturday Race", start: sameStart, cars: 4);
            seed.Events.Add(AnEvent(341, Now.AddDays(-3)));
            await seed.SaveChangesAsync();
        }

        var capture = new StubCapture();
        await Job(db, Settings(images: true, maxImages: 2), capture: capture, store: new StubStore())
            .RunAsync(CancellationToken.None);

        Assert.AreEqual(67, capture.Requests.Single().SessionId);
    }

    /// <summary>
    /// A meeting can legitimately run two races under one name, including twice in one day -- which
    /// is why they are separated by how far apart they started rather than by which day they fall on.
    /// Collapsing them would lose a real picture and report a data problem that is not there.
    /// </summary>
    [TestMethod]
    public async Task TwoRealRacesSharingANameHoursApart_AreBothPictured()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            // Same name, same day, four hours apart: a double-header, not a repeated row.
            AddRaceSession(seed, 341, sessionId: 1, "Race 1", start: Now.AddDays(-3).AddHours(-9), cars: 6);
            AddRaceSession(seed, 341, sessionId: 2, "Race 1", start: Now.AddDays(-3).AddHours(-5), cars: 6);
            seed.Events.Add(AnEvent(341, Now.AddDays(-3)));
            await seed.SaveChangesAsync();
        }

        var capture = new StubCapture();
        await Job(db, Settings(images: true, maxImages: 2), capture: capture, store: new StubStore())
            .RunAsync(CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[] { 1, 2 }, capture.Requests.Select(r => r.SessionId).ToList(),
            "Two races hours apart are two races, whatever they are called.");

        await using var check = db.CreateDbContext();
        var post = await check.SocialPosts.SingleAsync();
        Assert.IsFalse(post.ValidationWarnings?.Contains("repeats", StringComparison.Ordinal) ?? false,
            "Nothing repeated, so nothing should be reported as repeating.");
    }

    /// <summary>
    /// The digest deliberately admits sessions with no name. Grouping on a blank would make every
    /// one of them the same race and quietly drop all but the first.
    /// </summary>
    [TestMethod]
    public async Task UnnamedSessions_AreNotTreatedAsOneRace()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            AddRaceSession(seed, 341, sessionId: 1, string.Empty, cars: 5);
            AddRaceSession(seed, 341, sessionId: 2, string.Empty, cars: 5);
            seed.Events.Add(AnEvent(341, Now.AddDays(-3)));
            await seed.SaveChangesAsync();
        }

        var capture = new StubCapture();
        await Job(db, Settings(images: true, maxImages: 2), capture: capture, store: new StubStore())
            .RunAsync(CancellationToken.None);

        Assert.AreEqual(2, capture.Requests.Count,
            "A missing name is not evidence that two rows are the same race.");
    }

    /// <summary>
    /// "Not pictured a second time" is false about a race the limit meant was never pictured at all,
    /// and it would sit directly beside the warning saying so.
    /// </summary>
    [TestMethod]
    public async Task ARaceExcludedByTheLimit_IsNotAlsoReportedAsARepeat()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            AddRaceSession(seed, 341, sessionId: 1, "Race A", start: Now.AddDays(-5), cars: 5);
            AddRaceSession(seed, 341, sessionId: 2, "Race B", start: Now.AddDays(-4), cars: 5);
            // Beyond the limit, and itself duplicated.
            AddRaceSession(seed, 341, sessionId: 3, "Race C", start: Now.AddDays(-3), cars: 5);
            AddRaceSession(seed, 341, sessionId: 0, "Race C", start: Now.AddDays(-3), cars: 2);
            seed.Events.Add(AnEvent(341, Now.AddDays(-3)));
            await seed.SaveChangesAsync();
        }

        await Job(db, Settings(images: true, maxImages: 2), capture: new StubCapture(), store: new StubStore())
            .RunAsync(CancellationToken.None);

        await using var check = db.CreateDbContext();
        var warnings = (await check.SocialPosts.SingleAsync()).ValidationWarnings ?? string.Empty;

        StringAssert.Contains(warnings, "first 2 of 3 races");
        Assert.IsFalse(warnings.Contains("Race C", StringComparison.Ordinal),
            "Race C was pictured zero times; saying it was not pictured twice contradicts the limit warning.");
    }

    /// <summary>
    /// The limit counts races, not session rows, so a duplicate must not make a two-race weekend
    /// report itself as having lost one to the limit.
    /// </summary>
    [TestMethod]
    public async Task ADuplicateSession_DoesNotCountTowardTheImageLimit()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            var evt = AnEventWithResults(seed, 341, Now.AddDays(-3));
            AddRaceSession(seed, 341, sessionId: 0, "Saturday Race", start: Now.AddDays(-3).AddHours(-4).AddMinutes(11));
            AddRaceSession(seed, 341, sessionId: 2, "Sunday Race");
            seed.Events.Add(evt);
            await seed.SaveChangesAsync();
        }

        var capture = new StubCapture();
        await Job(db, Settings(images: true, maxImages: 2), capture: capture, store: new StubStore())
            .RunAsync(CancellationToken.None);

        // Asserted on the captures, not only on the warning text: an assertion that some phrase is
        // absent passes just as well when the phrase merely changed.
        Assert.AreEqual(2, capture.Requests.Count, "Two races, two pictures.");

        await using var check = db.CreateDbContext();
        var post = await check.SocialPosts.SingleAsync();

        Assert.IsFalse((post.ValidationWarnings ?? string.Empty).Contains("configured limit", StringComparison.Ordinal),
            "Three session rows are two races; the limit was not reached.");
    }

    /// <summary>
    /// A rejected post is a decision not to publish, so its pictures must stop being publicly
    /// fetchable rather than sitting in the CDN with nothing referencing them.
    /// </summary>
    [TestMethod]
    public async Task ImagesOfARejectedPost_AreDeleted()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            seed.Events.Add(AnEventWithResults(seed, 341, Now.AddDays(-3)));

            var rejected = APost(SocialPostState.Rejected, "a-hash");
            rejected.ImageRefs = ["https://cdn.example/social/Facebook/event-341/session-1.png"];
            seed.SocialPosts.Add(rejected);
            await seed.SaveChangesAsync();
        }

        var store = new StubStore();
        await Job(db, Settings(images: true), capture: new StubCapture(), store: store).RunAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "https://cdn.example/social/Facebook/event-341/session-1.png" }, store.Deleted);

        await using var check = db.CreateDbContext();
        var post = await check.SocialPosts.SingleAsync();

        Assert.AreEqual(SocialPostState.Rejected, post.State, "Rejecting is still the decision; only the pictures go");
        Assert.AreEqual(0, post.ImageRefs.Count, "A rejected post must not go on claiming pictures");
    }

    /// <summary>
    /// A redraft after the results changed produces different pictures, and the ones it replaced have
    /// nothing referencing them any more.
    /// </summary>
    [TestMethod]
    public async Task ImagesSupersededByARedraft_AreDeleted()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            seed.Events.Add(AnEventWithResults(seed, 341, Now.AddDays(-3)));

            // An earlier draft from a snapshot that has since been rewritten, so this redrafts.
            var stale = APost(SocialPostState.PendingReview, "a-hash-from-an-earlier-snapshot");
            stale.ImageRefs = ["https://cdn.example/social/Facebook/event-341/session-1-OLD.png"];
            seed.SocialPosts.Add(stale);
            await seed.SaveChangesAsync();
        }

        var store = new StubStore();
        await Job(db, Settings(images: true), capture: new StubCapture(), store: store).RunAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "https://cdn.example/social/Facebook/event-341/session-1-OLD.png" }, store.Deleted);

        await using var check = db.CreateDbContext();
        var post = await check.SocialPosts.SingleAsync();
        CollectionAssert.AreEqual(
            new[] { "https://cdn.example/social/Facebook/event-341/session-1.png" }, post.ImageRefs);
    }

    /// <summary>
    /// Through the job, not the set operation. Asserting on Superseded alone leaves the call site
    /// free to pass the whole previous list -- and because a redraft usually replaces every picture,
    /// no other test would notice. This one seeds an image the new capture reproduces, so deleting
    /// the previous set wholesale destroys a picture the post still references.
    /// </summary>
    [TestMethod]
    public async Task AnImageTheRedraftReproduces_IsNotDeleted()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            seed.Events.Add(AnEventWithResults(seed, 341, Now.AddDays(-3)));

            // Stale hash, so it redrafts -- but carrying the exact URL this capture will produce again.
            var stale = APost(SocialPostState.PendingReview, "a-hash-from-an-earlier-snapshot");
            stale.ImageRefs = ["https://cdn.example/social/Facebook/event-341/session-1.png"];
            seed.SocialPosts.Add(stale);
            await seed.SaveChangesAsync();
        }

        var store = new StubStore();
        await Job(db, Settings(images: true), capture: new StubCapture(), store: store).RunAsync(CancellationToken.None);

        CollectionAssert.AreEqual(Array.Empty<string>(), store.Deleted,
            "The redraft produced the same picture, so nothing was superseded");

        await using var check = db.CreateDbContext();
        CollectionAssert.AreEqual(
            new[] { "https://cdn.example/social/Facebook/event-341/session-1.png" },
            (await check.SocialPosts.SingleAsync()).ImageRefs);
    }

    /// <summary>
    /// A capture that fails is a gap, not a decision. Treating the missing URL as superseded would
    /// delete a good picture because the re-capture flaked -- and unrecoverably, since the next run
    /// finds the results unchanged and declines to redraft.
    /// </summary>
    [TestMethod]
    public async Task AnIncompleteCapture_DeletesNothing()
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            var evt = AnEventWithResults(seed, 341, Now.AddDays(-3));
            AddRaceSession(seed, 341, sessionId: 2, "Sunday Race");
            seed.Events.Add(evt);

            var stale = APost(SocialPostState.PendingReview, "a-hash-from-an-earlier-snapshot");
            stale.ImageRefs =
            [
                "https://cdn.example/social/Facebook/event-341/session-1-OLD.png",
                "https://cdn.example/social/Facebook/event-341/session-2-OLD.png",
            ];
            seed.SocialPosts.Add(stale);
            await seed.SaveChangesAsync();
        }

        var store = new StubStore();
        await Job(db, Settings(images: true), capture: new StubCapture(failOnSessionId: 2), store: store)
            .RunAsync(CancellationToken.None);

        CollectionAssert.AreEqual(Array.Empty<string>(), store.Deleted,
            "One session failed, so the previous pictures must be left alone");
    }

    /// <summary>
    /// Only a rejected post gives up its pictures. Without the state guard this path would strip the
    /// images off any post the run declines to redraft -- including a Published one, whose picture is
    /// on a live Facebook post.
    /// </summary>
    [TestMethod]
    [DataRow(SocialPostState.Published)]
    [DataRow(SocialPostState.Approved)]
    [DataRow(SocialPostState.Publishing)]
    public async Task ImagesOfAPostThatIsNotRejected_AreLeftAlone(SocialPostState state)
    {
        var db = Db();
        await using (var seed = db.CreateDbContext())
        {
            seed.Organizations.Add(new Organization { Id = 1, Name = "ChampCar", ShortName = "CC" });
            seed.Events.Add(AnEventWithResults(seed, 341, Now.AddDays(-3)));

            var post = APost(state, "a-hash");
            post.ImageRefs = ["https://cdn.example/social/Facebook/event-341/session-1.png"];
            seed.SocialPosts.Add(post);
            await seed.SaveChangesAsync();
        }

        var store = new StubStore();
        await Job(db, Settings(images: true), capture: new StubCapture(), store: store).RunAsync(CancellationToken.None);

        CollectionAssert.AreEqual(Array.Empty<string>(), store.Deleted, $"{state} posts keep their pictures");

        await using var check = db.CreateDbContext();
        Assert.AreEqual(1, (await check.SocialPosts.SingleAsync()).ImageRefs.Count);
    }

    /// <summary>
    /// A redraft of unchanged results produces byte-identical pictures, which keep the same
    /// content-addressed URLs. Deleting everything the post used to hold would remove the very image
    /// it is about to reference again.
    /// </summary>
    [TestMethod]
    public void ImagesTheRedraftStillReferences_AreNotDeleted()
    {
        var kept = "https://cdn.example/social/Facebook/event-341/session-1.png";

        var superseded = SocialImageCleanup.Superseded(
            [kept, "https://cdn.example/social/Facebook/event-341/session-2-OLD.png"],
            [kept, "https://cdn.example/social/Facebook/event-341/session-2-NEW.png"]);

        CollectionAssert.AreEqual(new[] { "https://cdn.example/social/Facebook/event-341/session-2-OLD.png" }, superseded.ToList());
    }

    /// <param name="start">
    /// When the session began. Defaults to spacing sessions out by id, which is convenient but is
    /// exactly what a test about duplicate rows must not rely on: the feed's spare row can carry the
    /// same start as the real one.
    /// </param>
    /// <param name="cars">
    /// How many cars the row holds. A spare row is a partial snapshot, so size is what tells it from
    /// the real session.
    /// </param>
    private static void AddRaceSession(
        TsContext context, int eventId, int sessionId, string name, DateTime? start = null, int cars = 1)
    {
        context.SessionResults.Add(new SessionResult
        {
            EventId = eventId,
            SessionId = sessionId,
            Start = start ?? Now.AddDays(-3).AddHours(sessionId),
            SessionState = new SessionState
            {
                EventId = eventId,
                SessionId = sessionId,
                SessionName = name,
                // Car "66" keeps its original name so that a default-sized session is exactly the
                // fixture every existing test was written against.
                CarPositions = [.. Numbers(cars).Select((n, i) => new CarPosition
                {
                    Number = n,
                    Class = "GP1",
                    ClassPosition = i + 1,
                    LastLapCompleted = 100,
                })],
                EventEntries = [.. Numbers(cars).Select(n => new EventEntry
                {
                    Number = n,
                    Name = n == "66" ? "Bad Decision Racing" : $"Bad Decision Racing {n}",
                    Class = "GP1",
                })],
            },
        });
    }

    /// <summary>Car numbers for a session of a given size, starting from the fixture's own car 66.</summary>
    private static IEnumerable<string> Numbers(int cars) =>
        Enumerable.Range(66, cars).Select(n => n.ToString());

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
