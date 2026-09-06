using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventManagement.Models;
using RedMist.Social.Imaging;
using System.Security.Claims;

namespace RedMist.EventManagement.Controllers;

/// <summary>
/// Review and disposition of generated social posts.
/// </summary>
/// <remarks>
/// Nothing here publishes. This is the human step the generator is built around: a post is written by
/// a model, and a person reads it, edits it, and decides. Every write is conditional on the version
/// the reviewer read, and every state change is checked against the states it is legal to move from,
/// because the two ways this goes wrong -- publishing copy nobody read, and publishing the same
/// result twice -- both look like an ordinary successful save at the moment they happen.
///
/// Gated on the site-admin realm role rather than the organization scoping the rest of this service
/// uses: these posts speak for the site, not for one organization, and the client_id claim that
/// scopes an organization's own events says nothing about that.
/// </remarks>
[ApiController]
[Authorize(Roles = Consts.SITE_ADMIN_ROLE)]
public abstract class SocialControllerBase : Controller
{
    /// <summary>Longest page a caller can ask for, so one request cannot pull the whole table.</summary>
    private const int MaxPageSize = 200;

    private const int DefaultPageSize = 50;

    /// <summary>Characters of the copy carried in a list row.</summary>
    private const int PreviewLength = 200;

    /// <summary>Matches SocialPost.ApprovedBy, which the username is truncated to rather than failing the save.</summary>
    private const int MaxApprovedByLength = 200;

    /// <summary>
    /// SocialPost.Error is unbounded text, so this is a self-imposed ceiling on what this controller
    /// writes into it rather than a column width.
    /// </summary>
    private const int MaxErrorLength = 2000;

    /// <summary>Match the [MaxLength] on the columns of the same names.</summary>
    private const int MaxExternalIdLength = 200;
    private const int MaxExternalUrlLength = 500;
    private const int MaxRejectionReasonLength = 1000;

    /// <summary>
    /// Ceiling on a reviewer's rewrite. Facebook's own limit is far higher; this is low enough to
    /// catch a paste accident and high enough that no real post approaches it. Refused rather than
    /// truncated, because silently cutting somebody's words in half is worse than making them look.
    /// </summary>
    private const int MaxEditedTextLength = 10_000;

    protected readonly IDbContextFactory<TsContext> tsContext;
    private readonly SocialImageCleanup imageCleanup;
    private readonly TimeProvider timeProvider;

    protected ILogger Logger { get; }

    protected SocialControllerBase(
        ILoggerFactory loggerFactory,
        IDbContextFactory<TsContext> tsContext,
        SocialImageCleanup imageCleanup,
        TimeProvider? timeProvider = null)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.imageCleanup = imageCleanup;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    #region Reading

    /// <summary>
    /// Lists posts, newest first, optionally filtered by state and event.
    /// </summary>
    /// <param name="state">Only posts in this state. Omit for every state.</param>
    /// <param name="eventId">Only posts about this event.</param>
    /// <param name="skip">Posts to skip.</param>
    /// <param name="take">Posts to return, capped at 200.</param>
    /// <response code="200">The page of posts.</response>
    /// <response code="403">If the caller does not hold the site-admin role.</response>
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<SocialPostPage>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public virtual async Task<ActionResult<SocialPostPage>> LoadPosts(
        SocialPostState? state = null, int? eventId = null, int skip = 0, int take = DefaultPageSize)
    {
        if (skip < 0)
            return BadRequest("skip cannot be negative.");
        if (take <= 0)
            return BadRequest("take must be greater than zero.");

        take = Math.Min(take, MaxPageSize);

        using var context = await tsContext.CreateDbContextAsync();
        var query = context.SocialPosts.AsNoTracking();
        if (state is not null)
            query = query.Where(p => p.State == state);
        if (eventId is not null)
            query = query.Where(p => p.EventId == eventId);

        var total = await query.CountAsync();

        // Id breaks the tie: several posts of one run share a creation time closely enough that
        // ordering on it alone is not stable, and an unstable order silently drops and repeats rows
        // across pages.
        var rows = await query
            .OrderByDescending(p => p.CreatedUtc)
            .ThenByDescending(p => p.Id)
            .Skip(skip)
            .Take(take)
            .Select(p => new
            {
                p.Id,
                p.Kind,
                p.EventId,
                EventName = context.Events.Where(e => e.Id == p.EventId).Select(e => e.Name).FirstOrDefault(),
                p.Channel,
                p.State,
                p.CreatedUtc,
                p.ScheduledUtc,
                p.HasUnverifiedClaims,
                p.ImageRefs,
                p.GeneratedText,
                p.EditedText,
                RowVersion = EF.Property<uint>(p, TsContext.RowVersionProperty),
            })
            .ToListAsync();

        return new SocialPostPage
        {
            TotalCount = total,
            Posts = [.. rows.Select(r => new SocialPostSummary
            {
                Id = r.Id,
                Kind = r.Kind,
                EventId = r.EventId,
                EventName = r.EventName,
                Channel = r.Channel,
                State = r.State,
                CreatedUtc = r.CreatedUtc,
                ScheduledUtc = r.ScheduledUtc,
                HasUnverifiedClaims = r.HasUnverifiedClaims,
                ImageCount = r.ImageRefs.Count,
                Preview = Preview(Effective(r.EditedText, r.GeneratedText)),
                RowVersion = r.RowVersion,
            })],
        };
    }

    /// <summary>
    /// Loads one post in full, including the digest its claims can be checked against.
    /// </summary>
    /// <param name="id">The post.</param>
    /// <response code="200">The post.</response>
    /// <response code="404">If no such post exists.</response>
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<SocialPostDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<SocialPostDetail>> LoadPost(int id)
    {
        using var context = await tsContext.CreateDbContextAsync();
        var row = await context.SocialPosts
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new
            {
                Post = p,
                EventName = context.Events.Where(e => e.Id == p.EventId).Select(e => e.Name).FirstOrDefault(),
                RowVersion = EF.Property<uint>(p, TsContext.RowVersionProperty),
            })
            .FirstOrDefaultAsync();

        if (row is null)
            return NotFound();

        var p = row.Post;
        return new SocialPostDetail
        {
            Id = p.Id,
            Kind = p.Kind,
            EventId = p.EventId,
            EventName = row.EventName,
            Channel = p.Channel,
            State = p.State,
            IdempotencyKey = p.IdempotencyKey,
            CreatedUtc = p.CreatedUtc,
            ScheduledUtc = p.ScheduledUtc,
            PublishedUtc = p.PublishedUtc,
            ApprovedBy = p.ApprovedBy,
            ApprovedUtc = p.ApprovedUtc,
            GeneratedText = p.GeneratedText,
            EditedText = p.EditedText,
            EffectiveText = p.EffectiveText,
            ImageRefs = p.ImageRefs,
            ValidationWarnings = p.ValidationWarnings,
            HasUnverifiedClaims = p.HasUnverifiedClaims,
            DigestJson = p.DigestJson,
            DigestSourceHash = p.DigestSourceHash,
            DigestVersion = p.DigestVersion,
            Model = p.Model,
            PromptVersion = p.PromptVersion,
            GenerationAttempts = p.GenerationAttempts,
            ExternalPostId = p.ExternalPostId,
            ExternalUrl = p.ExternalUrl,
            Error = p.Error,
            PublishAttempts = p.PublishAttempts,
            RowVersion = row.RowVersion,
        };
    }

    /// <summary>
    /// Reports what the caller's own token carries.
    /// </summary>
    /// <remarks>
    /// Anonymous on purpose: its whole use is telling "you are not an administrator" apart from "your
    /// token never carried roles in the first place", and a caller in the second case cannot reach an
    /// endpoint that requires the role. It echoes only what the caller themselves presented.
    /// </remarks>
    /// <response code="200">The caller's identity as this service resolved it.</response>
    [HttpGet]
    [AllowAnonymous]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<CallerIdentity>(StatusCodes.Status200OK)]
    public virtual ActionResult<CallerIdentity> WhoAmI()
    {
        return new CallerIdentity
        {
            Name = User.Identity?.Name,
            Roles = [.. User.FindAll(ClaimTypes.Role)
                .Concat(User.FindAll(Keycloak.AuthServices.Common.KeycloakConstants.RoleClaimType))
                .Select(c => c.Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)],
            HasRealmAccessClaim = User.HasClaim(c => c.Type == Keycloak.AuthServices.Common.KeycloakConstants.RealmAccessClaimType),
            IsSiteAdmin = User.IsInRole(Consts.SITE_ADMIN_ROLE),
        };
    }

    #endregion

    #region Writing

    /// <summary>
    /// Saves a reviewer's rewrite without approving anything.
    /// </summary>
    /// <response code="200">The post as saved.</response>
    /// <response code="404">If no such post exists.</response>
    /// <response code="409">If the post changed since it was read, or is in a state that cannot be edited.</response>
    [HttpPost]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<SocialPostDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public virtual async Task<ActionResult<SocialPostDetail>> SaveEdit(SocialPostEdit edit)
    {
        // Approved is excluded because the approval was given for particular words, and letting them
        // be changed afterwards would publish text that was never the text anybody cleared. Published
        // and Publishing are excluded because the words are already out, or may be.
        return await ChangeAsync(edit, [SocialPostState.Draft, SocialPostState.PendingReview, SocialPostState.Failed],
            post =>
            {
                if (TooLong(edit.EditedText) is { } refusal)
                    return refusal;

                // The field carries the whole edit, so null and blank both mean the same thing: there
                // is no rewrite, use what was generated.
                post.EditedText = Blank(edit.EditedText) ? null : edit.EditedText;
                return null;
            },
            afterSave: post =>
            {
                Logger.LogInformation("Post {PostId} text edited by {User}", post.Id, CallerName());
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Clears a post for publication, optionally applying a final edit at the same time.
    /// </summary>
    /// <remarks>
    /// Failed is accepted as well as PendingReview. A post whose publication was interrupted and then
    /// confirmed not to have gone out is good copy that simply did not send, and refusing it here
    /// would leave it stranded: it cannot be redrafted either, because the compose job declines to
    /// overwrite a draft somebody has edited by hand.
    /// </remarks>
    /// <response code="200">The post as approved.</response>
    /// <response code="400">If there is nothing to publish, or the text is longer than a channel accepts.</response>
    /// <response code="409">If the post changed since it was read, or is not awaiting review.</response>
    [HttpPost]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<SocialPostDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public virtual async Task<ActionResult<SocialPostDetail>> ApprovePost(SocialPostApproval approval)
    {
        return await ChangeAsync(approval, [SocialPostState.PendingReview, SocialPostState.Failed], post =>
        {
            if (TooLong(approval.EditedText) is { } refusal)
                return refusal;

            // Unlike SaveEdit, null here means "leave the rewrite alone": this field is an optional
            // last change made as part of approving, not the whole edit. Blank still clears it.
            if (approval.EditedText is not null)
                post.EditedText = Blank(approval.EditedText) ? null : approval.EditedText;

            // Approving something with nothing to say would hand the publish job an empty post.
            if (Blank(post.EffectiveText))
                return BadRequest("The post has no text to publish.");

            post.State = SocialPostState.Approved;
            post.ApprovedBy = Truncate(CallerName(), MaxApprovedByLength);
            post.ApprovedUtc = Now();
            post.ScheduledUtc = approval.ScheduledUtc?.UtcDateTime ?? Now();

            // The previous attempt's error would otherwise sit on an approved post and read as though
            // this one had failed.
            post.Error = null;

            return null;
        },
        afterSave: post =>
        {
            Logger.LogInformation(
                "Post {PostId} approved by {User}, scheduled for {Scheduled:O}",
                post.Id, post.ApprovedBy, post.ScheduledUtc);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Returns an approved post to review, so it can be edited or reconsidered before it goes out.
    /// </summary>
    /// <remarks>
    /// The alternative to this is rejecting, which gives up the post's images. A reviewer changing
    /// their mind before the publish job runs should not have to destroy anything to do it.
    /// </remarks>
    /// <response code="200">The post, back in review.</response>
    /// <response code="409">If the post changed since it was read, or is not approved.</response>
    [HttpPost]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<SocialPostDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public virtual async Task<ActionResult<SocialPostDetail>> UnapprovePost(SocialPostChange change)
    {
        // Approved and nothing else. Publishing in particular: moving a post out of it here would put
        // it somewhere ApprovePost accepts, which is the long way round to publishing something that
        // may already be live.
        return await ChangeAsync(change, [SocialPostState.Approved], post =>
        {
            post.State = SocialPostState.PendingReview;
            post.ApprovedBy = null;
            post.ApprovedUtc = null;
            return null;
        },
        afterSave: post =>
        {
            Logger.LogInformation("Post {PostId} returned to review by {User}", post.Id, CallerName());
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Declines a post and gives up its images.
    /// </summary>
    /// <remarks>
    /// The row is kept. A rejected post still holds its idempotency key, which is what stops the same
    /// event being drafted again the following night.
    /// </remarks>
    /// <response code="200">The post as rejected.</response>
    /// <response code="409">
    /// If the post changed since it was read, or is published, publishing, or already rejected.
    /// </response>
    [HttpPost]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<SocialPostDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public virtual async Task<ActionResult<SocialPostDetail>> RejectPost(SocialPostRejection rejection)
    {
        List<string> images = [];

        return await ChangeAsync(rejection,
            [SocialPostState.Draft, SocialPostState.PendingReview, SocialPostState.Approved, SocialPostState.Failed],
            post =>
            {
                // Captured before the save, released after it: the row is what says these images are
                // nobody's, so deleting first would destroy the pictures of a post that then failed to
                // be rejected.
                images = [.. post.ImageRefs];

                post.State = SocialPostState.Rejected;
                post.RejectionReason = Truncate(rejection.Reason, MaxRejectionReasonLength);
                post.ImageRefs = [];
                return null;
            },
            afterSave: post =>
            {
                Logger.LogInformation("Post {PostId} rejected by {User}", post.Id, CallerName());
                return ReleaseAsync(images);
            });
    }

    /// <summary>
    /// Deletes a post and its images.
    /// </summary>
    /// <remarks>
    /// Published and Publishing posts are refused. A published post exists on the channel whether or
    /// not this row does, and deleting the row is how the site loses the only record of something it
    /// said in public; a post in Publishing may or may not have gone out, and its row is the only
    /// evidence anyone will ever have. Reject them instead, or resolve the publishing one first.
    ///
    /// Deleting rather than rejecting also releases the idempotency key, so the next compose run will
    /// draft this event again. That is the point of offering both.
    /// </remarks>
    /// <response code="200">The post was deleted.</response>
    /// <response code="409">If the post changed since it was read, or is published or publishing.</response>
    [HttpPost]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public virtual async Task<IActionResult> DeletePost(SocialPostChange change)
    {
        if (Validate(change) is { } invalid)
            return invalid;

        using var context = await tsContext.CreateDbContextAsync();
        var post = await context.SocialPosts.FirstOrDefaultAsync(p => p.Id == change.Id);
        if (post is null)
            return NotFound();

        if (post.State is SocialPostState.Published or SocialPostState.Publishing)
        {
            return Conflict(
                $"A post that is {post.State} cannot be deleted: it may exist on the channel, and this row " +
                "is the only record of it. Resolve or reject it instead.");
        }

        var images = post.ImageRefs.ToList();
        var caller = CallerName();

        context.Entry(post).Property<uint>(TsContext.RowVersionProperty).OriginalValue = change.RowVersion;
        context.SocialPosts.Remove(post);

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale(change.Id);
        }

        Logger.LogInformation("Post {PostId} deleted by {User}", change.Id, caller);
        await ReleaseAsync(images);
        return Ok();
    }

    /// <summary>
    /// Records what became of a post whose publish attempt was interrupted.
    /// </summary>
    /// <remarks>
    /// Publishing means the process stopped without learning whether the channel accepted the post,
    /// so it is never retried automatically -- that is how one result is posted twice in public. A
    /// person looks at the channel and says which it was.
    /// </remarks>
    /// <response code="200">The post, moved out of Publishing.</response>
    /// <response code="400">If it was published but no channel id was given.</response>
    /// <response code="409">If the post changed since it was read, or is not publishing.</response>
    [HttpPost]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<SocialPostDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public virtual async Task<ActionResult<SocialPostDetail>> ResolvePost(SocialPostResolution resolution)
    {
        return await ChangeAsync(resolution, [SocialPostState.Publishing], post =>
        {
            if (resolution.WasPublished)
            {
                // Without the channel's id there is nothing to link to, nothing to delete it by, and
                // no way to tell this apart from a guess.
                if (Blank(resolution.ExternalPostId))
                    return BadRequest("A post recorded as published needs the channel's id for it.");

                post.State = SocialPostState.Published;
                post.PublishedUtc = Now();
                post.ExternalPostId = Truncate(resolution.ExternalPostId, MaxExternalIdLength);
                post.ExternalUrl = Truncate(resolution.ExternalUrl, MaxExternalUrlLength);
                post.Error = null;
            }
            else
            {
                post.State = SocialPostState.Failed;
                post.Error = Truncate(
                    $"Publication was interrupted and {CallerName()} confirmed the post did not reach the channel.",
                    MaxErrorLength);
            }

            return null;
        },
        afterSave: post =>
        {
            Logger.LogWarning(
                "Post {PostId} resolved out of Publishing by {User} as {State}",
                post.Id, CallerName(), post.State);
            return Task.CompletedTask;
        });
    }

    #endregion

    #region Plumbing

    /// <summary>
    /// The shape every write shares: validate, load, check the state, mutate, save conditionally.
    /// </summary>
    /// <remarks>
    /// One place for it so that no endpoint can be added that forgets the version check. The mutation
    /// returns an <see cref="ActionResult"/> to refuse with, or null to go ahead. Anything a caller
    /// wants done only if the row really was written -- releasing images, recording the decision in
    /// the log -- belongs in <paramref name="afterSave"/>, which is why the audit lines are not
    /// written inside the mutation: a refused approval would otherwise be logged as an approval.
    /// </remarks>
    private async Task<ActionResult<SocialPostDetail>> ChangeAsync(
        SocialPostChange change,
        SocialPostState[] allowedFrom,
        Func<SocialPost, ActionResult?> mutate,
        Func<SocialPost, Task>? afterSave = null)
    {
        if (Validate(change) is { } invalid)
            return invalid;

        using var context = await tsContext.CreateDbContextAsync();
        var post = await context.SocialPosts.FirstOrDefaultAsync(p => p.Id == change.Id);
        if (post is null)
            return NotFound();

        var entry = context.Entry(post);

        // Compared here as well as in the UPDATE's WHERE clause, and not only for the better message.
        // A mutation that changes nothing -- re-saving identical text -- makes EF issue no command at
        // all, so SaveChanges succeeds without ever testing the version and a stale write is answered
        // with 200.
        if (entry.Property<uint>(TsContext.RowVersionProperty).CurrentValue != change.RowVersion)
            return Stale(change.Id);

        if (!allowedFrom.Contains(post.State))
        {
            return Conflict(
                $"A post that is {post.State} cannot be changed this way; expected one of " +
                $"{string.Join(", ", allowedFrom)}.");
        }

        // The version the reviewer read, not the one just loaded. Loading refreshes it, so leaving it
        // alone would compare the row against itself and never conflict with anything. This closes the
        // race between the check above and the write.
        entry.Property<uint>(TsContext.RowVersionProperty).OriginalValue = change.RowVersion;

        if (mutate(post) is { } refusal)
            return refusal;

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale(change.Id);
        }

        if (afterSave is not null)
            await afterSave(post);

        return await LoadPost(change.Id);
    }

    private ActionResult? Validate(SocialPostChange change)
    {
        if (change is null)
            return BadRequest("A change is required.");
        if (change.Id <= 0)
            return BadRequest("A post id is required.");

        // Zero would otherwise be sent by a client that simply has not implemented the check, and
        // would match no row, which reads as "deleted by somebody else" rather than as a bug.
        if (change.RowVersion == 0)
            return BadRequest("RowVersion is required; send back the value that was read with the post.");

        return null;
    }

    private ConflictObjectResult Stale(int postId)
    {
        Logger.LogInformation("Post {PostId} was changed by somebody else; the request was refused", postId);
        return Conflict("This post changed after you read it. Load it again and take another look before deciding.");
    }

    /// <summary>
    /// Gives up images, after the row that had a claim to them has been saved.
    /// </summary>
    /// <remarks>
    /// Deliberately not tied to the request's cancellation token. This runs after the commit, and the
    /// row no longer references these images, so abandoning it because the reviewer closed the tab
    /// would leave them public with nothing anywhere pointing at them -- not the row, which has been
    /// emptied, and not the nightly cleanup, which reads the row. They are logged before the attempt
    /// for the same reason: a delete that fails has to leave the addresses somewhere recoverable.
    /// </remarks>
    private async Task ReleaseAsync(List<string> images)
    {
        if (images.Count == 0)
            return;

        Logger.LogInformation("Releasing {Total} results image(s): {Images}", images.Count, string.Join(", ", images));
        var released = await imageCleanup.ReleaseAsync(images, CancellationToken.None);
        Logger.LogInformation("Released {Released} of {Total} results image(s)", released, images.Count);
    }

    /// <summary>
    /// Who is acting, for the audit trail. The username rather than the subject id, because the point
    /// of recording it is that a person can tell who approved something.
    /// </summary>
    private string CallerName() => User.Identity?.Name ?? User.FindFirstValue("preferred_username") ?? "unknown";

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Refuses copy longer than a channel would take.
    /// </summary>
    /// <remarks>
    /// The generator is held to a much shorter limit and redrafts when it runs over, but a reviewer's
    /// rewrite goes through none of that. Without a bound here the first sign of trouble is the
    /// channel refusing the post, hours later, with nobody watching.
    /// </remarks>
    private ActionResult? TooLong(string? text) =>
        text is { Length: > MaxEditedTextLength }
            ? BadRequest($"The text is longer than {MaxEditedTextLength} characters, which no channel will accept.")
            : null;

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static string? Effective(string? edited, string? generated) => Blank(edited) ? generated : edited;

    private static string? Preview(string? text) =>
        text is null ? null : text.Length <= PreviewLength ? text : text[..PreviewLength];

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    #endregion
}
