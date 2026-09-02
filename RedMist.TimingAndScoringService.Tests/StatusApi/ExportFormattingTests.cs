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
                TimestampUtc = new DateTime(2026, 5, 1, 14, 30, 0, DateTimeKind.Utc),
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
        var lines = SplitLines(text);

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
        var lines = SplitLines(text);

        // Header, three rows, marker.
        Assert.AreEqual(5, lines.Length);
        StringAssert.StartsWith(lines[^1], LapExportWriter.CsvTruncationMarker);
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
        var result = await LapExportWriter.WriteCsvAsync(stream, ToAsync(rows), maxRows: int.MaxValue,
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
                Lap = 12,
                EntryTimeUtc = new DateTime(2026, 5, 1, 15, 0, 0, DateTimeKind.Utc),
                ExitTimeUtc = new DateTime(2026, 5, 1, 15, 1, 5, DateTimeKind.Utc),
                DurationMs = 65_000,
                DriverBefore = "Smith, Alice",
                DriverAfter = "O'Brien \"Bo\"",
                DriverChanged = true,
            }
        };

        using var stream = new MemoryStream();
        await PitStopReportWriter.WriteCsvAsync(stream, ToAsync(stops), 100, ExportBudget.MaxExportBytes,
            new ExportScanDiagnostics(), CancellationToken.None);
        var lines = SplitLines(ReadUtf8(stream));

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
        await LapExportWriter.WriteCsvAsync(stream, ToAsync(Array.Empty<LapExportRow>()), 10,
            ExportBudget.MaxExportBytes, new ExportScanDiagnostics(), CancellationToken.None);

        var bytes = stream.ToArray();
        CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
    }

    #endregion

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

    private static async Task<string> WriteLapCsvAsync(IEnumerable<LapExportRow> rows, int maxRows)
    {
        using var stream = new MemoryStream();
        await LapExportWriter.WriteCsvAsync(stream, ToAsync(rows), maxRows, ExportBudget.MaxExportBytes,
            new ExportScanDiagnostics(), CancellationToken.None);
        return ReadUtf8(stream);
    }

    private static string ReadUtf8(MemoryStream stream) =>
        new UTF8Encoding(false).GetString(stream.ToArray()).TrimStart('﻿');

    private static string[] SplitLines(string text) =>
        text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

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
