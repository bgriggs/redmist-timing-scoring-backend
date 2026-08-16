using RedMist.EventProcessor.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Mappers;

namespace RedMist.EventProcessor.EventStatus.Flagtronics;

/// <summary>
/// Tracks the health of each car's in-car telemetry link and publishes four things: the trust
/// signal <see cref="CarPosition.SignalBars"/>, the viewer-facing <see cref="CarPosition.GpsHealth"/>,
/// and for the session as a whole <see cref="SessionState.HasTelemetrySource"/> and
/// <see cref="SessionState.GpsSourceHealth"/>.
///
/// Bars and health are kept apart on purpose, because only one of them is safe to gate on. Bars
/// answer "can this car's positioning be trusted", and <see cref="LapData.GpsLapPositionEnricher"/>
/// will not publish a position without four of them - so bars must stay a fault-rate measure. How
/// often a car reports says nothing about whether what it reports is right, and folding it in
/// would withhold positions across a whole field whose devices are merely slow. Health is the
/// figure that does fold cadence in, and nothing on the server reads it.
///
/// The two session-level values differ the same way. Health per car is graded against the rest of
/// the field, so it means the same thing at every event; the source grade is absolute against the
/// rate the devices are specified to send, so a bad day reads worse than a good one.
///
/// Bars describe position data only. Pit state is deliberately excluded: when a car's pit data
/// is untrustworthy the intent is to fall back to another timing source, so the client is shown
/// corrected pit state and has nothing to be warned about.
///
/// The scale is driven by fault rate over a rolling window rather than by whether a GPS fix is
/// present. Measured over a live 8 hour race, per-car-per-minute fix quality is effectively
/// binary - 93.6% of car minutes at a 100% fix rate, 6.4% at 0%, and three of 24,685 anywhere in
/// between - so a fix-derived scale would render two of six states. Fault rate does grade, and it
/// grades for the right cars: 256 of the 464 partially degraded car minutes came from the two
/// cars whose devices were failing.
///
/// Not thread safe by design: both entry points must be called with the session state write lock
/// held, which is true of every current caller since both run inside the pipeline's write region.
/// Driving <see cref="Process"/> from a timer instead would need a lock around
/// <see cref="carSignals"/> - the dictionary and its queues are mutated on every call.
/// </summary>
public class TelemetrySignalTracker
{
    private ILogger Logger { get; }
    private readonly SessionContext sessionContext;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// How far back the fault rate is measured, and equally how far back the record count that
    /// gives a car its update rate reaches. Long enough to ride out a single bad reading, short
    /// enough that a device degrading mid-stint shows up while it matters.
    /// </summary>
    private static readonly TimeSpan SignalWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// No record within this long means the car is not reporting and drops to zero bars. Set
    /// above the observed tail of normal update gaps: 98.3% of gaps in a live race were under
    /// 30s, so a car quiet for longer has genuinely stopped rather than merely slowed.
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The session is considered to have a telemetry source until no car has reported for this
    /// long. Longer than <see cref="StaleAfter"/> because a single car going quiet is normal,
    /// whereas the whole feed stopping is not.
    /// </summary>
    private static readonly TimeSpan SourceStaleAfter = TimeSpan.FromSeconds(90);

    /// <summary>
    /// A car reports full bars for this long after first appearing, rather than a figure derived
    /// from one or two readings. Deliberately measured in elapsed time and not in sample count: a
    /// count threshold is unreachable for any car reporting slower than the window divided by the
    /// count, so a slow device would sit in warm-up permanently and show full bars no matter how
    /// faulted its data was - the exact opposite of what this is for.
    /// </summary>
    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long any changed rating must hold before clients see it. Without this, a car whose
    /// update rate straddles <see cref="StaleAfter"/>, or whose fault rate sits on a threshold,
    /// flips the icon back and forth and emits a patch each time. It applies to all three ratings,
    /// and matters most to the cadence-derived ones: a rate is continuous and spends far more of
    /// its time near a band edge than a fault rate, which is closer to all-or-nothing.
    /// </summary>
    private static readonly TimeSpan BarChangeConfirm = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Sentinel for "no session adopted yet". Cannot collide with a real session id: the feed
    /// emits small non-negative run numbers, and zero is among the valid ones.
    /// </summary>
    private const int UninitializedSessionId = -1;

    /// <summary>
    /// Cars that must be reporting before a fleet rate means anything. Three is the fewest at
    /// which one outlier cannot drag the median: below it a single struggling car halves the
    /// reference and every other car reads as exceptional beside it.
    /// </summary>
    private const int MinCarsForFleetRate = 3;

    private readonly Dictionary<string, CarSignal> carSignals = [];
    private readonly Debounced sourceHealthChange = new();
    private DateTimeOffset? lastTelemetryUtc;
    private int lastSessionId = UninitializedSessionId;


    public TelemetrySignalTracker(ILoggerFactory loggerFactory, SessionContext sessionContext, TimeProvider? timeProvider = null)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.sessionContext = sessionContext;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }


    /// <summary>
    /// Records one observation of a car's telemetry. <paramref name="faulted"/> means the record
    /// carried something that cannot be true of a car being tracked properly - no usable fix, a
    /// bad-GPS sentinel speed, or a pit/paddock zone reported at racing speed.
    /// </summary>
    public void RecordTick(string carNumber, bool faulted)
    {
        if (string.IsNullOrWhiteSpace(carNumber))
            return;

        CheckForSessionChange();

        var now = timeProvider.GetUtcNow();
        lastTelemetryUtc = now;

        if (!carSignals.TryGetValue(carNumber, out var signal))
        {
            signal = new CarSignal { FirstSeen = now };
            carSignals[carNumber] = signal;
        }

        signal.LastSeen = now;
        signal.Window.Enqueue((now, faulted));
        Trim(signal, now);
    }

    /// <summary>
    /// Recomputes bars for every known car and the session's telemetry-source flag, returning the
    /// patches for anything that changed. Driven from the pipeline rather than from the telemetry
    /// feed itself, because the interesting transitions - a car going quiet, the feed stopping -
    /// are the absence of records and would otherwise never be noticed.
    ///
    /// Note this rides on the RMonitor message lane, so decay stops if the primary timing feed
    /// stops. That matches the other periodic enrichers, and an event with no RMonitor data has
    /// bigger problems, but it does mean these values freeze rather than expire in that case.
    /// </summary>
    public PatchUpdates? Process()
    {
        CheckForSessionChange();

        var now = timeProvider.GetUtcNow();
        var carPatches = new List<CarPositionPatch>();

        // Every car is graded against this, so it has to be settled before any of them are.
        var fleetRate = CalculateFleetRate(now);

        List<string>? departed = null;
        foreach (var (carNumber, signal) in carSignals)
        {
            var car = sessionContext.GetCarByNumber(carNumber);
            if (car == null)
            {
                // Reported by the telemetry feed but unknown to the timing system. Once it has
                // also stopped reporting there is nothing left to say about it.
                if (now - signal.LastSeen > StaleAfter)
                    (departed ??= []).Add(carNumber);
                continue;
            }

            var bars = CalculateBars(signal, now);
            var health = CalculateGpsHealth(signal, car, bars, fleetRate, now);

            // Evaluated separately: the two answer different questions and move independently, so
            // a change in one must not wait on the other, nor suppress it. A null health is not a
            // value to publish but an absence of one, so it neither patches nor disturbs what is
            // already on the car.
            var publishBars = signal.Bars.ShouldPublish(car.SignalBars, bars, now, BarChangeConfirm);
            var publishHealth = health != null
                && signal.Health.ShouldPublish(car.GpsHealth, health, now, BarChangeConfirm);
            if (health == null)
                signal.Health.Reset();
            if (!publishBars && !publishHealth)
                continue;

            var patch = new CarPositionPatch { Number = carNumber };
            if (publishBars)
                patch.SignalBars = bars;
            if (publishHealth)
                patch.GpsHealth = health;
            CarPositionMapper.ApplyPatch(patch, car);
            carPatches.Add(patch);
        }

        if (departed != null)
        {
            foreach (var carNumber in departed)
                carSignals.Remove(carNumber);
        }

        var sessionPatches = new List<SessionStatePatch>();
        bool hasSource = lastTelemetryUtc is DateTimeOffset last && now - last <= SourceStaleAfter;
        if (sessionContext.SessionState.HasTelemetrySource != hasSource)
        {
            var patch = new SessionStatePatch { HasTelemetrySource = hasSource };
            SessionStateMapper.ApplyPatch(patch, sessionContext.SessionState);
            sessionPatches.Add(patch);
        }

        // Only ever published as a real grade. A patch carries "no change" as null, so a grade
        // cannot be withdrawn by sending one - it would be indistinguishable from saying nothing.
        // HasTelemetrySource above is what tells a client to stop drawing the indicator, and it
        // has just been set false; the number holds its last value behind that until a source
        // returns to move it.
        var sourceHealth = hasSource ? CalculateSourceHealth(fleetRate) : null;
        if (sourceHealth == null)
        {
            // Nothing was measured, so nothing can be confirming a change. Without this a revision
            // armed before the field emptied would come back "confirmed" by the minutes in which
            // it was never observed at all.
            sourceHealthChange.Reset();
        }
        else if (sourceHealthChange.ShouldPublish(sessionContext.SessionState.GpsSourceHealth, sourceHealth, now, BarChangeConfirm))
        {
            var patch = new SessionStatePatch { GpsSourceHealth = sourceHealth };
            SessionStateMapper.ApplyPatch(patch, sessionContext.SessionState);
            sessionPatches.Add(patch);
        }

        if (carPatches.Count == 0 && sessionPatches.Count == 0)
            return null;

        return new PatchUpdates([.. sessionPatches], [.. carPatches]);
    }

    /// <summary>
    /// Drops accumulated state when the session changes. Called from both entry points, so that
    /// ticks arriving after a change are kept rather than cleared by the next sweep. The initial
    /// adoption of a session id is not a change and must not clear anything - session id 0 is a
    /// valid value the feed really emits, so the sentinel has to be distinguishable from it.
    /// </summary>
    private void CheckForSessionChange()
    {
        int currentSessionId = sessionContext.SessionState.SessionId;
        if (lastSessionId == currentSessionId)
            return;

        if (lastSessionId != UninitializedSessionId)
        {
            Logger.LogInformation("Session changed from {LastSessionId} to {CurrentSessionId}, clearing telemetry signal state",
                lastSessionId, currentSessionId);
            carSignals.Clear();
            lastTelemetryUtc = null;
            sourceHealthChange.Reset();
        }

        lastSessionId = currentSessionId;
    }

    private static int CalculateBars(CarSignal signal, DateTimeOffset now)
    {
        // Not reporting at all: no connection.
        if (now - signal.LastSeen > StaleAfter)
            return CarPosition.MinSignalBars;

        if (now - signal.FirstSeen < WarmUp)
            return CarPosition.MaxSignalBars;

        Trim(signal, now);
        if (signal.Window.Count == 0)
            return CarPosition.MinSignalBars;

        int total = signal.Window.Count;
        int clean = 0;
        foreach (var (_, faulted) in signal.Window)
        {
            if (!faulted)
                clean++;
        }

        double cleanFraction = (double)clean / total;
        return cleanFraction switch
        {
            >= 0.95 => 5,
            >= 0.80 => 4,
            >= 0.60 => 3,
            >= 0.35 => 2,
            > 0 => 1,
            _ => CarPosition.MinSignalBars,
        };
    }

    /// <summary>
    /// Records per second the car is delivering, measured over how long it has actually been
    /// watched rather than the full window - a car seen for ten seconds cannot have filled sixty
    /// and must not be marked down as though it had. Counts every record regardless of quality:
    /// how often a car reports and how much of it is usable are separate questions, combined later.
    ///
    /// Only meaningful once the car is past <see cref="WarmUp"/>; callers must check. Below that
    /// the divisor approaches zero and the rate is one sample divided by however long the sweep
    /// happened to land after the record, which is noise in whichever direction the clock falls.
    ///
    /// Trimming here is safe despite being called more than once per sweep: every caller shares
    /// the single <c>now</c> taken at the top of <see cref="Process"/>, so the second trim finds
    /// nothing left to drop.
    /// </summary>
    private static double ObservedRate(CarSignal signal, DateTimeOffset now)
    {
        Trim(signal, now);
        var watched = Math.Min(SignalWindow.TotalSeconds, (now - signal.FirstSeen).TotalSeconds);
        return watched > 0 ? signal.Window.Count / watched : 0;
    }

    /// <summary>
    /// The rate the field as a whole is managing, as a median over the cars currently reporting
    /// from the racing surface. Median rather than mean because a handful of cars down to a
    /// once-a-minute heartbeat would drag a mean far enough to make every working car look
    /// exceptional beside them.
    ///
    /// Returns zero when too few cars are reporting to describe a field at all, which is the state
    /// at either end of a session and during a caution that empties the track into the pits. A
    /// fleet rate taken from one or two cars is that car's rate, and grading it against itself
    /// would rate every car full marks just as the session empties.
    /// </summary>
    private double CalculateFleetRate(DateTimeOffset now)
    {
        List<double>? rates = null;
        foreach (var (carNumber, signal) in carSignals)
        {
            if (now - signal.LastSeen > StaleAfter)
                continue;

            // A car still warming up has no rate worth averaging in - see ObservedRate - and
            // including one would drag the median by however the first sweep happened to land.
            if (now - signal.FirstSeen < WarmUp)
                continue;

            // Pit and paddock cars are excluded: they are not being positioned for the viewer and
            // their devices report differently there, so including them measures the wrong thing.
            // Their own health is withheld for the same reason rather than graded against this.
            var car = sessionContext.GetCarByNumber(carNumber);
            if (car == null || car.IsInPit)
                continue;

            (rates ??= []).Add(ObservedRate(signal, now));
        }

        if (rates == null || rates.Count < MinCarsForFleetRate)
            return 0;

        rates.Sort();

        // Upper median on an even count, which is a shade optimistic about the field and so a
        // shade harsh on each car. Not worth averaging the middle pair for.
        return rates[rates.Count / 2];
    }

    /// <summary>
    /// How well one car is keeping up with the field, combined with how much of what it sends is
    /// usable - the worse of the two, so neither a car that reports rarely nor one that reports
    /// constant rubbish can rate well on the strength of the other.
    ///
    /// Graded against the field rather than an absolute rate because the rate a healthy device
    /// sustains varies by half again between events; see <see cref="CarPosition.GpsHealth"/>.
    ///
    /// Null means no grade can be made now, and the last one should stand rather than being
    /// replaced by a worse guess. That covers a field too small to be a reference, and a car in
    /// the pits, whose cadence the fleet rate deliberately does not describe. Falling back to the
    /// fault rate alone would be worse than holding: a car legitimately reading low for slow
    /// reporting would jump to full marks at the moment the field emptied, which is exactly when
    /// nothing new has been learned about it.
    /// </summary>
    private static int? CalculateGpsHealth(CarSignal signal, CarPosition car, int bars, double fleetRate, DateTimeOffset now)
    {
        if (fleetRate <= 0 || car.IsInPit)
            return null;

        // The same allowance the bar count gets: a rate measured over the first moments is one
        // record divided by almost nothing. Reporting what the fault rate says meanwhile keeps the
        // two indicators consistent while the car settles.
        if (now - signal.FirstSeen < WarmUp)
            return bars;

        var share = ObservedRate(signal, now) / fleetRate;
        var cadence = share switch
        {
            >= 0.90 => 5,
            >= 0.70 => 4,
            >= 0.50 => 3,
            >= 0.30 => 2,
            > 0 => 1,
            _ => CarPosition.MinGpsHealth,
        };
        return Math.Min(bars, cadence);
    }

    /// <summary>
    /// The field's cadence as a grade, measured against the once-a-second update the devices are
    /// specified to send so the value can be compared between events. Deliberately absolute where
    /// the per-car figure is relative: a fleet-wide grade is only worth showing if a bad day reads
    /// worse than a good one. Null when there is no field to measure.
    /// </summary>
    private static int? CalculateSourceHealth(double fleetRate) => fleetRate switch
    {
        <= 0 => null,
        >= 0.75 => 5,
        >= 0.50 => 4,
        >= 0.33 => 3,
        >= 0.17 => 2,
        _ => 1,
    };

    private static void Trim(CarSignal signal, DateTimeOffset now)
    {
        var cutoff = now - SignalWindow;
        while (signal.Window.Count > 0 && signal.Window.Peek().At < cutoff)
            signal.Window.Dequeue();
    }

    private sealed class CarSignal
    {
        public Queue<(DateTimeOffset At, bool Faulted)> Window { get; } = new();
        public DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset LastSeen { get; set; }

        /// <summary>Bar count waiting out <see cref="BarChangeConfirm"/> before it is published.</summary>
        public Debounced Bars { get; } = new();
        /// <summary>GPS health waiting out the same, tracked separately since the two move apart.</summary>
        public Debounced Health { get; } = new();
    }

    /// <summary>
    /// Holds a changed value back until it has stayed changed, so a reading sitting on a threshold
    /// does not flip the indicator back and forth or emit a patch every sweep. Cadence needs this
    /// more than fault rate does: it is a continuous quantity that spends much of its time near a
    /// band edge, where fault rate is closer to all-or-nothing.
    /// </summary>
    private sealed class Debounced
    {
        private int? pending;
        private bool hasPending;
        private DateTimeOffset since;

        /// <summary>
        /// Whether <paramref name="candidate"/> should be published now, given what is already on
        /// the car. The first value publishes immediately so the indicator appears as soon as the
        /// car does; every later change has to hold for <paramref name="confirm"/>.
        /// </summary>
        public bool ShouldPublish(int? current, int? candidate, DateTimeOffset now, TimeSpan confirm)
        {
            if (current == candidate)
            {
                hasPending = false;
                return false;
            }

            if (current == null)
            {
                hasPending = false;
                return true;
            }

            if (!hasPending || pending != candidate)
            {
                pending = candidate;
                hasPending = true;
                since = now;
                return false;
            }

            if (now - since < confirm)
                return false;

            hasPending = false;
            return true;
        }

        /// <summary>Drops anything waiting, for when what it was waiting on no longer exists.</summary>
        public void Reset() => hasPending = false;
    }
}
