namespace RedMist.EventProcessor.EventStatus.LapData;

/// <summary>
/// Keeps one car's published lap position moving forward. A car on track only ever travels one
/// way, but its snapped GPS position does not: a few meters of error, or the snap settling on the
/// other side of a corner, routinely puts a reading a percent or two behind the one before it.
/// Published as-is, every one of those is a visible step backwards on the progress bar.
///
/// So the position is held as the furthest point the car has reached on its current lap. Moving
/// forward is always accepted. Moving backward is not, until the readings have put the car well
/// behind the held point for long enough that it cannot be noise - a car that really did go
/// backwards, or a held point that was itself a bad reading too far ahead. Both deserve correcting,
/// and both look the same from here: readings that keep disagreeing with what is held.
///
/// The lap count settles the start/finish line independently of GPS, because it comes from the
/// timing loop. When it ticks while the car's furthest known point is just short of the line, the
/// car has crossed and its GPS has not caught up, so the hold moves to the line rather than
/// carrying the old lap's position into the new one - and stays there while the GPS finishes the
/// approach, however slowly the car covers it.
///
/// Positions are distances in meters along the lap from the start/finish line, so that the hold
/// works the same on any length of track and is unaffected by rounding to whole percents.
/// </summary>
public sealed class StickyLapPosition
{
    /// <summary>
    /// How long readings must keep the car well behind the held point before the hold gives way.
    /// Long enough to outlast a burst of bad fixes, short enough that a real correction shows.
    /// </summary>
    public static readonly TimeSpan BackwardConfirmation = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How far behind the held point a reading may fall and still be put down to noise. Covers GPS
    /// error plus the snap moving along the track at a corner; beyond it the disagreement is worth
    /// timing.
    /// </summary>
    public const double BackwardToleranceMeters = 60.0;

    /// <summary>
    /// How close short of the line, as a share of the lap, a hold must be for a lap tick to be read
    /// as the car crossing it. Matches how far apart crossings may scatter before the start/finish
    /// calibration considers the line misplaced, since that is the spread a real crossing shows.
    /// </summary>
    private const double CrossingWindowFraction = 0.05;

    /// <summary>The same window's floor, for short tracks where a share of the lap is only meters.</summary>
    private const double MinCrossingWindowMeters = 150.0;

    private double? heldMeters;
    private double? lastMeasuredMeters;
    private int heldLap;
    private double lapLengthMeters;
    private double startFinishOffsetMeters;
    private DateTime lastUpdateUtc;
    private DateTime? behindSinceUtc;

    /// <summary>
    /// Whether the hold sits on the line because a lap tick put it there, rather than because the
    /// GPS did. Until the car is seen past the line, readings still on the approach the tick counted
    /// as crossed are level with the hold, not behind it.
    /// </summary>
    private bool heldAtLineByTick;

    /// <summary>The position currently held, or null when nothing is.</summary>
    public double? HeldMeters => heldMeters;

    /// <summary>
    /// Takes a new measured position and returns the position to publish.
    /// </summary>
    /// <param name="measuredMeters">Where the car measures, in meters along the lap from the line.</param>
    /// <param name="lap">The car's completed-lap count from the timing source.</param>
    /// <param name="lapLengthMeters">Length of the lap the position is measured against.</param>
    /// <param name="startFinishOffsetMeters">Where the line sits on the map, for spotting a moved line.</param>
    /// <param name="maxSpeedMetersPerSecond">
    /// Faster than any car can go. Bounds how long a hold stays meaningful: once a car could have
    /// covered half a lap since it was last known to be where it was, forward and backward can no
    /// longer be told apart.
    /// </param>
    /// <param name="now">The current time.</param>
    public double Apply(double measuredMeters, int lap, double lapLengthMeters, double startFinishOffsetMeters,
        double maxSpeedMetersPerSecond, DateTime now)
    {
        // Without a usable lap length there is no way round the lap to measure forward against.
        if (!double.IsFinite(lapLengthMeters) || lapLengthMeters <= 0)
        {
            Reset();
            return measuredMeters;
        }

        if (heldMeters is not double held || !IsStillComparable(lap, lapLengthMeters, startFinishOffsetMeters,
                maxSpeedMetersPerSecond, now))
        {
            return Start(measuredMeters, lap, lapLengthMeters, startFinishOffsetMeters, now);
        }

        lastUpdateUtc = now;
        lastMeasuredMeters = measuredMeters;

        if (lap > heldLap)
        {
            // The timing loop says the car has crossed the line. If the furthest the car is known to
            // have reached - this reading, which can be a second or more ahead of the hold at race
            // pace - is just short of it, that is the previous lap's position, so the hold moves up
            // to the line. Already past it means the GPS crossed first, and is already right. A count
            // that rises with the car well short of the line is not a crossing at all - a lap
            // credited or corrected by scoring - and says nothing about where the car is, so the
            // hold stays where the GPS put it.
            heldLap = lap;
            var furthest = SignedLapDelta(held, measuredMeters, lapLengthMeters) > 0 ? measuredMeters : held;
            if (IsShortOfTheLine(furthest, lapLengthMeters))
            {
                held = 0;
                heldAtLineByTick = true;
            }
            else if (!(heldAtLineByTick && IsOnTheApproach(measuredMeters, lapLengthMeters)))
            {
                // A further tick with the car still on the approach - a lap credited while it sits
                // there - leaves it counted across; anything else ends that.
                heldAtLineByTick = false;
            }
            behindSinceUtc = null;
        }

        var delta = SignedLapDelta(held, measuredMeters, lapLengthMeters);
        if (heldAtLineByTick && delta < 0 && IsOnTheApproach(measuredMeters, lapLengthMeters))
        {
            // Still on the approach the tick already counted as crossed. A car stopped there, or
            // crawling under a caution, would otherwise run out the backward clock and be put back
            // at the end of the lap it has just finished.
            delta = 0;
        }

        if (delta >= -BackwardToleranceMeters)
        {
            // Ahead, level, or behind by no more than noise. Only a reading that is actually ahead
            // moves the hold; the rest leave it where it is.
            behindSinceUtc = null;
            if (delta > 0)
            {
                held = measuredMeters;

                // Only once the car is clearly past the line. A single reading just over it can be a
                // glitch from a car still sitting on the approach, which must not then be judged
                // behind the line it was counted across.
                if (held > CrossingWindowMeters(lapLengthMeters))
                    heldAtLineByTick = false;
            }
        }
        else if (behindSinceUtc is not DateTime since)
        {
            behindSinceUtc = now;
        }
        else if (now - since >= BackwardConfirmation)
        {
            // Behind for long enough that it is the hold, not the readings, that is wrong.
            held = measuredMeters;
            behindSinceUtc = null;
            heldAtLineByTick = false;
        }

        heldMeters = held;
        return held;
    }

    /// <summary>
    /// Notes that the car is still being reported where its last reading put it, without a new
    /// reading arriving. A car sitting still with a steady fix produces no readings at all, and
    /// without this its hold would age out as though it had vanished, so the first reading after a
    /// stop would be published raw - and could step the bar back.
    /// </summary>
    public void KeepAlive(DateTime now)
    {
        if (heldMeters != null && now > lastUpdateUtc)
            lastUpdateUtc = now;
    }

    /// <summary>
    /// Completes a backward correction that has run its full confirmation while no further reading
    /// arrived to complete it. A car that stops while the hold is wrongly ahead of it - a bad reading
    /// just before a red flag - sends nothing more, so <see cref="Apply"/> alone would leave the bar
    /// ahead of the car for the whole stop.
    /// </summary>
    /// <returns>The position now held, or null when there was nothing to release.</returns>
    public double? TryRelease(double lapLengthMeters, double startFinishOffsetMeters, DateTime now)
    {
        if (heldMeters == null || lastMeasuredMeters is not double measured || behindSinceUtc is not DateTime since)
            return null;
        if (lapLengthMeters != this.lapLengthMeters || startFinishOffsetMeters != this.startFinishOffsetMeters)
            return null;
        if (now - since < BackwardConfirmation)
            return null;

        heldMeters = measured;
        behindSinceUtc = null;
        heldAtLineByTick = false;
        return measured;
    }

    /// <summary>Forgets the hold, so the next reading is taken as it comes.</summary>
    public void Reset()
    {
        heldMeters = null;
        lastMeasuredMeters = null;
        behindSinceUtc = null;
        heldAtLineByTick = false;
    }

    /// <summary>
    /// Whether the hold still describes the same car on the same map: the lap count has not been
    /// corrected downwards, the map and its line have not moved, and the car has not been gone so
    /// long that it could be anywhere.
    /// </summary>
    private bool IsStillComparable(int lap, double length, double offset, double maxSpeedMetersPerSecond, DateTime now)
    {
        if (lap < heldLap)
            return false;
        if (length != lapLengthMeters || offset != startFinishOffsetMeters)
            return false;
        var age = now - lastUpdateUtc;
        if (age < TimeSpan.Zero || age.TotalSeconds * maxSpeedMetersPerSecond >= length / 2)
            return false;
        return true;
    }

    private double Start(double measuredMeters, int lap, double length, double offset, DateTime now)
    {
        heldMeters = measuredMeters;
        lastMeasuredMeters = measuredMeters;
        heldLap = lap;
        lapLengthMeters = length;
        startFinishOffsetMeters = offset;
        lastUpdateUtc = now;
        behindSinceUtc = null;
        heldAtLineByTick = false;
        return measuredMeters;
    }

    /// <summary>Whether a position is within the crossing window short of the line.</summary>
    private static bool IsShortOfTheLine(double lapMeters, double lapLengthMeters) =>
        lapMeters > 0 && lapLengthMeters - lapMeters <= CrossingWindowMeters(lapLengthMeters);

    /// <summary>
    /// Whether a reading belongs to the approach a tick counted as crossed: the crossing window, plus
    /// the noise allowed anywhere else, since the tick may have been judged on a hold a little ahead
    /// of the reading.
    /// </summary>
    private static bool IsOnTheApproach(double lapMeters, double lapLengthMeters) =>
        lapMeters > 0 && lapLengthMeters - lapMeters <= CrossingWindowMeters(lapLengthMeters) + BackwardToleranceMeters;

    private static double CrossingWindowMeters(double lapLengthMeters) =>
        Math.Min(lapLengthMeters / 2, Math.Max(lapLengthMeters * CrossingWindowFraction, MinCrossingWindowMeters));

    /// <summary>
    /// How far <paramref name="toMeters"/> is ahead of <paramref name="fromMeters"/> around the lap,
    /// negative when behind, taking the shorter way round so that 98% to 2% reads as forward.
    /// Exactly half a lap reads as forward.
    /// </summary>
    internal static double SignedLapDelta(double fromMeters, double toMeters, double lapLengthMeters)
    {
        var delta = (toMeters - fromMeters) % lapLengthMeters;
        if (delta > lapLengthMeters / 2)
            delta -= lapLengthMeters;
        else if (delta <= -lapLengthMeters / 2)
            delta += lapLengthMeters;
        return delta;
    }
}
