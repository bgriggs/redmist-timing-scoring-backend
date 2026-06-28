using RedMist.TimingCommon.Models;
using System.Text.RegularExpressions;

namespace RedMist.ControlLogs.Announcements;

/// <summary>
/// Converts a race-control announcement (timestamp + free text) into one or more
/// <see cref="ControlLogEntry"/> items. The external timing source carries race control only as a
/// text line (e.g. "CAR 14 BEHIND THE WALL", "Car 14, 392, 909: Penalty - Code 60 Violation - 1 Lap
/// Penalty", "GREEN WILL START AT 17:21:05"); the involved car number(s) are embedded in that text.
///
/// One entry is emitted per involved car (each with that car in <see cref="ControlLogEntry.Car1"/>),
/// so per-car logs and penalty counts are correct for any number of cars. A message naming no car
/// (CODE 60, GREEN, etc.) produces a single car-less entry that surfaces only in the event-wide log.
///
/// <see cref="ParseAll"/> numbers the whole set chronologically: announcements are ordered oldest to
/// newest by timestamp and the n-th message gets <see cref="ControlLogEntry.OrderId"/> = n (1-based),
/// shared by all of that message's per-car entries. Use it rather than <see cref="Parse"/> when feeding
/// the store, since a single announcement has no ordering context of its own.
/// </summary>
public static class AnnouncementControlLogParser
{
    // The car list always immediately follows a CAR/CARS keyword and is a run of numbers joined by
    // ",", "&", or "AND" (e.g. "CARS 4 & 392", "CAR 4 AND 392", "Car 14, 392, 909"). Matching stops at
    // the first word token, so "CAR 14 SPUN AT TURN 1" yields 14 — not the "1" from "TURN 1". Only the
    // first CAR run in the text is used.
    private static readonly Regex CarRun = new(
        @"\bCARS?\b[\s:]*(?<nums>\d+(?:\s*(?:,|&|AND)\s*\d+)*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Number = new(@"\d+", RegexOptions.Compiled);

    // A turn/corner reference: "Turn" (any case) then a number with an optional trailing letter, e.g.
    // "TURN 12", "Turn 10A". Non-numeric turns ("TURN Pit Entry") and a bare "TURN " are not corners.
    private static readonly Regex TurnNumber = new(@"\bTurn\s+(\d+[A-Za-z]?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses an ordered set of announcements into per-car control log entries, numbering them
    /// chronologically: announcements are sorted oldest to newest by timestamp and the n-th message gets
    /// <see cref="ControlLogEntry.OrderId"/> = n (1-based), shared across that message's per-car entries.
    /// </summary>
    public static List<ControlLogEntry> ParseAll(IEnumerable<Announcement> announcements)
    {
        var ordered = announcements
            .Where(a => a is not null)
            .OrderBy(a => a.Timestamp)
            .ToList();

        var result = new List<ControlLogEntry>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var orderId = i + 1; // 1-based: the oldest message is 1
            foreach (var entry in Parse(ordered[i].Timestamp, ordered[i].Text))
            {
                entry.OrderId = orderId;
                result.Add(entry);
            }
        }
        return result;
    }

    /// <summary>
    /// Parses one announcement into per-car control log entries. Returns empty for blank text. The
    /// entries' <see cref="ControlLogEntry.OrderId"/> is left at 0 — chronological numbering needs the
    /// full set, so callers feeding the store use <see cref="ParseAll"/>.
    /// </summary>
    public static IEnumerable<ControlLogEntry> Parse(DateTime timestamp, string? text)
    {
        text = text?.Trim() ?? string.Empty;
        if (text.Length == 0)
            yield break;

        var cars = ExtractCars(text);
        var status = Classify(text, out var penaltyAction);

        if (cars.Count == 0)
        {
            yield return Build(timestamp, text, string.Empty, status, penaltyAction);
            yield break;
        }

        foreach (var car in cars)
            yield return Build(timestamp, text, car, status, penaltyAction);
    }

    /// <summary>The car numbers named in the message, in order, or an empty list when none are named.</summary>
    public static IReadOnlyList<string> ExtractCars(string text)
    {
        var m = CarRun.Match(text);
        if (!m.Success)
            return [];

        var cars = new List<string>();
        foreach (Match n in Number.Matches(m.Groups["nums"].Value))
            cars.Add(n.Value);
        return cars;
    }

    /// <summary>
    /// Classifies the message and, for penalties/warnings, returns the text to feed penalty counting in
    /// <paramref name="penaltyAction"/> (matched by the AKS-tuned regexes on <see cref="AnnouncementControlLog"/>).
    /// Reviews and observations carry no penalty action so they are not counted.
    /// </summary>
    private static string Classify(string text, out string penaltyAction)
    {
        var isPenalty = text.Contains("Penalty", StringComparison.OrdinalIgnoreCase);
        var isWarning = text.Contains("Warning", StringComparison.OrdinalIgnoreCase);

        penaltyAction = isPenalty || isWarning ? text : string.Empty;

        if (isPenalty)
            return "Penalty";
        if (isWarning)
            return "Warning";
        if (text.Contains("Under Review", StringComparison.OrdinalIgnoreCase))
            return "Under Review";
        return "Note";
    }

    // OrderId is assigned by ParseAll from the chronological position; left 0 here.
    private static ControlLogEntry Build(DateTime timestamp, string text, string car, string status, string penaltyAction) => new()
    {
        Timestamp = timestamp,
        Car1 = car,
        Note = text,
        Corner = ExtractCorner(text),
        Status = status,
        PenaltyAction = penaltyAction,
    };

    /// <summary>
    /// The turn/corner named in the message (e.g. "10", "10A"), upper-cased, or empty when none is named
    /// or the turn isn't numeric (e.g. "TURN Pit Entry"). Uses the first turn reference in the text.
    /// </summary>
    public static string ExtractCorner(string text)
        => TurnNumber.Match(text) is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : string.Empty;
}
