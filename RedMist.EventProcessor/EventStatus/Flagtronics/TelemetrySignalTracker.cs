using RedMist.EventProcessor.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Mappers;

namespace RedMist.EventProcessor.EventStatus.Flagtronics;

/// <summary>
/// Tracks the health of each car's in-car telemetry link and publishes it as
/// <see cref="CarPosition.SignalBars"/>, plus <see cref="SessionState.HasTelemetrySource"/> for
/// the session as a whole.
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
    /// How far back the fault rate is measured. Long enough to ride out a single bad reading,
    /// short enough that a device degrading mid-stint shows up while it matters.
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
    /// How long a changed bar count must hold before clients see it. Without this, a car whose
    /// update rate straddles <see cref="StaleAfter"/>, or whose fault rate sits on a threshold,
    /// flips the icon back and forth and emits a patch each time.
    /// </summary>
    private static readonly TimeSpan BarChangeConfirm = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Sentinel for "no session adopted yet". Cannot collide with a real session id: the feed
    /// emits small non-negative run numbers, and zero is among the valid ones.
    /// </summary>
    private const int UninitializedSessionId = -1;

    private readonly Dictionary<string, CarSignal> carSignals = [];
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
            if (car.SignalBars == bars)
            {
                signal.PendingBars = null;
                continue;
            }

            // The first value publishes immediately so the icon appears as soon as the car does.
            // Later changes have to hold, so a car sitting on a threshold does not flip the icon
            // back and forth or emit a patch every sweep.
            if (car.SignalBars != null)
            {
                if (signal.PendingBars != bars)
                {
                    signal.PendingBars = bars;
                    signal.PendingSince = now;
                    continue;
                }
                if (now - signal.PendingSince < BarChangeConfirm)
                    continue;
            }

            signal.PendingBars = null;
            var patch = new CarPositionPatch { Number = carNumber, SignalBars = bars };
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

        /// <summary>
        /// A bar count waiting out <see cref="BarChangeConfirm"/> before it is published.
        /// </summary>
        public int? PendingBars { get; set; }
        public DateTimeOffset PendingSince { get; set; }
    }
}
