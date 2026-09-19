using System.ComponentModel.DataAnnotations;

namespace RedMist.Database.Models;

/// <summary>
/// One post-event report for one event: the record that it was produced, what went into it, and who
/// it reached.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately section-agnostic. The report is assembled from a list of sections - viewership is the
/// first, and lap and competitor statistics, control log usage and sponsor impressions are the kind
/// of thing that follows - and each section owns its own tables keyed to this row. Nothing here knows
/// what a section contains.
/// </para>
/// <para>
/// The unique index on <see cref="EventId"/> is what makes an event reportable exactly once. The job
/// also anti-joins on this table when picking candidates; the index is the backstop for two runs
/// overlapping, where the loser sees a duplicate key rather than sending a second copy.
/// </para>
/// </remarks>
public class PostEventReport
{
    public long Id { get; set; }

    public int EventId { get; set; }

    public int OrganizationId { get; set; }

    public DateTime GeneratedUtc { get; set; }

    [MaxLength(20)]
    public string State { get; set; } = PostEventReportState.Pending;

    /// <summary>
    /// The codes of the sections that produced content, as a JSON array.
    /// </summary>
    /// <remarks>
    /// Recorded so that a later reader can tell what a given email actually said. Sections are added
    /// over time, and without this there is no way to know whether an old report omitted a section
    /// because it had nothing to say or because it did not exist yet.
    /// </remarks>
    public string? SectionsJson { get; set; }

    /// <summary>The improvement suggestions as rendered, so the email and the record cannot diverge.</summary>
    public string? SuggestionsJson { get; set; }

    public int RecipientCount { get; set; }

    public int SendFailureCount { get; set; }

    public string? Error { get; set; }

    public EventViewershipSummary? Viewership { get; set; }
}

/// <summary>
/// What became of a report. Text rather than an ordinal so the table can be read and triaged by hand.
/// </summary>
public static class PostEventReportState
{
    /// <summary>Rows written, nothing sent yet. A row left in this state means a run died mid-send.</summary>
    public const string Pending = "Pending";
    public const string Sent = "Sent";

    /// <summary>Some recipients got it and some did not; the failures are not retried.</summary>
    public const string PartiallySent = "PartiallySent";

    /// <summary>No section had anything to say, so nothing was sent.</summary>
    public const string NoContent = "NoContent";

    /// <summary>The organization has opted out of post-event reports.</summary>
    public const string Suppressed = "Suppressed";

    /// <summary>
    /// There was a report to send and nobody to send it to: the organization has no administrator
    /// whose username is a usable email address. Distinct from <see cref="NoContent"/> because the
    /// two need different things done about them - this one is a missing mapping, not a quiet event.
    /// </summary>
    public const string NoRecipients = "NoRecipients";

    public const string Failed = "Failed";

    public static readonly string[] All =
        [Pending, Sent, PartiallySent, NoContent, Suppressed, NoRecipients, Failed];
}
