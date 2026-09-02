using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.TimingCommon.Models;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace RedMist.StatusApi.Services.Exports;

/// <summary>
/// Reads lap rows out of the database for the export endpoints, a row at a time.
/// </summary>
/// <remarks>
/// <para>
/// Every query here is <c>AsNoTracking</c> and enumerated with <c>AsAsyncEnumerable</c> so rows
/// arrive from the data reader as the file is written and are dropped again immediately. Loading a
/// session with <c>ToListAsync</c> - which is what the interactive endpoints do, because they return
/// one car's laps to a phone - would put an entire endurance race on the heap of a pod that is also
/// running the live SignalR hub.
/// </para>
/// <para>
/// The ordering is <c>CarNumber</c> then <c>LapNumber</c> to match
/// <c>IX_CarLapLogs_EventId_SessionId_CarNumber_LapNumber</c>, so the rows come off the index in
/// order and Postgres never has to sort - and never has to materialize the result set to do it.
/// </para>
/// </remarks>
public static class ExportDataSource
{
    /// <summary>
    /// The rows an export covers: completed laps in one session, optionally for one car, unordered.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="LapQuery"/> because the existence and DISTINCT checks must not
    /// carry an ORDER BY. PostgreSQL rejects a SELECT DISTINCT whose ordering expressions are not in
    /// the select list, and the EF InMemory provider the tests run against would never show it.
    /// </remarks>
    /// <param name="db">The context to query.</param>
    /// <param name="eventId">The event.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="carNumber">A single car, or null/empty for every car.</param>
    /// <returns>The unordered, untracked query.</returns>
    public static IQueryable<CarLapLog> LapFilter(TsContext db, int eventId, int sessionId, string? carNumber)
    {
        // Lap 0 is the synthetic "car seen, no lap completed yet" row the processor writes. It is not
        // a lap and would show up in an export as a phantom first lap for every car.
        IQueryable<CarLapLog> query = db.CarLapLogs.AsNoTracking()
            .Where(l => l.EventId == eventId && l.SessionId == sessionId && l.LapNumber > 0);

        if (!string.IsNullOrWhiteSpace(carNumber))
            query = query.Where(l => l.CarNumber == carNumber);

        return query;
    }

    /// <summary>
    /// The base query for an export: <see cref="LapFilter"/> in the order the rows are written out,
    /// which is also the order of <c>IX_CarLapLogs_EventId_SessionId_CarNumber_LapNumber</c>.
    /// </summary>
    /// <param name="db">The context to query.</param>
    /// <param name="eventId">The event.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="carNumber">A single car, or null/empty for every car.</param>
    /// <returns>The ordered, untracked query.</returns>
    public static IQueryable<CarLapLog> LapQuery(TsContext db, int eventId, int sessionId, string? carNumber) =>
        LapFilter(db, eventId, sessionId, carNumber).OrderBy(l => l.CarNumber).ThenBy(l => l.LapNumber);

    /// <summary>
    /// Streams the stored lap JSON documents unchanged.
    /// </summary>
    /// <param name="contextFactory">Database context factory.</param>
    /// <param name="eventId">The event.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="carNumber">A single car, or null/empty for every car.</param>
    /// <param name="cancellationToken">Canceled when the client disconnects.</param>
    /// <returns>Each row's <c>LapData</c>, in export order.</returns>
    public static async IAsyncEnumerable<string> StreamRawLapJsonAsync(IDbContextFactory<TsContext> contextFactory,
        int eventId, int sessionId, string? carNumber, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = LapQuery(db, eventId, sessionId, carNumber).Select(l => l.LapData);

        await foreach (var raw in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
            yield return raw;
    }

    /// <summary>
    /// Streams laps projected down to the columns the CSV and PDF exports show.
    /// </summary>
    /// <param name="contextFactory">Database context factory.</param>
    /// <param name="eventId">The event.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="carNumber">A single car, or null/empty for every car.</param>
    /// <param name="diagnostics">Collects rows that could not be read so the export can say it lost them.</param>
    /// <param name="logger">Logger for rows that no longer deserialize.</param>
    /// <param name="cancellationToken">Canceled when the client disconnects.</param>
    /// <returns>The projected rows, in export order.</returns>
    public static async IAsyncEnumerable<LapExportRow> StreamLapRowsAsync(IDbContextFactory<TsContext> contextFactory,
        int eventId, int sessionId, string? carNumber, ExportScanDiagnostics diagnostics, ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = LapQuery(db, eventId, sessionId, carNumber)
            .Select(l => new LapRowSource(l.CarNumber, l.LapNumber, l.Timestamp, l.Flag, l.LapData));

        await foreach (var source in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var position = TryDeserialize(source.LapData, logger);
            if (position == null)
            {
                // Counted, not just dropped. A file that silently skips from lap 1 to lap 3 while its
                // Content-Disposition promises the session's laps is worse than one that says it lost
                // a row, because nobody reading it can tell.
                diagnostics.RowSkipped();
                continue;
            }

            yield return Project(source, position);
        }
    }

    /// <summary>
    /// Streams derived pit stops for every car in a session.
    /// </summary>
    /// <remarks>
    /// The rows arrive grouped by car because that is the index order, so one
    /// <see cref="PitStopAnalyzer"/> is run at a time and its results are released as soon as the car
    /// changes. Only one car's laps are ever being reasoned about, which is what keeps this bounded
    /// for a 24 hour race.
    /// </remarks>
    /// <param name="contextFactory">Database context factory.</param>
    /// <param name="eventId">The event.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="maxLapScan">Hard cap on lap rows read before the scan gives up.</param>
    /// <param name="diagnostics">
    /// Collects unreadable rows and whether the scan hit its cap, both of which change what the
    /// report means: past the cap, whole cars sorting late by number are missing and the car being
    /// processed is closed out mid-stint.
    /// </param>
    /// <param name="logger">Logger for rows that no longer deserialize and for hitting the cap.</param>
    /// <param name="cancellationToken">Canceled when the client disconnects.</param>
    /// <returns>The derived stops, grouped by car in natural index order.</returns>
    public static async IAsyncEnumerable<PitStopRecord> StreamPitStopsAsync(IDbContextFactory<TsContext> contextFactory,
        int eventId, int sessionId, int maxLapScan, ExportScanDiagnostics diagnostics, ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = LapQuery(db, eventId, sessionId, null)
            .Select(l => new PitLapSource(l.CarNumber, l.LapNumber, l.LapData));

        PitStopAnalyzer? analyzer = null;
        string? currentCar = null;
        var scanned = 0;

        await foreach (var source in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            if (scanned >= maxLapScan)
            {
                logger.LogWarning("Pit stop report for event {eventId} session {sessionId} stopped at the {max} lap scan limit",
                    eventId, sessionId, maxLapScan);
                diagnostics.ScanCapHit();
                break;
            }
            scanned++;

            if (currentCar != source.CarNumber)
            {
                if (analyzer != null)
                {
                    foreach (var stop in analyzer.Complete())
                        yield return stop;
                }
                currentCar = source.CarNumber;
                analyzer = new PitStopAnalyzer(source.CarNumber);
            }

            var position = TryDeserialize(source.LapData, logger);
            if (position == null)
            {
                // A dropped row does more damage here than in a lap export: it is not one missing
                // line, it silently changes what the derivation concludes. See PitStopAnalyzer for
                // which stops and driver changes a gap can cost.
                diagnostics.RowSkipped();
                continue;
            }

            analyzer!.AddLap(position, source.LapNumber);
        }

        if (analyzer != null)
        {
            foreach (var stop in analyzer.Complete())
                yield return stop;
        }
    }

    /// <summary>
    /// Projects a lap row onto the export columns.
    /// </summary>
    /// <param name="source">The database columns.</param>
    /// <param name="position">The deserialized lap snapshot.</param>
    /// <returns>The export row.</returns>
    public static LapExportRow Project(LapRowSource source, CarPosition position) => new()
    {
        // The car number and lap number come from the row's own columns rather than from the JSON:
        // those are the columns the query filtered and ordered on, so they are what the reader of the
        // export can match back to the database, whatever the payload says.
        CarNumber = source.CarNumber,
        LapNumber = source.LapNumber,
        TimestampUtc = source.Timestamp,
        Flag = ((Flags)source.Flag).ToString(),
        Class = position.Class,
        LapTime = position.LastLapTime,
        TotalTime = position.TotalTime,
        BestTime = position.BestTime,
        OverallPosition = position.OverallPosition,
        ClassPosition = position.ClassPosition,
        OverallGap = position.OverallGap,
        OverallDifference = position.OverallDifference,
        InClassGap = position.InClassGap,
        InClassDifference = position.InClassDifference,
        LapIncludedPit = position.LapIncludedPit,
        PitStopCount = position.PitStopCount,
        DriverName = position.DriverName,
        DriverSource = position.DriverSource,
    };

    /// <summary>
    /// Deserializes a stored lap payload, returning null rather than throwing.
    /// </summary>
    /// <remarks>
    /// The JSON was written by whichever build of the event processor was running when the lap was
    /// recorded, and an export can span a whole weekend of them. One row that no longer parses must
    /// cost that lap, not the download.
    /// </remarks>
    private static CarPosition? TryDeserialize(string? lapData, ILogger logger)
    {
        if (string.IsNullOrEmpty(lapData))
            return null;

        try
        {
            return JsonSerializer.Deserialize<CarPosition>(lapData);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // NotSupportedException as well as JsonException: the deserializer raises it for a value
            // that parses but cannot be converted to the target property. Letting that one through
            // would fail the whole export over a single row, which is the opposite of the intent.
            logger.LogWarning(ex, "Skipping unreadable lap row in export");
            return null;
        }
    }

    /// <summary>The database columns an export row is projected from.</summary>
    /// <param name="CarNumber">Car number column.</param>
    /// <param name="LapNumber">Lap number column.</param>
    /// <param name="Timestamp">When the lap was recorded, in UTC.</param>
    /// <param name="Flag">Track flag at the time, as the stored integer.</param>
    /// <param name="LapData">The serialized lap snapshot.</param>
    public record LapRowSource(string CarNumber, int LapNumber, DateTime Timestamp, int Flag, string LapData);

    /// <summary>The columns the pit stop scan needs.</summary>
    /// <param name="CarNumber">Car number column.</param>
    /// <param name="LapNumber">Lap number column, which is the number the report prints.</param>
    /// <param name="LapData">The serialized lap snapshot.</param>
    public record PitLapSource(string CarNumber, int LapNumber, string LapData);
}
