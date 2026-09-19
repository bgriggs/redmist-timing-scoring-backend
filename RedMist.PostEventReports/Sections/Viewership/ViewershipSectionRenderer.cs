using RedMist.Database.Models;
using RedMist.PostEventReports.Email;
using System.Text;

namespace RedMist.PostEventReports.Sections.Viewership;

/// <summary>
/// Renders the viewership block of a post-event report.
/// </summary>
/// <remarks>
/// The bars stack the <b>average</b> concurrency and print the <b>maximum</b> beside them. Per-type
/// time-weighted averages sum exactly to the all-types average, so a stacked bar of averages is
/// arithmetically honest; maxima do not sum, so stacking those would draw a picture of a moment that
/// never happened.
/// </remarks>
public static class ViewershipSectionRenderer
{
    private const int PeakColumnWidth = 70;
    private const int TimeColumnWidth = 78;

    public static string Render(EventViewershipSummary summary)
    {
        var sb = new StringBuilder();
        sb.Append(EmailHtml.OpenTable());

        AppendHeadline(sb, summary);
        AppendLegend(sb);

        // The event-wide chart covers the gaps between racing sessions as well, so it is not the sum
        // of the per-session charts and is worth showing on its own.
        var spent = AppendChart(sb, summary, sessionId: null, "Across the whole event", ChartRollup.MaxRows);

        AppendSessionTable(sb, summary);
        AppendSessionCharts(sb, summary, ChartRollup.MaxRowsPerReport - spent);

        AppendByClientType(sb, summary);
        AppendBusiestWindows(sb, summary);
        AppendFootnotes(sb, summary);

        sb.Append(EmailHtml.CloseTable);
        return sb.ToString();
    }

    private static void AppendHeadline(StringBuilder sb, EventViewershipSummary summary)
    {
        var offset = summary.TrackOffsetMinutes;
        var peak = summary.PeakUtc is { } at
            ? $"{EmailHtml.Number(summary.MaxConcurrent)} at {EmailHtml.Time(at, offset)}"
            : EmailHtml.Number(summary.MaxConcurrent);

        sb.Append("<tr><td>").Append(EmailHtml.OpenTable());
        sb.Append("<tr>");
        sb.Append(StatCell("Total viewing time", EmailHtml.Duration(summary.TotalViewerMinutes)));
        sb.Append(StatCell("Peak connections", peak));
        sb.Append(StatCell("Most used", EmailHtml.Escape(summary.TopClientType ?? "-")));
        sb.Append("</tr>");
        sb.Append(EmailHtml.CloseTable).Append("</td></tr>");
    }

    private static string StatCell(string label, string value)
    {
        return "<td width=\"33%\" align=\"center\" bgcolor=\"" + EmailPalette.CardLight + "\" class=\"rm-card\" "
            + "style=\"width:33%;padding:14px 8px;background-color:" + EmailPalette.CardLight + ";border:1px solid "
            + EmailPalette.RuleLight + ";\">"
            + "<div class=\"rm-muted\" style=\"font-family:" + EmailHtml.FontStack + ";font-size:11px;line-height:16px;"
            + "text-transform:uppercase;letter-spacing:0.4px;color:" + EmailPalette.MutedLight + ";\">"
            + EmailHtml.Escape(label) + "</div>"
            + "<div class=\"rm-text\" style=\"font-family:" + EmailHtml.FontStack + ";font-size:20px;line-height:28px;"
            + "font-weight:bold;color:" + EmailPalette.TextLight + ";\">" + value + "</div>"
            + "</td>";
    }

    private static void AppendLegend(StringBuilder sb)
    {
        sb.Append("<tr><td style=\"padding:16px 0 4px 0;\">").Append(EmailHtml.OpenTable());
        sb.Append("<tr>");
        foreach (var type in ViewerClientTypes.Reported)
        {
            sb.Append(EmailHtml.LegendSwatch(EmailPalette.ForClientType(type)));
            sb.Append(EmailHtml.Label(EmailHtml.Escape(type), muted: true));
        }
        sb.Append("<td style=\"width:100%;\">&nbsp;</td>");
        sb.Append("</tr>");
        sb.Append(EmailHtml.CloseTable).Append("</td></tr>");
    }

    /// <summary>
    /// Every racing session's numbers, whether or not it earned a chart.
    /// </summary>
    /// <remarks>
    /// The table is what makes the chart budget affordable: an endurance weekend's tenth session
    /// still gets its figures, it just does not get a picture of them.
    /// </remarks>
    private static void AppendSessionTable(StringBuilder sb, EventViewershipSummary summary)
    {
        if (summary.Sessions.Count == 0)
        {
            return;
        }

        sb.Append(EmailHtml.SectionHeading("By session"));
        sb.Append("<tr><td>").Append(EmailHtml.OpenTable());
        sb.Append("<tr>");
        sb.Append(HeaderCell("Session", "left"));
        sb.Append(HeaderCell("Started", "left"));
        sb.Append(HeaderCell("Viewing time", "right"));
        sb.Append(HeaderCell("Peak", "right"));
        sb.Append("</tr>");

        foreach (var session in summary.Sessions.OrderBy(s => s.StartUtc))
        {
            sb.Append("<tr>");
            sb.Append(BodyCell(EmailHtml.Escape(SessionLabel(session)), "left"));
            sb.Append(BodyCell(EmailHtml.DateAndTime(session.StartUtc, summary.TrackOffsetMinutes), "left"));
            sb.Append(BodyCell(EmailHtml.Duration(session.TotalViewerMinutes), "right"));
            sb.Append(BodyCell(EmailHtml.Number(session.MaxConcurrent), "right"));
            sb.Append("</tr>");
        }

        sb.Append(EmailHtml.CloseTable).Append("</td></tr>");
    }

    /// <summary>
    /// Charts for the sessions that drew the most watching, within what is left of the report's row
    /// budget.
    /// </summary>
    /// <remarks>
    /// Busiest first rather than chronological, because the budget can run out: if a session has to
    /// go without a picture it should be the one fewest people watched. The table above still has
    /// every session, in the order they ran.
    /// </remarks>
    private static void AppendSessionCharts(StringBuilder sb, EventViewershipSummary summary, int rowBudget)
    {
        var charted = new List<(EventViewershipSessionSummary Session, int MaxRows)>();

        foreach (var session in summary.Sessions
            .Where(s => s.TotalViewerMinutes > 0)
            .OrderByDescending(s => s.TotalViewerMinutes))
        {
            if (rowBudget < ChartRollup.MinUsefulRows)
            {
                break;
            }

            var allowance = Math.Min(ChartRollup.MaxRows, rowBudget);
            var chart = ChartRollup.Build(summary.Buckets, session.SessionId, allowance);
            if (chart.Rows.Count == 0)
            {
                continue;
            }

            rowBudget -= chart.Rows.Count;
            charted.Add((session, allowance));
        }

        foreach (var (session, allowance) in charted.OrderBy(c => c.Session.StartUtc))
        {
            AppendSessionBlock(sb, summary, session, allowance);
        }
    }

    private static string SessionLabel(EventViewershipSessionSummary session)
        => session.SessionName.Length == 0 ? $"Session {session.SessionId}" : session.SessionName;

    private static void AppendSessionBlock(StringBuilder sb, EventViewershipSummary summary,
        EventViewershipSessionSummary session, int maxRows)
    {
        var offset = summary.TrackOffsetMinutes;
        var peak = session.PeakUtc is { } at
            ? $"peak {EmailHtml.Number(session.MaxConcurrent)} at {EmailHtml.Time(at, offset)}"
            : $"peak {EmailHtml.Number(session.MaxConcurrent)}";

        var detail = $"{EmailHtml.DateAndTime(session.StartUtc, offset)} &middot; "
                     + $"{EmailHtml.Duration(session.TotalViewerMinutes)} of viewing &middot; {peak}";

        sb.Append(EmailHtml.SectionHeading(SessionLabel(session)));
        sb.Append(EmailHtml.Paragraph(detail, muted: true));
        AppendChartRows(sb, summary, session.SessionId, maxRows);
    }

    private static int AppendChart(StringBuilder sb, EventViewershipSummary summary, int? sessionId, string heading,
        int maxRows)
    {
        sb.Append(EmailHtml.SectionHeading(heading));
        return AppendChartRows(sb, summary, sessionId, maxRows);
    }

    private static int AppendChartRows(StringBuilder sb, EventViewershipSummary summary, int? sessionId, int maxRows)
    {
        var chart = ChartRollup.Build(summary.Buckets, sessionId, maxRows);
        if (chart.Rows.Count == 0)
        {
            sb.Append(EmailHtml.Paragraph("No connections were recorded.", muted: true));
            return 0;
        }

        if (chart.Interval != ViewershipAggregator.BucketLength)
        {
            sb.Append(EmailHtml.Paragraph(
                $"Shown in {EmailHtml.Escape(ChartRollup.Describe(chart.Interval))} so the chart fits in this email.",
                muted: true));
        }

        sb.Append("<tr><td>").Append(EmailHtml.OpenTable());

        foreach (var row in chart.Rows)
        {
            sb.Append("<tr>");
            sb.Append(EmailHtml.Label(EmailHtml.Time(row.StartUtc, summary.TrackOffsetMinutes),
                muted: true, align: "right", widthPx: TimeColumnWidth));
            sb.Append("<td style=\"padding:3px 0;\">");
            sb.Append(Bar(row, chart.Scale));
            sb.Append("</td>");
            sb.Append(EmailHtml.Label(
                $"{EmailHtml.Number(row.Max)}<span class=\"rm-muted\" style=\"color:{EmailPalette.MutedLight};\">&nbsp;peak</span>",
                align: "right", widthPx: PeakColumnWidth));
            sb.Append("</tr>");
        }

        sb.Append(EmailHtml.CloseTable).Append("</td></tr>");
        return chart.Rows.Count;
    }

    private static string Bar(ChartRow row, double scale)
    {
        var track = EmailHtml.ChartTrackWidth;
        var barWidth = scale <= 0 ? 0 : (int)Math.Round(row.Total / scale * track);
        if (barWidth > track)
        {
            barWidth = track;
        }

        var widths = EmailHtml.DistributePixels(row.AveragesByType, barWidth);

        var sb = new StringBuilder();
        sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"").Append(track)
          .Append("\" bgcolor=\"").Append(EmailPalette.TrackLight).Append("\" class=\"rm-track\" style=\"width:")
          .Append(track).Append("px;table-layout:fixed;border-collapse:collapse;background-color:")
          .Append(EmailPalette.TrackLight).Append(";\"><tr>");

        var used = 0;
        for (var i = 0; i < widths.Length; i++)
        {
            sb.Append(EmailHtml.BarSegment(widths[i], EmailPalette.ForClientType(ViewerClientTypes.Reported[i])));
            used += widths[i];
        }

        sb.Append(EmailHtml.BarSpacer(track - used));
        sb.Append("</tr></table>");
        return sb.ToString();
    }

    private static void AppendByClientType(StringBuilder sb, EventViewershipSummary summary)
    {
        var all = summary.Buckets.Where(b => b.SessionId == null).ToList();
        var totalSeconds = all.Where(b => b.ClientType == ViewerClientTypes.All).Sum(b => b.ViewerSeconds);
        if (totalSeconds <= 0)
        {
            return;
        }

        sb.Append(EmailHtml.SectionHeading("By platform"));
        sb.Append("<tr><td>").Append(EmailHtml.OpenTable());
        sb.Append("<tr>");
        sb.Append(HeaderCell("Platform", "left"));
        sb.Append(HeaderCell("Viewing time", "right"));
        sb.Append(HeaderCell("Share", "right"));
        sb.Append(HeaderCell("Peak", "right"));
        sb.Append("</tr>");

        foreach (var type in ViewerClientTypes.Reported)
        {
            var forType = all.Where(b => b.ClientType == type).ToList();
            var seconds = forType.Sum(b => b.ViewerSeconds);
            if (seconds <= 0)
            {
                continue;
            }

            sb.Append("<tr>");
            sb.Append(BodyCell(
                $"<span style=\"display:inline-block;width:10px;height:10px;background-color:{EmailPalette.ForClientType(type)};\">&nbsp;</span>&nbsp;"
                + EmailHtml.Escape(type), "left"));
            sb.Append(BodyCell(EmailHtml.Duration(seconds / 60d), "right"));
            sb.Append(BodyCell($"{EmailHtml.Number(seconds * 100d / totalSeconds, 1)}%", "right"));
            sb.Append(BodyCell(EmailHtml.Number(forType.Max(b => b.MaxConcurrent)), "right"));
            sb.Append("</tr>");
        }

        sb.Append(EmailHtml.CloseTable).Append("</td></tr>");
    }

    /// <summary>
    /// The busiest fifteen-minute windows, which is how the true peaks stay in the email even when
    /// the chart above has been widened to fit.
    /// </summary>
    private static void AppendBusiestWindows(StringBuilder sb, EventViewershipSummary summary)
    {
        var busiest = summary.Buckets
            .Where(b => b.SessionId == null && b.ClientType == ViewerClientTypes.All && b.MaxConcurrent > 0)
            .OrderByDescending(b => b.MaxConcurrent)
            .ThenBy(b => b.BucketStartUtc)
            .Take(5)
            .ToList();
        if (busiest.Count == 0)
        {
            return;
        }

        sb.Append(EmailHtml.SectionHeading("Busiest 15 minutes"));
        sb.Append("<tr><td>").Append(EmailHtml.OpenTable());
        sb.Append("<tr>");
        sb.Append(HeaderCell("When", "left"));
        sb.Append(HeaderCell("Peak", "right"));
        sb.Append(HeaderCell("Average", "right"));
        sb.Append("</tr>");

        foreach (var bucket in busiest)
        {
            sb.Append("<tr>");
            sb.Append(BodyCell(EmailHtml.DateAndTime(bucket.BucketStartUtc, summary.TrackOffsetMinutes), "left"));
            sb.Append(BodyCell(EmailHtml.Number(bucket.MaxConcurrent), "right"));
            sb.Append(BodyCell(EmailHtml.Number(bucket.AvgConcurrent, 1), "right"));
            sb.Append("</tr>");
        }

        sb.Append(EmailHtml.CloseTable).Append("</td></tr>");
    }

    /// <summary>
    /// The caveats. Stated in the email rather than left for us to remember, because both of them
    /// make a number mean something other than what it looks like it means.
    /// </summary>
    private static void AppendFootnotes(StringBuilder sb, EventViewershipSummary summary)
    {
        sb.Append(EmailHtml.Paragraph(
            "These are <strong>connections</strong>, not people. A phone that changes network or is put away "
            + "reconnects, so one person can account for several connections over an event. Peak and average "
            + "connections are the figures to compare between events.", muted: true));

        if (summary.OpenSessions > 0)
        {
            sb.Append(EmailHtml.Paragraph(
                $"{EmailHtml.Number(summary.OpenSessions)} connection(s) were still recorded as open when the event "
                + "finished and were counted up to the end, so the viewing time above may be slightly high.",
                muted: true));
        }
    }

    private static string HeaderCell(string text, string align) =>
        $"<td align=\"{align}\" class=\"rm-muted\" style=\"padding:6px 8px;border-bottom:1px solid {EmailPalette.RuleLight};"
        + $"font-family:{EmailHtml.FontStack};font-size:11px;line-height:16px;text-transform:uppercase;"
        + $"letter-spacing:0.4px;color:{EmailPalette.MutedLight};\">{EmailHtml.Escape(text)}</td>";

    private static string BodyCell(string html, string align) =>
        $"<td align=\"{align}\" class=\"rm-text\" style=\"padding:6px 8px;border-bottom:1px solid {EmailPalette.RuleLight};"
        + $"font-family:{EmailHtml.FontStack};font-size:13px;line-height:19px;color:{EmailPalette.TextLight};\">{html}</td>";
}
