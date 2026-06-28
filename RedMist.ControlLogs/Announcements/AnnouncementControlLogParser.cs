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

    /// <summary>
    /// Parses one announcement into per-car control log entries. Returns empty for blank text.
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

    private static ControlLogEntry Build(DateTime timestamp, string text, string car, string status, string penaltyAction) => new()
    {
        OrderId = StableOrderId(timestamp, car, text),
        Timestamp = timestamp,
        Car1 = car,
        Note = text,
        Status = status,
        PenaltyAction = penaltyAction,
    };

    /// <summary>
    /// A deterministic id for change detection: the same (timestamp, car, text) always yields the same
    /// value, and distinct announcements differ (timestamp disambiguates repeated text such as "CODE 60").
    /// FNV-1a so it is stable regardless of process or runtime hash randomization.
    /// </summary>
    private static int StableOrderId(DateTime timestamp, string car, string text)
    {
        unchecked
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            var hash = offset;
            void Mix(string s)
            {
                foreach (var ch in s)
                {
                    hash ^= ch;
                    hash *= prime;
                }
                hash ^= '|';
                hash *= prime;
            }
            Mix(timestamp.Ticks.ToString());
            Mix(car);
            Mix(text);
            return (int)(hash & 0x7FFFFFFF);
        }
    }
}
