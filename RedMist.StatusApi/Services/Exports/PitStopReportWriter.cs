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
        "CarNumber,StopNumber,Lap,PitEntryTimeUtc,PitExitTimeUtc,PitDuration,PitDurationMs,DriverBefore,DriverAfter,DriverChanged";

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
    /// Writes the stops as JSON inside an envelope that says what the report covers.
    /// </summary>
    /// <param name="output">Stream to write to.</param>
    /// <param name="stops">The derived stops, in export order.</param>
    /// <param name="context">What the export covers.</param>
    /// <param name="maxRows">Row cap; the export stops and marks itself truncated at this many rows.</param>
    /// <param name="maxBytes">Byte cap on the file; the export stops and marks itself truncated when the file reaches it.</param>
    /// <param name="diagnostics">Unreadable rows and whether the lap scan hit its cap.</param>
    /// <param name="cancellationToken">Canceled when the client disconnects.</param>
    /// <returns>What was written.</returns>
    public static async Task<ExportWriteResult> WriteJsonAsync(Stream output, IAsyncEnumerable<PitStopRecord> stops,
        ExportContext context, int maxRows, long maxBytes, ExportScanDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        var result = new ExportWriteResult();
        await using var writer = new Utf8JsonWriter(output, ExportJson.WriterOptions);

        writer.WriteStartObject();
        writer.WriteNumber("eventId", context.EventId);
        writer.WriteString("eventName", context.EventName);
        writer.WriteNumber("sessionId", context.SessionId);
        writer.WriteString("sessionName", context.SessionName);
        writer.WriteString("generatedUtc", context.GeneratedUtc);
        writer.WriteBoolean("sessionStillLive", context.SessionStillLive);
        writer.WriteStartArray("pitStops");

        await foreach (var stop in stops.WithCancellation(cancellationToken))
        {
            if (result.RowsWritten >= maxRows)
            {
                result.Truncated = true;
                break;
            }

            writer.WriteStartObject();
            writer.WriteString("carNumber", stop.CarNumber);
            writer.WriteNumber("stopNumber", stop.StopNumber);
            writer.WriteNumber("lap", stop.Lap);
            WriteNullableDateTime(writer, "pitEntryTimeUtc", stop.EntryTimeUtc);
            WriteNullableDateTime(writer, "pitExitTimeUtc", stop.ExitTimeUtc);
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
        // while whole cars - the ones sorting late by car number - are missing from it.
        diagnostics.ApplyTo(result);

        writer.WriteEndArray();
        writer.WriteNumber("pitStopCount", result.RowsWritten);
        writer.WriteNumber("skippedLapRows", result.SkippedRows);
        writer.WriteBoolean("truncated", result.Truncated);
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Writes the stops as CSV for Excel.
    /// </summary>
    /// <param name="output">Stream to write to.</param>
    /// <param name="stops">The derived stops, in export order.</param>
    /// <param name="context">What the export covers; supplies the still-live marker.</param>
    /// <param name="maxRows">Row cap; the export stops and appends a truncation marker at this many rows.</param>
    /// <param name="maxBytes">Byte cap on the file; the export stops and appends a truncation marker when the file reaches it.</param>
    /// <param name="diagnostics">Unreadable rows and whether the lap scan hit its cap.</param>
    /// <param name="cancellationToken">Canceled when the client disconnects.</param>
    /// <returns>What was written.</returns>
    public static async Task<ExportWriteResult> WriteCsvAsync(Stream output, IAsyncEnumerable<PitStopRecord> stops,
        ExportContext context, int maxRows, long maxBytes, ExportScanDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        var result = new ExportWriteResult();

        // Byte order mark for the same reason as the lap CSV: Excel needs it to read UTF-8, and
        // driver names are exactly the column where non-ASCII shows up.
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            bufferSize: 32 * 1024, leaveOpen: true);

        await writer.WriteLineAsync(CsvHeader);

        var line = new StringBuilder(256);
        await foreach (var stop in stops.WithCancellation(cancellationToken))
        {
            if (result.RowsWritten >= maxRows)
            {
                result.Truncated = true;
                break;
            }

            line.Clear();
            // Car number and the two driver names are free text from a race entry form; the rest are
            // values this code formatted itself.
            CsvFormat.AppendTextField(line, stop.CarNumber);
            CsvFormat.AppendField(line, CsvFormat.Number(stop.StopNumber));
            CsvFormat.AppendField(line, CsvFormat.Number(stop.Lap));
            CsvFormat.AppendField(line, CsvFormat.Timestamp(stop.EntryTimeUtc));
            CsvFormat.AppendField(line, CsvFormat.Timestamp(stop.ExitTimeUtc));
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

        if (result.Truncated)
        {
            await writer.WriteLineAsync(
                $"{CsvTruncationMarker} - export limit reached after {result.RowsWritten} stops; this file does not cover the whole session");
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
            notes.Add("Truncated: this report reached the export limit and does not cover the whole session.");
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

        ExportPdf.Build(context, subtitle, notes, table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(40);   // Car
                columns.ConstantColumn(34);   // Stop
                columns.ConstantColumn(34);   // Lap
                columns.ConstantColumn(100);  // Entry
                columns.ConstantColumn(100);  // Exit
                columns.ConstantColumn(62);   // Duration
                columns.RelativeColumn();     // Driver before
                columns.RelativeColumn();     // Driver after
                columns.ConstantColumn(54);   // Changed
            });

            ExportPdf.HeaderRow(table, "Car", "Stop", "Lap", "Pit Entry (UTC)", "Pit Exit (UTC)", "Duration",
                "Driver Before", "Driver After", "Changed");

            foreach (var stop in stops)
            {
                // QuestPDF composes the document twice to resolve the page count, so this is where a
                // disconnected client's report actually stops costing the pod its CPU budget.
                cancellationToken.ThrowIfCancellationRequested();

                ExportPdf.BodyRow(table,
                    stop.CarNumber,
                    stop.StopNumber.ToString(),
                    stop.Lap.ToString(),
                    CsvFormat.Timestamp(stop.EntryTimeUtc),
                    CsvFormat.Timestamp(stop.ExitTimeUtc),
                    CsvFormat.Duration(stop.DurationMs),
                    stop.DriverBefore,
                    stop.DriverAfter,
                    stop.DriverChanged ? "Yes" : string.Empty);
            }
        }).GeneratePdf(output);

        return Task.FromResult(result);
    }

    private static void WriteNullableDateTime(Utf8JsonWriter writer, string name, DateTime? value)
    {
        if (value.HasValue)
            writer.WriteString(name, value.Value);
        else
            writer.WriteNull(name);
    }
}
