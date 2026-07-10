using RedMist.EventProcessor.EventStatus.LapData;

namespace RedMist.EventProcessor.Tests.Utilities;

/// <summary>
/// Synthetic circular track for GPS projection tests: known circumference (2*pi*r) and lap fraction
/// (angle / 2*pi), so projections have exact expected values.
/// </summary>
internal static class CircleTrack
{
    public const double Radius = 300.0;
    public const double CenterLat = 45.0;
    public const double CenterLon = -75.0;
    private const double EarthR = 6_371_000.0;
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    public static double Circumference => 2 * Math.PI * Radius;

    /// <summary>Decimal-degree point at the given lap fraction.</summary>
    public static (double lat, double lon) Point(double fraction)
    {
        var a = 2 * Math.PI * fraction;
        var east = Radius * Math.Cos(a);
        var north = Radius * Math.Sin(a);
        var lat = CenterLat + (north / EarthR) * RadToDeg;
        var lon = CenterLon + (east / (EarthR * Math.Cos(CenterLat * DegToRad))) * RadToDeg;
        return (lat, lon);
    }

    /// <summary>
    /// Feeds the service a partial join-in lap (discarded) plus one full lap, so it learns and exposes a
    /// map. Uses car "9" as the map source.
    /// </summary>
    public static async Task FeedFullLapAsync(TrackMapService service)
    {
        for (int i = 0; i < 15; i++)
        {
            var (lat, lon) = Point((double)i / 90);
            await service.AddSampleAsync("9", lat, lon, 0);
        }
        for (int i = 0; i < 90; i++)
        {
            var (lat, lon) = Point((double)i / 90);
            await service.AddSampleAsync("9", lat, lon, 1);
        }
        var (lat2, lon2) = Point(0);
        await service.AddSampleAsync("9", lat2, lon2, 2);
    }
}