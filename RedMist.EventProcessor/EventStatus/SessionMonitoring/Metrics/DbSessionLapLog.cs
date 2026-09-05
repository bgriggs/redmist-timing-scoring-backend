using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingCommon.Models;
using System.Text.Json;

namespace RedMist.EventProcessor.EventStatus.SessionMonitoring.Metrics;

/// <summary>
/// The session's lap log as it stands in the database.
///
/// Reads on its own connection rather than sharing the caller's: the whole log is streamed rather
/// than loaded, and the caller is holding a context it is about to save results through.
/// </summary>
/// <param name="tsContext">Factory for the read connection.</param>
/// <param name="eventId">The event the session belongs to.</param>
/// <param name="sessionId">
/// The session to read. Session zero is a real session number that the feed does emit, so this is
/// never treated as "no session".
/// </param>
public sealed class DbSessionLapLog(IDbContextFactory<TsContext> tsContext, int eventId, int sessionId) : ISessionLapLog
{
    public IReadOnlyDictionary<string, LoggedCarSummary> ReadSummary()
    {
        using var db = tsContext.CreateDbContext();
        return db.CarLapLogs
            .AsNoTracking()
            .Where(l => l.EventId == eventId && l.SessionId == sessionId)
            .GroupBy(l => l.CarNumber)
            .Select(g => new { CarNumber = g.Key, LapCount = g.Count(), HighestLapNumber = g.Max(l => l.LapNumber) })
            .ToDictionary(x => x.CarNumber, x => new LoggedCarSummary(x.LapCount, x.HighestLapNumber));
    }

    /// <summary>
    /// The laps in the order they were completed.
    ///
    /// Ordered by row id, which is the order they were written, which is the order the one event
    /// processor feeding them emitted them - the same order its timestamps carry, since it stamps
    /// each lap as it handles it. Ordering on the id rather than the timestamp lets the sort run off
    /// the key instead of dragging the serialized positions through a sort of their own.
    ///
    /// Streamed row by row: a twelve hour enduro logs upwards of fourteen thousand laps and each row
    /// carries a car position of up to five thousand characters, so materializing the log would cost
    /// far more memory than the event processor has to spare.
    /// </summary>
    public IEnumerable<LoggedLap> ReadLaps()
    {
        using var db = tsContext.CreateDbContext();
        var rows = db.CarLapLogs
            .AsNoTracking()
            .Where(l => l.EventId == eventId && l.SessionId == sessionId)
            .OrderBy(l => l.Id)
            .Select(l => new { l.CarNumber, l.LapNumber, l.Flag, l.LapData });

        foreach (var row in rows)
        {
            var position = ParseLapData(row.LapData);
            if (position is null)
                continue;

            yield return new LoggedLap(row.CarNumber, row.LapNumber, (Flags)row.Flag,
                position.OverallPosition, position.ClassPosition);
        }
    }

    /// <summary>
    /// A single unreadable row is skipped rather than allowed to abandon the session's metrics: the
    /// numbers being derived are counts over thousands of laps, and one missing lap moves them far
    /// less than having nothing at all.
    /// </summary>
    private static CarPosition? ParseLapData(string lapData)
    {
        if (string.IsNullOrWhiteSpace(lapData))
            return null;

        try
        {
            return JsonSerializer.Deserialize<CarPosition>(lapData);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
