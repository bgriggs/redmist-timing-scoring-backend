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
    private int lastSessionId = -1;


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

        if (patches.Count == 0)
            return null;

        return new PatchUpdates([], [.. patches]);
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

    private CarPositionPatch BuildPatch(FlagtronicsVehicle vehicle, CarPosition car, bool deriveEdges)
    {
        var patch = new CarPositionPatch { Number = car.Number };

        // Pit state: pitActive is the level; entry/exit edges are derived from the transition
        bool wasInPit = car.IsInPit;
        if (car.IsInPit != vehicle.PitActive)
            patch.IsInPit = vehicle.PitActive;

        if (deriveEdges)
        {
            bool entered = vehicle.PitActive && !wasInPit;
            bool exited = !vehicle.PitActive && wasInPit;
            if (car.IsEnteredPit != entered)
                patch.IsEnteredPit = entered;
            if (car.IsExitedPit != exited)
                patch.IsExitedPit = exited;
        }

        if (vehicle.PitEntryTime != null && car.PitEntryTime != vehicle.PitEntryTime)
            patch.PitEntryTime = vehicle.PitEntryTime;

        var pitDurationMs = ParseDurationMs(vehicle.PitDuration);
        if (pitDurationMs != null && car.PitDurationMs != pitDurationMs)
            patch.PitDurationMs = pitDurationMs;

        if (car.PitSpeedEnforced != vehicle.Enforced)
            patch.PitSpeedEnforced = vehicle.Enforced;

        if (car.SpeedViolation != vehicle.SpeedViolation)
            patch.SpeedViolation = vehicle.SpeedViolation;

        if (vehicle.FlaggingZone != null && car.FlaggingZone != vehicle.FlaggingZone)
            patch.FlaggingZone = vehicle.FlaggingZone;

        // Track laps that included a pit stop, mirroring the X2 loop behavior
        if (vehicle.PitActive)
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
