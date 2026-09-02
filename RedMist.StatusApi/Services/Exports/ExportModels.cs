namespace RedMist.StatusApi.Services.Exports;

/// <summary>
/// Tells a client which exports are worth offering for a session before it asks for one. The
/// export endpoints themselves are expensive, so the UI asks this first and hides the options it
/// would only get an error from.
/// </summary>
public class ExportAvailability
{
    /// <summary>
    /// Whether the session has ended: either it has an end time, or it is no longer flagged live.
    /// Either on its own is unreliable - the flag is set independently by the session monitor and
    /// stays true if the monitor dies, while a session orphaned before it could be finalized never
    /// gets an end time - so both count. Exports are only produced for sessions that have ended: one
    /// still running has lap rows arriving, so an export of it would be a partial snapshot that looks
    /// like a complete record.
    /// </summary>
    public bool SessionCompleted { get; set; }

    /// <summary>
    /// Whether any completed lap rows exist for the session.
    /// </summary>
    public bool LapDataAvailable { get; set; }

    /// <summary>
    /// Whether the pit stop / driver change report can be built for this session. This requires
    /// Flagtronics data: both a driver identification and a pit entry time, neither of which any
    /// other timing source supplies.
    /// </summary>
    public bool PitReportAvailable { get; set; }

    /// <summary>
    /// Distinct car numbers that have lap data in the session, in natural (human) order.
    /// </summary>
    public List<string> CarNumbers { get; set; } = [];
}

/// <summary>
/// The output formats an export can be rendered in.
/// </summary>
public enum ExportFormat
{
    /// <summary>Raw JSON, streamed straight from the stored lap rows.</summary>
    Json,
    /// <summary>Comma separated values, RFC 4180 quoted, for Excel.</summary>
    Csv,
    /// <summary>A paginated PDF report.</summary>
    Pdf
}

/// <summary>
/// Format parsing shared by the export endpoints.
/// </summary>
public static class ExportFormats
{
    /// <summary>
    /// Parses the <c>format</c> query value. An absent or empty value means JSON, which keeps the
    /// query string short for the common case.
    /// </summary>
    /// <param name="value">The raw query value.</param>
    /// <param name="format">The parsed format.</param>
    /// <returns>True when the value was recognized.</returns>
    public static bool TryParse(string? value, out ExportFormat format)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "json":
                format = ExportFormat.Json;
                return true;
            case "csv":
                format = ExportFormat.Csv;
                return true;
            case "pdf":
                format = ExportFormat.Pdf;
                return true;
            default:
                format = ExportFormat.Json;
                return false;
        }
    }

    /// <summary>Gets the MIME type for a format.</summary>
    public static string ContentType(ExportFormat format) => format switch
    {
        ExportFormat.Csv => "text/csv",
        ExportFormat.Pdf => "application/pdf",
        _ => "application/json"
    };

    /// <summary>Gets the file extension, including the leading dot, for a format.</summary>
    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Csv => ".csv",
        ExportFormat.Pdf => ".pdf",
        _ => ".json"
    };
}

/// <summary>
/// One lap as it appears in a CSV or PDF export. This is a deliberately narrow projection of
/// <see cref="RedMist.TimingCommon.Models.CarPosition"/>: an export can cover a whole session, so
/// the row that is held while the file is written has to stay small.
/// </summary>
public sealed class LapExportRow
{
    public string CarNumber { get; set; } = string.Empty;
    public int LapNumber { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Flag { get; set; } = string.Empty;
    public string? Class { get; set; }
    public string? LapTime { get; set; }
    public string? TotalTime { get; set; }
    public string? BestTime { get; set; }
    public int OverallPosition { get; set; }
    public int ClassPosition { get; set; }
    public string? OverallGap { get; set; }
    public string? OverallDifference { get; set; }
    public string? InClassGap { get; set; }
    public string? InClassDifference { get; set; }
    public bool LapIncludedPit { get; set; }
    public int? PitStopCount { get; set; }
    public string? DriverName { get; set; }
    public string? DriverSource { get; set; }
}

/// <summary>
/// One derived pit stop for one car, with the driver change that went with it.
/// </summary>
/// <remarks>
/// Nothing in the timing feed states "this was a pit stop from A to B" - this record is inferred
/// from the lap rows either side of the stop. See <see cref="PitStopAnalyzer"/> for the rules and
/// the cases where they get it wrong.
/// </remarks>
public sealed class PitStopRecord
{
    /// <summary>The car that made the stop.</summary>
    public string CarNumber { get; set; } = string.Empty;

    /// <summary>1-based sequence number of this stop within the session for this car.</summary>
    public int StopNumber { get; set; }

    /// <summary>The lap number on which the stop was detected, i.e. the first lap the car completed after pitting.</summary>
    public int Lap { get; set; }

    /// <summary>When the car crossed the pit entry line, if the feed reported it.</summary>
    public DateTime? EntryTimeUtc { get; set; }

    /// <summary>When the car left the pits. Derived from entry time plus duration, so it is null whenever either of those is.</summary>
    public DateTime? ExitTimeUtc { get; set; }

    /// <summary>How long the car was in the pits, in milliseconds, if the feed finalized a duration for the stop.</summary>
    public int? DurationMs { get; set; }

    /// <summary>The driver identified on the laps before the stop, or empty when none was ever reported.</summary>
    public string DriverBefore { get; set; } = string.Empty;

    /// <summary>The driver identified on the laps after the stop, or empty when none was ever reported.</summary>
    public string DriverAfter { get; set; } = string.Empty;

    /// <summary>
    /// Whether the driver actually changed. False covers both a genuine same-driver stop (a splash
    /// and go) and a stop where the driver feed simply never moved, which look identical here.
    /// </summary>
    public bool DriverChanged { get; set; }
}
