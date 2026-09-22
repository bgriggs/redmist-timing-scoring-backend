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
    /// How long after the end of an event's last day before it is reported on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured from the end of the last day, not from the end date, which is stored as midnight at
    /// that day's start (see <c>EventDates</c>). A weekend that runs late or gets picked up again the
    /// next morning should be one report rather than two, and by the time it runs the viewership's
    /// upper bound - twelve hours after the last day - has passed. Rows still open then are counted
    /// to that bound, 12:00 UTC the next day, and no further.
    /// </para>
    /// <para>
    /// With the daily 06:30 UTC run, a day puts a Sunday event's report out on the Tuesday run. The
    /// cost of settling for less is mostly what has not finished yet rather than the bound: the
    /// logger's reconciler closes a row orphaned by a killed pod only when its six-hour cap comes
    /// round, and the orchestrator closes the rest at teardown, so an earlier report counts more rows
    /// as watching to the end on assumption - and an event that runs late or is resumed the next
    /// morning has not settled at all. If Monday delivery matters more, twelve hours here with the
    /// CronJob moved to <c>30 12 * * *</c> runs at 08:30 EDT / 05:30 PDT on the Monday, once the whole
    /// upper bound has passed.
    /// </para>
    /// </remarks>
    public TimeSpan SettlePeriod { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How far back, from the end of an event's last day, to look for events that were never
    /// reported on.
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
    /// <remarks>
    /// <para>
    /// Never applied inside the event's plausible span - twelve hours before its first day to twelve
    /// hours after its last - because that span already bounds the window: no interval reaches outside
    /// it. Truncating inside it cut the final afternoon off any event longer than this allowed, as soon
    /// as one connection the night before pinned the window start, and left the report disagreeing
    /// with the live view.
    /// </para>
    /// <para>
    /// Since the window can never be wider than that span, this no longer truncates anything; the
    /// span is what bounds the report. At the default it matches a Friday-to-Sunday event's span
    /// exactly. It is kept so the setting and its configuration key remain valid.
    /// </para>
    /// </remarks>
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
