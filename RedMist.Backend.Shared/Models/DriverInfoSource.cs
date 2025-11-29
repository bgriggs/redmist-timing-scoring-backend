using RedMist.TimingCommon.Models;

namespace RedMist.Backend.Shared.Models;

public record DriverInfoSource(DriverInfo DriverInfo, string ClientId, DateTime LastUpdated) { }

public static class DriverInfoExtensions
{
    public static bool EqualsDriverInfo(this DriverInfoSource source, DriverInfoSource other)
    {
        if (source.DriverInfo.EventId != other.DriverInfo.EventId)
            return false;
        if (!string.Equals(source.DriverInfo.CarNumber, other.DriverInfo.CarNumber, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(source.DriverInfo.DriverId, other.DriverInfo.DriverId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(source.DriverInfo.DriverName, other.DriverInfo.DriverName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (source.DriverInfo.TransponderId != other.DriverInfo.TransponderId)
            return false;
        return true;
    }
}