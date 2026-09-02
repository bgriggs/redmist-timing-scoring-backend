using System.Globalization;
using System.Text;

namespace RedMist.StatusApi.Services.Exports;

/// <summary>
/// RFC 4180 field quoting for the CSV exports.
/// </summary>
/// <remarks>
/// Everything that reaches a CSV here is free-form text from a timing system or a driver roster:
/// car numbers, class names and driver names all come from someone typing into a race entry form,
/// so a comma or a quote in one of them is normal and must not shift the columns of the row.
/// </remarks>
public static class CsvFormat
{
    /// <summary>
    /// Appends one field to a row, quoting it if it needs quoting, and appends a separating comma
    /// unless it is the last field.
    /// </summary>
    /// <remarks>
    /// Use this for values the export produces itself - numbers, timestamps, formatted lap and gap
    /// times. For anything that originated as free text use <see cref="AppendTextField"/>.
    /// </remarks>
    /// <param name="builder">The row being built.</param>
    /// <param name="value">The field value; null is written as an empty field.</param>
    /// <param name="last">True when this is the final field of the row.</param>
    public static void AppendField(StringBuilder builder, string? value, bool last = false)
    {
        builder.Append(Escape(value));
        if (!last)
            builder.Append(',');
    }

    /// <summary>
    /// Appends one field that came from free text - a car number, class or driver name - quoting it
    /// and neutralizing anything Excel would evaluate as a formula.
    /// </summary>
    /// <param name="builder">The row being built.</param>
    /// <param name="value">The field value; null is written as an empty field.</param>
    /// <param name="last">True when this is the final field of the row.</param>
    public static void AppendTextField(StringBuilder builder, string? value, bool last = false)
    {
        builder.Append(Escape(Neutralize(value)));
        if (!last)
            builder.Append(',');
    }

    /// <summary>
    /// Prefixes a value that Excel would otherwise evaluate with an apostrophe, which Excel strips on
    /// display and treats as "this cell is text".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Car numbers, class names and driver names are typed into a race entry form by whoever entered
    /// the car, and these files exist to be opened in Excel. A value starting with <c>=</c>,
    /// <c>+</c>, <c>-</c> or <c>@</c> is a formula there, and a formula can reach outside the
    /// spreadsheet, so it is not a display problem.
    /// </para>
    /// <para>
    /// This is applied only to the free-text columns. The export's own numeric and gap columns
    /// legitimately start with a minus sign, and prefixing those would turn a spreadsheet of times a
    /// user wants to chart into a spreadsheet of text.
    /// </para>
    /// </remarks>
    /// <param name="value">The field value.</param>
    /// <returns>The value, prefixed if it needed neutralizing.</returns>
    public static string? Neutralize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n'
            ? "'" + value
            : value;
    }

    /// <summary>
    /// Returns the field as it should appear in a CSV row: quoted, with embedded quotes doubled,
    /// when it contains a comma, a quote, a newline, or leading/trailing whitespace that a reader
    /// would otherwise strip.
    /// </summary>
    /// <param name="value">The field value; null becomes an empty field.</param>
    /// <returns>The escaped field.</returns>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var needsQuotes = false;
        for (var i = 0; i < value.Length && !needsQuotes; i++)
        {
            var c = value[i];
            needsQuotes = c is ',' or '"' or '\r' or '\n';
        }

        // Leading or trailing spaces survive a round trip only inside quotes, and a car number that
        // came through with a stray space still has to match the one the user asked for.
        if (!needsQuotes && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])))
            needsQuotes = true;

        if (!needsQuotes)
            return value;

        return string.Concat("\"", value.Replace("\"", "\"\""), "\"");
    }

    /// <summary>Formats a nullable integer for a CSV cell, invariantly, with null as an empty cell.</summary>
    public static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Formats an integer for a CSV cell, invariantly.</summary>
    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a finishing position, blank when there is not one.
    /// </summary>
    /// <remarks>
    /// Positions are 1-based, so a zero is the uninitialized value of the field rather than a
    /// placing. Writing it as a number puts a car in "position 0" in a spreadsheet somebody is about
    /// to sort. Both the CSV and the PDF go through here so the two exports of one session cannot
    /// disagree about whether a car was placed on a lap.
    /// </remarks>
    /// <param name="position">The position value from the lap row.</param>
    /// <returns>The position, or an empty cell when there is not one.</returns>
    public static string Position(int position) =>
        position <= 0 ? string.Empty : position.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The date and time layout used in the CSV and PDF exports.
    /// </summary>
    /// <remarks>
    /// Month/day/year with a 12-hour clock, because these files are read by people at American club
    /// race events and that is the clock on the wall there. It is not the shape a machine would want;
    /// the JSON export carries ISO 8601 with the offset for that.
    /// </remarks>
    public const string DisplayFormat = "M/d/yyyy h:mm:ss tt";

    /// <summary>
    /// Formats a track local timestamp for a CSV or PDF cell, blank when there is not one.
    /// </summary>
    /// <param name="value">The timestamp, already converted to the track offset.</param>
    /// <returns>The formatted timestamp, or an empty cell.</returns>
    public static string Timestamp(DateTimeOffset? value) =>
        value?.ToString(DisplayFormat, CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// The earliest year a timestamp can claim and still be believable.
    /// </summary>
    /// <remarks>
    /// In-car equipment reports times from its own clock, and a device whose clock was never set
    /// reports year 0001. Nothing in this system predates the 1990s, so anything earlier is a broken
    /// clock rather than an old race, and printing it as a time would put an obviously wrong date in
    /// front of somebody reading the report.
    /// </remarks>
    public const int MinimumPlausibleYear = 1990;

    /// <summary>Whether a timestamp is believable enough to show.</summary>
    /// <param name="value">The timestamp to test.</param>
    /// <returns>False for a null, or for a value from an unset device clock.</returns>
    public static bool IsPlausible(DateTime? value) => value is { } v && v.Year >= MinimumPlausibleYear;

    /// <summary>
    /// Formats a millisecond duration as a human readable m:ss.fff, with null as an empty cell.
    /// Longer stops widen to hours and then days rather than dropping the leading component - a car
    /// left in the pits overnight is exactly the stop somebody is looking for in the report.
    /// </summary>
    /// <param name="milliseconds">The duration, or null for an empty cell.</param>
    /// <returns>The formatted duration.</returns>
    public static string Duration(int? milliseconds)
    {
        if (milliseconds is null)
            return string.Empty;
        var ts = TimeSpan.FromMilliseconds(milliseconds.Value);
        if (ts.TotalDays >= 1)
            return ts.ToString(@"d\.hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture)
            : ts.ToString(@"m\:ss\.fff", CultureInfo.InvariantCulture);
    }
}
