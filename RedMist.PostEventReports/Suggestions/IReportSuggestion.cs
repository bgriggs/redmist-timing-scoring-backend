using RedMist.PostEventReports.Sections;

namespace RedMist.PostEventReports.Suggestions;

/// <summary>
/// A thing the organizer could do to grow their audience next time.
/// </summary>
/// <remarks>
/// A separate extension point from <see cref="IReportSection"/> rather than a section of its own,
/// because suggestions are cross-cutting: a future lap statistics section should be able to
/// contribute one into the same block at the end of the email without owning that block.
/// </remarks>
public interface IReportSuggestion
{
    /// <summary>Stable identifier, recorded with the report.</summary>
    string Code { get; }

    /// <summary>Lower sorts earlier. Only the strongest few are shown.</summary>
    int Priority { get; }

    /// <summary>The suggestion, or null when it does not apply to this event.</summary>
    Task<ReportSuggestion?> EvaluateAsync(ReportContext context, CancellationToken cancellationToken);
}

/// <summary>A suggestion as it will appear, and as it is recorded.</summary>
/// <param name="Code">The rule that produced it.</param>
/// <param name="Title">One line, shown in bold.</param>
/// <param name="BodyHtml">The explanation. Already escaped and email-safe.</param>
public sealed record ReportSuggestion(string Code, string Title, string BodyHtml);
