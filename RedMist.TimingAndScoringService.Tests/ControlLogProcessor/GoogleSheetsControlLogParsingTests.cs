using Google.Apis.Sheets.v4.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RedMist.ControlLogs;
using RedMist.Database;
using RedMist.EventProcessor.Tests.Utilities;
using ChampCarLog = RedMist.ControlLogs.ChampCarGoogleSheets.GoogleSheetsControlLog;
using LuckyDogLog = RedMist.ControlLogs.LuckyDogGoogleSheets.GoogleSheetsControlLog;
using SheetColor = Google.Apis.Sheets.v4.Data.Color;
using WrlLog = RedMist.ControlLogs.WrlGoogleSheets.GoogleSheetsControlLog;

namespace RedMist.TimingAndScoringService.Tests.ControlLogProcessor;

/// <summary>
/// Header-mapping and row-parsing tests for <see cref="GoogleSheetsControlLogBase"/>, driven through the
/// real per-series column layouts (WRL / ChampCar / LuckyDog) by feeding row data straight to the parse
/// seams instead of calling the Google Sheets API.
/// </summary>
[TestClass]
public class GoogleSheetsControlLogParsingTests
{
    #region Row builders

    private static CellData Cell(string? value) => new() { FormattedValue = value };

    /// <summary>A cell shaded the way the sheets mark the at-fault car: red=1, green=1, blue omitted.</summary>
    private static CellData YellowCell(string? value) => new()
    {
        FormattedValue = value,
        EffectiveFormat = new CellFormat { BackgroundColor = new SheetColor { Red = 1, Green = 1 } }
    };

    private static RowData Row(params string?[] values) => new() { Values = [.. values.Select(Cell)] };

    private static RowData Row(params CellData[] cells) => new() { Values = [.. cells] };

    private static ChampCarLog ChampCar() =>
        new(NullLoggerFactory.Instance, new ConfigurationBuilder().Build(), Mock.Of<IDbContextFactory<TsContext>>());

    private static WrlLog Wrl() =>
        new(NullLoggerFactory.Instance, new ConfigurationBuilder().Build(), Mock.Of<IDbContextFactory<TsContext>>());

    private static LuckyDogLog LuckyDog() =>
        new(NullLoggerFactory.Instance, new ConfigurationBuilder().Build(), Mock.Of<IDbContextFactory<TsContext>>());

    // Real sheet headers.
    private static RowData ChampCarHeader() => Row("Time", "Car #", "Car #", "Corner", "Cause", "Pit-In", "Penalty / Action", "Status", "Other Notes");
    private static RowData WrlHeader() => Row("Time", "Corner", "Car #", "Car #", "Note", "Status", "Penalty / Action", "Other Notes");

    #endregion

    #region Header mapping

    [TestMethod]
    public void InitializeColumnMappings_ChampCar_SecondCarColumnMapsToCar2()
    {
        // The sheet has two identically titled "Car #" columns; the second must land on Car2.
        using var log = ChampCar();

        log.InitializeColumnMappings(ChampCarHeader());

        Assert.AreEqual("Timestamp", log.ColumnIndexMappings[0].PropertyName);
        Assert.AreEqual("Car1", log.ColumnIndexMappings[1].PropertyName);
        Assert.AreEqual("Car2", log.ColumnIndexMappings[2].PropertyName);
        Assert.AreEqual("Corner", log.ColumnIndexMappings[3].PropertyName);
        Assert.AreEqual("Note", log.ColumnIndexMappings[4].PropertyName);
    }

    [TestMethod]
    public void InitializeColumnMappings_HeaderTitlesAreMatchedCaseInsensitively()
    {
        using var log = Wrl();

        log.InitializeColumnMappings(Row("time", "CORNER", "car #", "Car #", "nOtE", "status", "PENALTY / ACTION", "other notes"));

        Assert.AreEqual("Timestamp", log.ColumnIndexMappings[0].PropertyName);
        Assert.AreEqual("Car1", log.ColumnIndexMappings[2].PropertyName);
        Assert.AreEqual("Car2", log.ColumnIndexMappings[3].PropertyName);
        Assert.AreEqual("Note", log.ColumnIndexMappings[4].PropertyName);
    }

    [TestMethod]
    public void InitializeColumnMappings_UnknownAndBlankHeaderCells_AreSkippedAndDoNotShiftOtherColumns()
    {
        using var log = LuckyDog();

        // "Flag" has no mapping in the LuckyDog layout, and a blank header cell must not consume an index.
        log.InitializeColumnMappings(Row("Time", "Turn", "Flag", "Car", null, "Note"));

        Assert.AreEqual("Timestamp", log.ColumnIndexMappings[0].PropertyName);
        Assert.AreEqual("Corner", log.ColumnIndexMappings[1].PropertyName);
        Assert.IsFalse(log.ColumnIndexMappings.ContainsKey(2), "'Flag' is unmapped for LuckyDog");
        Assert.AreEqual("Car1", log.ColumnIndexMappings[3].PropertyName);
        Assert.IsFalse(log.ColumnIndexMappings.ContainsKey(4), "blank header cell is unmapped");
        Assert.AreEqual("OtherNotes", log.ColumnIndexMappings[5].PropertyName);
    }

    [TestMethod]
    public void InitializeColumnMappings_RequiredColumnMissing_ThrowsAwayEveryMapping()
    {
        // WRL requires both Time and Note. Dropping Note must invalidate the whole header, because the
        // caller treats an empty mapping set as "this worksheet cannot be parsed".
        using var log = Wrl();

        log.InitializeColumnMappings(Row("Time", "Corner", "Car #", "Car #", "Status", "Penalty / Action", "Other Notes"));

        Assert.AreEqual(0, log.ColumnIndexMappings.Count);
    }

    [TestMethod]
    public void InitializeColumnMappings_HeaderWithNoRecognizableColumns_ProducesNoMappings()
    {
        using var log = ChampCar();

        log.InitializeColumnMappings(Row("Lorem", "Ipsum", "Dolor"));

        Assert.AreEqual(0, log.ColumnIndexMappings.Count);
    }

    #endregion

    #region Row parsing

    [TestMethod]
    public void ParseRows_ChampCarRow_IsMappedOntoTheEntryWithOrderIdFromTheRowIndex()
    {
        using var log = ChampCar();
        log.InitializeColumnMappings(ChampCarHeader());

        var entries = log.ParseRows(
        [
            ChampCarHeader(),
            Row("2026-06-05 14:30:00", "15", "23", "T1", "contact", "", "1 Lap", "Penalty", "car 15 at fault"),
        ]);

        var entry = entries.Single();
        Assert.AreEqual(1, entry.OrderId, "OrderId is the row's index in the sheet");
        Assert.AreEqual(new DateTime(2026, 6, 5, 14, 30, 0), entry.Timestamp);
        Assert.AreEqual("15", entry.Car1);
        Assert.AreEqual("23", entry.Car2);
        Assert.AreEqual("T1", entry.Corner);
        Assert.AreEqual("contact", entry.Note);
        Assert.AreEqual("1 Lap", entry.PenaltyAction);
        Assert.AreEqual("Penalty", entry.Status);
        Assert.AreEqual("car 15 at fault", entry.OtherNotes);
    }

    [TestMethod]
    public void ParseRows_RowWithNoCells_IsSkippedButStillConsumesItsOrderId()
    {
        // Google returns RowData with a null Values for entirely empty rows.
        using var log = ChampCar();
        log.InitializeColumnMappings(ChampCarHeader());

        var entries = log.ParseRows(
        [
            ChampCarHeader(),
            new RowData(),
            Row("2026-06-05 14:30:00", "15", "", "T1", "contact", "", "", "", ""),
        ]);

        Assert.AreEqual(2, entries.Single().OrderId, "the skipped row still occupies index 1");
    }

    [TestMethod]
    public void ParseRows_ShortRow_ParsesTheCellsThatArePresent()
    {
        using var log = ChampCar();
        log.InitializeColumnMappings(ChampCarHeader());

        // Trailing cells simply absent - ChampCar only requires the timestamp.
        var entries = log.ParseRows([ChampCarHeader(), Row("2026-06-05 14:30:00", "15")]);

        var entry = entries.Single();
        Assert.AreEqual("15", entry.Car1);
        Assert.AreEqual(string.Empty, entry.Car2);
        Assert.AreEqual(string.Empty, entry.PenaltyAction);
    }

    [TestMethod]
    public void ParseRows_WrlRowTooShortToReachTheRequiredNoteColumn_IsDropped()
    {
        using var log = Wrl();
        log.InitializeColumnMappings(WrlHeader());

        // Note lives at index 4; a three-cell row never satisfies the required column.
        var entries = log.ParseRows([WrlHeader(), Row("2026-06-05 14:30:00", "T1", "15")]);

        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("see notes")]
    public void ParseRows_RowWithoutAUsableTimestamp_IsDropped(string timeCell)
    {
        // The sheet converter turns an unparsable time into default(DateTime), which then fails the
        // minimum-timestamp filter - this is the real guard that keeps trailing/blank rows out of the log.
        using var log = ChampCar();
        log.InitializeColumnMappings(ChampCarHeader());

        var entries = log.ParseRows([ChampCarHeader(), Row(timeCell, "15", "", "T1", "contact")]);

        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public void ParseRows_ChampCarFlagStateOnlyFillsNoteWhenCauseIsBlank()
    {
        // ChampCar maps both "Cause" and "Flag State" onto Note. Cause wins when it has content.
        using var log = ChampCar();
        var header = Row("Time", "Car #", "Car #", "Corner", "Cause", "Flag State", "Penalty / Action", "Status", "Other Notes");
        log.InitializeColumnMappings(header);

        var entries = log.ParseRows(
        [
            header,
            Row("2026-06-05 14:30:00", "15", "", "T1", "spun", "Full Course Yellow", "", "", ""),
            Row("2026-06-05 14:35:00", "23", "", "T2", "  ", "Full Course Yellow", "", "", ""),
        ]);

        Assert.AreEqual("spun", entries[0].Note, "Cause must not be overwritten by Flag State");
        Assert.AreEqual("Full Course Yellow", entries[1].Note, "Flag State fills in when Cause is blank");
    }

    [TestMethod]
    public void ParseRows_CarNumbersAreNotTrimmed()
    {
        // Padded car cells flow verbatim into the per-car cache keys downstream.
        using var log = ChampCar();
        log.InitializeColumnMappings(ChampCarHeader());

        var entries = log.ParseRows([ChampCarHeader(), Row("2026-06-05 14:30:00", " 15 ")]);

        Assert.AreEqual(" 15 ", entries.Single().Car1);
    }

    #endregion

    #region Highlighting

    [TestMethod]
    public void ParseRows_YellowShadedCarCell_MarksThatCarAsAtFault()
    {
        using var log = ChampCar();
        log.InitializeColumnMappings(ChampCarHeader());

        var entries = log.ParseRows(
        [
            ChampCarHeader(),
            Row(Cell("2026-06-05 14:30:00"), Cell("15"), YellowCell("23"), Cell("T1"), Cell("contact")),
        ]);

        var entry = entries.Single();
        Assert.IsFalse(entry.IsCar1Highlighted);
        Assert.IsTrue(entry.IsCar2Highlighted, "the shaded car cell identifies the at-fault car");
    }

    [TestMethod]
    public void ParseRows_ShadingWithAnExplicitBlueComponent_IsNotTreatedAsAHighlight()
    {
        // The highlight test is red=1, green=1, blue *absent*. Sheets omits zero components, so any cell
        // that reports a blue value at all - including 0 - is a different color and must not count.
        using var log = ChampCar();
        log.InitializeColumnMappings(ChampCarHeader());

        var shaded = new CellData
        {
            FormattedValue = "23",
            EffectiveFormat = new CellFormat { BackgroundColor = new SheetColor { Red = 1, Green = 1, Blue = 0 } }
        };
        var entries = log.ParseRows([ChampCarHeader(), Row(Cell("2026-06-05 14:30:00"), Cell("15"), shaded, Cell("T1"))]);

        Assert.IsFalse(entries.Single().IsCar2Highlighted);
    }

    [TestMethod]
    public void ParseRows_UnshadedRow_LeavesBothCarsUnhighlighted()
    {
        using var log = ChampCar();
        log.InitializeColumnMappings(ChampCarHeader());

        var entries = log.ParseRows([ChampCarHeader(), Row("2026-06-05 14:30:00", "15", "23", "T1")]);

        Assert.IsFalse(entries.Single().IsCar1Highlighted);
        Assert.IsFalse(entries.Single().IsCar2Highlighted);
    }

    #endregion

    #region Overridable thresholds

    /// <summary>
    /// Minimal sheet whose "Time" column genuinely fails to convert, which is what the missed-timestamp
    /// counter reacts to. The production sheets all use TryParse and therefore never trip it.
    /// </summary>
    private sealed class ThrowingTimeControlLog(int maxMissedTimestamps, int minimumTimestampYear = 2025)
        : GoogleSheetsControlLogBase(NullLoggerFactory.Instance, new ConfigurationBuilder().Build(), Mock.Of<IDbContextFactory<TsContext>>())
    {
        public override string Type => "test";
        protected override string CellRange => "A1:B100";
        protected override int MaxMissedTimestamps => maxMissedTimestamps;
        protected override int MinimumTimestampYear => minimumTimestampYear;

        protected override SheetColumnMapping[] ColumnMappings { get; } =
        [
            new SheetColumnMapping
            {
                SheetColumn = "Time",
                PropertyName = "Timestamp",
                IsRequired = true,
                Convert = s => DateTime.TryParse(s, out var dt) ? dt : throw new FormatException(s)
            },
            new SheetColumnMapping { SheetColumn = "Car #", PropertyName = "Car1" },
        ];
    }

    [TestMethod]
    public void ParseRows_StopsParsingOnceMissedTimestampsExceedTheLimit()
    {
        using var log = new ThrowingTimeControlLog(maxMissedTimestamps: 1);
        var header = Row("Time", "Car #");
        log.InitializeColumnMappings(header);

        // Two unusable timestamps push the count past the limit, so the good third row is never reached.
        var entries = log.ParseRows([header, Row("junk", "1"), Row("junk", "2"), Row("2026-06-05 14:30:00", "3")]);

        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public void ParseRows_MissedTimestampsBelowTheLimit_DoNotStopParsing()
    {
        using var log = new ThrowingTimeControlLog(maxMissedTimestamps: 5);
        var header = Row("Time", "Car #");
        log.InitializeColumnMappings(header);

        var entries = log.ParseRows([header, Row("junk", "1"), Row("junk", "2"), Row("2026-06-05 14:30:00", "3")]);

        Assert.AreEqual("3", entries.Single().Car1);
    }

    /// <summary>
    /// The minimum timestamp is a year, not a tick count. Written as <c>new DateTime(2025)</c> it bound
    /// the <c>DateTime(long ticks)</c> overload, putting the floor at 0001-01-01 — so the filter only
    /// ever rejected <c>default(DateTime)</c> and a stale row from a reused worksheet was published as
    /// if it were current.
    /// </summary>
    [TestMethod]
    public void ParseRows_RowOlderThanTheMinimumTimestampYear_IsDropped()
    {
        using var log = new ThrowingTimeControlLog(maxMissedTimestamps: 5, minimumTimestampYear: 2025);
        var header = Row("Time", "Car #");
        log.InitializeColumnMappings(header);

        var entries = log.ParseRows([header, Row("1998-05-01 10:00:00", "7"), Row("2026-05-01 10:00:00", "8")]);

        Assert.ContainsSingle(entries);
        Assert.AreEqual(2026, entries.Single().Timestamp.Year);
        Assert.AreEqual("8", entries.Single().Car1);
    }

    #endregion

    #region LoadControlLogAsync guards

    [TestMethod]
    [DataRow("no-semicolon")]
    [DataRow("a;b;c")]
    [DataRow("")]
    public async Task LoadControlLogAsync_MalformedParameter_FailsBeforeTouchingTheDatabase(string parameter)
    {
        var dbFactory = new Mock<IDbContextFactory<TsContext>>(MockBehavior.Strict);
        using var log = new WrlLog(NullLoggerFactory.Instance, new ConfigurationBuilder().Build(), dbFactory.Object);

        var (success, logs) = await log.LoadControlLogAsync(parameter);

        Assert.IsFalse(success);
        Assert.AreEqual(0, logs.Count());
        dbFactory.VerifyNoOtherCalls();
    }

    /// <summary>
    /// With no service-account row the load has to stop at the configuration lookup. Both Google call
    /// sites swallow their exceptions and return the same <c>(false, [])</c> as this guard, so the return
    /// value alone cannot tell the two apart - a guard regression would leave a bare assertion green while
    /// every CI run made a real outbound request to sheets.googleapis.com. The log is what distinguishes
    /// them, so it is asserted on directly.
    /// </summary>
    [TestMethod]
    public async Task LoadControlLogAsync_NoServiceAccountConfigured_FailsAtTheConfigLookupWithoutCallingSheets()
    {
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        // Strict, so anything the load reaches for beyond the one configuration read fails the test.
        var dbFactory = new Mock<IDbContextFactory<TsContext>>(MockBehavior.Strict);
        dbFactory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(new TsContext(options)));
        var (loggerFactory, logged) = RecordingLoggerFactory();
        using var log = new WrlLog(loggerFactory, new ConfigurationBuilder().Build(), dbFactory.Object);

        var (success, logs) = await log.LoadControlLogAsync("sheet-id;Tab 1");

        Assert.IsFalse(success);
        Assert.AreEqual(0, logs.Count());
        Assert.IsTrue(logged.Any(m => m.Contains("Unable to find control log configuration")),
            "The load should have stopped at the missing service-account configuration.");
        Assert.IsFalse(logged.Any(m => m.Contains("spreadsheet metadata") || m.Contains("from Google Sheets")),
            "The load went on to call the Sheets API instead of failing at the configuration guard.");
        dbFactory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Once);
        dbFactory.VerifyNoOtherCalls();
    }

    /// <summary>A logger factory that appends every formatted message to the returned list.</summary>
    private static (ILoggerFactory Factory, List<string> Messages) RecordingLoggerFactory()
    {
        var messages = new List<string>();
        var logger = new Mock<ILogger>();
        logger.Setup(x => x.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                var formatter = invocation.Arguments[4];
                var message = formatter?.GetType().GetMethod("Invoke")?
                    .Invoke(formatter, [invocation.Arguments[2], invocation.Arguments[3] as Exception])?.ToString();
                lock (messages)
                {
                    messages.Add(message ?? string.Empty);
                }
            }));

        var factory = new Mock<ILoggerFactory>();
        factory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(logger.Object);
        return (factory.Object, messages);
    }

    #endregion
}
