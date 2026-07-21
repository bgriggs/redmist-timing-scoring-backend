using System.Text.Json.Serialization;

namespace RedMist.EventProcessor.EventStatus.Flagtronics;

/// <summary>
/// Per-car record from the Flagtronics Vehicle Info feed (Feed Standard v3.0), relayed
/// verbatim by the relay. Only the fields consumed by the processor are declared; unknown
/// fields in the payload are ignored.
/// </summary>
public class FlagtronicsVehicle
{
    /// <summary>
    /// Bad GPS sentinel for <see cref="Speed"/>.
    /// </summary>
    public const int SPEED_BAD_GPS = 255;
    /// <summary>
    /// Stopped sentinel for <see cref="Speed"/>.
    /// </summary>
    public const int SPEED_STOPPED = 254;

    [JsonPropertyName("carNumber")]
    public string CarNumber { get; set; } = string.Empty;

    [JsonPropertyName("ft200DeviceId")]
    public long Ft200DeviceId { get; set; }

    /// <summary>
    /// MPH. Special values: 255 = bad GPS, 254 = stopped. Null when no device reports.
    /// </summary>
    [JsonPropertyName("speed")]
    public int? Speed { get; set; }

    [JsonPropertyName("lat")]
    public double? Lat { get; set; }

    [JsonPropertyName("lon")]
    public double? Lon { get; set; }

    /// <summary>
    /// Flag currently displayed to this car on the in-car device.
    /// </summary>
    [JsonPropertyName("carFlag")]
    public string? CarFlag { get; set; }

    /// <summary>
    /// Full-course flag state.
    /// </summary>
    [JsonPropertyName("fullCourseFlag")]
    public string? FullCourseFlag { get; set; }

    /// <summary>
    /// 1-127 on-track, 0 = uninitialized, 128+ = pit/paddock/reserved ranges.
    /// </summary>
    [JsonPropertyName("flaggingZone")]
    public int? FlaggingZone { get; set; }

    /// <summary>
    /// blePuck | rfidHelmet | manualOverride | none. Pre-v3.0 stations use BleDrid,
    /// HelmetDrid, Manual, None.
    /// </summary>
    [JsonPropertyName("driverSource")]
    public string? DriverSource { get; set; }

    [JsonPropertyName("pitEntryTime")]
    public DateTime? PitEntryTime { get; set; }

    /// <summary>
    /// hh:mm:ss.fff. While pitActive is true, this is the time in pit so far.
    /// </summary>
    [JsonPropertyName("pitDuration")]
    public string? PitDuration { get; set; }

    [JsonPropertyName("pitActive")]
    public bool PitActive { get; set; }

    /// <summary>
    /// True while the car is in a speed-enforced pit zone.
    /// </summary>
    [JsonPropertyName("enforced")]
    public bool Enforced { get; set; }

    [JsonPropertyName("speedViolation")]
    public bool SpeedViolation { get; set; }
}
