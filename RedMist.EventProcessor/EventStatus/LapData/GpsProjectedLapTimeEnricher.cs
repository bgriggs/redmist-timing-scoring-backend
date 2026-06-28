using RedMist.TimingCommon.LapTiming;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus.LapData;

/// <summary>
/// Produces a live, intra-lap estimate of a car's lap time from its GPS position on the learned
/// <see cref="TrackMap"/>: <c>projected = elapsed-since-lap-start / fraction-of-lap-complete</c>. Updates
/// continuously as the car moves, unlike the history-based <see cref="ProjectedLapTimeEnricher"/>, which
/// it falls back to early in a lap, when no map exists yet, or when GPS is unavailable.
///
/// Lap-start time is tracked internally (stamped when a car's completed-lap count increments) because the
/// external feed doesn't carry it. Emission is throttled per car to keep position-rate updates from
/// flooding the patch stream. All calls happen under the pipeline write lock.
/// </summary>
public class GpsProjectedLapTimeEnricher
{
    private readonly TrackMapService trackMapService;
    private readonly SessionContext sessionContext;
    private readonly ProjectedLapTimeEnricher historyFallback;
    private readonly TimeProvider timeProvider;
    private ILogger Logger { get; }

    /// <summary>Minimum gap between emitted projections for a car (~2 Hz).</summary>
    private static readonly TimeSpan EmitInterval = TimeSpan.FromMilliseconds(500);
    /// <summary>No real lap is under 10 s; anything shorter is treated as corrupt.</summary>
    private const int AbsoluteMinimumMs = 10_000;

    private readonly Dictionary<string, (int lap, DateTime startUtc)> lapState = [];
    private readonly Dictionary<string, DateTime> lastEmitUtc = [];

    public GpsProjectedLapTimeEnricher(ILoggerFactory loggerFactory, TrackMapService trackMapService,
        SessionContext sessionContext, ProjectedLapTimeEnricher historyFallback, TimeProvider? timeProvider = null)
    {
        this.trackMapService = trackMapService;
        this.sessionContext = sessionContext;
        this.historyFallback = historyFallback;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }

    /// <summary>
    /// Computes an updated projection for a car after a position update. Returns a patch only when the
    /// value changed and the per-car throttle allows it; otherwise null.
    /// </summary>
    public async Task<CarPositionPatch?> ProcessCarAsync(CarPosition car)
    {
        if (string.IsNullOrEmpty(car.Number))
            return null;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var lapStart = TrackLapStart(car.Number, car.LastLapCompleted, now);

        // Per-car throttle: position updates can arrive many times per second.
        if (lastEmitUtc.TryGetValue(car.Number, out var last) && now - last < EmitInterval)
            return null;
        lastEmitUtc[car.Number] = now;

        var projected = TryProjectFromGps(car, lapStart, now);
        if (projected <= 0)
            projected = await historyFallback.CalculateProjectedLapTimeAsync(car);

        if (projected <= 0 || projected == car.ProjectedLapTimeMs)
            return null;

        car.ProjectedLapTimeMs = projected;
        return new CarPositionPatch { Number = car.Number, ProjectedLapTimeMs = projected };
    }

    /// <summary>Stamps lap-start when the completed-lap count changes; returns the current lap's start time.</summary>
    private DateTime TrackLapStart(string number, int completedLaps, DateTime now)
    {
        if (!lapState.TryGetValue(number, out var state) || state.lap != completedLaps)
        {
            state = (completedLaps, now);
            lapState[number] = state;
        }
        return state.startUtc;
    }

    /// <summary>
    /// GPS-based projection, or 0 when it can't be produced or fails a sanity check against the car's
    /// best lap. Only applies under green/yellow, matching the history estimator.
    /// </summary>
    private int TryProjectFromGps(CarPosition car, DateTime lapStartUtc, DateTime now)
    {
        var flag = sessionContext.SessionState.CurrentFlag;
        if (flag != Flags.Green && flag != Flags.Yellow)
            return 0;
        if (car.Latitude is not double lat || car.Longitude is not double lon)
            return 0;

        var map = trackMapService.CurrentMap;
        var projection = GpsLapProjector.Project(map, lat, lon, now - lapStartUtc);
        if (projection is null)
            return 0;

        var ms = projection.Value.ProjectedLapTimeMs;
        if (ms < AbsoluteMinimumMs)
            return 0;

        // Reject gross errors (e.g. a snap to the wrong leg of a crossover) relative to the best lap.
        var best = FastestPaceEnricher.ParseRMTime(car.BestTime ?? string.Empty);
        if (best != TimeSpan.Zero)
        {
            var bestMs = best.TotalMilliseconds;
            if (ms < bestMs * 0.5 || ms > bestMs * 3.0)
            {
                Logger.LogDebug("GPS projection {ms}ms for car {car} outside [{lo},{hi}] of best; falling back.",
                    ms, car.Number, bestMs * 0.5, bestMs * 3.0);
                return 0;
            }
        }

        return ms;
    }
}