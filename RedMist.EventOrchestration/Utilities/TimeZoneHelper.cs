namespace RedMist.EventOrchestration.Utilities;

public static class TimeZoneHelper
{
    public static TimeZoneInfo GetMountainTimeZone()
    {
        try
        {
            // Try IANA timezone ID (Linux/macOS)
            return TimeZoneInfo.FindSystemTimeZoneById("America/Denver");
        }
        catch (TimeZoneNotFoundException)
        {
            // Fall back to Windows timezone ID
            return TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");
        }
    }
}
