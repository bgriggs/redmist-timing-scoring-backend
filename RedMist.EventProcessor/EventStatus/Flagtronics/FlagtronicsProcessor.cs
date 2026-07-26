using RedMist.EventProcessor.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Mappers;
using System.Text.Json;

namespace RedMist.EventProcessor.EventStatus.Flagtronics;

/// <summary>
/// Maps Flagtronics Vehicle Info records to car state: GPS position and speed, in-car pit
/// detection, per-car flag, pit speed enforcement, and driver ID source. Flagtronics is
/// supplemental to the primary timing source - laps, positions, and the car list stay owned
/// by RMonitor/Multiloop; records for cars not in the timing feed are ignored.
/// </summary>
public class FlagtronicsProcessor
{
    private ILogger Logger { get; }
    private readonly SessionContext sessionContext;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Latest record per car for re-applying state after a timing system reset.
    /// </summary>
    private readonly Dictionary<string, FlagtronicsVehicle> lastVehicles = [];
    private readonly Dictionary<string, HashSet<int>> carLapsWithPitStops = [];

    /// <summary>
    /// Cars seen on the racing surface (on-track flagging zone) at least once this session.
    /// Used to distinguish a genuine mid-race pit stop from pre-race grid/pit staging, where
    /// the whole field sits in pit zones before ever running a lap.
    /// </summary>
    private readonly HashSet<string> carsSeenOnTrack = [];

    /// <summary>
    /// Cars whose latched pitActive has been proven stuck by a start/finish crossing while
    /// they had no usable GPS to self-correct. While a car is here and still has no GPS, its
    /// pitActive is treated as false. Cleared when GPS returns (zone becomes authoritative)
    /// or pitActive resets. See <see cref="NotifyLapCompleted"/>.
    /// </summary>
    private readonly HashSet<string> pitActiveSuppressed = [];

    /// <summary>
    /// Debounced pit state per car. The raw per-tick reading glitches in both directions - a
    /// pit/paddock zone reported mid-lap at racing speed, an on-track zone for a tick or two in
    /// the middle of a stop, or a one-tick pitActive blip - and each glitch would otherwise emit
    /// a pit entry/exit edge pair. <see cref="committedPitState"/> is the state clients see;
    /// <see cref="pendingPitState"/> holds a candidate change that has not yet held long enough.
    /// </summary>
    private readonly Dictionary<string, bool> committedPitState = [];
    private readonly Dictionary<string, (bool State, DateTimeOffset Since, DateTimeOffset LastSeen, int Lap)> pendingPitState = [];
    private int lastSessionId = -1;

    /// <summary>
    /// Guards <see cref="carLapsWithPitStops"/> only. Everything else here runs on the pipeline
    /// thread, but <see cref="UpdateCarPositionForLogging"/> is called from the lap processor's
    /// background logging loop, which does not hold the session state lock.
    /// </summary>
    private readonly object pitLapsLock = new();

    /// <summary>
    /// Flagging zones 1-127 are on the racing surface; 128+ are pit/paddock ranges;
    /// 0 is uninitialized/no-GPS.
    /// </summary>
    private const int MAX_ON_TRACK_ZONE = 127;

    /// <summary>
    /// A pit/paddock flagging zone reported at or above this speed is treated as a GPS glitch
    /// (a car physically on the adjacent track momentarily mis-tagged to a pit zone) rather
    /// than real pit presence. Pit lanes run well below this; only on-track racing exceeds it.
    /// </summary>
    private const int PIT_ZONE_GLITCH_SPEED_MPH = 80;

    /// <summary>
    /// How long a changed pit reading must persist before it is applied. Real stops run for
    /// minutes while the observed glitches are one or two ticks, so this suppresses the spurious
    /// entry/exit edges at the cost of showing a genuine stop a few seconds late.
    ///
    /// Eight seconds is where the cost curve turns. Measured across a live 8 hour race, only 5
    /// spurious pit episodes in the whole session fell in the 8-10s band, so trimming from 10s to
    /// 8s is close to free; going to 5s would readmit 31 and to 3s, 51. Note this cannot be
    /// shortened for cars with good telemetry - 118 of the spurious episodes occurred while the
    /// car was reporting full signal bars, because the glitch is a pit-zone misclassification at
    /// pit-lane speed rather than a device fault, and the two are uncorrelated.
    /// </summary>
    private static readonly TimeSpan PitStateConfirmWindow = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Longest gap between two ticks that still counts as continuous observation of the same
    /// candidate pit state. A car that goes quiet for longer (device reset, GPS dead spot) has
    /// its candidate restarted rather than confirmed by a much later, unrelated reading.
    /// </summary>
    private static readonly TimeSpan PitStateEvidenceGap = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Longest pit duration treated as real. A device whose pit entry time is unset reports
    /// durations of hundreds of thousands of hours, well past what the millisecond conversion
    /// can represent. Those ticks carry no usable pit information at all, so the accompanying
    /// entry time and pitActive flag are discarded along with the duration.
    /// </summary>
    private static readonly TimeSpan MaxPitDuration = TimeSpan.FromHours(24);

    private readonly TimeProvider timeProvider;


    private readonly TelemetrySignalTracker? signalTracker;


    public FlagtronicsProcessor(ILoggerFactory loggerFactory, SessionContext sessionContext,
        TimeProvider? timeProvider = null, TelemetrySignalTracker? signalTracker = null)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.sessionContext = sessionContext;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.signalTracker = signalTracker;
    }


    public PatchUpdates? Process(TimingMessage message)
    {
        if (message.Type != Backend.Shared.Consts.FLAGTRONICS_TYPE)
            return null;

        // Check for session change and clear out old data
        if (lastSessionId != sessionContext.SessionState.SessionId)
        {
            Logger.LogInformation("Session changed from {LastSessionId} to {CurrentSessionId}, clearing Flagtronics processor state",
                lastSessionId, sessionContext.SessionState.SessionId);
            lastVehicles.Clear();
            lock (pitLapsLock)
            {
                carLapsWithPitStops.Clear();
            }
            carsSeenOnTrack.Clear();
            pitActiveSuppressed.Clear();
            committedPitState.Clear();
            pendingPitState.Clear();
            sessionContext.FlagtronicsFullCourseFlag = Flags.Unknown;
            lastSessionId = sessionContext.SessionState.SessionId;
        }

        List<FlagtronicsVehicle>? vehicles;
        try
        {
            vehicles = JsonSerializer.Deserialize<List<FlagtronicsVehicle>>(message.Data, JsonOptions);
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Invalid Flagtronics vehicle payload");
            return null;
        }

        if (vehicles == null || vehicles.Count == 0)
            return null;

        var patches = new List<CarPositionPatch>();
        foreach (var vehicle in vehicles)
        {
            if (string.IsNullOrWhiteSpace(vehicle.CarNumber))
                continue;

            lastVehicles[vehicle.CarNumber] = vehicle;
            signalTracker?.RecordTick(vehicle.CarNumber, IsPositionFaulted(vehicle));

            var car = sessionContext.GetCarByNumber(vehicle.CarNumber);
            if (car == null)
            {
                // Car not (yet) known to the timing system; picked up by a later
                // full-state resend once the timing feed registers it.
                continue;
            }

            var patch = BuildPatch(vehicle, car, isLiveTick: true);
            if (CarPositionMapper.GetChangedProperties(patch).Length > 1)
            {
                CarPositionMapper.ApplyPatch(patch, car);
                patches.Add(patch);
            }
        }

        var sessionPatches = new List<SessionStatePatch>();
        var flagPatch = ProcessFullCourseFlag(vehicles);
        if (flagPatch != null)
            sessionPatches.Add(flagPatch);

        if (patches.Count == 0 && sessionPatches.Count == 0)
            return null;

        return new PatchUpdates([.. sessionPatches], [.. patches]);
    }

    /// <summary>
    /// Records the Flagtronics full-course flag and applies the effective overall track flag.
    /// RMonitor is authoritative for the overall flag; the only Flagtronics override is that
    /// RMonitor cannot represent a purple full-course condition, so an RMonitor Yellow is
    /// upgraded to Purple35 while Flagtronics reports Purple (see
    /// <see cref="SessionContext.GetEffectiveTrackFlag"/>).
    /// </summary>
    private SessionStatePatch? ProcessFullCourseFlag(List<FlagtronicsVehicle> vehicles)
    {
        var fullCourseFlag = vehicles.LastOrDefault(v => !string.IsNullOrEmpty(v.FullCourseFlag))?.FullCourseFlag;
        if (fullCourseFlag == null)
            return null;

        sessionContext.FlagtronicsFullCourseFlag = fullCourseFlag.FlagtronicsToFlag();

        var effective = sessionContext.GetEffectiveTrackFlag();
        if (sessionContext.SessionState.CurrentFlag == effective)
            return null;

        var patch = new SessionStatePatch { CurrentFlag = effective };
        SessionStateMapper.ApplyPatch(patch, sessionContext.SessionState);
        return patch;
    }

    /// <summary>
    /// Re-applies the latest Flagtronics state for a car, e.g. after a timing system reset
    /// recreated it. Pit entry/exit edges are not derived here since the car's state may
    /// have been reset and would produce spurious transitions. This replays the last record
    /// rather than observing a new one, so it restores the already-committed pit state and
    /// never advances the debounce - otherwise a single glitched record left in
    /// <see cref="lastVehicles"/> would be re-confirmed until it passed the window.
    /// </summary>
    public CarPositionPatch? ProcessCar(string number)
    {
        if (!lastVehicles.TryGetValue(number, out var vehicle))
            return null;

        var car = sessionContext.GetCarByNumber(number);
        if (car == null)
            return null;

        var patch = BuildPatch(vehicle, car, isLiveTick: false);
        if (CarPositionMapper.GetChangedProperties(patch).Length > 1)
        {
            CarPositionMapper.ApplyPatch(patch, car);
            return patch;
        }
        return null;
    }

    /// <summary>
    /// Fallback for the residual no-GPS case: a completed lap means the car crossed the main
    /// start/finish line and is physically on track, so it cannot be in the pit. When the car
    /// has no usable GPS to self-correct a latched in-car pitActive, this clears the frozen
    /// pit state and suppresses the stuck flag until GPS returns or the device resets.
    /// Called by the pipeline when a car's completed-lap count advances. No-op when GPS is
    /// available (the flagging zone already handles those cars) or the car is not stuck in pit.
    /// </summary>
    public CarPositionPatch? NotifyLapCompleted(string number)
    {
        if (!lastVehicles.TryGetValue(number, out var vehicle))
            return null;

        // Match BuildPatch's no-GPS branch exactly: this fallback exists only for cars whose
        // pit state cannot be resolved from a flagging zone. A lat/lon without a zone still
        // cannot place the car in pit vs on track, so zone validity is the right test.
        bool hasValidZone = vehicle.FlaggingZone is int fz && fz >= 1;
        if (hasValidZone || !vehicle.PitActive || IsPitDurationBogus(vehicle.PitDuration))
            return null;

        var car = sessionContext.GetCarByNumber(number);
        if (car == null)
            return null;

        // The stuck flag may only be part-way through the confirm window, in which case the car
        // is not yet showing in pit. Dropping the candidate here is what stops it committing a
        // few seconds later, after the crossing already proved the car is on track.
        bool pendingEntry = pendingPitState.TryGetValue(number, out var pending) && pending.State;
        if (!car.IsInPit && !pendingEntry)
            return null;

        // Stuck pitActive with no GPS, yet the car just completed a lap: it is out on track.
        // Commit the correction directly so ProcessCar does not replay the stale in-pit state.
        pitActiveSuppressed.Add(number);
        committedPitState[number] = false;
        pendingPitState.Remove(number);

        // Internal state is corrected either way, but only the owning source publishes. This
        // fallback fires precisely when the car has no GPS, which is also when it is likely to
        // have been handed to X2 - without this gate both sources would write IsInPit in the
        // same pass, which is the flapping the per-car split exists to prevent.
        if (!car.IsInPit || !sessionContext.IsFlagtronicsPitTrusted(number))
            return null;

        var patch = new CarPositionPatch { Number = number, IsInPit = false };
        CarPositionMapper.ApplyPatch(patch, car);
        return patch;
    }

    private CarPositionPatch BuildPatch(FlagtronicsVehicle vehicle, CarPosition car, bool isLiveTick)
    {
        var patch = new CarPositionPatch { Number = car.Number };

        // A device with no valid pit entry time reports a wildly out-of-range pit duration, and
        // alongside it a pitActive that blips true while the car is out on track. Neither the
        // duration nor the entry time nor the flag carries information on such a tick.
        bool pitDataBogus = IsPitDurationBogus(vehicle.PitDuration);
        bool pitActive = vehicle.PitActive && !pitDataBogus;

        // The in-car pitActive flag is unreliable in both directions: on some devices it
        // latches true after a stop and never resets (freezing IsInPit while the car is back
        // racing), and it also lags or misses pit entry (indication off / late). The flagging
        // zone is an independent GPS-derived signal for the car's physical location and is the
        // authoritative pit-presence source when available:
        //   - on-track zone (1-127): not in pit, regardless of pitActive (clears a stuck flag)
        //   - pit/paddock zone (128+): in pit, regardless of pitActive (fixes late/missed
        //     indication), unless reported at racing speed, which means a GPS glitch tagged an
        //     on-track car to a pit zone for a tick - those defer to pitActive.
        //   - zone 0 (uninitialized/no-GPS) or missing: no position info, defer to pitActive,
        //     unless a start/finish crossing has proven the flag stuck (see NotifyLapCompleted).
        var realSpeed = vehicle.Speed is int sp && sp < FlagtronicsVehicle.SPEED_STOPPED ? sp : (int?)null;
        bool hasValidZone = vehicle.FlaggingZone is int fz && fz >= 1;
        bool rawInPit;
        if (hasValidZone)
        {
            // Position is known and authoritative, so any earlier stuck-flag suppression is moot.
            pitActiveSuppressed.Remove(vehicle.CarNumber);
            int zone = vehicle.FlaggingZone!.Value;
            if (zone <= MAX_ON_TRACK_ZONE)
                rawInPit = false;
            else if (realSpeed is int rs && rs >= PIT_ZONE_GLITCH_SPEED_MPH)
                rawInPit = pitActive; // pit zone at racing speed: GPS glitch, trust the flag
            else
                rawInPit = true;
        }
        else
        {
            // No usable GPS: defer to pitActive, unless a lap completion proved it stuck. The
            // suppression is released by the device dropping the flag, so it keys off the raw
            // flag - a bogus-duration tick is no evidence that the latch cleared.
            if (!vehicle.PitActive)
                pitActiveSuppressed.Remove(vehicle.CarNumber);
            rawInPit = pitActive && !pitActiveSuppressed.Contains(vehicle.CarNumber);
        }

        // Single-tick disagreements are glitches, not pit stops: hold the committed state until
        // the new reading proves itself. This is what keeps the badge from running
        // enter -> pit -> exit -> pit -> exit within a few seconds of one real stop.
        var (inPit, confirmedEntryLap) = ResolvePitState(vehicle.CarNumber, rawInPit, car, isLiveTick);

        bool onTrackZone = hasValidZone && vehicle.FlaggingZone!.Value <= MAX_ON_TRACK_ZONE;
        if (onTrackZone)
            carsSeenOnTrack.Add(vehicle.CarNumber);

        // Whenever pitActive is true but the car resolved as not in the pit (on-track zone or a
        // suppressed stuck flag), its reported pit duration runs away and its entry time is
        // bogus, so those fields are not applied. A clean exit (pitActive false) still carries
        // the real final duration. This keys off the raw flag and the raw pit reading: the
        // filtered pitActive would let a bogus-duration record through, and the debounced inPit
        // would withhold the duration for the confirm window at the start of a genuine stop.
        bool stuckOverride = vehicle.PitActive && !rawInPit;

        // Pit state: inPit is the level; entry/exit edges are derived from the transition
        // Once this car's telemetry has degraded far enough, X2 loop data owns its pit state and
        // none of the pit fields below are published - otherwise the two sources would fight.
        // The debounced state is still tracked, so the car can be taken back cleanly if its
        // device recovers. Position, speed and flag fields are unaffected and keep flowing.
        bool pitTrusted = sessionContext.IsFlagtronicsPitTrusted(vehicle.CarNumber);

        bool wasInPit = car.IsInPit;
        if (pitTrusted && car.IsInPit != inPit)
            patch.IsInPit = inPit;

        if (isLiveTick && pitTrusted)
        {
            bool entered = inPit && !wasInPit;
            bool exited = !inPit && wasInPit;
            if (car.IsEnteredPit != entered)
                patch.IsEnteredPit = entered;
            if (car.IsExitedPit != exited)
                patch.IsExitedPit = exited;
        }

        // Apply pit entry time / duration except when overriding a stuck pitActive, whose
        // reported duration runs away and whose entry time is bogus, or when the duration itself
        // shows the device has no valid entry time (it then reports 0001-01-01 as the entry).
        // Entry time and duration are not gated on ownership: X2 has no equivalent, so gating
        // them would simply lose the only source. Both are already independently sanity checked
        // against a stuck flag and a bogus duration above.
        if (!stuckOverride && !pitDataBogus && vehicle.PitEntryTime != null && car.PitEntryTime != vehicle.PitEntryTime)
            patch.PitEntryTime = vehicle.PitEntryTime;

        if (!stuckOverride)
        {
            var pitDurationMs = ParseDurationMs(vehicle.PitDuration);
            if (pitDurationMs != null && car.PitDurationMs != pitDurationMs)
                patch.PitDurationMs = pitDurationMs;
        }

        if (car.PitSpeedEnforced != vehicle.Enforced)
            patch.PitSpeedEnforced = vehicle.Enforced;

        if (car.SpeedViolation != vehicle.SpeedViolation)
            patch.SpeedViolation = vehicle.SpeedViolation;

        if (vehicle.FlaggingZone != null && car.FlaggingZone != vehicle.FlaggingZone)
            patch.FlaggingZone = vehicle.FlaggingZone;

        // Track laps that included a pit stop, mirroring the X2 loop behavior. Exclude the
        // pre-race grid/pit staging, where the whole field sits in pit zones (inPit by zone,
        // pitActive false) before ever turning a lap, so it does not tag the first lap: only
        // count when the device itself reports pitActive or the car has already run on track.
        // Lap tagging requires the raw reading to agree with the committed state. While an exit
        // is awaiting confirmation the badge still shows in-pit but the car has already left, so
        // tagging on the committed state alone attributes an extra lap at the end of a stop.
        if (pitTrusted && inPit && rawInPit && (pitActive || carsSeenOnTrack.Contains(vehicle.CarNumber)))
        {
            // Only a live observation extends the record. A replay re-applies the last record on
            // every RMonitor pass, so a car whose device stopped reporting mid-stop would
            // otherwise have every subsequent lap tagged - and that record is what gets logged.
            if (isLiveTick)
            {
                AddPitLap(vehicle.CarNumber, car.LastLapCompleted + 1);
                if (confirmedEntryLap is int entryLap)
                    AddPitLap(vehicle.CarNumber, entryLap);
            }
            if (!car.LapIncludedPit)
                patch.LapIncludedPit = true;
        }
        else if (pitTrusted)
        {
            // The set holds in-progress lap numbers, so the lap the car is on right now is
            // LastLapCompleted + 1. Testing LastLapCompleted instead kept the flag asserted for
            // the whole of the lap after the stop, which tagged a third lap in the lap log.
            bool lapIncludedPit = HasPitLap(vehicle.CarNumber, car.LastLapCompleted + 1);
            if (car.LapIncludedPit != lapIncludedPit)
                patch.LapIncludedPit = lapIncludedPit;
        }

        // GPS: a bad (0,0) reading is ignored rather than replacing the last good position
        if (vehicle.Lat is double lat && vehicle.Lon is double lon && (lat != 0 || lon != 0))
        {
            if (car.Latitude != lat)
                patch.Latitude = lat;
            if (car.Longitude != lon)
                patch.Longitude = lon;
        }

        // Speed: 255 = bad GPS (skip), 254 = stopped
        var speed = vehicle.Speed switch
        {
            null or FlagtronicsVehicle.SPEED_BAD_GPS => null,
            FlagtronicsVehicle.SPEED_STOPPED => 0,
            _ => vehicle.Speed
        };
        if (speed != null && car.SpeedMph != speed)
            patch.SpeedMph = speed;

        // Flag shown to this car on the in-car device
        if (!string.IsNullOrEmpty(vehicle.CarFlag))
        {
            var flag = vehicle.CarFlag.FlagtronicsToFlag();
            if (car.LocalFlag != flag)
                patch.LocalFlag = flag;
        }

        var driverSource = NormalizeDriverSource(vehicle.DriverSource);
        if (driverSource != null && car.DriverSource != driverSource)
            patch.DriverSource = driverSource;

        return patch;
    }

    /// <summary>
    /// Applies the debounce to a raw per-tick pit reading and returns the state clients should
    /// see. A change is only committed once the same reading has persisted for
    /// <see cref="PitStateConfirmWindow"/>; anything shorter is a glitch and is discarded.
    /// Replays (<paramref name="isLiveTick"/> false) return the committed state untouched.
    /// EntryLap is set only on the tick a pit entry is committed, and carries the lap the car
    /// was on when the entry was first seen, so a stop confirmed just after a start/finish
    /// crossing still tags the lap the car actually entered on.
    /// </summary>
    private (bool InPit, int? EntryLap) ResolvePitState(string carNumber, bool rawInPit, CarPosition car, bool isLiveTick)
    {
        if (!committedPitState.TryGetValue(carNumber, out var committed))
        {
            committed = car.IsInPit;
            committedPitState[carNumber] = committed;
        }

        if (!isLiveTick)
            return (committed, null);

        if (rawInPit == committed)
        {
            // Reading agrees with what clients already show: any candidate change is over.
            pendingPitState.Remove(carNumber);
            return (committed, null);
        }

        var now = timeProvider.GetUtcNow();

        // Start a candidate when there is none, when the reading changed, or when the car went
        // quiet for long enough that the old candidate says nothing about the present. Without
        // the last test, two isolated glitches either side of a dropout would confirm each other.
        if (!pendingPitState.TryGetValue(carNumber, out var pending)
            || pending.State != rawInPit
            || now - pending.LastSeen > PitStateEvidenceGap)
        {
            pendingPitState[carNumber] = (rawInPit, now, now, car.LastLapCompleted + 1);
            return (committed, null);
        }

        if (now - pending.Since < PitStateConfirmWindow)
        {
            pendingPitState[carNumber] = pending with { LastSeen = now };
            return (committed, null);
        }

        pendingPitState.Remove(carNumber);
        committedPitState[carNumber] = rawInPit;

        return (rawInPit, rawInPit ? pending.Lap : null);
    }

    private void AddPitLap(string carNumber, int lapNumber)
    {
        lock (pitLapsLock)
        {
            if (!carLapsWithPitStops.TryGetValue(carNumber, out var laps))
            {
                laps = [];
                carLapsWithPitStops[carNumber] = laps;
            }
            laps.Add(lapNumber);
        }
    }

    private bool HasPitLap(string carNumber, int lapNumber)
    {
        lock (pitLapsLock)
        {
            return carLapsWithPitStops.TryGetValue(carNumber, out var laps) && laps.Contains(lapNumber);
        }
    }

    /// <summary>
    /// Whether a record carries something that cannot be true of a car being tracked properly,
    /// for <see cref="CarPosition.SignalBars"/>. Three position-domain faults, all observed in
    /// production: no usable fix at all, the bad-GPS sentinel speed, and a pit/paddock zone
    /// reported at racing speed - a car doing 118 mph is not in the pit lane, so that reading
    /// places the car somewhere it cannot be. Pit-domain faults are excluded on purpose; see
    /// <see cref="TelemetrySignalTracker"/>.
    /// </summary>
    private static bool IsPositionFaulted(FlagtronicsVehicle vehicle)
    {
        bool hasValidZone = vehicle.FlaggingZone is int fz && fz >= 1;
        bool hasPosition = vehicle.Lat is double lat && vehicle.Lon is double lon && (lat != 0 || lon != 0);
        if (!hasValidZone && !hasPosition)
            return true;

        if (vehicle.Speed == FlagtronicsVehicle.SPEED_BAD_GPS)
            return true;

        return hasValidZone && vehicle.FlaggingZone!.Value > MAX_ON_TRACK_ZONE
            && vehicle.Speed is int sp && sp < FlagtronicsVehicle.SPEED_STOPPED && sp >= PIT_ZONE_GLITCH_SPEED_MPH;
    }

    /// <summary>
    /// Whether a reported pit duration is nonsensical, which marks the whole pit block on that
    /// record (duration, entry time and pitActive) as meaningless. This is deliberately narrow:
    /// a duration that simply does not parse could be a feed format change, and silently
    /// dropping pitActive for every car would disable in-car pit detection outright. Only an
    /// hour field beyond <see cref="MaxPitDuration"/> counts - the signature of a device whose
    /// pit entry time is unset, which reports hundreds of thousands of hours.
    /// </summary>
    private static bool IsPitDurationBogus(string? duration)
    {
        if (string.IsNullOrEmpty(duration))
            return false;

        if (TimeSpan.TryParse(duration, System.Globalization.CultureInfo.InvariantCulture, out var ts))
            return ts < TimeSpan.Zero || ts > MaxPitDuration;

        // TimeSpan rejects an hour field above 23, which is exactly how the fault presents.
        var hours = duration.Split(':')[0];
        return long.TryParse(hours, System.Globalization.CultureInfo.InvariantCulture, out var h)
            && h > MaxPitDuration.TotalHours;
    }

    /// <summary>
    /// Stamps the authoritative LapIncludedPit onto a lap about to be logged, mirroring
    /// <see cref="X2.PitProcessor.UpdateCarPositionForLogging"/>. The live flag describes the lap
    /// in progress, and the logged snapshot is captured the instant the lap counter advances, so
    /// it lands a tick either side of the pit transition; the recorded lap set is exact.
    /// Called from the lap processor's background logging loop - see <see cref="pitLapsLock"/>.
    ///
    /// Not gated on which source owns the car: laps are only recorded while this source owns it,
    /// so having a record is itself the proof. Gating on ownership would leave a lap stamped by
    /// neither source when ownership moved mid-stop, and would mean reading session state from
    /// that background thread.
    /// </summary>
    public void UpdateCarPositionForLogging(CarPosition carPosition)
    {
        if (string.IsNullOrEmpty(carPosition.Number))
            return;

        // The recorded laps belong to the session this processor last saw. If the feed stopped
        // and the event moved on, they say nothing about the lap being logged now.
        if (lastSessionId != sessionContext.SessionState.SessionId)
            return;

        // A car with no recorded pit laps is left alone rather than forced to false, so a car
        // the Flagtronics feed does not cover keeps whatever the primary pit source decided.
        lock (pitLapsLock)
        {
            if (carLapsWithPitStops.TryGetValue(carPosition.Number, out var laps))
                carPosition.LapIncludedPit = laps.Contains(carPosition.LastLapCompleted);
        }
    }

    /// <summary>
    /// Parses an hh:mm:ss.fff duration to milliseconds. Returns null when absent, unparseable,
    /// or beyond <see cref="MaxPitDuration"/>, so a device with an unset pit entry time cannot
    /// publish a garbage duration.
    /// </summary>
    private static int? ParseDurationMs(string? duration)
    {
        if (string.IsNullOrEmpty(duration))
            return null;
        if (!TimeSpan.TryParse(duration, System.Globalization.CultureInfo.InvariantCulture, out var ts))
            return null;
        if (ts < TimeSpan.Zero || ts > MaxPitDuration)
            return null;
        return (int)ts.TotalMilliseconds;
    }

    /// <summary>
    /// Normalizes pre-v3.0 driver source spellings so clients only see the v3.0 vocabulary.
    /// </summary>
    private static string? NormalizeDriverSource(string? source)
    {
        return source switch
        {
            null or "" => null,
            "BleDrid" => "blePuck",
            "HelmetDrid" => "rfidHelmet",
            "Manual" => "manualOverride",
            "None" => "none",
            _ => source
        };
    }
}
