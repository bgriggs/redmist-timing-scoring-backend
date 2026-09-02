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
/// carried by the previous rows. That is the lap the car completed at or after pitting.</item>
/// <item>A car's first lap is an exception: it establishes the baseline entry time rather than
/// producing a stop, unless that lap itself shows pit involvement. The first row a car has always
/// carries a sticky entry time from whatever it last did, so without this every car in a session
/// reports a phantom stop on lap 1.</item>
/// <item>The stop stays open for as long as the car is still in the pit at each crossing
/// (<c>IsInPit</c>), and closes on the first crossing where it is not - that crossing being the lap
/// the car rejoined on. Presence, not the entry time, is what holds a stop together: the equipment
/// reissues a fresh entry time partway through a long stop, so keying the merge on the entry time
/// staying put split one stop into two, the first with an absurdly short duration.</item>
/// <item>Across a merged span the duration is the <em>largest</em> value seen and the entry time the
/// <em>earliest</em> plausible one. A stop that spans the start/finish line is caught mid-stop by
/// the crossing: the lap row written there carries only the elapsed time so far, and the real total
/// arrives on the next lap.</item>
/// <item>When a session never reports entry times at all - a non-Flagtronics feed, where only the
/// timing system's own pit detection is present - the fallback is the rising edge of
/// <c>LapIncludedPit</c>. It gives a stop with no times attached, which is still worth listing.</item>
/// <item><c>DriverBefore</c> is the last non-empty driver seen before the stop;
/// <c>DriverAfter</c> is the driver seen while the stop is open.</item>
/// <item>The driver enricher can publish a change a lap late, because the puck has to be seen
/// before the car crosses start/finish. So if the stop still shows the old driver and the lap after
/// it closes shows a different one, that driver is taken as <c>DriverAfter</c> instead.</item>
/// </list>
/// <para>
/// Known ways this is wrong, all of them inherent to inferring from lap snapshots:
/// </para>
/// <list type="bullet">
/// <item><b>Missed exit.</b> If the feed never finalizes a duration, the stop is still reported but
/// <c>DurationMs</c> and <c>ExitTime</c> stay null. A stop with no length is reported rather than
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
/// time from a stop that happened before it. That row is taken as the baseline, so the earlier stop
/// is simply absent; the cost is that a car which genuinely pitted on its first surviving lap, and
/// whose row shows no pit involvement to prove it, is missed.</item>
/// <item><b>A second stop inside one pit visit.</b> Because a stop stays open until the car is seen
/// out of the pit, anything the equipment reports while it is still in there folds into the same
/// stop. That is the intent, but it means a car that genuinely stopped twice without the pit flag
/// ever clearing is reported once.</item>
/// <item><b>A pit flag that never clears.</b> The span is capped at
/// <see cref="MaxStopSpanLaps"/> crossings, after which the stop closes whatever the flag says. A
/// genuine repair longer than that is reported as ending too early.</item>
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

    /// <summary>
    /// The most laps one stop may span before it is closed regardless.
    /// </summary>
    /// <remarks>
    /// A stop closes when the car is seen out of the pit, so the only way one stays open is a pit
    /// flag that never clears - and without a bound that single stop would swallow the rest of the
    /// car's session. A real stop spanning more than a couple of crossings is already a long repair,
    /// so this is generous rather than tight: it exists to stop a stuck flag, not to judge a stop.
    /// </remarks>
    public const int MaxStopSpanLaps = 20;

    /// <summary>The stop still eligible for updates, or null.</summary>
    private PitStopRecord? current;

    /// <summary>Whether the car was still in the pit at the last crossing, so the stop is still open.</summary>
    private bool currentOpen;

    /// <summary>The most recent raw entry time seen for the current stop, kept for comparison.</summary>
    private DateTime? currentRawEntry;

    /// <summary>Whether the current stop ever saw an entry time at all, plausible or not.</summary>
    private bool currentSawRawEntry;

    /// <summary>Crossings folded into the current stop, bounded by <see cref="MaxStopSpanLaps"/>.</summary>
    private int currentSpanLaps;

    /// <summary>Laps seen since the current stop closed, for the late driver correction.</summary>
    private int lapsSinceClosed;

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
        var entry = lap.PitEntryTime;
        var duration = NormalizeDuration(lap.PitDurationMs);

        // Captured before sawAnyEntryTime is updated. The predicate below asks whether an entry time
        // was seen on an EARLIER lap, and testing the flag after setting it would always say yes.
        var sawEntryTimesBefore = sawAnyEntryTime;
        if (entry.HasValue)
            sawAnyEntryTime = true;

        if (current != null && currentOpen)
        {
            // While the car is still in the pit, every crossing belongs to the stop already open.
            // Nothing here can start a new one - which is the whole point: the equipment reissues a
            // fresh entry time partway through a long stop, and keying the merge on the entry time
            // staying put split that into two, the first with an absurdly short duration.
            MergeIntoOpenStop(lapNumber, entry, duration);

            // The car was out at this crossing, so this is the lap it rejoined on and the stop ends
            // here. The span cap is the guard against a pit flag that never clears.
            if (!lap.IsInPit || currentSpanLaps >= MaxStopSpanLaps)
                currentOpen = false;
        }
        else
        {
            var newStop = false;
            if (entry.HasValue)
            {
                if (entry != lastEntryTime)
                {
                    // The first entry time a car reports describes something that happened before the
                    // export window - it is sticky, and it is already on the row when the car appears -
                    // so on its own it says nothing and becomes the baseline. Unless the row also
                    // shows the car in the pit, which is a genuine stop.
                    newStop = sawEntryTimesBefore || lap.IsInPit || lap.LapIncludedPit;
                }
            }
            else if (!sawEntryTimesBefore && lap.LapIncludedPit && !previousLapIncludedPit)
            {
                // Fallback path only. Once a session has shown it reports entry times, a lap flagged
                // as including a pit without one is a lap the entry time simply has not reached yet,
                // not a second stop, so the fallback stays off for the rest of the car's laps.
                newStop = true;
            }

            if (newStop)
            {
                current = new PitStopRecord
                {
                    CarNumber = carNumber,
                    StopNumber = stops.Count + 1,
                    StartLap = lapNumber,
                    EndLap = lapNumber,
                    EntryTime = CsvFormat.IsPlausible(entry) ? entry : null,
                    DurationMs = duration,
                    DriverBefore = currentDriver,
                    DriverAfter = driver,
                };
                currentRawEntry = entry;
                currentSawRawEntry = entry.HasValue;
                currentSpanLaps = 1;
                currentOpen = lap.IsInPit;
                lapsSinceClosed = 0;
                SetExitTime(current);
                stops.Add(current);
            }
            else if (current != null)
            {
                lapsSinceClosed++;

                // A stop that has already closed can still have its duration finalized a lap late,
                // while the entry time says it is the same stop. This grows the number only - it
                // cannot reopen the stop or extend its span, so it cannot merge two stops together.
                if (lapsSinceClosed <= 1 && entry == currentRawEntry &&
                    duration is { } later && (current.DurationMs is null || later > current.DurationMs))
                {
                    current.DurationMs = later;
                    SetExitTime(current);
                }
            }
        }

        if (entry.HasValue)
            lastEntryTime = entry;
        previousLapIncludedPit = lap.LapIncludedPit;

        if (current != null)
        {
            // Adopt a driver seen while the stop is open, or on the lap immediately after it closes,
            // but only to fill a blank or to replace a name that is still the pre-stop driver.
            // Anything later is a mid-stint change and does not belong to this stop.
            if ((currentOpen || lapsSinceClosed <= 1) && driver.Length > 0 &&
                (current.DriverAfter.Length == 0 || SameDriver(current.DriverAfter, current.DriverBefore)))
            {
                current.DriverAfter = driver;
            }

            if (!currentOpen && lapsSinceClosed >= 2)
                ReleaseCurrent();
        }

        if (driver.Length > 0)
            currentDriver = driver;
    }

    /// <summary>
    /// Folds one more crossing into the stop that is still open.
    /// </summary>
    /// <remarks>
    /// The duration is the largest value seen, not the latest: a pit duration only grows while the
    /// car sits, so a bigger number is a better measurement of the same stop, while requiring growth
    /// means a repeated value cannot be mistaken for progress. The entry time is the earliest
    /// plausible one, because that is when the car actually came in, whatever the equipment
    /// renumbered it to afterwards.
    /// </remarks>
    private void MergeIntoOpenStop(int lapNumber, DateTime? entry, int? duration)
    {
        current!.EndLap = lapNumber;
        currentSpanLaps++;

        if (duration is { } d && (current.DurationMs is null || d > current.DurationMs))
            current.DurationMs = d;

        if (entry != currentRawEntry)
        {
            // The device gave this stop a new entry time midway through it. The merge is still right -
            // the car never left - but the reader should know the times underneath it moved.
            if (entry.HasValue && currentSawRawEntry)
                current.EntryTimeRenumbered = true;
            currentRawEntry = entry;
        }

        if (entry.HasValue)
            currentSawRawEntry = true;

        if (CsvFormat.IsPlausible(entry) && (current.EntryTime is null || entry < current.EntryTime))
            current.EntryTime = entry;

        SetExitTime(current);
    }

    /// <summary>Finishes the current stop off and stops tracking it.</summary>
    private void ReleaseCurrent()
    {
        if (current != null)
            current.EntryTimeUnavailable = current.EntryTime is null && currentSawRawEntry;

        current = null;
        currentRawEntry = null;
        currentSawRawEntry = false;
        currentOpen = false;
    }

    /// <summary>
    /// Closes the car out and returns its stops in the order they happened.
    /// </summary>
    /// <returns>The derived stops; empty when the car never pitted.</returns>
    public IReadOnlyList<PitStopRecord> Complete()
    {
        ReleaseCurrent();
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
        if (!stop.EntryTime.HasValue || !stop.DurationMs.HasValue)
        {
            stop.ExitTime = null;
            return;
        }

        try
        {
            stop.ExitTime = stop.EntryTime.Value.AddMilliseconds(stop.DurationMs.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            stop.ExitTime = null;
        }
    }
}
