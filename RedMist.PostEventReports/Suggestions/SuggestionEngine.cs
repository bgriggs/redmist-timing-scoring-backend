using Microsoft.Extensions.Logging;
using RedMist.PostEventReports.Sections;

namespace RedMist.PostEventReports.Suggestions;

/// <summary>
/// Runs the registered suggestion rules and picks the few worth showing.
/// </summary>
public class SuggestionEngine(ILoggerFactory loggerFactory, IEnumerable<IReportSuggestion> rules)
{
    /// <summary>
    /// The most suggestions shown on one report. A list of ten reads as a scolding rather than as
    /// help, and the ones past the first few are by definition the weaker ones.
    /// </summary>
    public const int MaxSuggestions = 3;

    private readonly ILogger logger = loggerFactory.CreateLogger<SuggestionEngine>();
    private readonly List<IReportSuggestion> rules = [.. rules];

    public async Task<List<ReportSuggestion>> EvaluateAsync(ReportContext context, CancellationToken cancellationToken)
    {
        var suggestions = new List<ReportSuggestion>();

        foreach (var rule in this.rules.OrderBy(r => r.Priority))
        {
            try
            {
                if (await rule.EvaluateAsync(context, cancellationToken) is { } suggestion)
                {
                    suggestions.Add(suggestion);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Contained on purpose: a broken rule must cost its own suggestion and nothing else.
                // The report is the valuable part and it goes out without this line.
                logger.LogError(ex, "Suggestion rule {code} failed for event {eventId}", rule.Code, context.Event.Id);
            }

            if (suggestions.Count >= MaxSuggestions)
            {
                break;
            }
        }

        return suggestions;
    }
}
