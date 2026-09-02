using QuestPDF.Fluent;
using System.Text;
using System.Text.Json;

namespace RedMist.StatusApi.Services.Exports;

/// <summary>
/// Renders a session's lap rows to JSON, CSV or PDF.
/// </summary>
/// <remarks>
/// Every method here writes to the stream it is given as rows arrive; none of them builds the whole
/// document first. The one exception is <see cref="WritePdfAsync"/>, which cannot stream because a
/// PDF's page breaks are only known once the layout has seen every row - which is exactly why the
/// PDF row cap is an order of magnitude tighter than the others.
/// </remarks>
public static class LapExportWriter
{
    /// <summary>Column headers for the CSV export, in order.</summary>
    public const string CsvHeader =
        "CarNumber,Lap,Time,Flag,Class,LapTime,TotalTime,BestTime,OverallPosition,ClassPosition," +
        "OverallGap,OverallDifference,InClassGap,InClassDifference,LapIncludedPit,PitStopCount,DriverName,DriverSource";

    /// <summary>
    /// Marker line appended to a CSV that hit the row cap. It is a comment rather than a header
    /// change so a reader that ignores it still gets a well-formed file, and a person opening it in
    /// Excel still sees, in the last row, that there was more.
    /// </summary>
    public const string CsvTruncationMarker = "# TRUNCATED";

    /// <summary>
    /// Marker line appended to a CSV that could not read some of the session's rows. Separate from
    /// truncation because they mean different things: truncation is a tail that is missing, skipped
    /// rows are holes in the middle.
    /// </summary>
    public const string CsvSkippedMarker = "# SKIPPED";

    /// <summary>
    /// Marker line appended to a CSV taken from a session that was still flagged live. It may not be
    /// the complete record, and unlike truncation nothing about the file's contents would reveal that.
    /// </summary>
    public const string CsvStillLiveMarker = "# SESSION STILL LIVE";

    /// <summary>Prefix of the comment line naming the zone the times in the file are in.</summary>
    public const string CsvTimeZoneMarker = "# TIMES";

    /// <summary>
    /// The comment line naming the zone the file's times are in.
    /// </summary>
    /// <remarks>
    /// Always written, even when the offset is known, because a bare clock time in a file that
    /// outlives the weekend is ambiguous otherwise. When the session carried no usable offset the
    /// times fall back to UTC, and saying so is the whole point - silently handing somebody UTC
    /// labeled as nothing is how a lap time gets read four hours out.
    /// </remarks>
    /// <param name="context">What the export covers.</param>
    /// <returns>The comment line.</returns>
    public static string TimeZoneComment(ExportContext context) => context.HasTrackOffset
        ? $"{CsvTimeZoneMarker} are track local time ({context.TimeZoneLabel})"
        : $"{CsvTimeZoneMarker} are UTC - this session carried no track time zone, so they are NOT track local time";

    /// <summary>
    /// Writes the stored lap payloads, field for field, inside an envelope that says what it covers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stored <c>LapData</c> is already a serialized <c>CarPosition</c>, so every field the
    /// system recorded reaches the caller; this is the only format that can say that. The bytes are
    /// not the stored bytes, though - each row is re-encoded on the way out, so escaping and
    /// whitespace are this writer's, not the event processor's.
    /// </para>
    /// <para>
    /// Each row is parsed and written back out rather than injected as raw text. Injecting it would
    /// be cheaper, but the stored payload is compact, and a compact object dropped into an indented
    /// document stays one long line - which would leave the file no more readable than before for the
    /// only part anybody wants to read. Writing the parsed element re-indents it to match the
    /// enclosing writer, and re-encodes its strings with this writer's relaxed encoder - which is
    /// what keeps a driver name readable rather than a run of escape sequences. The parse also keeps
    /// the validation guarantee: a row that no longer parses
    /// throws here and goes down the skip path, instead of corrupting the whole document. Cost is one
    /// document at a time, bounded by the 5000 character column, never a session.
    /// </para>
    /// </remarks>
    /// <param name="output">Stream to write to.</param>
    /// <param name="rawLapJson">The stored lap JSON documents, in export order.</param>
    /// <param name="context">What the export covers.</param>
    /// <param name="maxRows">Row cap; the export stops and marks itself truncated at this many rows.</param>
    /// <param name="maxBytes">Byte cap on the file; the export stops and marks itself truncated when the file reaches it.</param>
    /// <param name="diagnostics">
    /// What the row source lost on the way here. The JSON path also does its own validation, so the
    /// envelope's <c>skippedRows</c> is the sum of both.
    /// </param>
    /// <param name="cancellationToken">Canceled when the client disconnects.</param>
    /// <returns>What was written.</returns>
    public static async Task<ExportWriteResult> WriteJsonAsync(Stream output, IAsyncEnumerable<string> rawLapJson,
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
        if (context.CarNumber is null)
            writer.WriteNull("carNumber");
        else
            writer.WriteString("carNumber", context.CarNumber);
        writer.WriteString("generated", context.ToTrackTime(context.GeneratedUtc) ?? DateTimeOffset.UtcNow);
        writer.WriteString("trackTimeZone", context.TimeZoneLabel);
        writer.WriteBoolean("trackTimeZoneKnown", context.HasTrackOffset);

        // Two labels, deliberately. The envelope timestamps above are track local, but the lap
        // objects below are the stored record passed through untouched, and those are UTC. One label
        // covering both would tell a consumer that "pet":"...Z" is four hours from where it is.
        writer.WriteString("lapDataTimeZone", "UTC");
        writer.WriteString("lapDataNote",
            "Values inside each lap object are exactly as the timing system recorded them, in UTC. " +
            "The CSV and PDF exports of the same laps present times in track local time.");
        writer.WriteBoolean("sessionStillLive", context.SessionStillLive);
        writer.WriteStartArray("laps");

        await foreach (var raw in rawLapJson.WithCancellation(cancellationToken))
        {
            if (result.RowsWritten >= maxRows)
            {
                result.Truncated = true;
                break;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                result.SkippedRows++;
                continue;
            }

            try
            {
                // Disposed per row: the document holds a pooled buffer over the parsed payload, and
                // holding one per lap would put the session back on the heap.
                using var lap = JsonDocument.Parse(raw);
                lap.RootElement.WriteTo(writer);
                result.RowsWritten++;
            }
            catch (JsonException)
            {
                result.SkippedRows++;
                continue;
            }

            // Flush on a fixed budget so the writer's internal buffer never grows to the size of the
            // session. Utf8JsonWriter buffers until told otherwise.
            if (writer.BytesPending > 64 * 1024)
            {
                await writer.FlushAsync(cancellationToken);

                // The row cap alone does not bound the file: a lap payload can be 5KB, so the size
                // of an export is set by the data, not by the row count. The pod's ephemeral disk is
                // shared with everything else running on the node, so the file gets its own ceiling.
                if (ExportBudget.Exceeded(output, maxBytes))
                {
                    result.Truncated = true;
                    break;
                }
            }
        }

        diagnostics.ApplyTo(result);

        writer.WriteEndArray();
        writer.WriteNumber("lapCount", result.RowsWritten);
        writer.WriteNumber("skippedRows", result.SkippedRows);
        writer.WriteBoolean("truncated", result.Truncated);
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Writes the laps as CSV for Excel.
    /// </summary>
    /// <param name="output">Stream to write to.</param>
    /// <param name="rows">
    /// The projected lap rows, streamed in scan order: car number as text, then lap number. This
    /// format is not sorted into human car order, and must not be - it runs to two hundred thousand
    /// rows and sorting means holding all of them. The PDF, which holds its rows anyway, is sorted.
    /// </param>
    /// <param name="context">What the export covers; supplies the still-live marker.</param>
    /// <param name="maxRows">Row cap; the export stops and appends a truncation marker at this many rows.</param>
    /// <param name="maxBytes">Byte cap on the file; the export stops and appends a truncation marker when the file reaches it.</param>
    /// <param name="diagnostics">
    /// What the row source lost on the way here. Folded in after the rows are drained, so the markers
    /// at the end of the file reflect the whole read.
    /// </param>
    /// <param name="cancellationToken">Canceled when the client disconnects.</param>
    /// <returns>What was written.</returns>
    public static async Task<ExportWriteResult> WriteCsvAsync(Stream output, IAsyncEnumerable<LapExportRow> rows,
        ExportContext context, int maxRows, long maxBytes, ExportScanDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        var result = new ExportWriteResult();

        // Excel reads a UTF-8 CSV as the local ANSI code page unless it finds a byte order mark, so a
        // driver name with an accent in it arrives as mojibake without this.
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            bufferSize: 32 * 1024, leaveOpen: true);

        await writer.WriteLineAsync(CsvHeader);

        var line = new StringBuilder(256);
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            if (result.RowsWritten >= maxRows)
            {
                result.Truncated = true;
                break;
            }

            line.Clear();
            // The free-text columns - car number, class, driver - go through AppendTextField; the
            // rest are values this code formatted itself and are safe as they are.
            CsvFormat.AppendTextField(line, row.CarNumber);
            CsvFormat.AppendField(line, CsvFormat.Number(row.LapNumber));
            CsvFormat.AppendField(line, CsvFormat.Timestamp(context.ToTrackTime(row.Timestamp)));
            CsvFormat.AppendField(line, row.Flag);
            CsvFormat.AppendTextField(line, row.Class);
            CsvFormat.AppendField(line, row.LapTime);
            CsvFormat.AppendField(line, row.TotalTime);
            CsvFormat.AppendField(line, row.BestTime);
            CsvFormat.AppendField(line, CsvFormat.Position(row.OverallPosition));
            CsvFormat.AppendField(line, CsvFormat.Position(row.ClassPosition));
            CsvFormat.AppendField(line, row.OverallGap);
            CsvFormat.AppendField(line, row.OverallDifference);
            CsvFormat.AppendField(line, row.InClassGap);
            CsvFormat.AppendField(line, row.InClassDifference);
            CsvFormat.AppendField(line, row.LapIncludedPit ? "true" : "false");
            CsvFormat.AppendField(line, CsvFormat.Number(row.PitStopCount));
            CsvFormat.AppendTextField(line, row.DriverName);
            CsvFormat.AppendTextField(line, row.DriverSource, last: true);

            await writer.WriteLineAsync(line, cancellationToken);
            result.RowsWritten++;

            // Checked in batches because it needs a flush to be meaningful, and flushing per row
            // would defeat the buffer.
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

        // With the other comments at the end rather than between the header and the first row. A
        // leading comment is not a CSV feature: Excel and pandas both read it as the first data row,
        // which is a worse outcome than having to scroll to find the zone.
        await writer.WriteLineAsync(TimeZoneComment(context));

        if (result.Truncated)
        {
            // Deliberately does not name which cap was hit: the row cap, the size cap and the scan
            // cap all mean the same thing to whoever opens the file, which is that it stops short of
            // the session.
            await writer.WriteLineAsync(
                $"{CsvTruncationMarker} - export limit reached after {result.RowsWritten} rows; this file does not cover the whole session");
        }

        if (result.SkippedRows > 0)
        {
            await writer.WriteLineAsync(
                $"{CsvSkippedMarker} - {result.SkippedRows} lap row(s) could not be read and are missing from this file");
        }

        if (context.SessionStillLive)
        {
            await writer.WriteLineAsync(
                $"{CsvStillLiveMarker} - the session was still flagged live when this file was produced, " +
                "so it may not be the complete record");
        }

        await writer.FlushAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// The ways a lap PDF is not the whole truth, printed in its header block.
    /// </summary>
    /// <remarks>
    /// Separated from the layout so the wording can be asserted directly. A note that silently stops
    /// being emitted is not something a test of a binary PDF would ever catch.
    /// </remarks>
    /// <param name="result">What the writer produced.</param>
    /// <param name="context">What the export covers.</param>
    /// <returns>The notes, in the order they are printed.</returns>
    internal static List<string> BuildPdfNotes(ExportWriteResult result, ExportContext context)
    {
        var notes = new List<string>();

        if (!context.HasTrackOffset)
        {
            notes.Add("Times are UTC: this session carried no track time zone, so they are not track " +
                      "local time.");
        }

        if (result.Truncated)
        {
            // The rows in this report are in car number order but the cap cut them in the scan order,
            // so what is missing is not the tail of what is printed here.
            notes.Add(("Truncated: this report reached the export limit and does not cover the whole " +
                       "session. " + context.TruncationScanCaveat).TrimEnd());
        }

        if (result.SkippedRows > 0)
            notes.Add($"{result.SkippedRows} lap row(s) could not be read and are missing from this report.");

        if (context.SessionStillLive)
        {
            notes.Add("This session was still flagged live when the report was produced, so it may not be " +
                      "the complete record.");
        }

        return notes;
    }

    /// <summary>
    /// Writes the laps as a paginated PDF report.
    /// </summary>
    /// <param name="output">Stream to write to.</param>
    /// <param name="rows">
    /// The rows to render, already capped and ordered by car number then lap. A PDF layout needs
    /// every row up front, so this list is the one place a lap export holds rows in memory - and
    /// therefore the one lap format that can be ordered the way a person reads a grid sheet.
    /// </param>
    /// <param name="context">What the export covers.</param>
    /// <param name="truncated">Whether the caller stopped short of the full session.</param>
    /// <param name="diagnostics">What the row source lost on the way here; printed in the header block.</param>
    /// <param name="cancellationToken">Canceled when the client disconnects.</param>
    /// <returns>What was written.</returns>
    public static Task<ExportWriteResult> WritePdfAsync(Stream output, IReadOnlyList<LapExportRow> rows,
        ExportContext context, bool truncated, ExportScanDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExportPdf.EnsureConfigured();

        var result = diagnostics.ApplyTo(new ExportWriteResult { RowsWritten = rows.Count, Truncated = truncated });

        var subtitle = context.CarNumber is null
            ? $"Lap data - all cars ({rows.Count} laps)"
            : $"Lap data - car {context.CarNumber} ({rows.Count} laps)";

        ExportPdf.Build(context, subtitle, BuildPdfNotes(result, context), table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(34);  // Car
                columns.ConstantColumn(30);  // Lap
                columns.ConstantColumn(96);  // Time
                columns.ConstantColumn(52);  // Flag
                columns.ConstantColumn(56);  // Class
                columns.ConstantColumn(58);  // Lap time
                columns.ConstantColumn(30);  // Pos
                columns.ConstantColumn(30);  // Cls
                columns.ConstantColumn(58);  // Gap
                columns.ConstantColumn(28);  // Pit
                columns.RelativeColumn();    // Driver
            });

            ExportPdf.HeaderRow(table, "Car", "Lap", $"Time ({context.TimeZoneLabel})", "Flag", "Class",
                "Lap Time", "Pos", "Cls", "Gap", "Pit", "Driver");

            foreach (var row in rows)
            {
                // Composition is the expensive half of generating a PDF and QuestPDF runs it twice -
                // once more to resolve the page count in the footer. Checking here is what actually
                // stops the work for a client that has already gone away.
                cancellationToken.ThrowIfCancellationRequested();

                ExportPdf.BodyRow(table,
                    row.CarNumber,
                    row.LapNumber.ToString(),
                    CsvFormat.Timestamp(context.ToTrackTime(row.Timestamp)),
                    row.Flag,
                    row.Class ?? string.Empty,
                    row.LapTime ?? string.Empty,
                    ExportPdf.Position(row.OverallPosition),
                    ExportPdf.Position(row.ClassPosition),
                    row.OverallGap ?? string.Empty,
                    row.LapIncludedPit ? "Y" : string.Empty,
                    row.DriverName ?? string.Empty);
            }
        }).GeneratePdf(output);

        return Task.FromResult(result);
    }
}
