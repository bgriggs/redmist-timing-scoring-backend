using System.Globalization;

namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// Parses the "HH:MM:SS.fff" race clock strings sent by the timing feed.
/// </summary>
/// <remarks>
/// Hours are deliberately unbounded. Both the session's running race time and a car's total time
/// pass 24 hours in an endurance event, which the framework's exact parsers reject outright with an
/// "hh"/"HH" specifier - silently turning the rest of the race into zeros.
/// </remarks>
public static class RaceTimeParser
{
    /// <summary>
    /// Parses a race clock string, returning <see cref="TimeSpan.Zero"/> if it can't be read.
    /// </summary>
    public static TimeSpan Parse(string? time) => TryParse(time, out var result) ? result : TimeSpan.Zero;

    /// <summary>
    /// Parses a race clock string in the form "H:MM:SS" or "H:MM:SS.fff", with any number of hours.
    /// </summary>
    public static bool TryParse(string? input, out TimeSpan result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        var span = input.AsSpan();

        var firstColon = span.IndexOf(':');
        if (firstColon < 0)
            return false;

        var rest = span[(firstColon + 1)..];
        var secondColon = rest.IndexOf(':');
        if (secondColon < 0)
            return false;

        if (!int.TryParse(span[..firstColon], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours))
            return false;

        if (!int.TryParse(rest[..secondColon], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes))
            return false;

        if (!double.TryParse(rest[(secondColon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            return false;

        int wholeSeconds = (int)Math.Floor(seconds);
        int milliseconds = (int)Math.Round((seconds - wholeSeconds) * 1000);

        try
        {
            result = new TimeSpan(0, hours, minutes, wholeSeconds, milliseconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
