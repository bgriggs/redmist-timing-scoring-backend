using Microsoft.EntityFrameworkCore;
using Npgsql;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.Social.Digest;
using RedMist.Social.Generation;
using RedMist.Social.Imaging;
using Event = RedMist.TimingCommon.Models.Configuration.Event;

namespace RedMist.SocialCompose;

/// <summary>
/// Drafts a results post for each recently finished event and leaves it for a person to review.
/// Stops the host when the pass completes, so it can run as a scheduled job.
/// </summary>
/// <remarks>
/// Writes nothing publishable. Every draft lands in <see cref="SocialPostState.PendingReview"/>, and
/// nothing in this job can move a post past that, which is what makes it safe to run unattended
/// against a generative model.
/// </remarks>
/// <param name="imageCapture">
/// Photographs session results. Optional so the job runs without a browser -- the pipeline degrades
/// to text-only posts rather than stopping.
/// </param>
/// <param name="imageStore">Where captured images are put so a social platform can fetch them.</param>
/// <param name="imageCleanup">
/// Removes pictures a post no longer has a claim to -- superseded by a redraft, or belonging to a
/// post somebody rejected.
/// </param>
public class SocialComposeJob(
    ILoggerFactory loggerFactory,
    IDbContextFactory<TsContext> contextFactory,
    PostComposer composer,
    SocialComposeSettings settings,
    IHostApplicationLifetime lifetime,
    ISessionImageCapture? imageCapture = null,
    ISocialImageStore? imageStore = null,
    SocialImageCleanup? imageCleanup = null,
    TimeProvider? timeProvider = null) : BackgroundService
{
    /// <summary>Postgres unique-violation SQLSTATE.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>
    /// Cap on a recorded failure message. The column is unbounded text, but an exception carrying a
    /// large API error body has no business filling a review screen.
    /// </summary>
    private const int MaxErrorLength = 2000;

    private readonly ILogger logger = loggerFactory.CreateLogger<SocialComposeJob>();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForStartupAsync(stoppingToken);
        await RunAsync(stoppingToken);
    }

    /// <summary>
    /// Runs one compose pass. Separated from <see cref="ExecuteAsync"/> so the work can be invoked
    /// without the host-startup wait.
    /// </summary>
    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation(
                "Social compose starting: channel {Channel}, model {Model}, settle {SettleHours}h, lookback {LookbackDays}d",
                settings.Channel, settings.Model, settings.SettlePeriod.TotalHours, settings.LookbackWindow.TotalDays);

            await using var context = await CreateDbContextWithRetryAsync(stoppingToken);

            var events = await LoadCandidateEventsAsync(context, stoppingToken);
            if (events.Count == 0)
            {
                logger.LogInformation("No events finished within the window; nothing to draft");
                return;
            }

            logger.LogInformation("Found {Count} candidate event(s)", events.Count);

            var organizationNames = await LoadOrganizationNamesAsync(context, events, stoppingToken);

            // Loaded once, ahead of any post being tracked, so seeding a first prompt cannot be
            // swept into a SaveChanges that is really about a draft.
            var prompt = await GetOrSeedPromptAsync(context, stoppingToken);

            var drafted = 0;
            foreach (var evt in events)
            {
                stoppingToken.ThrowIfCancellationRequested();

                // The ceiling counts drafts written, not events examined. Counting candidates instead
                // would let events that are already drafted consume the budget, and since the ones
                // ahead in the queue leave the lookback window last, anything past the ceiling would
                // age out without ever being written about.
                if (drafted >= settings.MaxEventsPerRun)
                {
                    logger.LogWarning(
                        "Reached the {Ceiling}-draft ceiling for this run; {Remaining} candidate event(s) were left for the next one",
                        settings.MaxEventsPerRun, events.Count - events.IndexOf(evt));
                    break;
                }

                var organizationName = organizationNames.GetValueOrDefault(evt.OrganizationId, string.Empty);
                if (string.IsNullOrWhiteSpace(organizationName))
                    logger.LogWarning("Event {EventId}: organization {OrgId} has no name", evt.Id, evt.OrganizationId);

                try
                {
                    if (await ComposeForEventAsync(context, evt, organizationName, prompt, stoppingToken))
                        drafted++;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // One bad event must not cost the rest of the weekend's posts. Keyed on the token
                    // rather than on OperationCanceledException, because an HttpClient timeout is also
                    // an OperationCanceledException and would otherwise abort the whole run.
                    logger.LogError(ex, "Event {EventId}: compose failed and was skipped", evt.Id);
                }
            }

            logger.LogInformation("Social compose completed: {Drafted} draft(s) written", drafted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during social compose");

            // A BackgroundService that throws stops the host, but the process still exits 0, so
            // Kubernetes would record a run that died before drafting anything as Success -- no
            // backoffLimit retry and nothing to alert on. Set explicitly so "the run failed" and
            // "there was nothing to do" are distinguishable from outside the pod.
            Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    /// <summary>
    /// Finds events finished long enough ago to have final results.
    /// </summary>
    /// <remarks>
    /// The eligibility flags are repeated here even though <see cref="EventDigestBuilder"/> checks
    /// them too. That check is the authority; this one keeps private, hidden and simulated events out
    /// of the work set entirely, so they are never loaded, never digested and never counted against
    /// the per-run ceiling.
    ///
    /// Archived events are deliberately included. Archiving moves lap logs to cold storage and leaves
    /// SessionResults in place, so an archived event still has everything a digest needs -- and since
    /// archiving runs on much the same schedule as this job, excluding them would drop posts
    /// unpredictably depending on which job won the night.
    ///
    /// One-way: an event made private or deleted after a draft was written drops out of this query, so
    /// nothing here revisits or retracts the draft it already left in review. Checking a draft against
    /// a fresh digest before publishing belongs to the review step, and DigestSourceHash exists for it.
    ///
    /// Ordered oldest first, so an event close to leaving the lookback window is drafted before a
    /// newer one that will still be eligible tomorrow.
    /// </remarks>
    internal async Task<List<Event>> LoadCandidateEventsAsync(TsContext context, CancellationToken stoppingToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var settled = now - settings.SettlePeriod;
        var earliest = now - settings.LookbackWindow;

        return await context.Events
            .AsNoTracking()
            .Where(e => !e.IsDeleted && !e.IsSimulation && !e.IsPrivate && !e.HideName && !e.IsLive
                        && e.EndDate <= settled && e.EndDate >= earliest)
            .OrderBy(e => e.EndDate)
            .ToListAsync(stoppingToken);
    }

    private static async Task<Dictionary<int, string>> LoadOrganizationNamesAsync(
        TsContext context, List<Event> events, CancellationToken stoppingToken)
    {
        var ids = events.Select(e => e.OrganizationId).Distinct().ToList();
        return await context.Organizations
            .AsNoTracking()
            .Where(o => ids.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.Name, stoppingToken);
    }

    /// <summary>
    /// Drafts one event. Returns true when a draft was written, which is what the run ceiling counts:
    /// an event skipped because it is already drafted costs nothing and must not consume budget.
    /// </summary>
    private async Task<bool> ComposeForEventAsync(
        TsContext context, Event evt, string organizationName, SocialPrompt prompt, CancellationToken stoppingToken)
    {
        var results = await context.SessionResults
            .AsNoTracking()
            .Where(r => r.EventId == evt.Id)
            .ToListAsync(stoppingToken);

        var digest = EventDigestBuilder.Build(evt, organizationName, results, clock);

        if (!digest.IsEligibleForPublication)
        {
            logger.LogInformation("Event {EventId} ({Name}) is not eligible for publication; skipped", evt.Id, evt.Name);
            return false;
        }

        if (digest.Sessions.Count == 0)
        {
            logger.LogInformation("Event {EventId} ({Name}) has no race sessions with results; skipped", evt.Id, evt.Name);
            return false;
        }

        var key = SocialPost.BuildIdempotencyKey(SocialPostKind.EventResults, evt.Id, settings.Channel);
        var existing = await context.SocialPosts.FirstOrDefaultAsync(p => p.IdempotencyKey == key, stoppingToken);

        if (existing is not null && WhyNotRecompose(existing, digest) is { } reason)
        {
            logger.LogInformation("Event {EventId}: not redrafting because {Reason}", evt.Id, reason);
            await ReleaseRejectedImagesAsync(context, existing, stoppingToken);
            return false;
        }

        ComposedPost composed;
        try
        {
            composed = await composer.ComposeAsync(digest, prompt, settings.ToComposeOptions(), stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // Keyed on the token, not on OperationCanceledException: a client-side request timeout is
            // an OperationCanceledException, and treating it as shutdown would leave the event with no
            // row at all and no record that it was ever attempted.
            logger.LogError(ex, "Event {EventId}: generation failed", evt.Id);
            await RecordGenerationFailureAsync(context, existing, key, evt, ex, stoppingToken);
            return false;
        }

        // After generation, not before: a picture is no use without copy to attach it to, and
        // capturing first would spend a browser on events whose text never materializes.
        var images = await CaptureImagesAsync(digest, stoppingToken);

        var post = existing ?? CreatePost(key, evt.Id);
        var previousImages = post.ImageRefs.ToList();
        Apply(post, digest, composed, prompt, images);

        if (existing is null)
            context.SocialPosts.Add(post);

        if (!await SaveAsync(context, post, evt.Id, stoppingToken))
            return false;

        // Only after the row is safely saved. Deleting first would leave a post referencing pictures
        // that no longer exist if the save then failed, and an image nobody references is a far
        // smaller problem than a post whose images 404.
        await ReleaseSupersededImagesAsync(previousImages, post.ImageRefs, images, evt.Id, stoppingToken);

        logger.LogInformation(
            "Event {EventId} ({Name}): drafted {Length} characters in {Attempts} attempt(s){Flag}",
            evt.Id, evt.Name, composed.Text.Length, composed.Attempts,
            composed.HasUnverifiedClaims ? " -- FLAGGED, copy still fails validation" : string.Empty);

        return true;
    }

    /// <summary>
    /// Deletes the pictures a redraft has left with nothing referencing them.
    /// </summary>
    private async Task ReleaseSupersededImagesAsync(
        List<string> previous,
        List<string> current,
        CapturedImageSet images,
        int eventId,
        CancellationToken stoppingToken)
    {
        if (imageCleanup is null || !images.WasAttempted)
            return;

        // Nothing is deleted unless every session was photographed. A session whose capture failed is
        // simply missing from the new set, so it would otherwise look superseded and be deleted --
        // destroying a perfectly good picture because the re-capture flaked. That is unrecoverable in
        // practice: the next run finds the results unchanged, declines to redraft, and the image is
        // gone for good. An orphaned object is the cheaper mistake.
        if (!images.IsComplete)
        {
            logger.LogInformation(
                "Event {EventId}: not releasing any previous images, because at least one session could not be captured",
                eventId);
            return;
        }

        var superseded = SocialImageCleanup.Superseded(previous, current);
        if (superseded.Count == 0)
            return;

        var released = await imageCleanup.ReleaseAsync(superseded, stoppingToken);
        logger.LogInformation(
            "Event {EventId}: released {Released} of {Total} superseded results image(s)",
            eventId, released, superseded.Count);
    }

    /// <summary>
    /// Deletes the pictures belonging to a post somebody turned down.
    /// </summary>
    /// <remarks>
    /// A rejected post is a decision not to publish, so its pictures have no reason to stay publicly
    /// fetchable. Done here because this job is currently the only thing that revisits posts; once the
    /// review API exists, rejecting or deleting a post should release them at that moment rather than
    /// waiting for a nightly pass. This only reaches posts whose event is still inside the lookback
    /// window, so it is a safety net and not the mechanism.
    /// </remarks>
    private async Task ReleaseRejectedImagesAsync(
        TsContext context, SocialPost post, CancellationToken stoppingToken)
    {
        if (imageCleanup is null || post.State != SocialPostState.Rejected || post.ImageRefs.Count == 0)
            return;

        var urls = post.ImageRefs.ToList();

        // Saved before anything is deleted, the same order the redraft path uses. Reversing it opens a
        // window that a reviewer can walk into: un-reject a post while this is mid-delete, and the
        // concurrency token rightly refuses the save -- leaving a PendingReview post whose pictures
        // have already been destroyed, and which someone can then approve.
        post.ImageRefs = [];
        if (!await SaveAsync(context, post, post.EventId ?? 0, stoppingToken))
        {
            // The row moved under us, so its pictures are no longer ours to delete. Next run sees the
            // post as it now stands and decides again.
            logger.LogInformation(
                "Post {Key} changed while its images were being released; they were left in place",
                post.IdempotencyKey);
            return;
        }

        var released = await imageCleanup.ReleaseAsync(urls, stoppingToken);
        logger.LogInformation(
            "Rejected post {Key}: released {Released} of {Total} results image(s)",
            post.IdempotencyKey, released, urls.Count);
    }

    /// <summary>
    /// Why an existing post must be left alone, or null if it may be redrafted.
    /// </summary>
    /// <remarks>
    /// Three separate reasons, and the last is the one that matters most in practice. Session results
    /// are rewritten in place when a fuller snapshot arrives, which is why redrafting exists at all;
    /// but on every other night the source is identical, and regenerating would spend a call to
    /// replace a reviewer's draft with differently-worded copy saying the same thing. Comparing the
    /// digest hash is what keeps a nightly schedule from churning the review queue.
    ///
    /// Failed is exempt from that comparison, and it has to be. A failure records the hash it was
    /// trying to draft from, so a post whose results were rewritten and whose regeneration then hit an
    /// API outage would match on hash while still holding copy describing the superseded results.
    /// Treating that as "nothing changed" would strand it in Failed permanently, out of the review
    /// queue, carrying text a reviewer could still approve.
    /// </remarks>
    internal static string? WhyNotRecompose(SocialPost existing, EventDigest digest)
    {
        if (existing.State is not (SocialPostState.Draft or SocialPostState.PendingReview or SocialPostState.Failed))
            return $"a post already exists in state {existing.State}";

        if (!string.IsNullOrWhiteSpace(existing.EditedText))
            return "the existing draft has been edited by hand";

        if (existing.State != SocialPostState.Failed
            && string.Equals(existing.DigestSourceHash, digest.Provenance.SourceHash, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(existing.GeneratedText))
        {
            return "the results have not changed since the existing draft was written";
        }

        return null;
    }

    private SocialPost CreatePost(string key, int eventId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return new SocialPost
        {
            Kind = SocialPostKind.EventResults,
            EventId = eventId,
            Channel = settings.Channel,
            IdempotencyKey = key,
            State = SocialPostState.Draft,
            CreatedUtc = now,

            // "As soon as it is approved". Set only when the post is created, never on a redraft:
            // choosing a publication time is a reviewer's decision, and overwriting it here would
            // silently undo one.
            ScheduledUtc = now,
        };
    }

    private static void Apply(
        SocialPost post,
        EventDigest digest,
        ComposedPost composed,
        SocialPrompt prompt,
        CapturedImageSet images)
    {
        post.State = SocialPostState.PendingReview;
        post.DigestJson = DigestPromptFormatter.Serialize(digest);
        post.DigestSourceHash = digest.Provenance.SourceHash;
        post.DigestVersion = digest.Provenance.DigestVersion;
        post.GeneratedText = composed.Text;

        // Truncated rather than risking an overflow that would fail the whole event over a label.
        post.Model = Truncate(composed.Model, 100);
        post.PromptVersion = prompt.Version;
        post.GenerationAttempts = composed.Attempts;

        // Left alone when no capture was attempted. Turning images off is the documented response to
        // the timing page changing shape, and clearing the column then would erase the only record of
        // where the existing pictures live -- nothing lists the zone, so they would be unrecoverable.
        if (images.WasAttempted)
            post.ImageRefs = images.Urls;

        var warnings = composed.Warnings.Concat(images.Warnings).ToList();
        post.ValidationWarnings = warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);
        post.HasUnverifiedClaims = composed.HasUnverifiedClaims;
        post.Error = null;
    }

    /// <summary>
    /// Photographs each race session's results page, as far as it can.
    /// </summary>
    /// <remarks>
    /// Never throws. A picture is worth having and this is the only way to get one, but a browser
    /// that times out, a page that changed shape or a CDN that refuses an upload must not cost the
    /// event its post -- the copy stands on its own, and a reviewer can see from the warning that the
    /// image is missing. This is also why capture failures are recorded per session rather than
    /// abandoning the whole set at the first one.
    ///
    /// The pictures are taken live while the copy comes from the digest, so the two are not read from
    /// one snapshot. In practice they are seconds apart and a person reads both before anything is
    /// published; DigestSourceHash is what reveals a source that moved underneath a draft.
    /// </remarks>
    private async Task<CapturedImageSet> CaptureImagesAsync(
        EventDigest digest, CancellationToken stoppingToken)
    {
        if (!settings.ImagesEnabled || imageCapture is null || imageStore is null)
            return CapturedImageSet.NotAttempted;

        var urls = new List<string>();
        var warnings = new List<string>();
        var complete = true;

        foreach (var session in digest.Sessions.Take(settings.MaxImagesPerPost))
        {
            stoppingToken.ThrowIfCancellationRequested();

            try
            {
                var image = await imageCapture.CaptureAsync(
                    new SessionImageRequest(digest.EventId, session.SessionId, session.SessionName), stoppingToken);

                urls.Add(await imageStore.StoreAsync(settings.Channel, digest.EventId, session.SessionId, image, stoppingToken));
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Event {EventId} session {SessionId}: results image capture failed",
                    digest.EventId, session.SessionId);

                complete = false;
                warnings.Add(
                    $"NO IMAGE: the results picture for '{session.SessionName}' could not be captured " +
                    $"({ex.Message}). The post has no image for that session.");
            }
        }

        if (digest.Sessions.Count > settings.MaxImagesPerPost)
        {
            warnings.Add(
                $"Only the first {settings.MaxImagesPerPost} of {digest.Sessions.Count} race sessions were " +
                "pictured, which is the configured limit.");
        }

        return new CapturedImageSet(true, complete, urls, warnings);
    }

    /// <summary>
    /// Records that generation failed, so the attempt is visible rather than silently absent.
    /// </summary>
    /// <remarks>
    /// <see cref="SocialPostState.Failed"/> is redraftable, so this row is picked up again on the next
    /// run. That is the intent: an API outage should cost a night, not the post.
    ///
    /// The digest is deliberately not written here. Recording the hash of results this run never
    /// managed to draft from would claim a correspondence between the stored copy and the stored hash
    /// that does not exist; leaving the previous values in place keeps the row honest about which
    /// snapshot the text it still holds was written from.
    /// </remarks>
    private async Task RecordGenerationFailureAsync(
        TsContext context,
        SocialPost? existing,
        string key,
        Event evt,
        Exception failure,
        CancellationToken stoppingToken)
    {
        var post = existing ?? CreatePost(key, evt.Id);
        post.State = SocialPostState.Failed;
        post.Error = Truncate(failure.Message, MaxErrorLength);

        if (existing is null)
            context.SocialPosts.Add(post);

        await SaveAsync(context, post, evt.Id, stoppingToken);
    }

    /// <summary>
    /// Saves one post, treating a concurrent change as the other writer winning.
    /// </summary>
    /// <remarks>
    /// Both failures mean somebody else touched this post while the run was generating -- most likely
    /// a reviewer approving it, which takes a minute of reading against several seconds of generation.
    /// Their change stands. Retrying would reinstate this run's draft over an approval, which is
    /// precisely what the concurrency token exists to prevent.
    /// </remarks>
    private async Task<bool> SaveAsync(TsContext context, SocialPost post, int eventId, CancellationToken stoppingToken)
    {
        try
        {
            await context.SaveChangesAsync(stoppingToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning(
                "Event {EventId}: post {Key} changed while this draft was being generated; the other change stands",
                eventId, post.IdempotencyKey);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            logger.LogWarning(
                "Event {EventId}: post {Key} was created by another run; this draft was discarded",
                eventId, post.IdempotencyKey);
        }
        catch
        {
            // Anything else is unexpected, but it must not be left tracked either. The context is
            // shared across the whole run, so a rejected row would be re-attempted by the next event's
            // SaveChanges and fail identically -- costing every remaining event its draft after its
            // generation call had already been paid for.
            context.Entry(post).State = EntityState.Detached;
            throw;
        }

        // Left tracked, this entry would be retried by the next event's SaveChanges and fail again.
        context.Entry(post).State = EntityState.Detached;
        return false;
    }

    /// <summary>
    /// Finds the active prompt, seeding the built-in one the first time a channel is composed for.
    /// </summary>
    /// <remarks>
    /// Seeding writes a real, versioned row rather than using the built-in text implicitly. The
    /// database stays the single answer to "what were we telling the model", and the text is then
    /// editable in place, which is the whole reason prompts are stored rather than compiled in.
    ///
    /// Only when there is no prompt at all. Prompts that exist but are all inactive is a state
    /// somebody chose -- parking the seed while a replacement is written, or deactivating to stop
    /// posting -- and seeding over it would both undo that decision and collide with the existing
    /// version 1 on the unique (Kind, Channel, Version) index, failing with an error naming the wrong
    /// cause.
    /// </remarks>
    internal async Task<SocialPrompt> GetOrSeedPromptAsync(TsContext context, CancellationToken stoppingToken)
    {
        const SocialPostKind kind = SocialPostKind.EventResults;

        var existing = await context.SocialPrompts
            .FirstOrDefaultAsync(p => p.Kind == kind && p.Channel == settings.Channel && p.IsActive, stoppingToken);

        if (existing is not null)
            return existing;

        if (await context.SocialPrompts.AnyAsync(p => p.Kind == kind && p.Channel == settings.Channel, stoppingToken))
        {
            throw new InvalidOperationException(
                $"Prompts exist for {kind}/{settings.Channel} but none is active, so nothing can be " +
                "generated. Activate one. A prompt is only seeded when there is none at all, so that " +
                "deactivating them deliberately is not undone on the next run.");
        }

        var seed = DefaultPrompts.CreateSeed(kind, settings.Channel, clock.GetUtcNow().UtcDateTime);
        context.SocialPrompts.Add(seed);

        try
        {
            await context.SaveChangesAsync(stoppingToken);
            logger.LogInformation("Seeded prompt version {Version} for {Kind}/{Channel}", seed.Version, kind, settings.Channel);
            return seed;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // Another run seeded first. The partial unique index on (Kind, Channel) WHERE IsActive
            // guarantees there is exactly one to find.
            context.Entry(seed).State = EntityState.Detached;
            logger.LogInformation("Prompt for {Kind}/{Channel} was seeded by another run; using it", kind, settings.Channel);

            return await context.SocialPrompts
                .FirstOrDefaultAsync(p => p.Kind == kind && p.Channel == settings.Channel && p.IsActive, stoppingToken)
                ?? throw new InvalidOperationException(
                    $"No active prompt for {kind}/{settings.Channel} after a unique-violation on seeding.");
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>
    /// Waits for the host application to signal that it has fully started.
    /// </summary>
    private async Task WaitForStartupAsync(CancellationToken stoppingToken)
    {
        var tcs = new TaskCompletionSource();
        await using var reg = stoppingToken.Register(() => tcs.TrySetCanceled(stoppingToken));
        lifetime.ApplicationStarted.Register(() => tcs.TrySetResult());
        await tcs.Task;
        // Additional delay for K8s DNS/networking to fully stabilize
        await Task.Delay(TimeSpan.FromSeconds(3), clock, stoppingToken);
        logger.LogInformation("Host started, beginning social compose job");
    }

    /// <summary>
    /// Creates a DbContext with retry logic for transient connection failures.
    /// Uses an independent timeout so host shutdown doesn't cancel DB operations mid-connect.
    /// </summary>
    /// <remarks>
    /// The result of CanConnectAsync is checked rather than discarded. It returns false for an
    /// unreachable database instead of throwing, so ignoring it would hand back a context on the first
    /// attempt and leave the first real query to fail outside this loop -- making the retries and the
    /// backoff dead code precisely when they are needed.
    /// </remarks>
    private async Task<TsContext> CreateDbContextWithRetryAsync(CancellationToken stoppingToken)
    {
        const int maxRetries = 5;
        for (int attempt = 1; ; attempt++)
        {
            stoppingToken.ThrowIfCancellationRequested();
            TsContext? context = null;
            try
            {
                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                context = await contextFactory.CreateDbContextAsync(connectTimeout.Token);
                if (!await context.Database.CanConnectAsync(connectTimeout.Token))
                    throw new InvalidOperationException("The database is not reachable.");

                return context;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                // Disposed here rather than left to the finalizer, so a run that retries all five times
                // does not hold five pooled connections open while it waits.
                if (context is not null)
                    await context.DisposeAsync();

                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                logger.LogWarning(ex, "Database connection attempt {Attempt}/{MaxRetries} failed, retrying in {Delay}s", attempt, maxRetries, delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
            }
        }
    }
}
