using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.EventProcessor.Models;
using RedMist.TimingCommon.LapTiming;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Mappers;
using StackExchange.Redis;
using System.Globalization;
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

    /// <summary>
    /// Cars in the message just processed whose record carried real coordinates and was not
    /// faulted. A car reporting an unchanged position produces no patch at all, so consumers that
    /// need to tell "still reporting a good fix" from "gone off the air" cannot infer it from the
    /// patches alone. A device talking to us while its GPS is dead is not a fix, however much else
    /// it has to say.
    /// </summary>
    private readonly List<string> carsWithUsableFix = [];

    /// <summary>
    /// The last position accepted for each car, and how many readings since have been rejected as
    /// impossible jumps away from it. See <see cref="IsTeleport"/>.
    /// </summary>
    private readonly Dictionary<string, (double Lat, double Lon, DateTimeOffset At, int Rejected)> lastAcceptedFix = [];

    /// <summary>
    /// Cars whose record in <see cref="lastVehicles"/> had its position rejected. That record is
    /// replayed after a timing-system reset, and replaying its coordinates would put the car right
    /// back where the rejection had just refused to put it.
    /// </summary>
    private readonly HashSet<string> lastRecordPositionRejected = [];
    private int lastSessionId = -1;

    /// <summary>
    /// Cars whose most recent record carried a driver name, pending publication to the driver
    /// cache. The vehicle feed names cars the DriverID event feed never attributes, so this is
    /// the only route by which most of the field gets a driver.
    /// </summary>
    private readonly List<string> carsReportingDriver = [];

    /// <summary>
    /// The driver last considered for the cache per car, and when, so an unchanged name is not
    /// rewritten on every record. Records an attempt rather than a write: a car skipped because
    /// another source held its record is rate-limited the same way, and re-checked once the
    /// interval passes so it can be taken over when that record lapses.
    /// </summary>
    private readonly Dictionary<string, (string DriverId, string DriverName, DateTimeOffset AttemptedAt)> driverAttempts = [];

    /// <summary>
    /// Client id recorded on driver records published from the vehicle feed. Distinguishes this
    /// processor's own records, which it may freely update, from another source's.
    /// </summary>
    public const string FLAGTRONICS_VEHICLE_CLIENT_ID = "flagtronics-vehicle";

    /// <summary>
    /// Matches the expiration the external telemetry endpoint writes, so a record from either
    /// source ages out of the cache the same way.
    /// </summary>
    private static readonly TimeSpan DriverKeyExpiration = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How often an unchanged driver record is rewritten to keep it from expiring. A driver who
    /// is not re-identified during a stint produces no change to publish, so the record has to
    /// be refreshed on its own or the enricher would clear the name when the key lapses.
    /// </summary>
    private static readonly TimeSpan DriverKeyRefreshInterval = TimeSpan.FromMinutes(4);

    /// <summary>
    /// Spread applied to <see cref="DriverKeyRefreshInterval"/> per car. Every car is first
    /// published in the same pass, so a single interval would land the whole field's refresh in
    /// one message - a full grid of round trips while the session write lock is held.
    /// </summary>
    private static readonly TimeSpan DriverKeyRefreshSpread = TimeSpan.FromMinutes(2);

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
    public const int MAX_ON_TRACK_ZONE = 127;

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

    /// <summary>
    /// Faster than any car on any track this serves, used to bound how far one can have travelled
    /// between readings. Measured over a live race, implied speeds sit at 28 m/s median and 59 m/s
    /// at the 90th percentile, then jump to 167 m/s at the 99th and 1597 m/s at the worst - real
    /// motion and nonsense separate cleanly well below this.
    /// </summary>
    private const double MaxTrackSpeedMetersPerSecond = 90.0; // ~200 mph

    /// <summary>
    /// Added to the travel allowance to absorb GPS scatter when a car has barely moved, and the
    /// coarseness of the interval between readings.
    /// </summary>
    private const double TeleportSlackMeters = 50.0;

    /// <summary>
    /// How many readings may be rejected before the next one is adopted regardless. A car really
    /// can change place without driving there - recovered on a truck, a device reset, a fix
    /// re-acquired after a long outage - and without this it would be pinned to a position it left
    /// long ago for the rest of the session. Real glitches come in ones and twos over a live race,
    /// so a relocation that keeps being reported is the genuine article.
    /// </summary>
    private const int MaxConsecutiveTeleportRejections = 2;

    private readonly TimeProvider timeProvider;


    private readonly TelemetrySignalTracker? signalTracker;
    private readonly IConnectionMultiplexer? cacheMux;


    public FlagtronicsProcessor(ILoggerFactory loggerFactory, SessionContext sessionContext,
        TimeProvider? timeProvider = null, TelemetrySignalTracker? signalTracker = null,
        IConnectionMultiplexer? cacheMux = null)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.sessionContext = sessionContext;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.signalTracker = signalTracker;
        this.cacheMux = cacheMux;
    }


    /// <summary>
    /// Cars whose most recent record carried a usable position, whether or not it had changed.
    /// </summary>
    public IReadOnlyList<string> CarsWithUsableFix => carsWithUsableFix;

    public PatchUpdates? Process(TimingMessage message)
    {
        // Cleared before anything else, so that every early return below leaves it empty instead of
        // leaving the previous message's cars looking like current reports.
        carsWithUsableFix.Clear();
        carsReportingDriver.Clear();

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
            lastAcceptedFix.Clear();
            lastRecordPositionRejected.Clear();
            driverAttempts.Clear();
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
            // Evaluated once per record: it advances the per-car anchor it judges against.
            var teleported = IsTeleport(vehicle, timeProvider.GetUtcNow());
            if (teleported)
                lastRecordPositionRejected.Add(vehicle.CarNumber);
            else
                lastRecordPositionRejected.Remove(vehicle.CarNumber);

            var faulted = teleported || IsPositionFaulted(vehicle);
            signalTracker?.RecordTick(vehicle.CarNumber, faulted);

            var car = sessionContext.GetCarByNumber(vehicle.CarNumber);
            if (car == null)
            {
                // Car not (yet) known to the timing system; picked up by a later
                // full-state resend once the timing feed registers it.
                continue;
            }

            // Faulted is not the same test as usable here. A record carrying a flagging zone but no
            // coordinates is not faulted - the zone alone locates the car well enough to judge its
            // signal by - yet there is nothing in it to position from, and treating it as a fix
            // would keep a dead-GPS device's last known position alive indefinitely.
            var carriesPosition = vehicle.Lat is double vehicleLat && vehicle.Lon is double vehicleLon
                && (vehicleLat != 0 || vehicleLon != 0);
            if (!faulted && carriesPosition)
                carsWithUsableFix.Add(vehicle.CarNumber);

            if (!string.IsNullOrWhiteSpace(vehicle.DriverName))
                carsReportingDriver.Add(vehicle.CarNumber);

            var patch = BuildPatch(vehicle, car, isLiveTick: true, positionUsable: !teleported);
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
    /// Publishes driver names carried by the vehicle records to the cache the driver enricher
    /// reads, so the enricher stays the only writer of a car's driver and applies these the same
    /// way it applies an external one. Returns the cars whose record actually changed, for the
    /// caller to refresh; an unchanged record is still rewritten periodically to hold off the
    /// key's expiration but reports no change.
    /// </summary>
    /// <remarks>
    /// A record already set by another source with a name is left alone. The DriverID event feed
    /// arrives via the relay as a deliberate identification and outranks the vehicle feed's view,
    /// so this fills in the rest of the field rather than competing for the cars that feed covers.
    /// </remarks>
    public async Task<IReadOnlyList<string>> PublishDriverInfoAsync()
    {
        if (cacheMux == null || carsReportingDriver.Count == 0)
            return [];

        var eventId = sessionContext.EventId;
        if (eventId <= 0)
            return [];

        var now = timeProvider.GetUtcNow();
        var cache = cacheMux.GetDatabase();
        var changed = new List<string>();

        foreach (var carNumber in carsReportingDriver)
        {
            if (!lastVehicles.TryGetValue(carNumber, out var vehicle))
                continue;

            // The car was queued on a record that carried a name, but a later record for the same
            // car in the same message replaces it. Publishing the empty name that leaves would
            // clear the car's driver outright, which is worse than publishing nothing.
            if (string.IsNullOrWhiteSpace(vehicle.DriverName))
                continue;

            var driverName = vehicle.DriverName;
            var driverId = vehicle.DriverId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

            var isNew = !driverAttempts.TryGetValue(carNumber, out var attempted);
            var valueChanged = isNew || attempted.DriverId != driverId || attempted.DriverName != driverName;
            if (!valueChanged && now - attempted.AttemptedAt < RefreshIntervalFor(carNumber))
                continue;

            var key = string.Format(Consts.EVENT_DRIVER_KEY, eventId, carNumber);
            try
            {
                if (await IsHeldByAnotherSourceAsync(cache, key))
                {
                    // Rate-limited like a write so the override is not re-read on every record,
                    // and re-checked afterwards so the car is taken over if that record lapses.
                    driverAttempts[carNumber] = (driverId, driverName, now);
                    continue;
                }

                var source = new DriverInfoSource(
                    new DriverInfo
                    {
                        EventId = eventId,
                        CarNumber = carNumber,
                        DriverId = driverId,
                        DriverName = driverName,
                    },
                    FLAGTRONICS_VEHICLE_CLIENT_ID,
                    now.UtcDateTime);

                await cache.StringSetAsync(key, JsonSerializer.Serialize(source), expiry: DriverKeyExpiration);
                driverAttempts[carNumber] = (driverId, driverName, now);

                // Every write is reported, not only a changed name. A refresh that restores a key
                // which had expired - or that took the car over from a lapsed record - carries a
                // name the car may not currently be showing. The enricher returns no patch when
                // nothing actually differs, so reporting an unchanged car costs a cache read.
                changed.Add(carNumber);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed publishing Flagtronics driver for car {CarNumber}", carNumber);
            }
        }

        return changed;
    }

    /// <summary>
    /// Spreads each car's refresh across a window so the field does not all come due at once.
    /// Derived from the car number so a car keeps the same offset for the whole session.
    /// </summary>
    private static TimeSpan RefreshIntervalFor(string carNumber)
    {
        var offset = (uint)carNumber.GetHashCode(StringComparison.Ordinal) % (ulong)DriverKeyRefreshSpread.Ticks;
        return DriverKeyRefreshInterval + TimeSpan.FromTicks((long)offset);
    }

    /// <summary>
    /// Whether the car's driver record was set by something other than this processor and carries
    /// a name. Such a record outranks the vehicle feed and is not overwritten.
    /// </summary>
    private static async Task<bool> IsHeldByAnotherSourceAsync(IDatabase cache, string key)
    {
        var existingJson = await cache.StringGetAsync(key);
        if (!existingJson.HasValue)
            return false;

        try
        {
            var existing = JsonSerializer.Deserialize<DriverInfoSource>(existingJson!.ToString());
            // DriverInfo is a positional record member and is absent from a malformed payload.
            if (existing?.DriverInfo == null || string.IsNullOrWhiteSpace(existing.DriverInfo.DriverName))
                return false;
            return existing.ClientId != FLAGTRONICS_VEHICLE_CLIENT_ID;
        }
        catch (JsonException)
        {
            // An unreadable record is not evidence of a higher priority source.
            return false;
        }
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

        // The stored record is replayed as-is, so a position that was rejected when it arrived stays
        // rejected here - otherwise the replay would reinstate it moments later.
        var patch = BuildPatch(vehicle, car, isLiveTick: false,
            positionUsable: !lastRecordPositionRejected.Contains(number));
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

    /// <param name="vehicle">The record to map onto the car.</param>
    /// <param name="car">Current state, which the patch is diffed against.</param>
    /// <param name="isLiveTick">
    /// False when replaying a stored record after a reset, which must not derive pit entry/exit
    /// edges or advance the pit debounce.
    /// </param>
    /// <param name="positionUsable">
    /// False when the record's coordinates were rejected as an impossible jump, in which case the
    /// car keeps the last position it was actually seen at. Everything else on the record still
    /// applies - a bad fix does not invalidate the pit flag or the speed alongside it.
    /// </param>
    private CarPositionPatch BuildPatch(FlagtronicsVehicle vehicle, CarPosition car, bool isLiveTick,
        bool positionUsable = true)
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

        // GPS: a bad (0,0) reading is ignored rather than replacing the last good position, as is
        // one the car cannot have reached since it was last seen.
        if (positionUsable && vehicle.Lat is double lat && vehicle.Lon is double lon && (lat != 0 || lon != 0))
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
    /// Whether a reading places the car somewhere it cannot have reached since it was last seen.
    /// A device that briefly reports a position hundreds of meters away is not describing a car
    /// that moved: left alone those readings are written to the car as its position, fed to the
    /// track-map learner as part of the racing line, and counted as clean signal.
    ///
    /// Called exactly once per record, since it advances the per-car state it judges against.
    /// </summary>
    private bool IsTeleport(FlagtronicsVehicle vehicle, DateTimeOffset now)
    {
        if (vehicle.Lat is not double lat || vehicle.Lon is not double lon || (lat == 0 && lon == 0))
            return false;

        if (!lastAcceptedFix.TryGetValue(vehicle.CarNumber, out var last))
        {
            lastAcceptedFix[vehicle.CarNumber] = (lat, lon, now, 0);
            return false;
        }

        // Whole seconds are the finest the feed is observed at, so treat anything shorter as a
        // second rather than shrinking the allowance towards zero on same-tick readings.
        var elapsed = Math.Max(1.0, (now - last.At).TotalSeconds);
        var travelled = TrackGeometry.DistanceMeters(last.Lat, last.Lon, lat, lon);
        if (travelled <= elapsed * MaxTrackSpeedMetersPerSecond + TeleportSlackMeters)
        {
            // Accepting a reading only clears the run when the car has actually moved on. A device
            // alternating between a stale cached position and its true one would otherwise reset
            // the count on every other reading and never reach the escape hatch, pinning the car to
            // the stale position indefinitely.
            var moved = travelled > TeleportSlackMeters;
            lastAcceptedFix[vehicle.CarNumber] = (lat, lon, now, moved ? 0 : last.Rejected);
            return false;
        }

        if (last.Rejected >= MaxConsecutiveTeleportRejections)
        {
            Logger.LogDebug("Car {car} keeps reporting a position it cannot have reached; adopting it after {count} rejections.",
                vehicle.CarNumber, last.Rejected);
            lastAcceptedFix[vehicle.CarNumber] = (lat, lon, now, 0);
            return false;
        }

        lastAcceptedFix[vehicle.CarNumber] = (last.Lat, last.Lon, last.At, last.Rejected + 1);
        return true;
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
    /// Whether this source recorded the given lap as including a pit stop. The live flag
    /// describes the lap in progress, and the logged snapshot is captured the instant the lap
    /// counter advances, so it lands a tick either side of the pit transition; the recorded lap
    /// set is exact. Called from the lap processor's background logging loop, hence
    /// <see cref="pitLapsLock"/>.
    ///
    /// A query rather than a stamp, so the caller can take the union across sources. Ownership
    /// can move mid-session, which leaves each source with only part of the car's history - a
    /// source that writes its own answer wholesale erases the other's.
    /// </summary>
    public bool WasPitLap(string? carNumber, int lapNumber)
    {
        if (string.IsNullOrEmpty(carNumber))
            return false;

        // The recorded laps belong to the session this processor last saw. If the feed stopped
        // and the event moved on, they say nothing about the lap being logged now.
        if (lastSessionId != sessionContext.SessionState.SessionId)
            return false;

        return HasPitLap(carNumber, lapNumber);
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
