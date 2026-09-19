using System.Globalization;
using System.Net;
using System.Text;

namespace RedMist.PostEventReports.Email;

/// <summary>
/// The markup primitives every section builds with, and the rules they all have to obey.
/// </summary>
/// <remarks>
/// <para>
/// Email clients are not browsers. Outlook on Windows renders with Word, which ignores most of CSS;
/// Gmail strips the head in some contexts and clips a message over about 102 KB. So:
/// </para>
/// <list type="bullet">
/// <item>Layout is presentation tables only. No flexbox, grid, float, absolute positioning, SVG or
/// background images.</item>
/// <item>A colored cell carries both a <c>bgcolor</c> attribute, which Word honours, and a
/// <c>background-color</c> style, which everything else does.</item>
/// <item>Text carries an explicit color and its container an explicit background. Partial dark-mode
/// inversion produces black on black exactly where one of the two is missing.</item>
/// <item>Widths are pixels against a fixed track. Word re-derives percentage widths unpredictably.</item>
/// </list>
/// <para>
/// Every caller-supplied string goes through <see cref="Escape"/>. Event, organization and session
/// names are free text entered by people, and an apostrophe or an ampersand in one of them is far
/// more likely than not.
/// </para>
/// </remarks>
public static class EmailHtml
{
    public const string FontStack = "Arial,sans-serif";

    /// <summary>The width in pixels of the track a chart bar is drawn against.</summary>
    public const int ChartTrackWidth = 380;

    public static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>A cell carrying both the attribute and the style form of a background color.</summary>
    public static string BarSegment(int widthPx, string color)
    {
        // A zero-width cell renders as a one-pixel color artifact in Outlook, so it is never emitted.
        if (widthPx <= 0)
        {
            return string.Empty;
        }

        // The width attribute plus table-layout:fixed carries the width; repeating it in the style
        // costs a dozen bytes on every segment of every row, which is what decides whether a long
        // event's report is clipped.
        return $"<td width=\"{widthPx}\" height=\"14\" bgcolor=\"{color}\" " +
               $"style=\"height:14px;line-height:14px;font-size:0;background-color:{color};\">&nbsp;</td>";
    }

    /// <summary>The unfilled remainder of a chart track.</summary>
    public static string BarSpacer(int widthPx) =>
        widthPx <= 0
            ? string.Empty
            : $"<td width=\"{widthPx}\" height=\"14\" style=\"width:{widthPx}px;height:14px;line-height:14px;font-size:0;\">&nbsp;</td>";

    public static string Label(string text, bool muted = false, string align = "left", int? widthPx = null)
    {
        var color = muted ? EmailPalette.MutedLight : EmailPalette.TextLight;
        var cls = muted ? "rm-muted" : "rm-text";
        var width = widthPx is { } w ? $" width=\"{w}\"" : string.Empty;
        var widthStyle = widthPx is { } w2 ? $"width:{w2}px;" : string.Empty;
        return $"<td align=\"{align}\"{width} class=\"{cls}\" " +
               $"style=\"{widthStyle}padding:0 8px;font-family:{FontStack};font-size:12px;line-height:18px;color:{color};white-space:nowrap;\">"
               + text + "</td>";
    }

    /// <summary>A colored square for a chart legend. Never a glyph, which dark mode recolors.</summary>
    public static string LegendSwatch(string color) =>
        $"<td width=\"12\" height=\"12\" bgcolor=\"{color}\" " +
        $"style=\"width:12px;height:12px;line-height:12px;font-size:0;background-color:{color};\">&nbsp;</td>";

    public static string SectionHeading(string text) =>
        $"<tr><td class=\"rm-text\" style=\"padding:24px 0 8px 0;font-family:{FontStack};font-size:16px;font-weight:bold;"
        + $"line-height:22px;color:{EmailPalette.TextLight};\">{Escape(text)}</td></tr>";

    public static string Paragraph(string html, bool muted = false)
    {
        var color = muted ? EmailPalette.MutedLight : EmailPalette.TextLight;
        var cls = muted ? "rm-muted" : "rm-text";
        return $"<tr><td class=\"{cls}\" style=\"padding:0 0 12px 0;font-family:{FontStack};font-size:13px;"
               + $"line-height:19px;color:{color};\">{html}</td></tr>";
    }

    /// <summary>Opens a presentation table with collapsed borders.</summary>
    public static string OpenTable(string? extraStyle = null, int? widthPx = null)
    {
        var width = widthPx is { } w ? $" width=\"{w}\"" : " width=\"100%\"";
        var widthStyle = widthPx is { } w2 ? $"width:{w2}px;" : "width:100%;";
        return $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\"{width} "
               + $"style=\"{widthStyle}border-collapse:collapse;{extraStyle}\">";
    }

    public const string CloseTable = "</table>";

    public static string Number(double value, int decimals = 0) =>
        value.ToString(decimals == 0 ? "N0" : $"N{decimals}", CultureInfo.InvariantCulture);

    /// <summary>
    /// A duration in minutes as something a person reads, e.g. "3h 12m".
    /// </summary>
    public static string Duration(double minutes)
    {
        if (minutes < 1)
        {
            return "under a minute";
        }

        var total = (int)Math.Round(minutes);
        var hours = total / 60;
        var rest = total % 60;
        return hours == 0 ? $"{rest}m" : rest == 0 ? $"{hours}h" : $"{hours}h {rest}m";
    }

    /// <summary>
    /// A UTC instant as track-local time, or as UTC when the offset is unknown.
    /// </summary>
    /// <remarks>
    /// The "UTC" suffix is not decoration. Presenting UTC as though it were local is worse than
    /// admitting the offset is unknown: an organizer reading a peak at 09:00 for a race that started
    /// at 14:00 local concludes the feature is broken.
    /// </remarks>
    public static string Time(DateTime utc, int? offsetMinutes)
    {
        if (offsetMinutes is not { } minutes)
        {
            return utc.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC";
        }

        return utc.AddMinutes(minutes).ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    public static string DateAndTime(DateTime utc, int? offsetMinutes)
    {
        if (offsetMinutes is not { } minutes)
        {
            return utc.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture) + " UTC";
        }

        return utc.AddMinutes(minutes).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Splits a total into pixel widths summing to <paramref name="totalPixels"/>.
    /// </summary>
    /// <remarks>
    /// Largest-remainder, so the segments add up to the bar width rather than drifting a pixel per
    /// row. A share that is real but rounds to nothing is given one pixel, because a single API
    /// client disappearing entirely from the chart reads as a bug rather than as a small number.
    /// </remarks>
    public static int[] DistributePixels(IReadOnlyList<double> values, int totalPixels)
    {
        var widths = new int[values.Count];
        var sum = values.Sum();
        if (sum <= 0 || totalPixels <= 0)
        {
            return widths;
        }

        var remainders = new List<(int Index, double Remainder)>();
        var assigned = 0;

        for (var i = 0; i < values.Count; i++)
        {
            var exact = values[i] / sum * totalPixels;
            var floor = (int)Math.Floor(exact);
            if (floor == 0 && values[i] > 0)
            {
                floor = 1;
            }
            widths[i] = floor;
            assigned += floor;
            remainders.Add((i, exact - Math.Floor(exact)));
        }

        // Hand out what rounding left over, largest remainder first.
        foreach (var (index, _) in remainders.OrderByDescending(r => r.Remainder))
        {
            if (assigned >= totalPixels)
            {
                break;
            }
            widths[index]++;
            assigned++;
        }

        // The one-pixel floor above can overshoot when several tiny shares share a narrow bar. Take
        // it back from the widest first, and once everything is down to a single pixel drop segments
        // rather than let the row run wider than the value it represents.
        while (assigned > totalPixels)
        {
            var widest = Array.IndexOf(widths, widths.Max());
            if (widths[widest] <= 0)
            {
                break;
            }
            widths[widest]--;
            assigned--;
        }

        return widths;
    }

    public static StringBuilder AppendRow(this StringBuilder sb, string cells) => sb.Append("<tr>").Append(cells).Append("</tr>");
}
