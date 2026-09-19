using Microsoft.Extensions.Configuration;

namespace RedMist.PostEventReports;

/// <summary>
/// What the job reads from configuration.
/// </summary>
/// <remarks>
/// Section-specific knobs nest under the section's own key, so adding a section brings its settings
/// with it rather than growing this type.
/// </remarks>
public class PostEventReportSettings
{
    /// <summary>
    /// How long after an event's end date before it is reported on.
    /// </summary>
    /// <remarks>
    /// An event's end date lands at midnight, and a weekend that runs late or gets picked up again
    /// the next morning should be one report rather than two. A day also puts the report in the
    /// organizer's inbox on the Monday, when they are looking at how the weekend went.
    /// </remarks>
    public TimeSpan SettlePeriod { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How far back to look for events that were never reported on.
    /// </summary>
    /// <remarks>
    /// Bounds the damage of a first run, or of the job having been down for a while: it catches up
    /// over a fortnight rather than emailing every organizer about every event in the history of the
    /// system.
    /// </remarks>
    public TimeSpan LookbackWindow { get; init; } = TimeSpan.FromDays(14);

    /// <summary>The most events one run will report on.</summary>
    public int MaxEventsPerRun { get; init; } = 25;

    /// <summary>
    /// The most recipients one event's report will be sent to.
    /// </summary>
    /// <remarks>
    /// An organization with an unexpectedly long administrator list should produce a log line, not a
    /// mail blast.
    /// </remarks>
    public int MaxRecipientsPerEvent { get; init; } = 50;

    /// <summary>
    /// When set, every organizer email is redirected here with the intended recipient in the subject.
    /// </summary>
    /// <remarks>
    /// How the first production run should go out: real events, real numbers, real rendering, and
    /// nothing reaching an organizer until the output has been looked at in a mail client.
    /// </remarks>
    public string? DryRunRecipient { get; init; }

    /// <summary>The longest window the viewership section will report on.</summary>
    public TimeSpan MaxWindow { get; init; } = TimeSpan.FromHours(96);

    /// <summary>Below this much total viewing, the viewership section says nothing.</summary>
    public double MinViewerMinutes { get; init; } = 1;

    public static PostEventReportSettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("PostEventReport");
        var viewership = section.GetSection("Viewership");

        return new PostEventReportSettings
        {
            SettlePeriod = TimeSpan.FromHours(section.GetValue("SettlePeriodHours", 24d)),
            LookbackWindow = TimeSpan.FromDays(section.GetValue("LookbackDays", 14d)),
            MaxEventsPerRun = section.GetValue("MaxEventsPerRun", 25),
            MaxRecipientsPerEvent = section.GetValue("MaxRecipientsPerEvent", 50),
            DryRunRecipient = section.GetValue<string?>("DryRunRecipient"),
            MaxWindow = TimeSpan.FromHours(viewership.GetValue("MaxWindowHours", 96d)),
            MinViewerMinutes = viewership.GetValue("MinViewerMinutes", 1d),
        };
    }
}
