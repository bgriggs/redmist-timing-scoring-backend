using QuestPDF.Fluent;
using System.Text;
using System.Text.Json;

namespace RedMist.StatusApi.Services.Exports;

/// <summary>
/// Renders derived pit stops to JSON, CSV or PDF.
/// </summary>
/// <remarks>
/// The records these methods write are inferred, not recorded - see <see cref="PitStopAnalyzer"/>
/// for the rules. Every format therefore carries the same nullable fields rather than filling in a
/// plausible value: an empty exit time means the feed never closed the stop, and that is worth
/// showing to whoever reads the report.
/// </remarks>
public static class PitStopReportWriter
{
    /// <summary>Column headers for the CSV export, in order.</summary>
    public const string CsvHeader =
        "CarNumber,StopNumber,StartLap,EndLap,PitEntryTime,PitExitTime,PitDuration,PitDurationMs,DriverBefore,DriverAfter,DriverChanged";

    /// <summary>Marker line appended to a CSV that hit the row cap or the scan cap.</summary>
    public const string CsvTruncationMarker = "# TRUNCATED";

    /// <summary>
    /// Marker line appended to a CSV built from lap rows that could not all be read. This matters
    /// more here than in a lap export: a missing row does not leave a hole in the output, it changes
    /// what the derivation concluded.
    /// </summary>
    public const string CsvSkippedMarker = "# SKIPPED";

    /// <summary>
    /// Marker line appended to a CSV taken from a session that was still flagged live, and so may not
    /// be the complete record.
    /// </summary>
    public const string CsvStillLiveMarker = "# SESSION STILL LIVE";

    /// <summary>
    /// Marker line appended when some stops were found from an entry timestamp too implausible to
    /// print. Their durations are still reported; only the clock times are withheld.
    /// </summary>
    public const string CsvNoEntryTimeMarker = "# NO ENTRY TIME";

    /// <summary>
    /// Marker line appended when a stop absorbed a change of pit entry time partway through it.
    /// </summary>
    public const string CsvRenumberedMarker = "# ENTRY TIME RENUMBERED";

    /// <summary>
    /// Writes the stops as JSON inside an envelope that says what the report covers.
    /// </summary>
    /// <param name="output">Stream to write to.</param>
    /// <param name="stops">The derived stops, already ordered and capped by the caller.</param>
    /// <param name="context">What the export covers.</param>
    /// <param name="truncated">Whether the caller stopped short of the full session.</param>
    /// <param name="maxBytes">Byte cap on the file; the export stops and marks itself truncated when the file reaches it.</param>
    /// <param name="diagnostics">Unreadable rows and whether the lap scan hit its cap.</param>
    /// <param name="cancellationToken">Canceled when the client disconnects.</param>
    /// <returns>What was written.</returns>
    public static async Task<ExportWriteResult> WriteJsonAsync(Stream output, IReadOnlyList<PitStopRecord> stops,
        ExportContext context, bool truncated, long maxBytes, ExportScanDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        var result = new ExportWriteResult { Truncated = truncated };
        await using var writer = new Utf8JsonWriter(output, ExportJson.WriterOptions);

        writer.WriteStartObject();
        writer.WriteNumber("eventId", context.EventId);
        writer.WriteString("eventName", context.EventName);
        writer.WriteNumber("sessionId", context.SessionId);
        writer.WriteString("sessionName", context.SessionName);
        writer.WriteString("generated", context.ToTrackTime(context.GeneratedUtc) ?? DateTimeOffset.UtcNow);
        writer.WriteString("trackTimeZone", context.TimeZoneLabel);
        writer.WriteBoolean("trackTimeZoneKnown", context.HasTrackOffset);
        writer.WriteBoolean("sessionStillLive", context.SessionStillLive);
        writer.WriteStartArray("pitStops");

        foreach (var stop in stops)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteString("carNumber", stop.CarNumber);
            writer.WriteNumber("stopNumber", stop.StopNumber);
            writer.WriteNumber("startLap", stop.StartLap);
            writer.WriteNumber("endLap", stop.EndLap);
            WriteNullableTime(writer, "pitEntryTime", context.ToTrackTime(stop.EntryTime));
            WriteNullableTime(writer, "pitExitTime", context.ToTrackTime(stop.ExitTime));
            writer.WriteBoolean("pitEntryTimeUnavailable", stop.EntryTimeUnavailable);
            writer.WriteBoolean("pitEntryTimeRenumbered", stop.EntryTimeRenumbered);
            if (stop.DurationMs.HasValue)
                writer.WriteNumber("pitDurationMs", stop.DurationMs.Value);
            else
                writer.WriteNull("pitDurationMs");
            writer.WriteString("pitDuration", CsvFormat.Duration(stop.DurationMs));
            writer.WriteString("driverBefore", stop.DriverBefore);
            writer.WriteString("driverAfter", stop.DriverAfter);
            writer.WriteBoolean("driverChanged", stop.DriverChanged);
            writer.WriteEndObject();
            result.RowsWritten++;

            if (writer.BytesPending > 64 * 1024)
            {
                await writer.FlushAsync(cancellationToken);
                if (ExportBudget.Exceeded(output, maxBytes))
                {
                    result.Truncated = true;
                    break;
                }
            }
        }

        // Folded in before the envelope is closed: the scan cap is only known once the rows are
        // drained, and it is exactly the case where the file would otherwise claim to be complete
        // while whole cars are missing from it. Which cars is not obvious either - the scan cuts in
        // the timing system's own text ordering and the report is sorted afterwards, so the absent
        // cars are scattered through the numbering rather than being the tail.
        diagnostics.ApplyTo(result);

        writer.WriteEndArray();
        writer.WriteNumber("pitStopCount", result.RowsWritten);
        // Over the rows written rather than the buffer, so these can never exceed pitStopCount.
        var written = stops.Take(result.RowsWritten).ToList();
        writer.WriteNumber("stopsWithoutEntryTime", written.Count(x => x.EntryTimeUnavailable));
        writer.WriteNumber("stopsWithRenumberedEntryTime", written.Count(x => x.EntryTimeRenumbered));
        writer.WriteNumber("skippedLapRows", result.SkippedRows);
        writer.WriteBoolean("truncated", result.Truncated);
        if (result.Truncated && context.TruncatedAfterCarNumber is { } lastCar)
        {
            writer.WriteString("truncatedAfterCarNumber", lastCar);
            writer.WriteString("truncationNote",
                "The scan stopped at its limit after car " + lastCar + " in the timing system's own " +
                "ordering, which sorts car numbers as text. This report is sorted numerically, so the " +
                "cars it is missing are scattered through the numbering rather than being the last ones here.");
        }
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Writes the stops as CSV for Excel.
    /// </summary>
    /// <param name="output">Stream to write to.</param>
    /// <param name="stops">The derived stops, already ordered and capped by the caller.</param>
    /// <param name="context">What the export covers; supplies the time zone and still-live markers.</param>
    /// <param name="truncated">Whether the caller stopped short of the full session.</param>
    /// <param name="maxBytes">Byte cap on the file; the export stops and appends a truncation marker when the file reaches it.</param>
    /// <param name="diagnostics">Unreadable rows and whether the lap scan hit its cap.</param>
    /// <param name="cancellationToken">Canceled when the client disconnects.</param>
    /// <returns>What was written.</returns>
    public static async Task<ExportWriteResult> WriteCsvAsync(Stream output, IReadOnlyList<PitStopRecord> stops,
        ExportContext context, bool truncated, long maxBytes, ExportScanDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        var result = new ExportWriteResult { Truncated = truncated };

        // Byte order mark for the same reason as the lap CSV: Excel needs it to read UTF-8, and
        // driver names are exactly the column where non-ASCII shows up.
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            bufferSize: 32 * 1024, leaveOpen: true);

        await writer.WriteLineAsync(CsvHeader);

        var line = new StringBuilder(256);
        foreach (var stop in stops)
        {
            cancellationToken.ThrowIfCancellationRequested();
            line.Clear();
            // Car number and the two driver names are free text from a race entry form; the rest are
            // values this code formatted itself.
            CsvFormat.AppendTextField(line, stop.CarNumber);
            CsvFormat.AppendField(line, CsvFormat.Number(stop.StopNumber));
            CsvFormat.AppendField(line, CsvFormat.Number(stop.StartLap));
            CsvFormat.AppendField(line, CsvFormat.Number(stop.EndLap));
            CsvFormat.AppendField(line, CsvFormat.Timestamp(context.ToTrackTime(stop.EntryTime)));
            CsvFormat.AppendField(line, CsvFormat.Timestamp(context.ToTrackTime(stop.ExitTime)));
            CsvFormat.AppendField(line, CsvFormat.Duration(stop.DurationMs));
            CsvFormat.AppendField(line, CsvFormat.Number(stop.DurationMs));
            CsvFormat.AppendTextField(line, stop.DriverBefore);
            CsvFormat.AppendTextField(line, stop.DriverAfter);
            CsvFormat.AppendField(line, stop.DriverChanged ? "true" : "false", last: true);

            await writer.WriteLineAsync(line, cancellationToken);
            result.RowsWritten++;

            if (result.RowsWritten % ExportBudget.CheckInterval == 0)
            {
                await writer.FlushAsync(cancellationToken);
                if (ExportBudget.Exceeded(output, maxBytes))
                {
                    result.Truncated = true;
                    break;
                }
            }
        }

        diagnostics.ApplyTo(result);

        // Trailing, with the other comments, so the file stays a clean CSV for anything that parses
        // it. LapExportWriter owns the wording; both reports say the same thing about the same zone.
        await writer.WriteLineAsync(LapExportWriter.TimeZoneComment(context));

        if (result.Truncated)
        {
            var after = context.TruncatedAfterCarNumber is { } lastCar
                ? $" after car {lastCar} in the timing system's own text ordering, so the missing cars are " +
                  "scattered through the numbering rather than being the last ones listed here"
                : string.Empty;
            await writer.WriteLineAsync(
                $"{CsvTruncationMarker} - export limit reached after {result.RowsWritten} stops{after}; " +
                "this file does not cover the whole session");
        }

        if (result.SkippedRows > 0)
        {
            await writer.WriteLineAsync(
                $"{CsvSkippedMarker} - {result.SkippedRows} lap row(s) could not be read; stops and driver changes around them may be wrong or missing");
        }

        if (context.SessionStillLive)
        {
            await writer.WriteLineAsync(
                $"{CsvStillLiveMarker} - the session was still flagged live when this file was produced, " +
                "so later stops may be missing");
        }

        // Counted over the rows actually written, not over the buffer: a byte cap that stops the
        // file early would otherwise leave the file claiming more affected stops than it contains.
        var written = stops.Take(result.RowsWritten).ToList();

        var withoutEntryTime = written.Count(x => x.EntryTimeUnavailable);
        if (withoutEntryTime > 0)
        {
            await writer.WriteLineAsync(
                $"{CsvNoEntryTimeMarker} - {withoutEntryTime} stop(s) came from in-car equipment whose clock " +
                "was not set, so their entry and exit times are blank; their durations are unaffected");
        }

        var renumbered = written.Count(x => x.EntryTimeRenumbered);
        if (renumbered > 0)
        {
            await writer.WriteLineAsync(
                $"{CsvRenumberedMarker} - {renumbered} stop(s) had their pit entry time reissued by the " +
                "equipment partway through the stop; the entry shown is the earliest seen and the duration " +
                "the longest");
        }

        await writer.FlushAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Writes the stops as a paginated PDF report.
    /// </summary>
    /// <param name="output">Stream to write to.</param>
    /// <param name="stops">The derived stops. Already capped by the caller; a PDF's layout needs them all up front.</param>
    /// <param name="context">What the export covers.</param>
    /// <param name="truncated">Whether the caller stopped short of the full session.</param>
    /// <param name="diagnostics">Unreadable rows and whether the lap scan hit its cap; printed in the header block.</param>
    /// <param name="cancellationToken">Canceled when the client disconnects.</param>
    /// <returns>What was written.</returns>
    public static Task<ExportWriteResult> WritePdfAsync(Stream output, IReadOnlyList<PitStopRecord> stops,
        ExportContext context, bool truncated, ExportScanDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = diagnostics.ApplyTo(new ExportWriteResult { RowsWritten = stops.Count, Truncated = truncated });

        var changes = stops.Count(s => s.DriverChanged);
        var subtitle = $"Pit stops and driver changes - {stops.Count} stops, {changes} driver changes";

        var notes = new List<string>();
        if (result.Truncated)
        {
            var after = context.TruncatedAfterCarNumber is { } lastCar
                ? $" The scan stopped after car {lastCar} in the timing system's own text ordering, so the " +
                  "missing cars are scattered through the numbering rather than being the last ones listed."
                : string.Empty;
            notes.Add("Truncated: this report reached the export limit and does not cover the whole session." + after);
        }
        if (result.SkippedRows > 0)
        {
            notes.Add($"{result.SkippedRows} lap row(s) could not be read; stops and driver changes around them " +
                      "may be wrong or missing.");
        }
        if (context.SessionStillLive)
        {
            notes.Add("This session was still flagged live when the report was produced, so later stops may " +
                      "be missing.");
        }
        if (!context.HasTrackOffset)
        {
            notes.Add("Times are UTC: this session carried no track time zone, so they are not track " +
                      "local time.");
        }
        var withoutEntryTime = stops.Count(x => x.EntryTimeUnavailable);
        if (withoutEntryTime > 0)
        {
            notes.Add($"{withoutEntryTime} stop(s) came from in-car equipment whose clock was not set, so " +
                      "their entry and exit times are blank. Their durations are unaffected.");
        }
        var renumbered = stops.Count(x => x.EntryTimeRenumbered);
        if (renumbered > 0)
        {
            notes.Add($"{renumbered} stop(s) had their pit entry time reissued by the equipment partway " +
                      "through the stop. The entry shown is the earliest seen and the duration the longest.");
        }

        ExportPdf.Build(context, subtitle, notes, table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(40);   // Car
                columns.ConstantColumn(34);   // Stop
                columns.ConstantColumn(52);   // Laps - wide enough for a three digit span like 114-116
                columns.ConstantColumn(100);  // Entry
                columns.ConstantColumn(100);  // Exit
                columns.ConstantColumn(62);   // Duration
                columns.RelativeColumn();     // Driver before
                columns.RelativeColumn();     // Driver after
                columns.ConstantColumn(54);   // Changed
            });

            ExportPdf.HeaderRow(table, "Car", "Stop", "Laps", $"Pit Entry ({context.TimeZoneLabel})",
                $"Pit Exit ({context.TimeZoneLabel})", "Duration", "Driver Before", "Driver After", "Changed");

            foreach (var stop in stops)
            {
                // QuestPDF composes the document twice to resolve the page count, so this is where a
                // disconnected client's report actually stops costing the pod its CPU budget.
                cancellationToken.ThrowIfCancellationRequested();

                ExportPdf.BodyRow(table,
                    stop.CarNumber,
                    stop.StopNumber.ToString(),
                    LapSpan(stop),
                    CsvFormat.Timestamp(context.ToTrackTime(stop.EntryTime)),
                    CsvFormat.Timestamp(context.ToTrackTime(stop.ExitTime)),
                    CsvFormat.Duration(stop.DurationMs),
                    stop.DriverBefore,
                    stop.DriverAfter,
                    stop.DriverChanged ? "Yes" : string.Empty);
            }
        }).GeneratePdf(output);

        return Task.FromResult(result);
    }

    /// <summary>
    /// Renders the laps a stop covered: one number for an ordinary stop, a range for one that spanned
    /// the start/finish line.
    /// </summary>
    private static string LapSpan(PitStopRecord stop) =>
        stop.EndLap > stop.StartLap ? $"{stop.StartLap}-{stop.EndLap}" : stop.StartLap.ToString();

    /// <summary>
    /// Writes a track local timestamp, or an explicit null. ISO 8601 carrying the offset, so the
    /// value is both machine-parseable and unambiguous about the zone it is in.
    /// </summary>
    private static void WriteNullableTime(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value.HasValue)
            writer.WriteString(name, value.Value);
        else
            writer.WriteNull(name);
    }
}
