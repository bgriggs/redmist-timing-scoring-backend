using RedMist.StatusApi.Services.Exports;
using System.Text;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

/// <summary>
/// The formatting layer of the exports: CSV quoting, car number ordering and format parsing.
/// Car numbers, class names and driver names are all free text that someone typed into a race entry
/// form, so a comma or a quote in one of them is a normal Saturday, not an edge case.
/// </summary>
[TestClass]
public class ExportFormattingTests
{
    #region CSV escaping

    [TestMethod]
    public void Escape_PlainValue_IsLeftAlone()
    {
        Assert.AreEqual("42", CsvFormat.Escape("42"));
        Assert.AreEqual("Alice Smith", CsvFormat.Escape("Alice Smith"));
    }

    [TestMethod]
    public void Escape_NullOrEmpty_BecomesAnEmptyField()
    {
        Assert.AreEqual(string.Empty, CsvFormat.Escape(null));
        Assert.AreEqual(string.Empty, CsvFormat.Escape(string.Empty));
    }

    [TestMethod]
    public void Escape_ValueWithComma_IsQuoted()
    {
        Assert.AreEqual("\"Smith, Alice\"", CsvFormat.Escape("Smith, Alice"));
    }

    [TestMethod]
    public void Escape_ValueWithQuote_IsQuotedAndTheQuoteDoubled()
    {
        Assert.AreEqual("\"Alice \"\"Ace\"\" Smith\"", CsvFormat.Escape("Alice \"Ace\" Smith"));
    }

    [TestMethod]
    public void Escape_ValueWithNewline_IsQuoted()
    {
        Assert.AreEqual("\"line1\nline2\"", CsvFormat.Escape("line1\nline2"));
        Assert.AreEqual("\"line1\r\nline2\"", CsvFormat.Escape("line1\r\nline2"));
    }

    /// <summary>
    /// A car number that came through with a stray space still has to match the one the user asked
    /// for, and an unquoted leading space does not survive a round trip through a CSV reader.
    /// </summary>
    [TestMethod]
    public void Escape_ValueWithEdgeWhitespace_IsQuoted()
    {
        Assert.AreEqual("\" 42\"", CsvFormat.Escape(" 42"));
        Assert.AreEqual("\"42 \"", CsvFormat.Escape("42 "));
    }

    [TestMethod]
    public void AppendField_SeparatesWithCommasAndOmitsTheTrailingOne()
    {
        var builder = new StringBuilder();
        CsvFormat.AppendField(builder, "a");
        CsvFormat.AppendField(builder, "b, c");
        CsvFormat.AppendField(builder, "d", last: true);

        Assert.AreEqual("a,\"b, c\",d", builder.ToString());
    }

    /// <summary>
    /// The times in a CSV or PDF are the clock at the track, written the way an American club race
    /// entrant reads a date. A UTC timestamp four hours ahead is the same instant, but nobody at the
    /// track was looking at that clock.
    /// </summary>
    [TestMethod]
    public void Timestamp_FormatsTrackLocalTimeAsMonthDayYear()
    {
        var context = Context();
        var utc = new DateTime(2026, 8, 30, 12, 47, 28, DateTimeKind.Utc);

        Assert.AreEqual("8/30/2026 8:47:28 AM", CsvFormat.Timestamp(context.ToTrackTime(utc)));
    }

    [TestMethod]
    public void Timestamp_NullIsAnEmptyCell()
    {
        Assert.AreEqual(string.Empty, CsvFormat.Timestamp(null));
    }

    /// <summary>
    /// A device clock that was never set reports year 0001. That is not a time and must never be
    /// printed as one.
    /// </summary>
    [TestMethod]
    public void IsPlausible_RejectsUnsetDeviceClocks()
    {
        Assert.IsFalse(CsvFormat.IsPlausible(new DateTime(1, 4, 11, 12, 12, 45, DateTimeKind.Utc)));
        Assert.IsFalse(CsvFormat.IsPlausible(null));
        Assert.IsTrue(CsvFormat.IsPlausible(new DateTime(2026, 8, 30, 12, 47, 28, DateTimeKind.Utc)));
    }

    /// <summary>
    /// Shifting a timestamp can leave the range of DateTime, and these values come out of a payload
    /// another service wrote. A blank cell costs one value; an exception costs the whole export.
    /// </summary>
    [TestMethod]
    public void ToTrackTime_ValueThatCannotBeShifted_IsBlankRatherThanThrowing()
    {
        foreach (var hours in new[] { 0.5, 5.75, 14.0 })
        {
            var context = new ExportContext { TrackOffset = TimeSpan.FromHours(hours) };
            Assert.IsNull(context.ToTrackTime(DateTime.MaxValue), $"offset {hours}");
        }

        Assert.IsNull(new ExportContext { TrackOffset = TimeSpan.FromHours(-4) }.ToTrackTime(DateTime.MinValue));
    }

    /// <summary>
    /// A stop that absorbed a reissued entry time is marked, so the reader knows the times under it
    /// moved rather than being one coherent measurement.
    /// </summary>
    [TestMethod]
    public async Task WriteCsvAsync_PitStops_MarksARenumberedEntryTime()
    {
        var stops = new[]
        {
            new PitStopRecord
            {
                CarNumber = "53", StopNumber = 1, StartLap = 114, EndLap = 116,
                DurationMs = 81_000, EntryTimeRenumbered = true,
            }
        };

        using var stream = new MemoryStream();
        await PitStopReportWriter.WriteCsvAsync(stream, stops, Context(), truncated: false,
            ExportBudget.MaxExportBytes, new ExportScanDiagnostics(), CancellationToken.None);

        var text = ReadUtf8(stream);
        StringAssert.Contains(text, PitStopReportWriter.CsvRenumberedMarker);
        StringAssert.Contains(text, "114,116");
    }

    /// <summary>
    /// The scan cuts in the timing system text ordering and the report is sorted numerically
    /// afterwards, so a truncated report is missing cars from the middle. Saying only "truncated"
    /// would let a reader assume the tail is what is gone.
    /// </summary>
    [TestMethod]
    public async Task WriteCsvAsync_PitStops_TruncationNamesWhereTheScanStopped()
    {
        var stops = new[] { new PitStopRecord { CarNumber = "7", StopNumber = 1, StartLap = 2, EndLap = 2 } };
        var context = Context();
        context.TruncatedAfterCarNumber = "18x";

        using var stream = new MemoryStream();
        await PitStopReportWriter.WriteCsvAsync(stream, stops, context, truncated: true,
            ExportBudget.MaxExportBytes, new ExportScanDiagnostics(), CancellationToken.None);

        var text = ReadUtf8(stream);
        StringAssert.Contains(text, PitStopReportWriter.CsvTruncationMarker);
        StringAssert.Contains(text, "after car 18x");
        StringAssert.Contains(text, "scattered through the numbering");
    }

    [TestMethod]
    public void TimeZoneLabel_NamesTheOffsetOrSaysItIsUnknown()
    {
        Assert.AreEqual("UTC-04:00", Context().TimeZoneLabel);
        Assert.AreEqual("UTC+05:30", new ExportContext { TrackOffset = TimeSpan.FromHours(5.5) }.TimeZoneLabel);
        Assert.AreEqual("UTC", Context(withTrackOffset: false).TimeZoneLabel);
    }

    /// <summary>
    /// A session with no usable offset falls back to UTC, and the file has to say so - silently
    /// handing somebody UTC labeled as track time sends them looking for a lap hours out.
    /// </summary>
    [TestMethod]
    public async Task WriteCsvAsync_Laps_NamesTheTimeZoneInTheCommentBlock()
    {
        var rows = new[] { new LapExportRow { CarNumber = "42", LapNumber = 1 } };

        var withZone = await WriteLapCsvWithContextAsync(rows, Context());
        StringAssert.Contains(withZone, "track local time (UTC-04:00)");

        var withoutZone = await WriteLapCsvWithContextAsync(rows, Context(withTrackOffset: false));
        StringAssert.Contains(withoutZone, "NOT track local time");
    }

    /// <summary>
    /// The comment block is at the end. A comment between the header and the first row is not a CSV
    /// feature - Excel and pandas both read it as a data row - so the data area stays clean.
    /// </summary>
    [TestMethod]
    public async Task WriteCsvAsync_Laps_PutsCommentsAfterTheDataNotInsideIt()
    {
        var rows = new[] { new LapExportRow { CarNumber = "42", LapNumber = 1 } };

        var lines = SplitLines(await WriteLapCsvWithContextAsync(rows, Context()));

        Assert.AreEqual(LapExportWriter.CsvHeader, lines[0]);
        StringAssert.StartsWith(lines[1], "42,1,", "the row after the header must be data");
        Assert.IsTrue(lines[^1].StartsWith('#'));
    }

    [TestMethod]
    public void Duration_FormatsMinutesAndHours()
    {
        Assert.AreEqual(string.Empty, CsvFormat.Duration(null));
        Assert.AreEqual("1:02.500", CsvFormat.Duration(62_500));
        Assert.AreEqual("1:00:00.000", CsvFormat.Duration(3_600_000));
    }

    /// <summary>
    /// A car left in the pits overnight is exactly the stop somebody opens the report to find, so the
    /// days component must not be silently dropped.
    /// </summary>
    [TestMethod]
    public void Duration_LongerThanADay_KeepsTheDaysComponent()
    {
        Assert.AreEqual("1.02:00:00.000", CsvFormat.Duration(93_600_000));
    }

    /// <summary>
    /// These files exist to be opened in Excel, where a cell starting with =, +, - or @ is a formula,
    /// and a formula can reach outside the spreadsheet. Car numbers and driver names are typed in by
    /// whoever entered the car.
    /// </summary>
    [TestMethod]
    public void AppendTextField_NeutralizesValuesExcelWouldEvaluate()
    {
        foreach (var hostile in new[] { "=1+1", "+1", "-1", "@SUM(A1)", "	cmd" })
        {
            var builder = new StringBuilder();
            CsvFormat.AppendTextField(builder, hostile, last: true);
            StringAssert.StartsWith(builder.ToString().TrimStart('"'), "'",
                $"'{hostile}' should have been neutralized");
        }
    }

    [TestMethod]
    public void AppendTextField_LeavesOrdinaryValuesAlone()
    {
        var builder = new StringBuilder();
        CsvFormat.AppendTextField(builder, "99x", last: true);

        Assert.AreEqual("99x", builder.ToString());
    }

    /// <summary>
    /// The neutralizing is confined to the free-text columns. Gap and position values the export
    /// formats itself legitimately start with a minus sign, and prefixing those would turn a
    /// spreadsheet of numbers the user wants to chart into a spreadsheet of text.
    /// </summary>
    [TestMethod]
    public void AppendField_DoesNotNeutralizeTheExportsOwnNumericValues()
    {
        var builder = new StringBuilder();
        CsvFormat.AppendField(builder, "-1.234", last: true);

        Assert.AreEqual("-1.234", builder.ToString());
    }

    #endregion

    #region CSV writers

    /// <summary>
    /// The whole point of the escaping is that the row still has the right number of columns, so
    /// this asserts on the written line rather than on the helper.
    /// </summary>
    [TestMethod]
    public async Task WriteCsvAsync_Laps_QuotesCarNumbersAndDriverNamesContainingSeparators()
    {
        var rows = new[]
        {
            new LapExportRow
            {
                CarNumber = "9,9",
                LapNumber = 4,
                Timestamp = new DateTime(2026, 5, 1, 14, 30, 0, DateTimeKind.Utc),
                Flag = "Green",
                Class = "GT, Am",
                LapTime = "1:32.104",
                DriverName = "Alice \"Ace\" Smith",
                DriverSource = "blePuck",
                OverallPosition = 3,
                ClassPosition = 1,
            }
        };

        var text = await WriteLapCsvAsync(rows, maxRows: 100);
        var lines = DataLines(text);

        Assert.AreEqual(LapExportWriter.CsvHeader, lines[0]);
        StringAssert.Contains(lines[1], "\"9,9\"");
        StringAssert.Contains(lines[1], "\"GT, Am\"");
        StringAssert.Contains(lines[1], "\"Alice \"\"Ace\"\" Smith\"");
        Assert.AreEqual(CountFields(lines[0]), CountFields(lines[1]), "escaped values must not add columns");
    }

    [TestMethod]
    public async Task WriteCsvAsync_Laps_AppendsATruncationMarkerAtTheRowCap()
    {
        var rows = Enumerable.Range(1, 5)
            .Select(i => new LapExportRow { CarNumber = "1", LapNumber = i })
            .ToArray();

        var text = await WriteLapCsvAsync(rows, maxRows: 3);

        // Header plus three rows, with the truncation marker in the comment block.
        Assert.AreEqual(4, DataLines(text).Length);
        StringAssert.StartsWith(SplitLines(text)[^1], LapExportWriter.CsvTruncationMarker);
    }

    /// <summary>
    /// The row cap does not bound the file - a stored lap payload can be 5KB - so exports are also
    /// written under a size ceiling. The check runs in batches, because seeing the true file size
    /// needs a flush and flushing per row would defeat the write buffer, so the file overshoots by up
    /// to one batch. That is the intended trade: the point is to bound the file, not to land on an
    /// exact byte.
    /// </summary>
    [TestMethod]
    public async Task WriteCsvAsync_Laps_StopsAtTheByteBudget()
    {
        var rows = Enumerable.Range(1, ExportBudget.CheckInterval * 3)
            .Select(i => new LapExportRow { CarNumber = "1", LapNumber = i })
            .ToArray();

        using var stream = new MemoryStream();
        var result = await LapExportWriter.WriteCsvAsync(stream, ToAsync(rows), Context(), maxRows: int.MaxValue,
            maxBytes: 100, new ExportScanDiagnostics(), CancellationToken.None);

        Assert.IsTrue(result.Truncated);
        Assert.AreEqual(ExportBudget.CheckInterval, result.RowsWritten);
        StringAssert.StartsWith(SplitLines(ReadUtf8(stream))[^1], LapExportWriter.CsvTruncationMarker);
    }

    [TestMethod]
    public async Task WriteJsonAsync_Laps_StopsAtTheByteBudget()
    {
        // Each payload is padded so the writer crosses its 64KB flush threshold - and therefore
        // reaches a budget check - well before the row cap could.
        var padding = new string('x', 500);
        var laps = Enumerable.Range(1, 5_000)
            .Select(i => $"{{\"n\":\"1\",\"llp\":{i},\"pad\":\"{padding}\"}}")
            .ToArray();

        using var stream = new MemoryStream();
        var context = new ExportContext { EventId = 1, SessionId = 10, GeneratedUtc = DateTime.UtcNow };
        var result = await LapExportWriter.WriteJsonAsync(stream, ToAsync(laps), context, maxRows: int.MaxValue,
            maxBytes: 100_000, new ExportScanDiagnostics(), CancellationToken.None);

        Assert.IsTrue(result.Truncated);
        Assert.IsTrue(result.RowsWritten < laps.Length);

        // Still a well-formed document: truncation closes the array and the envelope rather than
        // stopping mid-write.
        using var document = System.Text.Json.JsonDocument.Parse(ReadUtf8(stream));
        Assert.IsTrue(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.AreEqual(result.RowsWritten, document.RootElement.GetProperty("laps").GetArrayLength());
    }

    [TestMethod]
    public async Task WriteCsvAsync_PitStops_QuotesDriverNamesAndKeepsColumnCount()
    {
        var stops = new[]
        {
            new PitStopRecord
            {
                CarNumber = "99x",
                StopNumber = 1,
                StartLap = 12,
                EndLap = 12,
                EntryTime = new DateTime(2026, 5, 1, 15, 0, 0, DateTimeKind.Utc),
                ExitTime = new DateTime(2026, 5, 1, 15, 1, 5, DateTimeKind.Utc),
                DurationMs = 65_000,
                DriverBefore = "Smith, Alice",
                DriverAfter = "O'Brien \"Bo\"",
                DriverChanged = true,
            }
        };

        using var stream = new MemoryStream();
        await PitStopReportWriter.WriteCsvAsync(stream, stops, Context(), truncated: false,
            ExportBudget.MaxExportBytes, new ExportScanDiagnostics(), CancellationToken.None);
        var lines = DataLines(ReadUtf8(stream));

        Assert.AreEqual(PitStopReportWriter.CsvHeader, lines[0]);
        StringAssert.Contains(lines[1], "\"Smith, Alice\"");
        StringAssert.Contains(lines[1], "\"O'Brien \"\"Bo\"\"\"");
        StringAssert.Contains(lines[1], "1:05.000");
        Assert.AreEqual(CountFields(lines[0]), CountFields(lines[1]));
    }

    /// <summary>
    /// Excel reads a UTF-8 CSV as the local code page unless it finds a byte order mark, and driver
    /// names are exactly the column where non-ASCII turns up.
    /// </summary>
    [TestMethod]
    public async Task WriteCsvAsync_StartsWithAByteOrderMark()
    {
        using var stream = new MemoryStream();
        await LapExportWriter.WriteCsvAsync(stream, ToAsync(Array.Empty<LapExportRow>()), Context(), 10,
            ExportBudget.MaxExportBytes, new ExportScanDiagnostics(), CancellationToken.None);

        var bytes = stream.ToArray();
        CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
    }

    #endregion

    /// <summary>
    /// A session that ended but is still flagged live may have been picked up again, so the file it
    /// produces may be a partial record - and nothing about the rows themselves would show that.
    /// </summary>
    [TestMethod]
    public async Task WriteCsvAsync_Laps_MarksASessionThatWasStillLive()
    {
        var rows = new[] { new LapExportRow { CarNumber = "42", LapNumber = 1 } };

        using var stream = new MemoryStream();
        await LapExportWriter.WriteCsvAsync(stream, ToAsync(rows), Context(sessionStillLive: true),
            100, ExportBudget.MaxExportBytes, new ExportScanDiagnostics(), CancellationToken.None);

        var lines = SplitLines(ReadUtf8(stream));
        StringAssert.StartsWith(lines[^1], LapExportWriter.CsvStillLiveMarker);
    }

    [TestMethod]
    public async Task WriteCsvAsync_PitStops_MarksASessionThatWasStillLive()
    {
        var stops = new[] { new PitStopRecord { CarNumber = "42", StopNumber = 1, StartLap = 3, EndLap = 3 } };

        using var stream = new MemoryStream();
        await PitStopReportWriter.WriteCsvAsync(stream, stops, Context(sessionStillLive: true),
            truncated: false, ExportBudget.MaxExportBytes, new ExportScanDiagnostics(), CancellationToken.None);

        var lines = SplitLines(ReadUtf8(stream));
        StringAssert.StartsWith(lines[^1], PitStopReportWriter.CsvStillLiveMarker);
    }

    [TestMethod]
    public async Task WriteCsvAsync_Laps_OrdinarySession_HasNoStillLiveMarker()
    {
        var text = await WriteLapCsvAsync([new LapExportRow { CarNumber = "42", LapNumber = 1 }], 100);

        Assert.IsFalse(text.Contains(LapExportWriter.CsvStillLiveMarker, StringComparison.Ordinal));
    }

    #region Car number ordering

    [TestMethod]
    public void CarNumbers_SortNumericallyNotLexically()
    {
        var cars = new List<string> { "12", "2", "100", "1" };
        cars.Sort(CarNumberComparer.Instance);

        CollectionAssert.AreEqual(new[] { "1", "2", "12", "100" }, cars);
    }

    [TestMethod]
    public void CarNumbers_WithLetterSuffixes_SortAfterTheBareNumber()
    {
        var cars = new List<string> { "99x", "9", "99", "9a", "10" };
        cars.Sort(CarNumberComparer.Instance);

        CollectionAssert.AreEqual(new[] { "9", "9a", "10", "99", "99x" }, cars);
    }

    /// <summary>
    /// Entries with no number at all - a course car, a safety car - have nothing numeric to compare
    /// and must not collapse to zero and sort ahead of car 1.
    /// </summary>
    [TestMethod]
    public void CarNumbers_WithoutDigits_SortLast()
    {
        var cars = new List<string> { "Course", "3", "Safety", "1" };
        cars.Sort(CarNumberComparer.Instance);

        CollectionAssert.AreEqual(new[] { "1", "3", "Course", "Safety" }, cars);
    }

    #endregion

    #region Format parsing

    [TestMethod]
    public void TryParse_AcceptsTheThreeFormatsAndDefaultsToJson()
    {
        Assert.IsTrue(ExportFormats.TryParse(null, out var none));
        Assert.AreEqual(ExportFormat.Json, none);
        Assert.IsTrue(ExportFormats.TryParse("", out var empty));
        Assert.AreEqual(ExportFormat.Json, empty);
        Assert.IsTrue(ExportFormats.TryParse("CSV", out var csv));
        Assert.AreEqual(ExportFormat.Csv, csv);
        Assert.IsTrue(ExportFormats.TryParse(" pdf ", out var pdf));
        Assert.AreEqual(ExportFormat.Pdf, pdf);
    }

    [TestMethod]
    public void TryParse_RejectsAnythingElse()
    {
        Assert.IsFalse(ExportFormats.TryParse("xlsx", out _));
        Assert.IsFalse(ExportFormats.TryParse("html", out _));
    }

    [TestMethod]
    public void ContentTypeAndExtension_MatchTheFormat()
    {
        Assert.AreEqual("application/json", ExportFormats.ContentType(ExportFormat.Json));
        Assert.AreEqual("text/csv", ExportFormats.ContentType(ExportFormat.Csv));
        Assert.AreEqual("application/pdf", ExportFormats.ContentType(ExportFormat.Pdf));
        Assert.AreEqual(".json", ExportFormats.Extension(ExportFormat.Json));
        Assert.AreEqual(".csv", ExportFormats.Extension(ExportFormat.Csv));
        Assert.AreEqual(".pdf", ExportFormats.Extension(ExportFormat.Pdf));
    }

    #endregion

    #region Helpers

    /// <summary>
    /// A context describing an ordinary finished session at a track four hours behind UTC, which is
    /// what event 382 session 88 reported.
    /// </summary>
    private static ExportContext Context(bool sessionStillLive = false, bool withTrackOffset = true) => new()
    {
        EventId = 1,
        SessionId = 10,
        SessionName = "Race",
        GeneratedUtc = new DateTime(2026, 5, 1, 18, 0, 0, DateTimeKind.Utc),
        SessionStillLive = sessionStillLive,
        TrackOffset = withTrackOffset ? TimeSpan.FromHours(-4) : null,
    };

    private static async Task<string> WriteLapCsvAsync(IEnumerable<LapExportRow> rows, int maxRows)
    {
        using var stream = new MemoryStream();
        await LapExportWriter.WriteCsvAsync(stream, ToAsync(rows), Context(), maxRows,
            ExportBudget.MaxExportBytes, new ExportScanDiagnostics(), CancellationToken.None);
        return ReadUtf8(stream);
    }

    private static async Task<string> WriteLapCsvWithContextAsync(IEnumerable<LapExportRow> rows, ExportContext context)
    {
        using var stream = new MemoryStream();
        await LapExportWriter.WriteCsvAsync(stream, ToAsync(rows), context, 100,
            ExportBudget.MaxExportBytes, new ExportScanDiagnostics(), CancellationToken.None);
        return ReadUtf8(stream);
    }

    private static string ReadUtf8(MemoryStream stream) =>
        new UTF8Encoding(false).GetString(stream.ToArray()).TrimStart('﻿');

    private static string[] SplitLines(string text) =>
        text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The header and data rows, with the comment block left out. Comments carry things a reader
    /// needs - the time zone, truncation, skipped rows - but they are not data, and a test asserting
    /// on row positions should not have to move every time one is added.
    /// </summary>
    private static string[] DataLines(string text) =>
        [.. SplitLines(text).Where(l => !l.StartsWith('#'))];

    /// <summary>Counts CSV fields, honoring quoting, so a test can prove escaping did not add a column.</summary>
    private static int CountFields(string line)
    {
        var fields = 1;
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"')
                inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
                fields++;
        }
        return fields;
    }

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    #endregion
}
