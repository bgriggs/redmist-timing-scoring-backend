using RedMist.PostEventReports.Sections;
using RedMist.PostEventReports.Suggestions;
using RedMist.TimingCommon.Models;
using System.Globalization;
using System.Text;
using Event = RedMist.TimingCommon.Models.Configuration.Event;

namespace RedMist.PostEventReports.Email;

/// <summary>
/// The shell the report is assembled into: the wrapper, the header, each section's block in order,
/// the suggestions, and the footer.
/// </summary>
/// <remarks>
/// Knows nothing about what any section contains. It owns the page chrome and the dark-mode rules so
/// the sections stay consistent with each other and the email-client constraints are enforced in one
/// place rather than in every section that gets added later.
/// </remarks>
public static class PostEventReportRenderer
{
    private const int ContentWidth = 600;

    public static string Render(Event evt, Organization organization,
        IReadOnlyList<ReportSectionResult> sections, IReadOnlyList<ReportSuggestion> suggestions)
    {
        var sb = new StringBuilder();

        sb.Append("<!DOCTYPE html><html lang=\"en\"><head>");
        sb.Append("<meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<meta name=\"color-scheme\" content=\"light dark\">");
        sb.Append("<meta name=\"supported-color-schemes\" content=\"light dark\">");
        sb.Append("<title>").Append(EmailHtml.Escape(evt.Name)).Append("</title>");
        AppendStyle(sb);
        sb.Append("</head>");

        sb.Append("<body class=\"rm-page\" style=\"margin:0;padding:0;background-color:")
          .Append(EmailPalette.PageLight).Append(";\">");

        AppendPreheader(sb, sections);

        sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\" ")
          .Append("bgcolor=\"").Append(EmailPalette.PageLight).Append("\" class=\"rm-page\" ")
          .Append("style=\"width:100%;border-collapse:collapse;background-color:").Append(EmailPalette.PageLight)
          .Append(";\"><tr><td align=\"center\" style=\"padding:24px 16px;\">");

        sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"")
          .Append(ContentWidth).Append("\" style=\"width:").Append(ContentWidth)
          .Append("px;max-width:100%;border-collapse:collapse;\">");

        AppendHeader(sb, evt, organization);

        foreach (var section in sections)
        {
            sb.Append("<tr><td style=\"padding:8px 0;\">").Append(section.BodyHtml).Append("</td></tr>");
        }

        AppendSuggestions(sb, suggestions);
        AppendFooter(sb);

        sb.Append("</table></td></tr></table></body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// The grey line the inbox shows next to the subject. Without one, Gmail picks the first text it
    /// finds, which is whatever the header happens to start with.
    /// </summary>
    private static void AppendPreheader(StringBuilder sb, IReadOnlyList<ReportSectionResult> sections)
    {
        var preheader = sections.Select(s => s.Preheader).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        if (string.IsNullOrWhiteSpace(preheader))
        {
            return;
        }

        sb.Append("<div style=\"display:none;max-height:0;overflow:hidden;mso-hide:all;\">")
          .Append(EmailHtml.Escape(preheader)).Append("</div>");
    }

    private static void AppendHeader(StringBuilder sb, Event evt, Organization organization)
    {
        var dates = evt.StartDate.Date == evt.EndDate.Date
            ? evt.StartDate.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)
            : $"{evt.StartDate.ToString("d MMM", CultureInfo.InvariantCulture)} - {evt.EndDate.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";

        var subtitle = string.IsNullOrWhiteSpace(evt.TrackName)
            ? dates
            : $"{EmailHtml.Escape(evt.TrackName)} &middot; {EmailHtml.Escape(dates)}";

        sb.Append("<tr><td bgcolor=\"").Append(EmailPalette.CardLight).Append("\" class=\"rm-card\" ")
          .Append("style=\"padding:20px;background-color:").Append(EmailPalette.CardLight)
          .Append(";border-top:4px solid ").Append(EmailPalette.Accent).Append(";\">");

        sb.Append("<div class=\"rm-muted\" style=\"font-family:").Append(EmailHtml.FontStack)
          .Append(";font-size:11px;line-height:16px;text-transform:uppercase;letter-spacing:0.6px;color:")
          .Append(EmailPalette.MutedLight).Append(";\">Post-event report &middot; ")
          .Append(EmailHtml.Escape(organization.Name)).Append("</div>");

        sb.Append("<div class=\"rm-text\" style=\"font-family:").Append(EmailHtml.FontStack)
          .Append(";font-size:22px;line-height:30px;font-weight:bold;color:").Append(EmailPalette.TextLight)
          .Append(";\">").Append(EmailHtml.Escape(evt.Name)).Append("</div>");

        sb.Append("<div class=\"rm-muted\" style=\"font-family:").Append(EmailHtml.FontStack)
          .Append(";font-size:13px;line-height:19px;color:").Append(EmailPalette.MutedLight).Append(";\">")
          .Append(subtitle).Append("</div>");

        sb.Append("</td></tr>");
    }

    private static void AppendSuggestions(StringBuilder sb, IReadOnlyList<ReportSuggestion> suggestions)
    {
        if (suggestions.Count == 0)
        {
            return;
        }

        sb.Append("<tr><td style=\"padding:8px 0;\">").Append(EmailHtml.OpenTable());
        sb.Append(EmailHtml.SectionHeading("Ideas for next time"));

        foreach (var suggestion in suggestions)
        {
            sb.Append("<tr><td bgcolor=\"").Append(EmailPalette.CardLight).Append("\" class=\"rm-card\" ")
              .Append("style=\"padding:12px 14px;background-color:").Append(EmailPalette.CardLight)
              .Append(";border:1px solid ").Append(EmailPalette.RuleLight).Append(";\">");
            sb.Append("<div class=\"rm-text\" style=\"font-family:").Append(EmailHtml.FontStack)
              .Append(";font-size:13px;line-height:19px;font-weight:bold;color:").Append(EmailPalette.TextLight)
              .Append(";\">").Append(EmailHtml.Escape(suggestion.Title)).Append("</div>");
            sb.Append("<div class=\"rm-muted\" style=\"font-family:").Append(EmailHtml.FontStack)
              .Append(";font-size:13px;line-height:19px;color:").Append(EmailPalette.MutedLight).Append(";\">")
              .Append(suggestion.BodyHtml).Append("</div>");
            sb.Append("</td></tr>");
            sb.Append("<tr><td style=\"height:8px;line-height:8px;font-size:0;\">&nbsp;</td></tr>");
        }

        sb.Append(EmailHtml.CloseTable).Append("</td></tr>");
    }

    private static void AppendFooter(StringBuilder sb)
    {
        sb.Append("<tr><td class=\"rm-muted\" style=\"padding:20px 4px;font-family:").Append(EmailHtml.FontStack)
          .Append(";font-size:11px;line-height:17px;color:").Append(EmailPalette.MutedLight).Append(";\">")
          .Append("Sent by Red Mist Timing because you are listed as an administrator of this organization. ")
          .Append("Reply to this email if you would rather not receive post-event reports.")
          .Append("</td></tr>");
    }

    /// <summary>
    /// The stylesheet.
    /// </summary>
    /// <remarks>
    /// Outlook on Windows ignores this entirely, which is why everything structural is also inlined.
    /// What it buys is dark mode for the clients that do honour it, and Outlook.com needs the
    /// <c>data-ogsc</c> and <c>data-ogsb</c> selectors because it rewrites classes into those
    /// attributes rather than applying the media query.
    /// </remarks>
    private static void AppendStyle(StringBuilder sb)
    {
        sb.Append("<style>");
        sb.Append(":root{color-scheme:light dark;supported-color-schemes:light dark;}");
        sb.Append("@media (prefers-color-scheme:dark){");
        sb.Append(".rm-page{background-color:").Append(EmailPalette.PageDark).Append(" !important;}");
        sb.Append(".rm-card{background-color:").Append(EmailPalette.CardDark).Append(" !important;}");
        sb.Append(".rm-text{color:").Append(EmailPalette.TextDark).Append(" !important;}");
        sb.Append(".rm-muted{color:").Append(EmailPalette.MutedDark).Append(" !important;}");
        sb.Append(".rm-track{background-color:").Append(EmailPalette.TrackDark).Append(" !important;}");
        sb.Append("}");
        sb.Append("[data-ogsc] .rm-text{color:").Append(EmailPalette.TextDark).Append(" !important;}");
        sb.Append("[data-ogsc] .rm-muted{color:").Append(EmailPalette.MutedDark).Append(" !important;}");
        sb.Append("[data-ogsb] .rm-page{background-color:").Append(EmailPalette.PageDark).Append(" !important;}");
        sb.Append("[data-ogsb] .rm-card{background-color:").Append(EmailPalette.CardDark).Append(" !important;}");
        sb.Append("[data-ogsb] .rm-track{background-color:").Append(EmailPalette.TrackDark).Append(" !important;}");
        sb.Append("</style>");
    }
}
