using RedMist.Database.Models;

namespace RedMist.EventManagement.Models;

/// <summary>
/// One post as it appears in a review list.
/// </summary>
/// <remarks>
/// Deliberately not the entity. Returning <see cref="SocialPost"/> would invite a client to post one
/// back, and a bound entity would let a reviewer's request set State, ApprovedBy, ExternalPostId or
/// PublishAttempts -- every field whose whole purpose is that only the server writes it.
/// </remarks>
public class SocialPostSummary
{
    public int Id { get; set; }
    public SocialPostKind Kind { get; set; }
    public int? EventId { get; set; }

    /// <summary>Name of the event this reports on, so a reviewer is not reading bare ids.</summary>
    public string? EventName { get; set; }

    public SocialChannel Channel { get; set; }
    public SocialPostState State { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ScheduledUtc { get; set; }

    /// <summary>
    /// The generated copy still failed validation after every attempt. Carried in the summary rather
    /// than only in the detail so a list can mark it before anything is opened.
    /// </summary>
    /// <remarks>
    /// It describes what the model wrote, and is not recomputed when a reviewer rewrites the post --
    /// a flagged draft stays flagged after the offending sentence is removed. Deliberate: the flag
    /// says this one needed looking at, and clearing it on edit would erase that from the record.
    /// </remarks>
    public bool HasUnverifiedClaims { get; set; }

    /// <summary>How many results images are attached, which the reviewer has to look at too.</summary>
    public int ImageCount { get; set; }

    /// <summary>Opening of the text that would be published, for recognizing a post in a list.</summary>
    public string? Preview { get; set; }

    /// <inheritdoc cref="SocialPostDetail.RowVersion"/>
    public uint RowVersion { get; set; }
}

/// <summary>
/// Everything a person needs in order to decide whether a post should go out.
/// </summary>
public class SocialPostDetail
{
    public int Id { get; set; }
    public SocialPostKind Kind { get; set; }
    public int? EventId { get; set; }
    public string? EventName { get; set; }
    public SocialChannel Channel { get; set; }
    public SocialPostState State { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
    public DateTime ScheduledUtc { get; set; }
    public DateTime? PublishedUtc { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedUtc { get; set; }

    /// <summary>What the model wrote, kept verbatim so an edit can be compared against it.</summary>
    public string? GeneratedText { get; set; }

    public string? EditedText { get; set; }

    /// <summary>What would actually be published, which is the edit unless it is blank.</summary>
    public string? EffectiveText { get; set; }

    /// <summary>
    /// Public URLs of the results images. Part of the post under review: they are fetchable from the
    /// moment they are stored, and they go out attached to the copy.
    /// </summary>
    public List<string> ImageRefs { get; set; } = [];

    public string? ValidationWarnings { get; set; }
    public bool HasUnverifiedClaims { get; set; }

    /// <summary>The facts the copy was generated from, for checking a claim against the numbers.</summary>
    public string? DigestJson { get; set; }

    public string? DigestSourceHash { get; set; }
    public string? DigestVersion { get; set; }
    public string? Model { get; set; }
    public int? PromptVersion { get; set; }
    public int GenerationAttempts { get; set; }

    public string? ExternalPostId { get; set; }
    public string? ExternalUrl { get; set; }
    public string? Error { get; set; }
    public int PublishAttempts { get; set; }

    /// <summary>
    /// The row's version when it was read, which every write has to send back.
    /// </summary>
    /// <remarks>
    /// A reviewer reads a draft and takes a minute over it; the compose job rewrites that same row in
    /// seconds when the results change. Without this the approval would be applied to copy nobody
    /// read, and the whole point of the review step is that somebody read the words that went out.
    /// A mismatch is reported as a conflict so the page is re-read rather than overwritten.
    /// </remarks>
    public uint RowVersion { get; set; }
}

/// <summary>A page of posts, with the total so a UI can page without guessing.</summary>
public class SocialPostPage
{
    public List<SocialPostSummary> Posts { get; set; } = [];

    /// <summary>Posts matching the filter, not the number returned.</summary>
    public int TotalCount { get; set; }
}

/// <summary>
/// A change to one post, conditional on the version that was read. Used directly by the changes that
/// carry nothing else -- returning a post to review, and deleting one.
/// </summary>
public class SocialPostChange
{
    public int Id { get; set; }

    /// <inheritdoc cref="SocialPostDetail.RowVersion"/>
    public uint RowVersion { get; set; }
}

/// <summary>Saves a reviewer's rewrite without deciding anything about it.</summary>
public class SocialPostEdit : SocialPostChange
{
    /// <summary>
    /// The rewritten copy, in full. Null and blank both mean "there is no rewrite, use what was
    /// generated" -- the generated text is never overwritten, so clearing the box restores it rather
    /// than publishing nothing. Note this differs from <see cref="SocialPostApproval.EditedText"/>,
    /// where null leaves an existing rewrite alone.
    /// </summary>
    public string? EditedText { get; set; }
}

/// <summary>Clears a post for publication.</summary>
public class SocialPostApproval : SocialPostChange
{
    /// <summary>
    /// A final edit applied as part of approving, so the two are one atomic decision. Null leaves any
    /// existing rewrite untouched, which is what makes this safe to omit; blank clears it and
    /// restores the generated copy. <see cref="SocialPostEdit.EditedText"/> is the whole edit and
    /// treats null as blank.
    /// </summary>
    public string? EditedText { get; set; }

    /// <summary>
    /// When it may go out. Omitted means now, which is what "publish as soon as it is approved" means
    /// to the publish job.
    /// </summary>
    /// <remarks>
    /// An offset rather than a bare timestamp, so the wire format has to say which moment is meant.
    /// A date picker sends local wall-clock with no zone; read as UTC that silently moves a scheduled
    /// post by hours, and the column it lands in cannot tell the difference afterwards.
    /// </remarks>
    public DateTimeOffset? ScheduledUtc { get; set; }
}

/// <summary>Declines a post and gives up its images.</summary>
public class SocialPostRejection : SocialPostChange
{
    /// <summary>Why, recorded so the prompt can be judged against the reasons drafts are refused.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// Records what actually happened to a post whose publish attempt was interrupted.
/// </summary>
/// <remarks>
/// A post in Publishing means the process died without learning whether the channel accepted it. Only
/// a person who has looked at the channel can say, which is why this is an endpoint rather than a
/// retry.
/// </remarks>
public class SocialPostResolution : SocialPostChange
{
    /// <summary>True if the post is live on the channel and must not be sent again.</summary>
    public bool WasPublished { get; set; }

    /// <summary>The channel's id for it, required when it did go out; it is the proof it exists.</summary>
    public string? ExternalPostId { get; set; }

    public string? ExternalUrl { get; set; }
}

/// <summary>
/// What a caller's token actually carries, for diagnosing a refusal.
/// </summary>
/// <remarks>
/// Every way this API's gating can be misconfigured -- a client issuing lightweight access tokens, a
/// missing roles scope, a renamed realm role -- produces the same 403 as simply not being an
/// administrator. This reports the caller their own token's contents so the two can be told apart
/// without reading server logs. It discloses nothing they do not already hold.
/// </remarks>
public class CallerIdentity
{
    public string? Name { get; set; }

    /// <summary>Roles the API resolved, after the realm-roles transformation has run.</summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>
    /// Whether the token carried realm_access at all. False here with a valid token is the signature
    /// of lightweight access tokens or a client missing the roles scope: authentication works and
    /// every role check refuses.
    /// </summary>
    public bool HasRealmAccessClaim { get; set; }

    /// <summary>Whether this caller satisfies the role these endpoints require.</summary>
    public bool IsSiteAdmin { get; set; }
}
