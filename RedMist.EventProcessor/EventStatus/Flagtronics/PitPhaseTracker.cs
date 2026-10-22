using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus.Flagtronics;

/// <summary>
/// Follows a car through a pit visit from in-car GPS telemetry: entered, stopped in the box,
/// moving off towards the exit, and exited. Layered on the debounced in-pit state rather than the
/// raw zone, so a visit only begins and ends where the badge clients already see does, and the
/// glitch protection there covers this too. Within the visit the device's own pitActive flag is
/// no help - it has been seen dropping while the car was still stopped in its box - so the stages
/// come from speed and flagging zone.
///
/// A stop is only timed when its start and its end were both seen: a stopped reading with a
/// moving one shortly before it, and a moving one shortly after the last stopped reading. A stop
/// whose start or end fell in a gap, or in a stretch of readings without a speed, is still shown,
/// but as plain in-pit, and no duration is reported for it.
///
/// A visit can hold several stops - held by traffic before the box, the box itself, a queue at a
/// closed exit - which GPS alone cannot tell apart. Each stop in the lane is shown as in the box,
/// with its own clock, and the visit's stop time is its longest stop, reported when the visit
/// ends.
/// </summary>
public class PitPhaseTracker
{
    /// <summary>
    /// At or below this a car counts as stopped. A parked GPS receiver drifts a mile an hour or two.
    /// </summary>
    public const int STOPPED_MAX_MPH = 3;

    /// <summary>
    /// At or above this a car counts as moving. The gap to <see cref="STOPPED_MAX_MPH"/> keeps a
    /// creeping car from flicking between the two.
    /// </summary>
    public const int MOVING_MIN_MPH = 5;

    /// <summary>
    /// First and last flagging zones seen used for the road from the track to the start of the
    /// speed-enforced pit lane. Inferred from recorded data - not enforced, cars slowing from track
    /// speed - and consistent across every event checked, but not confirmed by Flagtronics. There
    /// are no boxes on it, so a car stopped here is queueing to get in.
    /// </summary>
    public const int PIT_ENTRY_ROAD_FIRST_ZONE = 144;
    public const int PIT_ENTRY_ROAD_LAST_ZONE = 151;

    /// <summary>
    /// First and last flagging zones seen used for the road from the end of the speed-enforced pit
    /// lane back to the track, inferred the same way as <see cref="PIT_ENTRY_ROAD_FIRST_ZONE"/> -
    /// cars accelerating from pit lane speed.
    /// </summary>
    public const int PIT_EXIT_ROAD_FIRST_ZONE = 152;
    public const int PIT_EXIT_ROAD_LAST_ZONE = 159;

    /// <summary>
    /// First flagging zone seen used for the paddock and garages, inferred the same way as
    /// <see cref="PIT_ENTRY_ROAD_FIRST_ZONE"/>.
    /// </summary>
    public const int PADDOCK_FIRST_ZONE = 160;

    /// <summary>
    /// How long a car has to stay stopped before it counts as in its box. Long enough to pass over a
    /// car slowing for traffic in the lane; real stops run from tens of seconds to minutes.
    /// </summary>
    public static readonly TimeSpan StopConfirm = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a car already moving off has to stop again in the paddock to count as back in its
    /// box, as when it is pushed into the garage, or stay parked on the exit road to count as simply
    /// in the pit. Longer than <see cref="StopConfirm"/>, so a pause in passing does not count.
    /// </summary>
    public static readonly TimeSpan RestopConfirm = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How soon after a visit ended in the box the car can be back in the pit, still stopped, for
    /// the two to count as one visit. The in-pit state is debounced, but a parked device whose
    /// fixes wander into a track zone, or report no position, can still end a visit while the car
    /// sits in its box; the stop it was in carries on rather than starting again.
    /// </summary>
    public static readonly TimeSpan SplitVisitWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Consecutive moving readings needed to leave the box. One reading can be a GPS jump.
    /// </summary>
    public const int MOVING_READINGS_TO_LEAVE = 2;

    /// <summary>
    /// Furthest apart two moving readings can be and still count as consecutive. A moving device
    /// reports about once a second; two readings at a parked device's pace are two separate jumps.
    /// </summary>
    public static readonly TimeSpan MovingReadingSpacing = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Readings on the exit road needed to count a car that never stopped as driving through.
    /// </summary>
    public const int EXIT_ROAD_READINGS_TO_DRIVE_THROUGH = 2;

    /// <summary>
    /// How long <see cref="PitPhase.ExitedPit"/> is shown before the car returns to
    /// <see cref="PitPhase.None"/>.
    /// </summary>
    public static readonly TimeSpan ExitedHold = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Longest gap between readings that still counts as continuous observation. A stationary
    /// device reports less often - ten to twenty seconds apart has been seen - but one silent for
    /// longer says nothing about what the car did in between.
    /// </summary>
    public static readonly TimeSpan StopEvidenceGap = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Longest the departure can be uncertain by when readings without a speed came between the last
    /// stopped reading and the first moving one. Those readings arrive at a moving device's pace, so
    /// the car may have been under way for any of them.
    /// </summary>
    public static readonly TimeSpan SpeedlessDepartureTolerance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How far back a stop seen before the visit was confirmed may count towards it when the moment
    /// the car entered the pit is not known. The in-pit state is debounced, so a car stopping in a
    /// box just past pit-in is stopped before the visit begins; this covers that window without
    /// letting a long stop beside the pit lane, on track, count as minutes in a box.
    /// </summary>
    public static readonly TimeSpan StopBackdateLimit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a car can be in the pit with no speed in its readings before it is shown as simply
    /// in the pit rather than still entering. Some devices report pitActive and nothing else, and
    /// others lose their fix on the way to the box.
    /// </summary>
    public static readonly TimeSpan NoSpeedFallback = TimeSpan.FromSeconds(20);

    private sealed class CarPhase
    {
        public PitPhase Phase;
        public DateTimeOffset? VisitStart;
        public DateTimeOffset? ExitedAt;

        // Readings of any kind.
        public DateTimeOffset? LastReadingAt;
        public bool LastReadingHadSpeed;
        public DateTimeOffset? LastSpeedlessAt;
        public DateTimeOffset? LastSpeedAt;

        // Motion, from readings that carry a speed.
        public DateTimeOffset? LastMotionAt;
        public DateTimeOffset? StoppedSince;
        public bool StopStartSeen;
        public bool StoppedInPitZone;
        public DateTimeOffset? LastMoveAt;
        public DateTimeOffset? ConfirmFrom;
        public DateTimeOffset? LastStoppedAt;
        public int MovingReadings;
        public DateTimeOffset? FirstMovingAt;
        public DateTimeOffset? LastMovingAt;
        public int ExitRoadReadings;

        // This visit's stops.
        public DateTimeOffset? VisitStopStart;
        public TimeSpan? LongestTimedStop;
        public TimeSpan? LongestUntimedStop;
        public DateTimeOffset? StopEarliestPossibleStart;
        public bool EndedInBox;
        public bool DepartedSinceVisitEnd;

        /// <summary>
        /// Readings this visit that may not time a stop - untrusted, or before the car was seen on
        /// track.
        /// </summary>
        public bool VisitUntimeable;

        /// <summary>
        /// The current stop began or ended out of sight, in a gap or a stretch with no speed.
        /// </summary>
        public bool StopTimingLost;

        public int? LastStopDurationMs;
    }

    private readonly Dictionary<string, CarPhase> cars = [];

    /// <summary>
    /// The phase last worked out for a car, without advancing it.
    /// </summary>
    public PitPhase GetPhase(string carNumber) =>
        cars.TryGetValue(carNumber, out var c) ? c.Phase : PitPhase.None;

    /// <summary>
    /// When the car's current or most recent stop in the pit began - for a car pushed back into the
    /// garage, the stop it carries on. Null when the stop could not be timed.
    /// </summary>
    public DateTime? GetStopStart(string carNumber) =>
        cars.TryGetValue(carNumber, out var c) && !c.VisitUntimeable && !c.StopTimingLost
            ? c.VisitStopStart?.UtcDateTime : null;

    /// <summary>
    /// The longest stop of the car's most recent visit that could be timed in full, set when the
    /// visit ends. A visit with no stop, or whose longest stop could not be timed, leaves it as it
    /// was.
    /// </summary>
    public int? GetLastStopDurationMs(string carNumber) =>
        cars.TryGetValue(carNumber, out var c) ? c.LastStopDurationMs : null;

    public void Clear() => cars.Clear();

    /// <summary>
    /// Advances a car's phase on a live reading.
    /// </summary>
    /// <param name="inPit">The car's debounced in-pit state, as clients are shown it.</param>
    /// <param name="speedMph">Reported speed, with the stopped sentinel as 0; null when the reading carries none or the device reports bad GPS.</param>
    /// <param name="flaggingZone">Reported flagging zone, if any.</param>
    /// <param name="canTime">
    /// Whether this reading may time a stop. False while this source does not own the car's pit
    /// state - the phase is followed either way, so a car handed back lands where it really is, but
    /// a frozen GPS repeating a standstill looks just like a stop - and before the car has been seen
    /// on track, while the field waits in the pit for the start. A visit with any such reading
    /// shows as plain in-pit and reports no stop time.
    /// </param>
    /// <param name="enteredAt">
    /// On the reading that opens a visit, when the raw readings first put the car in the pit. A
    /// stop seen before the debounced visit began counts from no earlier than this.
    /// </param>
    public PitPhase Observe(string carNumber, bool inPit, int? speedMph, int? flaggingZone, DateTimeOffset now,
        bool canTime = true, DateTimeOffset? enteredAt = null)
    {
        if (!cars.TryGetValue(carNumber, out var c))
        {
            c = new CarPhase();
            cars[carNumber] = c;
        }

        var previousReadingAt = c.LastReadingAt;
        var previousHadSpeed = c.LastReadingHadSpeed;
        c.LastReadingAt = now;
        c.LastReadingHadSpeed = speedMph != null;
        if (speedMph == null)
            c.LastSpeedlessAt = now;

        bool onEntryRoad = flaggingZone is int ez && ez >= PIT_ENTRY_ROAD_FIRST_ZONE && ez <= PIT_ENTRY_ROAD_LAST_ZONE;
        bool onExitRoad = flaggingZone is int xz && xz >= PIT_EXIT_ROAD_FIRST_ZONE && xz <= PIT_EXIT_ROAD_LAST_ZONE;
        bool inPaddock = flaggingZone is int pz && pz >= PADDOCK_FIRST_ZONE;

        if (!inPit)
        {
            if (c.Phase is PitPhase.EnteredPit or PitPhase.InPitBox or PitPhase.ExitingPit or PitPhase.InPit)
            {
                EndVisit(c, now);
            }
            else
            {
                Expire(c, now);
            }

            // Motion is still followed outside a visit, so a car that stops in a box before the
            // debounced visit begins is already known to be stopped when it does.
            TrackMotion(c, speedMph, flaggingZone, onExitRoad, now, previousReadingAt, previousHadSpeed);
            return c.Phase;
        }

        if ((c.Phase is PitPhase.ExitedPit or PitPhase.None) && c.EndedInBox && c.ExitedAt is DateTimeOffset exitedAt
            && now - exitedAt <= SplitVisitWindow && !c.DepartedSinceVisitEnd && c.StoppedSince != null)
        {
            // Never left since the visit ended in the box - a stray moving reading is not a
            // departure - and stopped again: the same visit, and the same stop.
            c.Phase = PitPhase.InPitBox;
            c.ExitedAt = null;
            c.EndedInBox = false;
        }
        else if (c.Phase is PitPhase.None or PitPhase.ExitedPit)
        {
            c.Phase = PitPhase.EnteredPit;
            c.VisitStart = now;
            c.ExitedAt = null;
            c.VisitStopStart = null;
            c.LongestTimedStop = null;
            c.LongestUntimedStop = null;
            c.EndedInBox = false;
            c.VisitUntimeable = false;
            c.StopTimingLost = false;
            c.LastSpeedAt = null;
            c.MovingReadings = 0;
            c.FirstMovingAt = null;
            c.ExitRoadReadings = 0;
            if (c.StoppedSince is DateTimeOffset stopped && !c.StoppedInPitZone)
            {
                // Stopped since before the car was in the pit, and somewhere that is not the pit - on
                // track beside the lane, or with no position - so when it stopped in the pit is not
                // known. A stop that began on a pit reading is the box itself, whatever restarted the
                // debounced entry since.
                var earliest = enteredAt ?? now - StopBackdateLimit;
                if (stopped < earliest)
                {
                    c.StoppedSince = earliest;
                    c.StopStartSeen = false;
                }
                if (c.ConfirmFrom < c.StoppedSince)
                    c.ConfirmFrom = c.StoppedSince;
            }
        }

        if (!canTime)
            c.VisitUntimeable = true;

        if (speedMph is not int speed)
        {
            // A reading with no speed says nothing about whether the car is moving.
            if (c.Phase == PitPhase.EnteredPit && now - (c.LastSpeedAt ?? c.VisitStart) >= NoSpeedFallback)
                c.Phase = PitPhase.InPit;
            return c.Phase;
        }
        c.LastSpeedAt = now;

        TrackMotion(c, speed, flaggingZone, onExitRoad, now, previousReadingAt, previousHadSpeed);

        if (c.StoppedSince is DateTimeOffset stoppedSince && c.ConfirmFrom is DateTimeOffset confirmFrom)
        {
            var stoppedFor = now - confirmFrom;
            bool inLane = !onEntryRoad && !onExitRoad;
            if ((c.Phase is PitPhase.EnteredPit or PitPhase.InPit) && inLane && stoppedFor >= StopConfirm)
            {
                StartStop(c, stoppedSince);
            }
            else if (c.Phase == PitPhase.EnteredPit && onExitRoad && stoppedFor >= RestopConfirm)
            {
                // Parked on the exit road without having stopped in a box - queued for a drive-through
                // under a closed exit, or retired there. In the pit, but not entering any more.
                c.Phase = PitPhase.InPit;
            }
            else if (c.Phase == PitPhase.ExitingPit && inPaddock && stoppedFor >= RestopConfirm)
            {
                // Pushed back into the garage: the stop carries on from the start of the last one.
                c.Phase = PitPhase.InPitBox;
            }
            else if (c.Phase == PitPhase.ExitingPit && inLane && !inPaddock && stoppedFor >= StopConfirm)
            {
                // Stopped again in the lane. The earlier stop may have been traffic short of the box
                // - common when the lane jams under a full course yellow - or this may be a queue at
                // a closed exit; either way the car is stationary in the pit, and the visit keeps
                // the longest of its stops.
                StartStop(c, stoppedSince);
            }
        }
        else if (c.FirstMovingAt is DateTimeOffset firstMoving)
        {
            if (c.Phase == PitPhase.InPitBox && c.MovingReadings >= MOVING_READINGS_TO_LEAVE)
            {
                LeaveBox(c, firstMoving);
            }
            else if ((c.Phase is PitPhase.EnteredPit or PitPhase.InPit) && c.ExitRoadReadings >= EXIT_ROAD_READINGS_TO_DRIVE_THROUGH)
            {
                // Through to the exit road without stopping: a drive-through.
                c.Phase = PitPhase.ExitingPit;
            }
        }

        return c.Phase;
    }

    private static void StartStop(CarPhase c, DateTimeOffset stoppedSince)
    {
        c.Phase = PitPhase.InPitBox;
        c.VisitStopStart = stoppedSince;
        c.StopTimingLost = !c.StopStartSeen;

        // The earliest the stop can have begun, for weighing it against the visit's other stops
        // when its own start was not seen: no earlier than the car was last seen moving.
        c.StopEarliestPossibleStart = c.LastMoveAt is DateTimeOffset moved && moved < stoppedSince ? moved : c.VisitStart;
    }

    /// <summary>
    /// Closes the visit, reporting its longest stop when that one was timed in full. A visit ending
    /// in the box was left without a moving reading to say when, so that stop's length is unknown,
    /// and it may have been the longest.
    /// </summary>
    private static void EndVisit(CarPhase c, DateTimeOffset now)
    {
        c.EndedInBox = c.Phase == PitPhase.InPitBox;
        c.DepartedSinceVisitEnd = false;
        c.Phase = PitPhase.ExitedPit;
        c.ExitedAt = now;

        if (!c.EndedInBox && !c.VisitUntimeable && c.LongestTimedStop is TimeSpan longest
            && longest >= (c.LongestUntimedStop ?? TimeSpan.Zero))
        {
            c.LastStopDurationMs = (int)Math.Min(int.MaxValue, longest.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Ends the stop at the first moving reading, which is only the departure if the car was seen
    /// stopped shortly before it. Across a gap, or a stretch of readings without a speed, the moment
    /// it moved off is unknown.
    /// </summary>
    private static void LeaveBox(CarPhase c, DateTimeOffset firstMoving)
    {
        if (c.LastStoppedAt is not DateTimeOffset lastStopped)
        {
            c.StopTimingLost = true;
        }
        else
        {
            var uncertainty = firstMoving - lastStopped;
            bool speedlessBetween = c.LastSpeedlessAt is DateTimeOffset speedless && speedless > lastStopped && speedless < firstMoving;
            if (uncertainty > StopEvidenceGap || (speedlessBetween && uncertainty > SpeedlessDepartureTolerance))
                c.StopTimingLost = true;
        }

        if (c.VisitStopStart is DateTimeOffset start && firstMoving > start)
        {
            if (!c.VisitUntimeable && !c.StopTimingLost)
            {
                var length = firstMoving - start;
                c.LongestTimedStop = c.LongestTimedStop is TimeSpan timed && timed > length ? timed : length;
            }
            else
            {
                // As long as it can have been, so a timed stop is only reported when it was
                // certainly the longer.
                var earliest = c.StopEarliestPossibleStart is DateTimeOffset e && e < start ? e : start;
                var length = firstMoving - earliest;
                c.LongestUntimedStop = c.LongestUntimedStop is TimeSpan untimed && untimed > length ? untimed : length;
            }
        }
        c.Phase = PitPhase.ExitingPit;
    }

    /// <summary>
    /// Follows whether the car is stopped or moving, and since when, from readings that carry a
    /// speed - one without is no evidence either way, so the gaps here are measured between readings
    /// that have one. A gap in the middle of a stop restarts its confirmation but keeps its start.
    /// Moving readings only run together when they arrive close together, and readings repeated at
    /// the same instant count once, so neither a duplicated record nor two jumps seen at a parked
    /// device's pace can confirm a departure.
    /// </summary>
    private static void TrackMotion(CarPhase c, int? speedMph, int? flaggingZone, bool onExitRoad, DateTimeOffset now,
        DateTimeOffset? previousReadingAt, bool previousHadSpeed)
    {
        if (speedMph is not int speed)
            return;

        bool gap = c.LastMotionAt is DateTimeOffset lastMotion && now - lastMotion > StopEvidenceGap;
        c.LastMotionAt = now;

        if (speed <= STOPPED_MAX_MPH)
        {
            c.MovingReadings = 0;
            c.FirstMovingAt = null;
            c.LastMovingAt = null;
            c.ExitRoadReadings = 0;
            c.LastStoppedAt = now;
            if (c.StoppedSince == null)
            {
                // The stop's start was seen only if the reading just before this one showed the car
                // under way, and recently.
                c.StoppedSince = now;
                c.ConfirmFrom = now;
                c.StoppedInPitZone = flaggingZone is int zone && zone > FlagtronicsProcessor.MAX_ON_TRACK_ZONE;
                c.StopStartSeen = previousHadSpeed && previousReadingAt is DateTimeOffset previous
                    && now - previous <= StopEvidenceGap;
            }
            else if (gap)
            {
                c.ConfirmFrom = now;
            }
        }
        else if (speed >= MOVING_MIN_MPH)
        {
            c.StoppedSince = null;
            c.ConfirmFrom = null;
            if (c.FirstMovingAt == null || gap || (c.LastMovingAt is DateTimeOffset lastMoving && now - lastMoving > MovingReadingSpacing))
            {
                c.FirstMovingAt = now;
                c.MovingReadings = 1;
                c.ExitRoadReadings = 0;
            }
            else if (c.LastMovingAt != now)
            {
                c.MovingReadings++;
            }
            c.LastMovingAt = now;
            c.LastMoveAt = now;
            if (c.MovingReadings >= MOVING_READINGS_TO_LEAVE)
                c.DepartedSinceVisitEnd = true;
            if (onExitRoad)
                c.ExitRoadReadings++;
            else
                c.ExitRoadReadings = 0;
        }
        else
        {
            // Creeping: neither confirms a stop nor a departure.
            c.StoppedSince = null;
            c.ConfirmFrom = null;
            c.MovingReadings = 0;
            c.FirstMovingAt = null;
            c.LastMovingAt = null;
            c.ExitRoadReadings = 0;
        }
    }

    /// <summary>
    /// Ends an expired <see cref="PitPhase.ExitedPit"/> without a reading, for a car whose device has
    /// gone quiet after leaving the pit. Returns whether the car's phase changed.
    /// </summary>
    public bool Expire(string carNumber, DateTimeOffset now)
    {
        if (!cars.TryGetValue(carNumber, out var c))
            return false;
        var before = c.Phase;
        Expire(c, now);
        return c.Phase != before;
    }

    /// <summary>
    /// Cars currently shown as just having exited, which <see cref="Expire(string, DateTimeOffset)"/> may end.
    /// </summary>
    public IEnumerable<string> CarsExited() =>
        cars.Where(kv => kv.Value.Phase == PitPhase.ExitedPit).Select(kv => kv.Key).ToList();

    private static void Expire(CarPhase c, DateTimeOffset now)
    {
        if (c.Phase == PitPhase.ExitedPit && c.ExitedAt is DateTimeOffset exited && now - exited >= ExitedHold)
            c.Phase = PitPhase.None;
    }
}
