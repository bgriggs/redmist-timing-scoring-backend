using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;

namespace RedMist.Database.Models;

/// <summary>
/// One social media post moving through draft, review, approval and publication.
/// </summary>
/// <remarks>
/// The table is the queue. Posts are a handful a week, must survive for days, and are edited by a
/// human mid-flight, so <see cref="State"/> plus a scheduled time answers "what is ready to publish"
/// with an ordinary query and leaves an audit trail that a stream would not.
/// </remarks>
public class SocialPost
{
    [Key]
    public int Id { get; set; }

    /// <summary>What the post is about, which decides the digest shape and the prompt used.</summary>
    public SocialPostKind Kind { get; set; }

    /// <summary>Event this post reports on. Null for posts not tied to an event, such as a feature announcement.</summary>
    public int? EventId { get; set; }

    public SocialChannel Channel { get; set; }

    /// <summary>
    /// Natural key for the thing this post covers, e.g. "EventResults:341:Facebook". Uniquely indexed
    /// so a retried compose job updates the existing draft instead of creating a second one for the
    /// same event. Build it with <see cref="BuildIdempotencyKey"/>, never by hand.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// Where the post is in its lifecycle.
    /// </summary>
    /// <remarks>
    /// A writer that finds an existing row by <see cref="IdempotencyKey"/> must scope its update to
    /// the states it is allowed to overwrite. Terminal states are not protected by the schema, so a
    /// re-compose after publication -- which is expected, since stored results are rewritten in place
    /// when a fuller snapshot arrives -- would otherwise reset a published post to
    /// <see cref="SocialPostState.PendingReview"/> and orphan its <see cref="ExternalPostId"/>.
    /// </remarks>
    public SocialPostState State { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// When the post may go out. Non-nullable on purpose: the publish job selects on
    /// <c>State = 'Approved' AND ScheduledUtc &lt;= now</c>, and SQL three-valued logic drops NULLs
    /// from that comparison, so a nullable column would leave an approved post looking approved
    /// forever while never being picked up. Set it to "now" to mean "as soon as it is approved".
    /// </summary>
    public DateTime ScheduledUtc { get; set; }

    public DateTime? PublishedUtc { get; set; }

    [MaxLength(200)]
    public string? ApprovedBy { get; set; }

    public DateTime? ApprovedUtc { get; set; }

    /// <summary>
    /// The facts the copy was generated from, serialized. Held as JSON rather than a typed digest
    /// because the digest lives in RedMist.Social, which depends on this project rather than the
    /// other way round; storing it keeps a draft readable even after the source results change.
    /// </summary>
    public string? DigestJson { get; set; }

    /// <summary>
    /// Hash of the source the digest was built from, from DigestProvenance. Compared against a fresh
    /// digest at review time to reveal that the results were rewritten, or the event withdrawn, after
    /// this draft was written.
    /// </summary>
    [MaxLength(64)]
    public string? DigestSourceHash { get; set; }

    /// <summary>What the model wrote, kept verbatim even after editing.</summary>
    public string? GeneratedText { get; set; }

    /// <summary>
    /// What a human changed it to. Kept separate from <see cref="GeneratedText"/> so the amount of
    /// rewriting is measurable -- the most direct signal of whether the prompt is working.
    /// </summary>
    public string? EditedText { get; set; }

    /// <summary>
    /// The text that will actually be published. An edit that is blank falls back to the generated
    /// copy rather than publishing nothing: clearing the edit box in a review UI produces an empty
    /// string, not a null, and an empty post is worse than an unedited one.
    /// </summary>
    [NotMapped]
    public string? EffectiveText => string.IsNullOrWhiteSpace(EditedText) ? GeneratedText : EditedText;

    public List<string> ImageRefs { get; set; } = [];

    /// <summary>Model that produced <see cref="GeneratedText"/>, for telling a model change from a prompt change.</summary>
    [MaxLength(100)]
    public string? Model { get; set; }

    /// <summary>
    /// Version of the prompt used, referencing <see cref="SocialPrompt.Version"/> for the same kind
    /// and channel. Null for posts written by hand, which is distinct from version zero.
    /// </summary>
    public int? PromptVersion { get; set; }

    /// <summary>
    /// Version of the digest projection that produced the facts, from DigestProvenance. Stored as its
    /// own column alongside <see cref="Model"/> and <see cref="PromptVersion"/> so that a change in
    /// the numbers can be told from a change in the wording without digging into the digest JSON.
    /// </summary>
    [MaxLength(20)]
    public string? DigestVersion { get; set; }

    /// <summary>How many generation attempts it took, counting those rejected by output validation.</summary>
    public int GenerationAttempts { get; set; }

    /// <summary>Findings from digest and copy validation, surfaced to the reviewer.</summary>
    public string? ValidationWarnings { get; set; }

    /// <summary>
    /// True when the copy still failed validation after every generation attempt -- most importantly,
    /// when it states a number the digest cannot support.
    /// </summary>
    /// <remarks>
    /// A column rather than a line in <see cref="ValidationWarnings"/> because it has to be queryable
    /// and impossible to miss. The draft is offered for review either way, so this is the only thing
    /// separating "read this carefully" from "read this"; leaving it as free text would make the
    /// review UI substring-match a log convention to tell the two apart.
    /// </remarks>
    public bool HasUnverifiedClaims { get; set; }

    /// <summary>Identifier returned by the channel once published; the proof a post exists remotely.</summary>
    [MaxLength(200)]
    public string? ExternalPostId { get; set; }

    [MaxLength(500)]
    public string? ExternalUrl { get; set; }

    /// <summary>Why the last generation or publish attempt failed.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// Publish attempts made. Incremented as part of claiming the post, so a row left in
    /// <see cref="SocialPostState.Publishing"/> shows how many times it has been tried.
    /// </summary>
    public int PublishAttempts { get; set; }

    /// <summary>
    /// Builds the natural key for a post. Defined once because the uniqueness guarantee is only as
    /// good as every writer agreeing on the format: two call sites formatting it differently would
    /// pass the unique index and produce two drafts, then two public posts, for the same event.
    /// </summary>
    /// <param name="kind">What the post is about.</param>
    /// <param name="eventId">The event being reported on, where there is one.</param>
    /// <param name="channel">Where it will be published.</param>
    /// <param name="discriminator">
    /// What makes this post distinct when it is not tied to an event. Required in that case: without
    /// it every feature announcement on a channel would share one key, so the second would collide
    /// with -- and, under find-by-key-then-update, silently overwrite -- the first, including one
    /// already published. Any stable per-post token does: a release tag, a slug, a date.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when neither an event nor a discriminator identifies the post, which would otherwise
    /// produce a key shared by every post of that kind and channel.
    /// </exception>
    public static string BuildIdempotencyKey(
        SocialPostKind kind,
        int? eventId,
        SocialChannel channel,
        string? discriminator = null)
    {
        var hasDiscriminator = !string.IsNullOrWhiteSpace(discriminator);
        if (eventId is null && !hasDiscriminator)
        {
            throw new ArgumentException(
                "A post with no event needs a discriminator, otherwise every post of this kind and " +
                "channel shares one key and they overwrite each other.",
                nameof(discriminator));
        }

        var subject = eventId?.ToString(CultureInfo.InvariantCulture) ?? discriminator!.Trim();
        return hasDiscriminator && eventId is not null
            ? $"{kind}:{subject}:{discriminator!.Trim()}:{channel}"
            : $"{kind}:{subject}:{channel}";
    }
}

/// <summary>What a post is about.</summary>
public enum SocialPostKind
{
    /// <summary>Results of an event's race sessions.</summary>
    EventResults,

    /// <summary>A new capability or change worth telling people about.</summary>
    FeatureAnnouncement,

    /// <summary>Written by hand rather than generated.</summary>
    Manual,
}

/// <summary>Where a post is published.</summary>
public enum SocialChannel
{
    Facebook,
    Instagram,
}

/// <summary>
/// Lifecycle of a post. Only a human moves a post to <see cref="Approved"/>, and only the publish job
/// moves it beyond that.
/// </summary>
public enum SocialPostState
{
    /// <summary>Created but not yet generated.</summary>
    Draft,

    /// <summary>Generated and validated, waiting for a person to read it.</summary>
    PendingReview,

    /// <summary>Cleared for publication, subject to <see cref="SocialPost.ScheduledUtc"/>.</summary>
    Approved,

    /// <summary>
    /// Claimed by the publish job, which is about to call the channel. This is a trap, not a
    /// transient: a post left here means the process died without knowing whether the channel
    /// accepted it, so it must never be retried automatically -- that is how the same result gets
    /// posted twice in public. A person resolves it.
    /// </summary>
    Publishing,

    /// <summary>Live on the channel, with <see cref="SocialPost.ExternalPostId"/> set.</summary>
    Published,

    /// <summary>Read and declined. Kept rather than deleted so the same event is not drafted again.</summary>
    Rejected,

    /// <summary>Generation or publication failed in a way that is safe to retry.</summary>
    Failed,
}
