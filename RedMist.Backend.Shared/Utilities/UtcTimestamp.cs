namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// Forcing a timestamp that has been through JSON or a cache to UTC before it is persisted.
/// </summary>
/// <remarks>
/// The solution runs with <c>Npgsql.EnableLegacyTimestampBehavior</c>, under which a DateTime is
/// stored according to its Kind. A value written without one is persisted as if it were local time,
/// which is invisible in production - the containers run UTC - and shows up only on a developer
/// machine or if a container timezone ever changes.
/// </remarks>
public static class UtcTimestamp
{
    /// <summary>
    /// Returns <paramref name="value"/> as UTC.
    /// </summary>
    /// <remarks>
    /// An Unspecified value is taken at face value rather than converted. Every producer in this
    /// system stamps UTC, so such a value lost its Kind in transit, not its meaning; converting it
    /// would shift it by whatever timezone the reading machine happens to be in.
    /// </remarks>
    public static DateTime Normalize(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
