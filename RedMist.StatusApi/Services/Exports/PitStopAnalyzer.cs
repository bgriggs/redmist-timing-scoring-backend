using RedMist.TimingCommon.Models;

namespace RedMist.StatusApi.Services.Exports;

/// <summary>
/// Derives one car's pit stops, and the driver change that went with each, from that car's lap
/// rows in order.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in the persisted record says "this was a pit stop". A lap row is a snapshot of the car
/// taken when it crossed start/finish, and the pit fields on it are sticky: <c>PitEntryTime</c> and
/// <c>PitDurationMs</c> describe the most recent stop and keep describing it until the next one. So
/// a stop is detected as a <em>change</em>, not as a flag:
/// </para>
/// <list type="number">
/// <item>A new stop begins on the first lap row whose <c>PitEntryTime</c> differs from the one
/// carried by the previous rows. That is the lap the car completed after pitting.</item>
/// <item>When a session never reports entry times at all - a non-Flagtronics feed, where only the
/// timing system's own pit detection is present - the fallback is the rising edge of
/// <c>LapIncludedPit</c>. It gives a stop with no times attached, which is still worth listing.</item>
/// <item><c>DriverBefore</c> is the last non-empty driver seen before the pit lap;
/// <c>DriverAfter</c> is the driver on the pit lap itself.</item>
/// <item>The driver enricher can publish a change a lap late, because the puck has to be seen
/// before the car crosses start/finish. So if the pit lap still shows the old driver and the very
/// next lap shows a different one, that driver is taken as <c>DriverAfter</c> instead.</item>
/// </list>
/// <para>
/// Known ways this is wrong, all of them inherent to inferring from lap snapshots:
/// </para>
/// <list type="bullet">
/// <item><b>Missed exit.</b> If the feed never finalizes a duration, the stop is still reported but
/// <c>DurationMs</c> and <c>ExitTimeUtc</c> stay null. A stop with no length is reported rather than
/// dropped, because the driver change around it is usually the point of the report.</item>
/// <item><b>Driver that never changes.</b> A same-driver stop and a stop where the driver feed was
/// simply stale are indistinguishable here; both come out as <c>DriverChanged</c> false.</item>
/// <item><b>Stop at session end.</b> A car that pits and never crosses start/finish again produces
/// no lap row for that stop, so the stop is invisible. A stop detected on the car's last lap has no
/// following lap to correct a late driver update, so its <c>DriverAfter</c> may be the old driver.</item>
/// <item><b>Two stops on one lap.</b> A car that pits, rejoins and pits again without crossing
/// start/finish collapses into one record: only the later entry time survives into the lap row.</item>
/// <item><b>Repeated entry time.</b> If the feed re-sends an identical <c>PitEntryTime</c> for a
/// genuinely new stop, that stop is missed, because the detection is keyed on the value changing.</item>
/// <item><b>Partial lap history.</b> If a car's earlier laps are missing - purged, or the export
/// covers a session whose start was archived away - the first surviving row carries a sticky entry
/// time from a stop that happened before it, and is reported as a stop on that lap.</item>
/// <item><b>Lap renumbering.</b> Laps are processed in lap-number order, so a timing system that
/// resets or renumbers laps inside one session scrambles the sequence and the stops with it.</item>
/// <item><b>A lap row that could not be read.</b> A gap in the sequence does not leave a gap in the
/// output, it changes the answer: losing the lap that carried the entry-time change loses the stop
/// entirely, and losing the lap after a pit lap loses the late-driver correction, leaving
/// <c>DriverAfter</c> as the pre-stop driver and the stop reported as no change. The export counts
/// these rows and says so on the file rather than trying to compensate.</item>
/// <item><b>Back-to-back stops.</b> A stop detected on the lap immediately after another one takes
/// over as the open stop, so the earlier stop keeps whatever driver the pit lap itself showed and
/// never gets its one-lap-late correction. A car that pits on two consecutive laps is rare enough,
/// and the alternative - holding two open stops - would let a change be attributed to both.</item>
/// <item><b>An incomplete scan.</b> When the export stops at its lap-scan limit the car being
/// processed is closed out mid-stint, so its last stop is reported as if the session ended there.
/// The file is marked truncated in that case.</item>
/// </list>
/// </remarks>
public sealed class PitStopAnalyzer
{
    private readonly string carNumber;
    private readonly List<PitStopRecord> stops = [];

    /// <summary>Most recent non-empty driver name seen, which is the "before" for the next stop.</summary>
    private string currentDriver = string.Empty;
    private DateTime? lastEntryTime;
    private bool previousLapIncludedPit;
    private bool sawAnyEntryTime;

    /// <summary>The stop still open for a late driver or duration update, or null.</summary>
    private PitStopRecord? pending;
    private int lapsSincePending;

    /// <summary>
    /// Initializes a new instance of the <see cref="PitStopAnalyzer"/> class for one car.
    /// </summary>
    /// <param name="carNumber">The car number, copied onto every record this produces.</param>
    public PitStopAnalyzer(string carNumber) => this.carNumber = carNumber;

    /// <summary>Stops found so far. Only complete once <see cref="Complete"/> has been called.</summary>
    public int StopCount => stops.Count;

    /// <summary>
    /// Feeds the analyzer the car's next lap, taking the lap number from the payload.
    /// </summary>
    /// <param name="lap">The lap snapshot, deserialized from the stored lap row.</param>
    public void AddLap(CarPosition lap) => AddLap(lap, lap.LastLapCompleted);

    /// <summary>
    /// Feeds the analyzer the car's next lap. Laps must arrive in ascending lap-number order.
    /// </summary>
    /// <param name="lap">The lap snapshot, deserialized from the stored lap row.</param>
    /// <param name="lapNumber">
    /// The lap number from the row's own column. It is passed separately because that column is what
    /// the query filtered and ordered on, so it is the number a reader of the report can match back
    /// to the database - and it is what the lap exports print - whatever the payload says.
    /// </param>
    public void AddLap(CarPosition lap, int lapNumber)
    {
        var driver = (lap.DriverName ?? string.Empty).Trim();

        var newStop = false;
        if (lap.PitEntryTime.HasValue)
        {
            sawAnyEntryTime = true;
            if (lap.PitEntryTime != lastEntryTime)
                newStop = true;
            lastEntryTime = lap.PitEntryTime;
        }
        else if (!sawAnyEntryTime && lap.LapIncludedPit && !previousLapIncludedPit)
        {
            // Fallback path only. Once a session has shown it reports entry times, a lap flagged as
            // including a pit without one is a lap the entry time simply has not reached yet, not a
            // second stop, so the fallback stays off for the rest of the car's laps.
            newStop = true;
        }
        previousLapIncludedPit = lap.LapIncludedPit;

        if (newStop)
        {
            pending = new PitStopRecord
            {
                CarNumber = carNumber,
                StopNumber = stops.Count + 1,
                Lap = lapNumber,
                EntryTimeUtc = lap.PitEntryTime,
                DurationMs = NormalizeDuration(lap.PitDurationMs),
                DriverBefore = currentDriver,
                DriverAfter = driver,
            };
            SetExitTime(pending);
            stops.Add(pending);
            lapsSincePending = 0;
        }
        else if (pending != null)
        {
            lapsSincePending++;

            // A duration that was still counting up when the car crossed the line is finalized on
            // the next lap, so take it if the stop it belongs to is still the one we are holding.
            if (pending.DurationMs is null && lap.PitEntryTime == pending.EntryTimeUtc)
            {
                pending.DurationMs = NormalizeDuration(lap.PitDurationMs);
                SetExitTime(pending);
            }

            // Adopt a driver seen on the lap immediately after the stop, but only to fill a blank or
            // to replace a name that is still the pre-stop driver. Anything later than one lap is a
            // mid-stint change and does not belong to this stop.
            if (lapsSincePending <= 1 && driver.Length > 0 &&
                (pending.DriverAfter.Length == 0 || SameDriver(pending.DriverAfter, pending.DriverBefore)))
            {
                pending.DriverAfter = driver;
            }

            if (lapsSincePending >= 1)
                pending = null;
        }

        if (driver.Length > 0)
            currentDriver = driver;
    }

    /// <summary>
    /// Closes the car out and returns its stops in the order they happened.
    /// </summary>
    /// <returns>The derived stops; empty when the car never pitted.</returns>
    public IReadOnlyList<PitStopRecord> Complete()
    {
        pending = null;
        foreach (var stop in stops)
        {
            stop.DriverChanged = stop.DriverBefore.Length > 0
                && stop.DriverAfter.Length > 0
                && !SameDriver(stop.DriverBefore, stop.DriverAfter);
        }
        return stops;
    }

    private static bool SameDriver(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A duration of zero or less is the field's uninitialized state leaking through rather than an
    /// instantaneous stop, and reporting "0:00.000" as a pit time reads as a real measurement.
    /// </summary>
    private static int? NormalizeDuration(int? durationMs) => durationMs is > 0 ? durationMs : null;

    /// <summary>
    /// Derives the exit time from entry plus duration.
    /// </summary>
    /// <remarks>
    /// Both operands come out of a JSON payload written by another service, so a corrupt entry time
    /// near <see cref="DateTime.MaxValue"/> would overflow the addition. That must not be the thing
    /// that fails a whole session's export: an exit time that cannot be computed is left null, which
    /// is a case the report already renders.
    /// </remarks>
    private static void SetExitTime(PitStopRecord stop)
    {
        if (!stop.EntryTimeUtc.HasValue || !stop.DurationMs.HasValue)
        {
            stop.ExitTimeUtc = null;
            return;
        }

        try
        {
            stop.ExitTimeUtc = stop.EntryTimeUtc.Value.AddMilliseconds(stop.DurationMs.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            stop.ExitTimeUtc = null;
        }
    }
}
