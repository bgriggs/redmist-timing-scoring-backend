using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database.Models;
using RedMist.PostEventReports.Email;

namespace RedMist.PostEventReports.Sections.Viewership;

/// <summary>
/// The viewership section: how many people watched the event, on what, and when.
/// </summary>
/// <remarks>
/// The first section of the post-event report, and the model for the ones that follow. It reads its
/// own data, computes its own numbers, attaches its own rows to the report, and renders its own
/// block; nothing about it is known to the job or the email shell.
/// </remarks>
public class ViewershipSection(PostEventReportSettings settings) : IReportSection
{
    public const string SectionCode = "viewership";

    public string Code => SectionCode;

    public int Order => 10;

    public async Task<ReportSectionResult?> BuildAsync(ReportContext context, CancellationToken cancellationToken)
    {
        var sessions = await context.Db.EventViewerSessions
            .AsNoTracking()
            .Where(s => s.EventId == context.Event.Id)
            .ToListAsync(cancellationToken);

        if (sessions.Count == 0)
        {
            return null;
        }

        // Guards against one corrupt timestamp producing a window of weeks. The event's own dates are
        // used only as this bound, never as the window, because they land at midnight.
        //
        // Hours rather than days, and deliberately: the bound has to be tighter than the longest
        // window that will be reported on, or a single stray early connection pins the window start
        // and the truncation at the far end cuts real racing sessions off the report entirely.
        var earliest = DateTime.SpecifyKind(context.Event.StartDate, DateTimeKind.Utc).AddHours(-12);
        var latest = DateTime.SpecifyKind(context.Event.EndDate, DateTimeKind.Utc).AddHours(12);
        if (latest > context.NowUtc)
        {
            latest = context.NowUtc;
        }

        var summary = ViewershipAggregator.Aggregate(
            sessions,
            context.RacingSessions,
            TrackTime.ForEvent(await SessionOffsetsAsync(context, cancellationToken)),
            earliest,
            latest,
            settings.MaxWindow);

        if (summary.TotalViewerMinutes < settings.MinViewerMinutes)
        {
            // Below this there is nothing worth telling anyone: a couple of stray connections from
            // the organizer's own laptop while they set the relay up.
            return null;
        }

        // Rendered before the summary is attached to the report. The other order would leave the
        // summary and every one of its bucket rows hanging off a report the section had just failed
        // to produce a block for - persisted, but describing content nobody was sent.
        var body = ViewershipSectionRenderer.Render(summary);
        context.Report.Viewership = summary;

        var preheader = $"Peak {summary.MaxConcurrent} watching"
                        + (summary.TopClientType is { } top ? $" - mostly on {top}" : string.Empty);

        return new ReportSectionResult(Code, "Viewership", body, preheader);
    }

    /// <summary>
    /// The events's sessions' reported UTC offsets, in the order the sessions ran.
    /// </summary>
    /// <remarks>
    /// In order because the first usable one wins: the sessions of one event are at one track, so
    /// they either agree or the later ones are corrupt.
    /// </remarks>
    private static async Task<List<double>> SessionOffsetsAsync(ReportContext context, CancellationToken cancellationToken)
        => await context.Db.Sessions
            .AsNoTracking()
            .Where(s => s.EventId == context.Event.Id)
            .OrderBy(s => s.StartTime)
            .Select(s => s.LocalTimeZoneOffset)
            .ToListAsync(cancellationToken);
}
