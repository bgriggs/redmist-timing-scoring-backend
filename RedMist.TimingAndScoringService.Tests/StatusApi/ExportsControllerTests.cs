using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using Microsoft.AspNetCore.Mvc;
using RedMist.StatusApi.Controllers.V1;
using RedMist.StatusApi.Services.Exports;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using System.Text;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

/// <summary>
/// The Exports controller end to end over an in-memory database: what it advertises, what it
/// refuses, and what the generated files actually contain.
/// </summary>
[TestClass]
public class ExportsControllerTests
{
    private const int EventId = 1;
    private const int SessionId = 10;

    private ExportsControllerHarness _h = null!;

    [TestInitialize]
    public void Setup() => _h = new ExportsControllerHarness();

    [TestCleanup]
    public void Cleanup() => _h.Dispose();

    #region GetAvailability

    [TestMethod]
    public async Task GetAvailability_UnknownSession_ReportsEverythingUnavailable()
    {
        var availability = await AvailabilityAsync(EventId, 999);

        Assert.IsFalse(availability.SessionCompleted);
        Assert.IsFalse(availability.LapDataAvailable);
        Assert.IsFalse(availability.PitReportAvailable);
        Assert.AreEqual(0, availability.CarNumbers.Count);
    }

    /// <summary>
    /// A session with no end time short-circuits: no export can be produced from one, so the endpoint
    /// does not pay for the lap probes to describe it. Everything reads false, which is what the
    /// client needs to hide the options anyway.
    /// </summary>
    [TestMethod]
    public async Task GetAvailability_SessionThatHasNotEnded_ReturnsEverythingFalseWithoutProbingLaps()
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId, isLive: true, ended: false);
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 1, "Alice", "blePuck",
            pitEntry: new DateTime(2026, 5, 1, 15, 0, 0, DateTimeKind.Utc), pitDurationMs: 60_000));
        await _h.SaveAsync();

        var availability = await AvailabilityAsync(EventId, SessionId);

        Assert.IsFalse(availability.SessionCompleted);
        Assert.IsFalse(availability.LapDataAvailable);
        Assert.IsFalse(availability.PitReportAvailable);
        Assert.AreEqual(0, availability.CarNumbers.Count);
    }

    /// <summary>
    /// The answer for a completed session is immutable, so it is cached like every sibling endpoint
    /// caches its session data.
    /// </summary>
    [TestMethod]
    public async Task GetAvailability_CompletedSession_IsServedFromCacheOnTheSecondCall()
    {
        await SeedPlainSessionAsync("42");

        var first = await AvailabilityAsync(EventId, SessionId);
        Assert.IsTrue(first.LapDataAvailable);

        // Removing the rows would change the uncached answer; the cached one must not move.
        _h.Db.CarLapLogs.RemoveRange(_h.Db.CarLapLogs);
        await _h.SaveAsync();

        var second = await AvailabilityAsync(EventId, SessionId);
        Assert.IsTrue(second.LapDataAvailable, "the completed-session answer should have been cached");
    }

    [TestMethod]
    public async Task GetAvailability_CompletedSessionWithLaps_ListsCarsInNaturalOrder()
    {
        await SeedPlainSessionAsync("12", "2", "99x", "1");

        var availability = await AvailabilityAsync(EventId, SessionId);

        Assert.IsTrue(availability.SessionCompleted);
        Assert.IsTrue(availability.LapDataAvailable);
        CollectionAssert.AreEqual(new[] { "1", "2", "12", "99x" }, availability.CarNumbers);
    }

    /// <summary>
    /// Lap 0 is the synthetic "car seen, no lap completed" row. A car that only ever produced one of
    /// those has nothing to export and must not be offered.
    /// </summary>
    [TestMethod]
    public async Task GetAvailability_IgnoresLapZeroRows()
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId);
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("7", 0));
        await _h.SaveAsync();

        var availability = await AvailabilityAsync(EventId, SessionId);

        Assert.IsFalse(availability.LapDataAvailable);
        Assert.AreEqual(0, availability.CarNumbers.Count);
    }

    [TestMethod]
    public async Task GetAvailability_WithoutFlagtronicsData_HasNoPitReport()
    {
        await SeedPlainSessionAsync("42");

        var availability = await AvailabilityAsync(EventId, SessionId);

        Assert.IsTrue(availability.LapDataAvailable);
        Assert.IsFalse(availability.PitReportAvailable);
    }

    /// <summary>
    /// A driver source of "none" is the equipment saying it is fitted but has not identified anyone.
    /// That is not a driver change report.
    /// </summary>
    [TestMethod]
    public async Task GetAvailability_DriverSourceNone_HasNoPitReport()
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId);
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 1, driverSource: "none",
            pitEntry: new DateTime(2026, 5, 1, 15, 0, 0, DateTimeKind.Utc), pitDurationMs: 60_000));
        await _h.SaveAsync();

        var availability = await AvailabilityAsync(EventId, SessionId);

        Assert.IsFalse(availability.PitReportAvailable);
    }

    /// <summary>
    /// Driver identification without any pit entry time is a session where the equipment saw drivers
    /// but no pit data, so there are no stops to report against.
    /// </summary>
    [TestMethod]
    public async Task GetAvailability_DriversButNoPitTimes_HasNoPitReport()
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId);
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 1, "Alice", "blePuck"));
        await _h.SaveAsync();

        var availability = await AvailabilityAsync(EventId, SessionId);

        Assert.IsFalse(availability.PitReportAvailable);
    }

    [TestMethod]
    public async Task GetAvailability_WithDriversAndPitTimes_OffersThePitReport()
    {
        await SeedFlagtronicsSessionAsync();

        var availability = await AvailabilityAsync(EventId, SessionId);

        Assert.IsTrue(availability.SessionCompleted);
        Assert.IsTrue(availability.LapDataAvailable);
        Assert.IsTrue(availability.PitReportAvailable);
    }

    /// <summary>
    /// Documented trade-off, pinned rather than fixed: the driver probe only looks at each car's
    /// opening laps, because an uncapped substring scan over a whole endurance session is hundreds of
    /// megabytes of I/O to answer "no" for the many sessions that have no Flagtronics at all.
    /// Equipment that appears only later in a session is therefore not detected - and the report
    /// endpoint refuses on the same check, so the client is never offered something it cannot have.
    /// </summary>
    [TestMethod]
    public async Task GetAvailability_FlagtronicsDataOnlyAfterTheProbeDepth_IsNotDetected()
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId);
        for (var lap = 1; lap <= ExportsController.DriverProbeLapDepth; lap++)
            _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", lap));
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", ExportsController.DriverProbeLapDepth + 1,
            "Alice", "blePuck", pitEntry: new DateTime(2026, 5, 1, 15, 0, 0, DateTimeKind.Utc), pitDurationMs: 60_000));
        await _h.SaveAsync();

        var availability = await AvailabilityAsync(EventId, SessionId);

        Assert.IsFalse(availability.PitReportAvailable);

        var report = await _h.Controller.GetPitStops(EventId, SessionId, "json");
        AssertStatus(report, StatusCodes.Status404NotFound);
    }

    #endregion

    #region GetCarLaps

    /// <summary>
    /// The real state this feature has to cope with: a session that plainly ended - it has an end
    /// time and a full session of laps - while its live flag was never cleared, because the session
    /// monitor is what clears it and a monitor that dies leaves it stuck true. Event 5 session 68 in
    /// the test environment is exactly this. Keying on the flag made the feature silently unavailable
    /// for those sessions.
    /// </summary>
    [TestMethod]
    public async Task GetAvailability_EndedButStillFlaggedLive_ReportsCompletedWithItsCars()
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId, name: "Sunday 7 Hour", isLive: true, ended: true);
        SeedPittingCar("42", "Alice", "Bob");
        await _h.SaveAsync();

        var availability = await AvailabilityAsync(EventId, SessionId);

        Assert.IsTrue(availability.SessionCompleted);
        Assert.IsTrue(availability.LapDataAvailable);
        Assert.IsTrue(availability.PitReportAvailable);
        CollectionAssert.AreEqual(new[] { "42" }, availability.CarNumbers);
    }

    /// <summary>
    /// The other half of the union: a session whose row was orphaned before an end time was ever
    /// written - the processor dies mid-session, so finalization writes no end time while the flag
    /// still gets cleared - plus any row old enough to predate the column being populated. These
    /// exported before this rule existed and must keep exporting.
    /// </summary>
    [TestMethod]
    public async Task GetAvailability_NoEndTimeButNotFlaggedLive_IsStillExportable()
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId, isLive: false, ended: false);
        SeedPittingCar("42", "Alice", "Bob");
        await _h.SaveAsync();

        var availability = await AvailabilityAsync(EventId, SessionId);

        Assert.IsTrue(availability.SessionCompleted);
        Assert.IsTrue(availability.LapDataAvailable);
        CollectionAssert.AreEqual(new[] { "42" }, availability.CarNumbers);

        var laps = Assert.IsInstanceOfType<FileStreamResult>(
            await _h.Controller.GetCarLaps(EventId, SessionId, "42", "csv"));
        await laps.FileStream.DisposeAsync();
    }

    /// <summary>
    /// The stale-entry sequence the bypass alone did not cover: the session ends cleanly and is
    /// cached, is picked up again, then ends again. Without dropping the entry when the flag is seen
    /// set, that third call serves the pre-resume answer - so a session that first ended with no cars
    /// would keep reporting no lap data long after the real race had run.
    /// </summary>
    [TestMethod]
    public async Task GetAvailability_EndedThenResumedThenEndedAgain_DoesNotServeThePreResumeAnswer()
    {
        _h.AddEvent(EventId);
        var session = _h.AddSession(EventId, SessionId, isLive: false, ended: true);
        await _h.SaveAsync();

        // 1. Ends with nothing to export; that answer is cached.
        Assert.IsFalse((await AvailabilityAsync(EventId, SessionId)).LapDataAvailable);

        // 2. Picked up again and the real race runs.
        session.IsLive = true;
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 1));
        await _h.SaveAsync();
        Assert.IsTrue((await AvailabilityAsync(EventId, SessionId)).LapDataAvailable);

        // 3. Ends again. The pre-resume answer must not come back.
        session.IsLive = false;
        await _h.SaveAsync();

        var afterSecondEnd = await AvailabilityAsync(EventId, SessionId);
        Assert.IsTrue(afterSecondEnd.LapDataAvailable, "the cached pre-resume answer should have been dropped");
        CollectionAssert.AreEqual(new[] { "42" }, afterSecondEnd.CarNumbers);
    }

    /// <summary>
    /// That answer is deliberately not cached: a session still flagged live may have been picked up
    /// again and may still be gaining laps, and the cache is the one part that would keep serving a
    /// stale answer after they arrived.
    /// </summary>
    [TestMethod]
    public async Task GetAvailability_EndedButStillFlaggedLive_IsNotCached()
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId, isLive: true, ended: true);
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 1));
        await _h.SaveAsync();

        Assert.IsTrue((await AvailabilityAsync(EventId, SessionId)).LapDataAvailable);

        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("7", 1));
        await _h.SaveAsync();

        var second = await AvailabilityAsync(EventId, SessionId);
        CollectionAssert.AreEqual(new[] { "7", "42" }, second.CarNumbers, "a session that may still be running must be re-read");
    }

    [TestMethod]
    public async Task GetCarLaps_UnknownSession_Is404()
    {
        var result = await _h.Controller.GetCarLaps(EventId, 999, "42", "json");

        AssertStatus(result, StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// The file endpoints use the same rule, so a session the availability check offers can actually
    /// produce its reports. Event 5 session 68 again.
    /// </summary>
    [TestMethod]
    public async Task FileEndpoints_EndedButStillFlaggedLive_AreServed()
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId, isLive: true, ended: true);
        SeedPittingCar("42", "Alice", "Bob");
        await _h.SaveAsync();

        var laps = Assert.IsInstanceOfType<FileStreamResult>(
            await _h.Controller.GetCarLaps(EventId, SessionId, "42", "csv"));
        await laps.FileStream.DisposeAsync();

        var pits = Assert.IsInstanceOfType<FileStreamResult>(
            await _h.Controller.GetPitStops(EventId, SessionId, "json"));
        await pits.FileStream.DisposeAsync();
    }

    [TestMethod]
    public async Task GetCarLaps_SessionThatHasNotEnded_Is404()
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId, isLive: true, ended: false);
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 1));
        await _h.SaveAsync();

        var result = await _h.Controller.GetCarLaps(EventId, SessionId, "42", "json");

        AssertStatus(result, StatusCodes.Status404NotFound);
    }

    [TestMethod]
    public async Task GetCarLaps_UnknownCar_Is404()
    {
        await SeedPlainSessionAsync("42");

        var result = await _h.Controller.GetCarLaps(EventId, SessionId, "77", "json");

        AssertStatus(result, StatusCodes.Status404NotFound);
    }

    [TestMethod]
    public async Task GetCarLaps_UnsupportedFormat_Is400()
    {
        await SeedPlainSessionAsync("42");

        var result = await _h.Controller.GetCarLaps(EventId, SessionId, "42", "xlsx");

        AssertStatus(result, StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// The JSON export is the stored payload copied through unchanged, so what comes out must still
    /// deserialize as the lap snapshots that went in.
    /// </summary>
    [TestMethod]
    public async Task GetCarLaps_Json_WrapsTheStoredLapPayloadsVerbatim()
    {
        await SeedPlainSessionAsync("42");

        var result = await _h.Controller.GetCarLaps(EventId, SessionId, "42", "json");
        var (text, file) = await ReadFileAsync(result);

        Assert.AreEqual("application/json", file.ContentType);
        Assert.AreEqual("event-1-session-10-car-42-laps.json", file.FileDownloadName);

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.AreEqual(EventId, root.GetProperty("eventId").GetInt32());
        Assert.AreEqual(SessionId, root.GetProperty("sessionId").GetInt32());
        Assert.AreEqual("42", root.GetProperty("carNumber").GetString());
        Assert.AreEqual(3, root.GetProperty("lapCount").GetInt32());
        Assert.IsFalse(root.GetProperty("truncated").GetBoolean());

        var laps = root.GetProperty("laps");
        Assert.AreEqual(3, laps.GetArrayLength());
        var first = JsonSerializer.Deserialize<CarPosition>(laps[0].GetRawText());
        Assert.AreEqual("42", first!.Number);
        Assert.AreEqual(1, first.LastLapCompleted);
    }

    /// <summary>
    /// The downloaded JSON is meant to be opened and read, so it is indented - and that has to reach
    /// the lap payloads too. They are stored compact, so injecting them as raw text would leave every
    /// lap as one long line inside an otherwise indented document, which is the only part anybody
    /// actually wants to read.
    /// </summary>
    [TestMethod]
    public async Task GetCarLaps_Json_IsIndentedIncludingTheLapPayloads()
    {
        await SeedPlainSessionAsync("42");

        var result = await _h.Controller.GetCarLaps(EventId, SessionId, "42", "json");
        var (text, _) = await ReadFileAsync(result);

        var lines = SplitLines(text);
        Assert.IsTrue(lines.Any(l => l.StartsWith("  \"eventId\"")), "the envelope should be indented");

        // "n" is the stored short name for the car number, so finding it on its own line, indented
        // deeper than the array, proves the nested payload was re-indented rather than injected as
        // raw text - which would have left the whole lap on one line.
        Assert.IsTrue(lines.Any(l => l.StartsWith("      \"n\": \"42\"")),
            "each lap payload should be expanded and indented inside the laps array");

        // Still valid JSON that round-trips to the same laps.
        using var document = JsonDocument.Parse(text);
        Assert.AreEqual(3, document.RootElement.GetProperty("laps").GetArrayLength());
    }

    [TestMethod]
    public async Task GetPitStops_Json_IsIndented()
    {
        await SeedFlagtronicsSessionAsync();

        var result = await _h.Controller.GetPitStops(EventId, SessionId, "json");
        var (text, _) = await ReadFileAsync(result);

        var lines = SplitLines(text);
        Assert.IsTrue(lines.Any(l => l.StartsWith("  \"eventId\"")));
        Assert.IsTrue(lines.Any(l => l.StartsWith("      \"carNumber\"")));
        using var document2 = JsonDocument.Parse(text);
        Assert.AreEqual(1, document2.RootElement.GetProperty("pitStops").GetArrayLength());
    }

    /// <summary>
    /// The pretty-printing exists so these files can be opened and read, and driver names are the
    /// field a person actually reads. The HTML-safe default encoder would turn a routine club-racing
    /// surname into escape sequences - O\u0027Brien, Jos\u00E9 M\u00FCller - which would have made
    /// the indented file harder to read than the compact one it replaced.
    /// </summary>
    [TestMethod]
    public async Task GetCarLaps_Json_LeavesDriverNamesReadable()
    {
        const string driver = "O'Brien Jos\u00E9 M\u00FCller & Co <x>";
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId);
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 1, driver, "blePuck"));
        await _h.SaveAsync();

        var result = await _h.Controller.GetCarLaps(EventId, SessionId, "42", "json");
        var (text, _) = await ReadFileAsync(result);

        StringAssert.Contains(text, driver, "the driver name should appear literally, not escaped");
        Assert.IsFalse(text.Contains("\\u00", StringComparison.OrdinalIgnoreCase), "nothing should be unicode-escaped");

        // And it is still valid JSON that round-trips to the same name.
        using var document = JsonDocument.Parse(text);
        var lap = JsonSerializer.Deserialize<CarPosition>(
            document.RootElement.GetProperty("laps")[0].GetRawText());
        Assert.AreEqual(driver, lap!.DriverName);
    }

    [TestMethod]
    public async Task GetPitStops_Json_LeavesDriverNamesReadable()
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId);
        SeedPittingCar("42", "O'Brien", "Jos\u00E9 M\u00FCller");
        await _h.SaveAsync();

        var result = await _h.Controller.GetPitStops(EventId, SessionId, "json");
        var (text, _) = await ReadFileAsync(result);

        StringAssert.Contains(text, "O'Brien");
        StringAssert.Contains(text, "Jos\u00E9 M\u00FCller");
    }

    /// <summary>
    /// A session that ended but is still flagged live is exported - refusing it is what made the
    /// feature useless for event 5 session 68 - but the file has to admit it might be partial, since
    /// nothing about the rows would show it.
    /// </summary>
    [TestMethod]
    public async Task GetCarLaps_EndedButStillFlaggedLive_MarksTheFile()
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId, isLive: true, ended: true);
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 1));
        await _h.SaveAsync();

        var json = await _h.Controller.GetCarLaps(EventId, SessionId, "42", "json");
        var (jsonText, _) = await ReadFileAsync(json);
        using var document = JsonDocument.Parse(jsonText);
        Assert.IsTrue(document.RootElement.GetProperty("sessionStillLive").GetBoolean());

        var csv = await _h.Controller.GetCarLaps(EventId, SessionId, "42", "csv");
        var (csvText, _) = await ReadFileAsync(csv);
        StringAssert.Contains(csvText, LapExportWriter.CsvStillLiveMarker);
    }

    [TestMethod]
    public async Task GetPitStops_EndedButStillFlaggedLive_MarksTheFile()
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId, isLive: true, ended: true);
        SeedPittingCar("42", "Alice", "Bob");
        await _h.SaveAsync();

        var json = await _h.Controller.GetPitStops(EventId, SessionId, "json");
        var (jsonText, _) = await ReadFileAsync(json);
        using var document = JsonDocument.Parse(jsonText);
        Assert.IsTrue(document.RootElement.GetProperty("sessionStillLive").GetBoolean());

        var csv = await _h.Controller.GetPitStops(EventId, SessionId, "csv");
        var (csvText, _) = await ReadFileAsync(csv);
        StringAssert.Contains(csvText, PitStopReportWriter.CsvStillLiveMarker);
    }

    /// <summary>An ordinary finished session carries the flag as false, so the field is stable.</summary>
    [TestMethod]
    public async Task GetCarLaps_OrdinarySession_ReportsNotStillLive()
    {
        await SeedPlainSessionAsync("42");

        var result = await _h.Controller.GetCarLaps(EventId, SessionId, "42", "json");
        var (text, _) = await ReadFileAsync(result);

        using var document = JsonDocument.Parse(text);
        Assert.IsFalse(document.RootElement.GetProperty("sessionStillLive").GetBoolean());
    }

    /// <summary>
    /// A row written by an older build that no longer parses must cost that lap, not the download.
    /// </summary>
    [TestMethod]
    public async Task GetCarLaps_Json_SkipsUnreadableRowsAndSaysSo()
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId);
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 1));
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 2), rawLapData: "{ not json");
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 3));
        await _h.SaveAsync();

        var result = await _h.Controller.GetCarLaps(EventId, SessionId, "42", "json");
        var (text, _) = await ReadFileAsync(result);

        using var document = JsonDocument.Parse(text);
        Assert.AreEqual(2, document.RootElement.GetProperty("lapCount").GetInt32());
        Assert.AreEqual(1, document.RootElement.GetProperty("skippedRows").GetInt32());
    }

    /// <summary>
    /// The CSV path drops unreadable rows in the row source, not in the writer, so nothing used to
    /// count them: the file jumped from lap 1 to lap 3 while its Content-Disposition promised the
    /// session, and the log said zero skipped. <c>CarLapLog.LapData</c> is capped at 5000 characters,
    /// so a stored payload that no longer parses is a live possibility rather than a hypothetical.
    /// </summary>
    [TestMethod]
    public async Task GetCarLaps_Csv_MarksRowsItCouldNotRead()
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId);
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 1));
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 2), rawLapData: "{ truncated");
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 3));
        await _h.SaveAsync();

        var result = await _h.Controller.GetCarLaps(EventId, SessionId, "42", "csv");
        var (text, _) = await ReadFileAsync(result);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.AreEqual(4, lines.Length, "header, two readable laps, and the skipped marker");
        StringAssert.StartsWith(lines[^1], LapExportWriter.CsvSkippedMarker);
        StringAssert.Contains(lines[^1], "1 lap row");
    }

    [TestMethod]
    public async Task GetPitStops_Csv_MarksLapRowsItCouldNotRead()
    {
        var entry = new DateTime(2026, 5, 1, 15, 0, 0, DateTimeKind.Utc);
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId);
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 1, "Alice", "blePuck"));
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 2, "Alice", "blePuck"),
            rawLapData: "{ truncated");
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 3, "Bob", "blePuck",
            pitEntry: entry, pitDurationMs: 60_000));
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 4, "Bob", "blePuck",
            pitEntry: entry, pitDurationMs: 60_000));
        await _h.SaveAsync();

        var result = await _h.Controller.GetPitStops(EventId, SessionId, "csv");
        var (text, _) = await ReadFileAsync(result);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        StringAssert.StartsWith(lines[^1], PitStopReportWriter.CsvSkippedMarker);
        StringAssert.Contains(lines[^1], "may be wrong or missing");
    }

    /// <summary>
    /// The JSON envelope counts rows the source dropped as well as ones its own validation rejected.
    /// </summary>
    [TestMethod]
    public async Task GetPitStops_Json_ReportsSkippedLapRows()
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId);
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 1, "Alice", "blePuck"));
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 2, "Alice", "blePuck"),
            rawLapData: "{ truncated");
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 3, "Bob", "blePuck",
            pitEntry: new DateTime(2026, 5, 1, 15, 0, 0, DateTimeKind.Utc), pitDurationMs: 60_000));
        await _h.SaveAsync();

        var result = await _h.Controller.GetPitStops(EventId, SessionId, "json");
        var (text, _) = await ReadFileAsync(result);

        using var document = JsonDocument.Parse(text);
        Assert.AreEqual(1, document.RootElement.GetProperty("skippedLapRows").GetInt32());
    }

    [TestMethod]
    public async Task GetCarLaps_Csv_HasAHeaderAndOneRowPerLap()
    {
        await SeedPlainSessionAsync("42");

        var result = await _h.Controller.GetCarLaps(EventId, SessionId, "42", "csv");
        var (text, file) = await ReadFileAsync(result);

        Assert.AreEqual("text/csv", file.ContentType);
        Assert.AreEqual("event-1-session-10-car-42-laps.csv", file.FileDownloadName);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        StringAssert.Contains(lines[0], "CarNumber,Lap,TimestampUtc");
        Assert.AreEqual(4, lines.Length, "header plus three laps");
        StringAssert.StartsWith(lines[1], "42,1,");
    }

    [TestMethod]
    public async Task GetCarLaps_AllCars_WhenCarNumberIsOmitted()
    {
        await SeedPlainSessionAsync("42", "7");

        var result = await _h.Controller.GetCarLaps(EventId, SessionId, null, "csv");
        var (text, file) = await ReadFileAsync(result);

        Assert.AreEqual("event-1-session-10-laps.csv", file.FileDownloadName);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(7, lines.Length, "header plus three laps for each of two cars");
    }

    /// <summary>An empty car number is the same request as no car number at all.</summary>
    [TestMethod]
    public async Task GetCarLaps_BlankCarNumber_IsTreatedAsAllCars()
    {
        await SeedPlainSessionAsync("42", "7");

        var result = await _h.Controller.GetCarLaps(EventId, SessionId, "   ", "csv");
        var (_, file) = await ReadFileAsync(result);

        Assert.AreEqual("event-1-session-10-laps.csv", file.FileDownloadName);
    }

    [TestMethod]
    public async Task GetCarLaps_Pdf_ProducesAPdfDocument()
    {
        await SeedPlainSessionAsync("42");

        var result = await _h.Controller.GetCarLaps(EventId, SessionId, "42", "pdf");
        var file = Assert.IsInstanceOfType<FileStreamResult>(result);
        Assert.AreEqual("application/pdf", file.ContentType);
        Assert.AreEqual("event-1-session-10-car-42-laps.pdf", file.FileDownloadName);

        var header = new byte[5];
        await using (var stream = file.FileStream)
        {
            Assert.AreEqual(5, await stream.ReadAtLeastAsync(header, 5, throwOnEndOfStream: false));
        }

        Assert.AreEqual("%PDF-", Encoding.ASCII.GetString(header));
    }

    /// <summary>
    /// The export is built on disk and handed back as a delete-on-close handle, so the file has to be
    /// gone once the response stream is disposed - which is what MVC does after writing the body, and
    /// also what it does when a client disconnects mid-download.
    /// </summary>
    [TestMethod]
    public async Task GetCarLaps_TempFile_IsDeletedWhenTheResponseStreamIsDisposed()
    {
        await SeedPlainSessionAsync("42");

        var result = await _h.Controller.GetCarLaps(EventId, SessionId, "42", "csv");
        var file = Assert.IsInstanceOfType<FileStreamResult>(result);
        var path = ExportsControllerHarness.StagedPath(file.FileStream);

        Assert.IsNotNull(path);
        Assert.IsTrue(File.Exists(path), "the export should exist while the response stream is open");

        await file.FileStream.DisposeAsync();

        Assert.IsFalse(File.Exists(path), "the export should be removed when the stream closes");
    }

    #endregion

    #region GetPitStops

    [TestMethod]
    public async Task GetPitStops_WithoutFlagtronicsData_Is404()
    {
        await SeedPlainSessionAsync("42");

        var result = await _h.Controller.GetPitStops(EventId, SessionId, "json");

        AssertStatus(result, StatusCodes.Status404NotFound);
    }

    [TestMethod]
    public async Task GetPitStops_UnknownSession_Is404()
    {
        var result = await _h.Controller.GetPitStops(EventId, 999, "json");

        AssertStatus(result, StatusCodes.Status404NotFound);
    }

    [TestMethod]
    public async Task GetPitStops_Json_ReportsTheDerivedStops()
    {
        await SeedFlagtronicsSessionAsync();

        var result = await _h.Controller.GetPitStops(EventId, SessionId, "json");
        var (text, file) = await ReadFileAsync(result);

        Assert.AreEqual("application/json", file.ContentType);
        Assert.AreEqual("event-1-session-10-pit-stops.json", file.FileDownloadName);

        using var document = JsonDocument.Parse(text);
        var stops = document.RootElement.GetProperty("pitStops");
        Assert.AreEqual(1, stops.GetArrayLength());
        Assert.AreEqual("42", stops[0].GetProperty("carNumber").GetString());
        Assert.AreEqual("Alice", stops[0].GetProperty("driverBefore").GetString());
        Assert.AreEqual("Bob", stops[0].GetProperty("driverAfter").GetString());
        Assert.IsTrue(stops[0].GetProperty("driverChanged").GetBoolean());
        Assert.AreEqual(60_000, stops[0].GetProperty("pitDurationMs").GetInt32());
    }

    /// <summary>
    /// The scan streams laps grouped by car, so each car's stops have to come out attributed to that
    /// car and not run together across the boundary.
    /// </summary>
    [TestMethod]
    public async Task GetPitStops_Csv_KeepsCarsSeparate()
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId);
        SeedPittingCar("42", "Alice", "Bob");
        SeedPittingCar("7", "Carol", "Dave");
        await _h.SaveAsync();

        var result = await _h.Controller.GetPitStops(EventId, SessionId, "csv");
        var (text, _) = await ReadFileAsync(result);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(3, lines.Length, "header plus one stop for each of two cars");
        Assert.AreEqual(1, lines.Count(l => l.StartsWith("42,")));
        Assert.AreEqual(1, lines.Count(l => l.StartsWith("7,")));
        StringAssert.Contains(text, "Alice,Bob");
        StringAssert.Contains(text, "Carol,Dave");
    }

    /// <summary>
    /// The lap number in the report comes from the row's own column, which is what the query filtered
    /// and ordered on and what the lap exports print, rather than from the payload - so the two
    /// exports of the same session always agree on which lap a stop was.
    /// </summary>
    [TestMethod]
    public async Task GetPitStops_TakesTheLapNumberFromTheRowColumnNotThePayload()
    {
        var entry = new DateTime(2026, 5, 1, 15, 0, 0, DateTimeKind.Utc);
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId);
        _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 1, "Alice", "blePuck"));

        // A payload whose LastLapCompleted disagrees with the row it was stored on.
        var pitLap = ExportsControllerHarness.Lap("42", 2, "Bob", "blePuck", pitEntry: entry, pitDurationMs: 60_000);
        var stale = ExportsControllerHarness.Lap("42", 999, "Bob", "blePuck", pitEntry: entry, pitDurationMs: 60_000);
        _h.AddLap(EventId, SessionId, pitLap, rawLapData: JsonSerializer.Serialize(stale));
        await _h.SaveAsync();

        var result = await _h.Controller.GetPitStops(EventId, SessionId, "json");
        var (text, _) = await ReadFileAsync(result);

        using var document = JsonDocument.Parse(text);
        var stops = document.RootElement.GetProperty("pitStops");
        Assert.AreEqual(1, stops.GetArrayLength());
        Assert.AreEqual(2, stops[0].GetProperty("lap").GetInt32());
    }

    [TestMethod]
    public async Task GetPitStops_Pdf_ProducesAPdfDocument()
    {
        await SeedFlagtronicsSessionAsync();

        var result = await _h.Controller.GetPitStops(EventId, SessionId, "pdf");
        var file = Assert.IsInstanceOfType<FileStreamResult>(result);

        var header = new byte[5];
        await using (var stream = file.FileStream)
        {
            Assert.AreEqual(5, await stream.ReadAtLeastAsync(header, 5, throwOnEndOfStream: false));
        }

        Assert.AreEqual("%PDF-", Encoding.ASCII.GetString(header));
    }

    #endregion

    #region Concurrency limit

    /// <summary>
    /// With every generation slot taken the request is turned away rather than queued, and it says
    /// when to come back. Queueing would let a burst of exports pile up in a pod that is also
    /// carrying the live SignalR feed, which is the outcome the limit exists to prevent.
    /// </summary>
    [TestMethod]
    public async Task GetCarLaps_WhenAllGenerationSlotsAreTaken_Is503WithRetryAfter()
    {
        using var harness = new ExportsControllerHarness(maxConcurrentExports: 1, waitTimeout: TimeSpan.FromMilliseconds(20));
        harness.AddEvent(EventId);
        harness.AddSession(EventId, SessionId);
        harness.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 1));
        await harness.SaveAsync();

        using var held = await harness.Limiter.TryAcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None)
            ?? throw new InvalidOperationException("expected the first slot to be free");

        var result = await harness.Controller.GetCarLaps(EventId, SessionId, "42", "json");

        AssertStatus(result, StatusCodes.Status503ServiceUnavailable);
        Assert.AreEqual(ExportConcurrencyLimiter.RetryAfterSeconds.ToString(),
            harness.Controller.Response.Headers.RetryAfter.ToString());
    }

    /// <summary>
    /// PDF gets a narrower lane than JSON and CSV. The binding resource is CPU, not memory: this pod
    /// is limited to 300m and a report measures at roughly 740ms of unthrottled CPU, which at that
    /// quota is seconds of throttling for the SignalR hub the pod exists to run.
    /// </summary>
    [TestMethod]
    public async Task GetCarLaps_Pdf_IsRefusedWhileAnotherPdfIsRendering()
    {
        using var harness = new ExportsControllerHarness(maxConcurrentExports: 4,
            waitTimeout: TimeSpan.FromMilliseconds(20), maxConcurrentPdfRenders: 1);
        harness.AddEvent(EventId);
        harness.AddSession(EventId, SessionId);
        harness.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 1));
        await harness.SaveAsync();

        using var held = await harness.Limiter.TryAcquireAsync(ExportFormat.Pdf, CancellationToken.None)
            ?? throw new InvalidOperationException("expected the PDF lane to be free");

        AssertStatus(await harness.Controller.GetCarLaps(EventId, SessionId, "42", "pdf"),
            StatusCodes.Status503ServiceUnavailable);

        // The general slots are still free, so the cheap formats are unaffected.
        var csv = await harness.Controller.GetCarLaps(EventId, SessionId, "42", "csv");
        var file = Assert.IsInstanceOfType<FileStreamResult>(csv);
        await file.FileStream.DisposeAsync();
    }

    /// <summary>
    /// Finished files wait on disk while their readers download them, and the permit is released
    /// before that happens. Past a ceiling on those staged bytes the next export is refused, because
    /// the rate limiter cannot bound this on its own - it partitions on a client IP read from
    /// request headers that a caller can vary per request.
    /// </summary>
    [TestMethod]
    public async Task GetCarLaps_WhenTooManyBytesAreStaged_Is503()
    {
        using var harness = new ExportsControllerHarness(maxStagedBytes: 32);
        harness.AddEvent(EventId);
        harness.AddSession(EventId, SessionId);
        harness.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 1));
        await harness.SaveAsync();

        var first = await harness.Controller.GetCarLaps(EventId, SessionId, "42", "csv");
        var held = Assert.IsInstanceOfType<FileStreamResult>(first);
        Assert.IsTrue(harness.StagedExports.StagedBytes > 0);

        AssertStatus(await harness.Controller.GetCarLaps(EventId, SessionId, "42", "csv"),
            StatusCodes.Status503ServiceUnavailable);

        // Draining the first download frees the capacity again.
        await held.FileStream.DisposeAsync();
        Assert.AreEqual(0, harness.StagedExports.StagedBytes);

        var third = await harness.Controller.GetCarLaps(EventId, SessionId, "42", "csv");
        var file = Assert.IsInstanceOfType<FileStreamResult>(third);
        await file.FileStream.DisposeAsync();
    }

    /// <summary>
    /// The permit has to come back when generation throws, or the second failure takes the replica
    /// export capability out for the life of the process.
    /// </summary>
    [TestMethod]
    public async Task GetCarLaps_WhenGenerationThrows_ReleasesItsSlot()
    {
        // Validation shares one context; the row source asks for a second and gets an error, which is
        // a database failure partway through generation - after the request has already been accepted.
        using var harness = new ExportsControllerHarness(
            dbFactory: inner => new FailAfterNContextsFactory(inner, 1));
        harness.AddEvent(EventId);
        harness.AddSession(EventId, SessionId);
        harness.AddLap(EventId, SessionId, ExportsControllerHarness.Lap("42", 1));
        await harness.SaveAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await harness.Controller.GetCarLaps(EventId, SessionId, "42", "csv"));

        Assert.AreEqual(harness.Limiter.MaxConcurrentExports, harness.Limiter.AvailableSlots);
        Assert.AreEqual(harness.Limiter.MaxConcurrentPdfRenders, harness.Limiter.AvailablePdfSlots);
        Assert.AreEqual(0, harness.StagedExports.StagedBytes, "a failed export must not be counted as staged");
    }

    [TestMethod]
    public async Task GetPitStops_WhenAllGenerationSlotsAreTaken_Is503()
    {
        using var harness = new ExportsControllerHarness(maxConcurrentExports: 1, waitTimeout: TimeSpan.FromMilliseconds(20));
        harness.AddEvent(EventId);
        harness.AddSession(EventId, SessionId);
        SeedPittingCar(harness, "42", "Alice", "Bob");
        await harness.SaveAsync();

        using var held = await harness.Limiter.TryAcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None)
            ?? throw new InvalidOperationException("expected the first slot to be free");

        var result = await harness.Controller.GetPitStops(EventId, SessionId, "json");

        AssertStatus(result, StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// The success path returns its slot. The failure path is covered by
    /// <see cref="GetCarLaps_WhenGenerationThrows_ReleasesItsSlot"/>.
    /// </summary>
    [TestMethod]
    public async Task GetCarLaps_ReleasesItsSlotAfterGenerating()
    {
        await SeedPlainSessionAsync("42");

        var result = await _h.Controller.GetCarLaps(EventId, SessionId, "42", "csv");
        var file = Assert.IsInstanceOfType<FileStreamResult>(result);
        await file.FileStream.DisposeAsync();

        Assert.AreEqual(_h.Limiter.MaxConcurrentExports, _h.Limiter.AvailableSlots);
    }

    #endregion

    #region Helpers

    private async Task<ExportAvailability> AvailabilityAsync(int eventId, int sessionId)
    {
        var response = await _h.Controller.GetAvailability(eventId, sessionId);
        var ok = Assert.IsInstanceOfType<OkObjectResult>(response.Result);
        return Assert.IsInstanceOfType<ExportAvailability>(ok.Value);
    }

    /// <summary>Splits file text into lines, tolerating either line ending.</summary>
    private static string[] SplitLines(string text) =>
        text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

    private static void AssertStatus(IActionResult result, int expected)
    {
        var objectResult = Assert.IsInstanceOfType<ObjectResult>(result);
        Assert.AreEqual(expected, objectResult.StatusCode);
    }

    private static async Task<(string Text, FileStreamResult File)> ReadFileAsync(IActionResult result)
    {
        var file = Assert.IsInstanceOfType<FileStreamResult>(result);
        await using var stream = file.FileStream;
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync();
        return (text, file);
    }

    private async Task SeedPlainSessionAsync(params string[] carNumbers)
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId);
        foreach (var car in carNumbers)
        {
            for (var lap = 1; lap <= 3; lap++)
                _h.AddLap(EventId, SessionId, ExportsControllerHarness.Lap(car, lap, lapTime: "1:32.104"));
        }
        await _h.SaveAsync();
    }

    private async Task SeedFlagtronicsSessionAsync()
    {
        _h.AddEvent(EventId);
        _h.AddSession(EventId, SessionId);
        SeedPittingCar("42", "Alice", "Bob");
        await _h.SaveAsync();
    }

    private void SeedPittingCar(string car, string driverBefore, string driverAfter) =>
        SeedPittingCar(_h, car, driverBefore, driverAfter);

    /// <summary>
    /// Four laps with one stop in the middle: two on the first driver, the pit lap, then a lap that
    /// confirms the new driver.
    /// </summary>
    private static void SeedPittingCar(ExportsControllerHarness harness, string car, string driverBefore, string driverAfter)
    {
        var entry = new DateTime(2026, 5, 1, 15, 0, 0, DateTimeKind.Utc);
        harness.AddLap(EventId, SessionId, ExportsControllerHarness.Lap(car, 1, driverBefore, "blePuck"));
        harness.AddLap(EventId, SessionId, ExportsControllerHarness.Lap(car, 2, driverBefore, "blePuck"));
        harness.AddLap(EventId, SessionId, ExportsControllerHarness.Lap(car, 3, driverAfter, "blePuck",
            pitEntry: entry, pitDurationMs: 60_000, lapIncludedPit: true));
        harness.AddLap(EventId, SessionId, ExportsControllerHarness.Lap(car, 4, driverAfter, "blePuck",
            pitEntry: entry, pitDurationMs: 60_000));
    }

    #endregion

    /// <summary>
    /// Hands out working contexts until the given count, then fails. That is how a database error
    /// partway through generation - after the validation queries have already passed - is reproduced.
    /// </summary>
    private sealed class FailAfterNContextsFactory : IDbContextFactory<TsContext>
    {
        private readonly IDbContextFactory<TsContext> inner;
        private readonly int allowed;
        private int created;

        public FailAfterNContextsFactory(IDbContextFactory<TsContext> inner, int allowed)
        {
            this.inner = inner;
            this.allowed = allowed;
        }

        public TsContext CreateDbContext() => Next() ?? throw new InvalidOperationException("database gone");

        public async ValueTask<TsContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Next() ?? throw new InvalidOperationException("database gone");

        private TsContext? Next() =>
            Interlocked.Increment(ref created) <= allowed ? inner.CreateDbContext() : null;
    }
}
