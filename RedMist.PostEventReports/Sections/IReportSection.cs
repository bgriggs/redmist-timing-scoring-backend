using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.TimingCommon.Models;
using Event = RedMist.TimingCommon.Models.Configuration.Event;

namespace RedMist.PostEventReports.Sections;

/// <summary>
/// One block of a post-event report.
/// </summary>
/// <remarks>
/// <para>
/// The report is assembled from whatever sections are registered, so adding one - lap and competitor
/// statistics, control log usage, flag and caution summary, sponsor impressions, a year-on-year
/// comparison - is one class and one registration, with no change to the job, the email shell or the
/// schedule, and no second thing landing in the organizer's inbox.
/// </para>
/// <para>
/// A section owns whatever tables it needs, hung off the report row. The report itself knows nothing
/// about what any section contains.
/// </para>
/// </remarks>
public interface IReportSection
{
    /// <summary>
    /// Stable identifier recorded against the report, so a later reader can tell what a given email
    /// actually said rather than guessing from which sections exist today.
    /// </summary>
    string Code { get; }

    /// <summary>Position in the email. Lower first.</summary>
    int Order { get; }

    /// <summary>
    /// Builds this section, or returns null when it has nothing worth saying.
    /// </summary>
    /// <remarks>
    /// Returning null is the normal way to be left out - an event with no viewers, a section whose
    /// data was never collected. Throwing is not: the engine contains it, but the section is then
    /// missing from a report that still goes out, and the admin is told.
    /// </remarks>
    Task<ReportSectionResult?> BuildAsync(ReportContext context, CancellationToken cancellationToken);
}

/// <summary>A section's rendered contribution to the report.</summary>
/// <param name="Code">The section's <see cref="IReportSection.Code"/>.</param>
/// <param name="Title">Heading shown above the block.</param>
/// <param name="BodyHtml">The block's markup, already escaped and email-safe.</param>
/// <param name="Preheader">
/// A short line for the inbox preview, or null. The first section that offers one wins.
/// </param>
public sealed record ReportSectionResult(string Code, string Title, string BodyHtml, string? Preheader = null);

/// <summary>
/// Everything a section or a suggestion is given about the event being reported on.
/// </summary>
/// <remarks>
/// Deliberately broad. A section that needs something not here should get it from
/// <see cref="Db"/> rather than have the job grow another parameter for it, so adding a section stays
/// a local change.
/// </remarks>
public sealed class ReportContext(TsContext db, Event evt, Organization organization,
    IReadOnlyList<SessionWindow> racingSessions, DateTime nowUtc)
{
    public TsContext Db { get; } = db;

    public Event Event { get; } = evt;

    public Organization Organization { get; } = organization;

    /// <summary>The event's racing sessions and when each of them ran, in order.</summary>
    public IReadOnlyList<SessionWindow> RacingSessions { get; } = racingSessions;

    public DateTime NowUtc { get; } = nowUtc;

    /// <summary>
    /// The report row being built. Sections attach their own data to it.
    /// </summary>
    public PostEventReport Report { get; internal set; } = null!;
}
