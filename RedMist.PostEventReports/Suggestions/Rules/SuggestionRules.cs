using Microsoft.EntityFrameworkCore;
using RedMist.Database.Models;
using RedMist.PostEventReports.Email;
using RedMist.PostEventReports.Sections;

namespace RedMist.PostEventReports.Suggestions.Rules;

/// <summary>
/// Suggests turning on control logs, when the organization has none configured.
/// </summary>
/// <remarks>
/// Checked against the organization rather than the event because control log configuration is
/// organization-wide. An organization that has never set one up is the case worth prompting.
/// </remarks>
public class NoControlLogSuggestion : IReportSuggestion
{
    public string Code => "no-control-log";
    public int Priority => 10;

    public Task<ReportSuggestion?> EvaluateAsync(ReportContext context, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(context.Organization.ControlLogType))
        {
            return Task.FromResult<ReportSuggestion?>(null);
        }

        return Task.FromResult<ReportSuggestion?>(new ReportSuggestion(Code,
            "Show your control log",
            "Events that publish their control log give competitors a reason to keep the timing page open between "
            + "sessions - penalties, black flags and stewards' decisions all land there. Control logs are set up "
            + "once for your organization and then appear automatically on every event."));
    }
}

/// <summary>
/// Suggests adding an event URL, when the event has none.
/// </summary>
public class NoEventUrlSuggestion : IReportSuggestion
{
    public string Code => "no-event-url";
    public int Priority => 20;

    public Task<ReportSuggestion?> EvaluateAsync(ReportContext context, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(context.Event.EventUrl))
        {
            return Task.FromResult<ReportSuggestion?>(null);
        }

        return Task.FromResult<ReportSuggestion?>(new ReportSuggestion(Code,
            "Link your event page",
            "This event had no event URL set, so people who found the timing page had nowhere to go for the "
            + "schedule, entry list or tickets. Adding one turns a viewer into someone who comes back."));
    }
}

/// <summary>
/// Suggests announcing the event on social media, when nothing was ever published for it.
/// </summary>
/// <remarks>
/// Looks for a published post rather than any post, because a draft that was never approved did not
/// reach anybody.
/// </remarks>
public class ShareOnSocialSuggestion : IReportSuggestion
{
    public string Code => "share-on-social";
    public int Priority => 30;

    public async Task<ReportSuggestion?> EvaluateAsync(ReportContext context, CancellationToken cancellationToken)
    {
        var published = await context.Db.SocialPosts
            .AsNoTracking()
            .AnyAsync(p => p.EventId == context.Event.Id && p.PublishedUtc != null, cancellationToken);
        if (published)
        {
            return null;
        }

        return new ReportSuggestion(Code,
            "Tell people it is on",
            "Nothing was posted about this event on social media. A single post on the morning of the event, with "
            + "the link to live timing, is the cheapest way to raise the numbers above - most people who would "
            + "watch simply do not know it is running.");
    }
}

/// <summary>
/// Suggests embedding live timing in the organization's own site, when there is a site to embed in.
/// </summary>
public class EmbedTimingSuggestion : IReportSuggestion
{
    public string Code => "embed-timing";
    public int Priority => 40;

    public Task<ReportSuggestion?> EvaluateAsync(ReportContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Organization.Website))
        {
            return Task.FromResult<ReportSuggestion?>(null);
        }

        return Task.FromResult<ReportSuggestion?>(new ReportSuggestion(Code,
            "Put live timing on your own site",
            $"Visitors to {EmailHtml.Escape(context.Organization.Website)} have to find their way here on their own. "
            + "Embedding the live timing page keeps them on your site and puts your event in front of people who "
            + "came looking for something else."));
    }
}

/// <summary>
/// Suggests Flagtronics, when the organization is not using it.
/// </summary>
public class NoFlagtronicsSuggestion : IReportSuggestion
{
    public string Code => "no-flagtronics";
    public int Priority => 50;

    public Task<ReportSuggestion?> EvaluateAsync(ReportContext context, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(context.Organization.FlagtronicsUrl))
        {
            return Task.FromResult<ReportSuggestion?>(null);
        }

        return Task.FromResult<ReportSuggestion?>(new ReportSuggestion(Code,
            "Add flag status to your timing",
            "Flagtronics feeds corner flag status straight into live timing, so viewers see a full-course yellow as "
            + "it happens rather than inferring it from the lap times. It is one of the things that keeps people "
            + "watching through a caution instead of closing the page."));
    }
}
