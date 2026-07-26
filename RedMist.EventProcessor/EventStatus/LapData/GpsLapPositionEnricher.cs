using RedMist.EventProcessor.EventStatus.Flagtronics;
using RedMist.TimingCommon.LapTiming;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus.LapData;

/// <summary>
/// Reports where a car physically is around the lap, as a percentage, by snapping its GPS position
/// onto the learned <see cref="TrackMap"/>. This is a measurement, not an estimate: unlike
/// <see cref="ProjectedLapTimeEnricher"/>, which infers progress from elapsed time against a
/// predicted lap time, it says where the car actually is.
///
/// Because it is a measurement, it is only published when it can be trusted, which is judged on two
/// timescales. Per reading: the car is out of the pits, its position sits close enough to the track
/// path to believe, and the map's start/finish line has been located. Sustained:
/// <see cref="CarPosition.SignalBars"/> says the car's telemetry link has been healthy enough,
/// recently enough, for its positioning to be worth showing at all. Bars are smoothed and lag by
/// design, so they cannot stand in for the per-reading checks, and the per-reading checks cannot see
/// a device that is quietly degrading - both layers are needed.
///
/// When either fails, the percentage is published as <see cref="CarPosition.InvalidTrackPosition"/>
/// and consumers fall back to the time-based projection. The sentinel is published rather than
/// simply withheld because a patch carries "no change" as null, so a stale percentage would
/// otherwise stay on screen forever.
///
/// Lap-time projection is entirely <see cref="ProjectedLapTimeEnricher"/>'s job; this enricher never
/// writes it.
///
/// All calls happen under the pipeline write lock.
/// </summary>
public class GpsLapPositionEnricher
{
    private readonly TrackMapService trackMapService;
    private readonly SessionContext sessionContext;
    private readonly TimeProvider timeProvider;
    private ILogger Logger { get; }

    /// <summary>Minimum gap between emitted positions for a car. Position updates arrive far faster.</summary>
    private static readonly TimeSpan EmitInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a car's position may go unrefreshed before its percentage is retired. A dropout is
    /// silent - the feed simply stops carrying new positions for the car - so nothing but elapsed
    /// time distinguishes a car that has gone dark from one that is still out there.
    /// </summary>
    private static readonly TimeSpan FixTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How far off the centerline a position may sit and still be believed. Covers track width plus
    /// GPS error; beyond it the car is in a paddock, on an access road, or the fix is junk.
    /// </summary>
    private const double MaxLateralOffsetMeters = 30.0;

    /// <summary>
    /// Beyond this age, the last known position is too stale to narrow the search around, and the
    /// snap falls back to searching the whole map.
    /// </summary>
    private static readonly TimeSpan MaxAnchorAge = TimeSpan.FromSeconds(5);

    /// <summary>Faster than any car on any track this serves; sizes the search window, so generous is safe.</summary>
    private const double MaxTrackSpeedMetersPerSecond = 90.0; // ~200 mph

    /// <summary>Added to the search window to absorb GPS jitter when a car is barely moving.</summary>
    private const double SearchWindowSlackMeters = 25.0;

    /// <summary>
    /// How much further off the racing line an anchored match may sit than an unconstrained one
    /// before the anchor is treated as the thing that is wrong. Wide enough to cover the width of
    /// the track, so a car genuinely on its own leg of a crossover keeps its anchor.
    /// </summary>
    private const double AnchorDisagreementMeters = 10.0;

    /// <summary>
    /// Signal strength a car's telemetry link must sustain before its position is shown at all.
    /// Bars are deliberately smoothed and lagging, so they answer "can this car's positioning be
    /// trusted" rather than "is this particular reading good" - the per-sample checks below cover
    /// the latter. Set where measured fix quality separates cleanly: devices are either reporting
    /// well or failing, with very little in between, so this excludes the genuinely degraded
    /// without discarding cars that merely dropped a reading.
    ///
    /// The lag has a cost worth knowing: a car that loses its fix for a while - a garage stop, a
    /// tunnel - fills the signal window with faults, and has to earn the rate back before its
    /// position reappears, on the order of a minute after it is back on track and reporting
    /// cleanly. That is the price of the sustained reading; the alternative is trusting a device
    /// that has just spent a minute producing nonsense.
    /// </summary>
    private const int MinTrustedSignalBars = 4;

    /// <summary>
    /// State carried between position updates for one car: what was last published, when the car was
    /// last seen, and where it was, which anchors the next snap.
    /// </summary>
    private sealed class CarTrackState
    {
        public DateTime LastFixUtc;
        public DateTime LastEmitUtc;
        /// <summary>Last position believed to be on the racing surface, and when it was taken.</summary>
        public double? LastDistanceAlongMeters;
        public DateTime LastTrustedSnapUtc;
        public int? LastLapCompleted;
        public int? PublishedPercent;
    }

    private readonly Dictionary<string, CarTrackState> carStates = [];
    private int lastSessionId = -1;

    public GpsLapPositionEnricher(ILoggerFactory loggerFactory, TrackMapService trackMapService,
        SessionContext sessionContext, TimeProvider? timeProvider = null)
    {
        this.trackMapService = trackMapService;
        this.sessionContext = sessionContext;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }

    /// <summary>
    /// Updates a car's track position after a GPS update. Returns a patch only when the published
    /// value changes and the per-car throttle allows it; otherwise null.
    /// </summary>
    /// <param name="car">The car whose position was just updated.</param>
    /// <param name="fromInCarTelemetry">
    /// Whether the position came from the car's own telemetry device, in which case
    /// <see cref="CarPosition.SignalBars"/> describes its quality. Positions from the timing source
    /// are not judged by the in-car link's health - a car whose device has failed is still
    /// positioned correctly by a source that never depended on it.
    /// </param>
    public async Task<CarPositionPatch?> ProcessCarAsync(CarPosition car, bool fromInCarTelemetry)
    {
        if (string.IsNullOrEmpty(car.Number))
            return null;

        ResetOnSessionChange();

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (!carStates.TryGetValue(car.Number, out var state))
            carStates[car.Number] = state = new CarTrackState();
        state.LastFixUtc = now;

        // A lap rollover is a sighting of the start/finish line and is worth the snap even when the
        // throttle would otherwise skip this update - they are rare and the calibration depends on them.
        var isRollover = state.LastLapCompleted is int previousLap && car.LastLapCompleted == previousLap + 1;
        state.LastLapCompleted = car.LastLapCompleted;

        var throttled = now - state.LastEmitUtc < EmitInterval;
        if (throttled && !isRollover)
            return null;

        var snap = SnapCar(car, state, now);

        // Only a position worth believing is worth keeping: it becomes the anchor for the next snap,
        // and at a rollover it is a sighting of the start/finish line that calibrates the map for
        // the whole event. Anchoring on a position that was rejected as untrustworthy would keep
        // re-confirming itself and the anchor would never age out; calibrating from one would bake
        // a failing device's readings into the map permanently.
        var trusted = snap != null
            && (!fromInCarTelemetry || HasTrustedSignal(car))
            && IsOnRacingSurface(car, snap.Value);
        if (trusted)
        {
            if (isRollover)
            {
                await trackMapService.AddStartFinishObservationAsync(snap!.Value.DistanceAlongMeters,
                    sessionContext.CancellationToken);
            }

            state.LastDistanceAlongMeters = snap!.Value.DistanceAlongMeters;
            state.LastTrustedSnapUtc = now;
        }

        if (throttled)
            return null;
        state.LastEmitUtc = now;

        return Publish(car, state, DeterminePercent(snap, trusted));
    }

    /// <summary>
    /// Notes that the source is still reporting these cars, whether or not they moved. Without this,
    /// a car sitting still with a perfectly healthy fix - stopped on track, held at a red flag -
    /// produces no position change, and so would look indistinguishable from one that had dropped
    /// off the air entirely.
    /// </summary>
    public void MarkSeen(IEnumerable<string> carNumbers)
    {
        ResetOnSessionChange();

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var number in carNumbers)
        {
            if (string.IsNullOrEmpty(number))
                continue;
            if (!carStates.TryGetValue(number, out var state))
                carStates[number] = state = new CarTrackState();
            state.LastFixUtc = now;
        }
    }

    /// <summary>
    /// Retires the position of every car whose GPS has gone quiet. Driven by the pipeline rather
    /// than a timer of its own: by other cars' GPS arriving, and by the timing feed's own heartbeat
    /// so that a source going down entirely - taking every car's GPS with it - is still noticed.
    /// </summary>
    public List<CarPositionPatch> ExpireStalePositions()
    {
        ResetOnSessionChange();

        var patches = new List<CarPositionPatch>();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        foreach (var car in sessionContext.SessionState.CarPositions)
        {
            if (string.IsNullOrEmpty(car.Number))
                continue;
            if (!carStates.TryGetValue(car.Number, out var state) || state.PublishedPercent == null)
                continue;
            if (state.PublishedPercent == CarPosition.InvalidTrackPosition)
                continue;
            if (now - state.LastFixUtc < FixTimeout)
                continue;

            Logger.LogDebug("No GPS for car {car} in {seconds:F0}s; retiring its track position.",
                car.Number, (now - state.LastFixUtc).TotalSeconds);

            // Drop the anchor as well: where the car was ten seconds ago says nothing useful about
            // where it is when it comes back.
            state.LastDistanceAlongMeters = null;

            var patch = Publish(car, state, CarPosition.InvalidTrackPosition);
            if (patch != null)
                patches.Add(patch);
        }

        return patches;
    }

    /// <summary>
    /// Snaps the car onto the map, preferring a search anchored to where it was last seen. A plain
    /// nearest-point snap is ambiguous wherever the track runs close to itself, and a few meters of
    /// GPS error at a crossover or between parallel straights is enough to put a car on the wrong leg.
    /// </summary>
    private TrackSnapResult? SnapCar(CarPosition car, CarTrackState state, DateTime now)
    {
        var map = trackMapService.CurrentMap;
        if (map == null)
            return null;
        if (car.Latitude is not double lat || car.Longitude is not double lon)
            return null;

        var unconstrained = TrackGeometry.Snap(map.Points, map.TotalLengthMeters, lat, lon);
        if (state.LastDistanceAlongMeters is not double anchor)
            return unconstrained;

        var age = now - state.LastTrustedSnapUtc;
        if (age <= TimeSpan.Zero || age > MaxAnchorAge)
            return unconstrained;

        // How far the car could possibly have travelled since, with room to spare. A window covering
        // half the map or more constrains nothing, so it is not worth the second search.
        var window = age.TotalSeconds * MaxTrackSpeedMetersPerSecond + SearchWindowSlackMeters;
        if (window >= map.TotalLengthMeters / 2)
            return unconstrained;

        var anchored = TrackGeometry.SnapNear(map.Points, map.TotalLengthMeters, lat, lon, anchor, window);
        if (anchored == null)
            return unconstrained;

        // The anchor is a hint about which part of the track to believe, not evidence in itself.
        // When honouring it means claiming the car is much further off the racing line than an
        // unconstrained match would, the hint is what is wrong: the car did not drift sideways, it
        // was on the other leg of the crossover - or the far side of the track - all along. A car
        // genuinely on its own leg matches both ways with a similar offset and keeps its anchor.
        if (unconstrained != null &&
            anchored.Value.LateralOffsetMeters - unconstrained.Value.LateralOffsetMeters > AnchorDisagreementMeters)
        {
            Logger.LogDebug("Car {car} matches the track {offset:F0} m better away from where it was last seen; " +
                "dropping the anchor.", car.Number,
                anchored.Value.LateralOffsetMeters - unconstrained.Value.LateralOffsetMeters);
            return unconstrained;
        }

        return anchored;
    }

    /// <summary>
    /// Whether the car's in-car telemetry link is healthy enough for a position it delivered to be
    /// believed. Only consulted for positions that came from that link, since bars say nothing
    /// about a position the timing source produced.
    ///
    /// Null bars mean the car has no in-car device at all, which is not a verdict against it.
    /// <see cref="SessionState.HasTelemetrySource"/> is deliberately not consulted either: it is
    /// false for an event whose GPS comes entirely from the timing source, so gating on it would
    /// disable track position for exactly those events.
    /// </summary>
    private static bool HasTrustedSignal(CarPosition car)
    {
        if (car.SignalBars is not int bars)
            return true;
        return bars >= MinTrustedSignalBars;
    }

    /// <summary>
    /// Whether a snapped position looks like a car out on the track, as opposed to one in the pits
    /// or a fix too far off the path to believe.
    /// </summary>
    private bool IsOnRacingSurface(CarPosition car, TrackSnapResult snap)
    {
        // In the pits the car is off the racing line entirely, and a pit lane often parallels the
        // main straight closely enough to snap onto it, so geometry alone will not catch this.
        if (car.IsInPit || (car.FlaggingZone is int zone && zone > FlagtronicsProcessor.MAX_ON_TRACK_ZONE))
            return false;

        if (snap.LateralOffsetMeters > MaxLateralOffsetMeters)
        {
            Logger.LogDebug("Car {car} is {offset:F0} m off the track path; not reporting a position.",
                car.Number, snap.LateralOffsetMeters);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Converts a trusted snap into a percentage to publish, or
    /// <see cref="CarPosition.InvalidTrackPosition"/> when there is nothing trustworthy to state.
    /// </summary>
    private int DeterminePercent(TrackSnapResult? snap, bool trusted)
    {
        var map = trackMapService.CurrentMap;
        if (map == null || snap == null || !trusted || !trackMapService.IsStartFinishCalibrated)
            return CarPosition.InvalidTrackPosition;

        // Truncated, not rounded: a car has completed 99% of the lap right up until it crosses the
        // line, at which point it wraps to 0. Rounding would instead produce a half-width bucket of
        // "100" that means the same place as 0.
        var fraction = TrackGeometry.FractionFromStartFinish(map, snap.Value.DistanceAlongMeters);
        return Math.Clamp((int)(fraction * 100), 0, 99);
    }

    /// <summary>Applies a percentage to the car and returns a patch, or null when nothing changed.</summary>
    private static CarPositionPatch? Publish(CarPosition car, CarTrackState state, int percent)
    {
        if (state.PublishedPercent == percent && car.LapPositionPercent == percent)
            return null;

        state.PublishedPercent = percent;
        car.LapPositionPercent = percent;
        return new CarPositionPatch { Number = car.Number, LapPositionPercent = percent };
    }

    /// <summary>
    /// Drops per-car state when the session changes. Positions, anchors and lap counts all belong to
    /// the session that produced them; the map and its calibration belong to the event and survive.
    /// </summary>
    private void ResetOnSessionChange()
    {
        var sessionId = sessionContext.SessionState.SessionId;
        if (lastSessionId == sessionId)
            return;

        if (carStates.Count > 0)
        {
            Logger.LogInformation("Session changed from {last} to {current}; clearing track positions.",
                lastSessionId, sessionId);
        }
        carStates.Clear();
        lastSessionId = sessionId;
    }
}
