using System.Globalization;

namespace RedMist.StatusApi.Services.Exports;

/// <summary>
/// Identifies what an export covers. Carried into the writers so the header of a PDF, the envelope
/// of a JSON file and the download's file name all describe the same thing.
/// </summary>
public sealed class ExportContext
{
    /// <summary>The event the export covers.</summary>
    public int EventId { get; set; }

    /// <summary>The event's name, or empty when the event hides its name.</summary>
    public string EventName { get; set; } = string.Empty;

    /// <summary>The session the export covers.</summary>
    public int SessionId { get; set; }

    /// <summary>The session's name.</summary>
    public string SessionName { get; set; } = string.Empty;

    /// <summary>The single car the export covers, or null for every car in the session.</summary>
    public string? CarNumber { get; set; }

    /// <summary>When the export was produced, in UTC.</summary>
    public DateTime GeneratedUtc { get; set; }

    /// <summary>
    /// The track offset from UTC, or null when the session did not carry a usable one.
    /// </summary>
    /// <remarks>
    /// Everything a person reads in an export is a time of day at a race track - when a car crossed
    /// the line, when it entered the pits - and the only useful frame for that is the clock the
    /// people at the track were looking at. Stored timestamps are UTC, so the offset is what turns
    /// them back into that.
    /// </remarks>
    public TimeSpan? TrackOffset { get; set; }

    /// <summary>Whether the export could put its times into track local time.</summary>
    public bool HasTrackOffset => TrackOffset.HasValue;

    /// <summary>
    /// How the export describes the zone its times are in, for the header, the envelope and the CSV
    /// comment block.
    /// </summary>
    public string TimeZoneLabel => TrackOffset is { } offset
        ? string.Create(CultureInfo.InvariantCulture,
              $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration().Hours:00}:{offset.Duration().Minutes:00}")
        : "UTC";

    /// <summary>
    /// Converts a stored UTC timestamp into track local time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stored value is UTC but often carries <see cref="DateTimeKind.Unspecified"/>, because the
    /// database layer runs with Npgsql legacy timestamp behavior. The kind is therefore stated here
    /// rather than trusted, so the conversion cannot silently take the server local zone as the
    /// starting point.
    /// </para>
    /// <para>
    /// A timestamp that is not believable comes back as null rather than as a converted date. Two
    /// things arrive here that are not times: a device clock that was never set, which reports year
    /// 0001, and a row whose timestamp was never populated, which is the same value. Neither should
    /// be printed, and shifting either by a negative offset walks off the end of
    /// <see cref="DateTime"/> and throws - which, in an export, would cost the whole file over one
    /// bad row.
    /// </para>
    /// </remarks>
    /// <param name="utc">The stored timestamp, or null.</param>
    /// <returns>The same instant at the track offset, or null when there is no usable time.</returns>
    public DateTimeOffset? ToTrackTime(DateTime? utc)
    {
        if (!CsvFormat.IsPlausible(utc))
            return null;

        // The shift itself has to be range-checked. ToOffset adds the offset to the instant and
        // throws if the result leaves DateTime, and these values come out of a payload another
        // service wrote - a year-9999 timestamp with a positive offset is all it takes. Returning
        // null costs one blank cell; throwing here would cost the whole export.
        var offset = TrackOffset ?? TimeSpan.Zero;
        if (offset > TimeSpan.Zero && utc!.Value > DateTime.MaxValue - offset)
            return null;
        if (offset < TimeSpan.Zero && utc!.Value < DateTime.MinValue - offset)
            return null;

        var instant = new DateTimeOffset(DateTime.SpecifyKind(utc!.Value, DateTimeKind.Utc));
        return instant.ToOffset(offset);
    }

    /// <summary>
    /// Whether the session was still flagged live when this file was produced, despite having an end
    /// time.
    /// </summary>
    /// <remarks>
    /// Such a session ended at least once and may then have been picked up again, so it can still be
    /// gaining laps - which would make this file a partial record of it. It is exported anyway,
    /// because the far more common cause is a session monitor that died and left the flag stuck on a
    /// session that really did finish, and refusing those makes the feature useless for them. What
    /// the export cannot do is stay quiet about it: every format says so on the file, so that
    /// somebody opening it a month later can tell.
    /// </remarks>
    public bool SessionStillLive { get; set; }

    /// <summary>
    /// The last car number the scan reached before it hit its cap, or null when it did not.
    /// </summary>
    /// <remarks>
    /// Set for the reports that sort their rows - the pit report and the lap PDF. The scan reads in
    /// the database order, which sorts car numbers as text, and the report is put into human order
    /// afterwards. So a truncated report is not missing its tail: the cars it lost are scattered
    /// through the numbering, and naming where the scan stopped is the only thing that lets a reader
    /// work out what is absent.
    /// </remarks>
    public string? TruncatedAfterCarNumber { get; set; }

    /// <summary>
    /// The sentence a truncated report adds to explain which rows are missing, or empty when the
    /// scan boundary is not known.
    /// </summary>
    /// <remarks>
    /// Shared by every format so they cannot drift into describing the same truncation differently.
    /// </remarks>
    public string TruncationScanCaveat => TruncatedAfterCarNumber is { } lastCar
        ? $"The scan stopped after car {lastCar} in the timing system's own text ordering, so the " +
          "missing cars are scattered through the numbering rather than being the last ones listed here."
        : string.Empty;
}

/// <summary>
/// What a writer actually produced, so the caller can log it and the file can say so.
/// </summary>
public sealed class ExportWriteResult
{
    /// <summary>Rows written to the file.</summary>
    public int RowsWritten { get; set; }

    /// <summary>Whether the row cap was hit and the export stops short of the full session.</summary>
    public bool Truncated { get; set; }

    /// <summary>
    /// Rows that could not be read and were left out. Stored lap JSON was written by whichever
    /// version of the event processor was running at the time, so a row that no longer parses is a
    /// possibility the export has to survive rather than fail on.
    /// </summary>
    public int SkippedRows { get; set; }
}
