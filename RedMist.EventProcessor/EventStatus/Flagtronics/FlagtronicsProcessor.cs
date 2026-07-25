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
    private int lastSessionId = -1;

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


    public FlagtronicsProcessor(ILoggerFactory loggerFactory, SessionContext sessionContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.sessionContext = sessionContext;
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
            carLapsWithPitStops.Clear();
            carsSeenOnTrack.Clear();
            pitActiveSuppressed.Clear();
            sessionContext.IsFlagtronicsFlagActive = false;
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

        // Flagtronics data is flowing: in-car pit detection takes precedence over X2 loop data
        sessionContext.IsFlagtronicsPitActive = true;

        var patches = new List<CarPositionPatch>();
        foreach (var vehicle in vehicles)
        {
            if (string.IsNullOrWhiteSpace(vehicle.CarNumber))
                continue;

            lastVehicles[vehicle.CarNumber] = vehicle;

            var car = sessionContext.GetCarByNumber(vehicle.CarNumber);
            if (car == null)
            {
                // Car not (yet) known to the timing system; picked up by a later
                // full-state resend once the timing feed registers it.
                continue;
            }

            var patch = BuildPatch(vehicle, car, deriveEdges: true);
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
    /// The Flagtronics full-course flag takes precedence over the RMonitor heartbeat flag
    /// while usable. Flags mapping to Unknown (None/Blank/NoSignal or future names) release
    /// precedence so the timing system flag takes over again.
    /// </summary>
    private SessionStatePatch? ProcessFullCourseFlag(List<FlagtronicsVehicle> vehicles)
    {
        var fullCourseFlag = vehicles.LastOrDefault(v => !string.IsNullOrEmpty(v.FullCourseFlag))?.FullCourseFlag;
        if (fullCourseFlag == null)
            return null;

        var flag = fullCourseFlag.FlagtronicsToFlag();
        if (flag == Flags.Unknown)
        {
            sessionContext.IsFlagtronicsFlagActive = false;
            return null;
        }

        sessionContext.IsFlagtronicsFlagActive = true;
        if (sessionContext.SessionState.CurrentFlag == flag)
            return null;

        var patch = new SessionStatePatch { CurrentFlag = flag };
        SessionStateMapper.ApplyPatch(patch, sessionContext.SessionState);
        return patch;
    }

    /// <summary>
    /// Re-applies the latest Flagtronics state for a car, e.g. after a timing system reset
    /// recreated it. Pit entry/exit edges are not derived here since the car's state may
    /// have been reset and would produce spurious transitions.
    /// </summary>
    public CarPositionPatch? ProcessCar(string number)
    {
        if (!lastVehicles.TryGetValue(number, out var vehicle))
            return null;

        var car = sessionContext.GetCarByNumber(number);
        if (car == null)
            return null;

        var patch = BuildPatch(vehicle, car, deriveEdges: false);
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
        if (hasValidZone || !vehicle.PitActive)
            return null;

        var car = sessionContext.GetCarByNumber(number);
        if (car == null || !car.IsInPit)
            return null;

        // Stuck pitActive with no GPS, yet the car just completed a lap: it is out on track.
        pitActiveSuppressed.Add(number);
        var patch = new CarPositionPatch { Number = number, IsInPit = false };
        CarPositionMapper.ApplyPatch(patch, car);
        return patch;
    }

    private CarPositionPatch BuildPatch(FlagtronicsVehicle vehicle, CarPosition car, bool deriveEdges)
    {
        var patch = new CarPositionPatch { Number = car.Number };

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
        bool inPit;
        if (hasValidZone)
        {
            // Position is known and authoritative, so any earlier stuck-flag suppression is moot.
            pitActiveSuppressed.Remove(vehicle.CarNumber);
            int zone = vehicle.FlaggingZone!.Value;
            if (zone <= MAX_ON_TRACK_ZONE)
                inPit = false;
            else if (realSpeed is int rs && rs >= PIT_ZONE_GLITCH_SPEED_MPH)
                inPit = vehicle.PitActive; // pit zone at racing speed: GPS glitch, trust the flag
            else
                inPit = true;
        }
        else
        {
            // No usable GPS: defer to pitActive, unless a lap completion proved it stuck.
            if (!vehicle.PitActive)
                pitActiveSuppressed.Remove(vehicle.CarNumber);
            inPit = vehicle.PitActive && !pitActiveSuppressed.Contains(vehicle.CarNumber);
        }

        bool onTrackZone = hasValidZone && vehicle.FlaggingZone!.Value <= MAX_ON_TRACK_ZONE;
        if (onTrackZone)
            carsSeenOnTrack.Add(vehicle.CarNumber);

        // Whenever pitActive is true but the car resolved as not in the pit (on-track zone or a
        // suppressed stuck flag), its reported pit duration runs away and its entry time is
        // bogus, so those fields are not applied. A clean exit (pitActive false) still carries
        // the real final duration.
        bool stuckOverride = vehicle.PitActive && !inPit;

        // Pit state: inPit is the level; entry/exit edges are derived from the transition
        bool wasInPit = car.IsInPit;
        if (car.IsInPit != inPit)
            patch.IsInPit = inPit;

        if (deriveEdges)
        {
            bool entered = inPit && !wasInPit;
            bool exited = !inPit && wasInPit;
            if (car.IsEnteredPit != entered)
                patch.IsEnteredPit = entered;
            if (car.IsExitedPit != exited)
                patch.IsExitedPit = exited;
        }

        // Apply pit entry time / duration except when overriding a stuck pitActive, whose
        // reported duration runs away and whose entry time is bogus.
        if (!stuckOverride && vehicle.PitEntryTime != null && car.PitEntryTime != vehicle.PitEntryTime)
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

        // GPS fix present when the zone is valid or a non-zero position is reported. Drives
        // the no-GPS lap-completion fallback and lets clients show a GPS dropout.
        bool hasGps = hasValidZone || (vehicle.Lat is double gpsLat && vehicle.Lon is double gpsLon && (gpsLat != 0 || gpsLon != 0));
        if (car.HasGps != hasGps)
            patch.HasGps = hasGps;

        // Track laps that included a pit stop, mirroring the X2 loop behavior. Exclude the
        // pre-race grid/pit staging, where the whole field sits in pit zones (inPit by zone,
        // pitActive false) before ever turning a lap, so it does not tag the first lap: only
        // count when the device itself reports pitActive or the car has already run on track.
        if (inPit && (vehicle.PitActive || carsSeenOnTrack.Contains(vehicle.CarNumber)))
        {
            if (!carLapsWithPitStops.TryGetValue(vehicle.CarNumber, out var laps))
            {
                laps = [];
                carLapsWithPitStops[vehicle.CarNumber] = laps;
            }
            laps.Add(car.LastLapCompleted + 1);
            if (!car.LapIncludedPit)
                patch.LapIncludedPit = true;
        }
        else
        {
            bool lapIncludedPit = carLapsWithPitStops.TryGetValue(vehicle.CarNumber, out var laps) && laps.Contains(car.LastLapCompleted);
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
    /// Parses an hh:mm:ss.fff duration to milliseconds.
    /// </summary>
    private static int? ParseDurationMs(string? duration)
    {
        if (string.IsNullOrEmpty(duration))
            return null;
        if (TimeSpan.TryParse(duration, System.Globalization.CultureInfo.InvariantCulture, out var ts))
            return (int)ts.TotalMilliseconds;
        return null;
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
