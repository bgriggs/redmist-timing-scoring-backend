using RedMist.EventProcessor.EventStatus.LapData;

namespace RedMist.EventProcessor.Tests.Utilities;

/// <summary>
/// Synthetic "dogbone" track: two long straights running in opposite directions, close enough
/// together that a position between them is ambiguous. Unlike <see cref="CircleTrack"/>, which is
/// convex and never passes near itself, this is the shape that makes a nearest-point match unsafe -
/// the geometry a crossover, a hairpin, or a pit lane beside the main straight produces.
///
/// Distances run from the origin along the out leg (heading east at north = 0), across the far end,
/// back along the return leg (heading west at north = <see cref="LegSeparation"/>), and across the
/// near end to close.
/// </summary>
internal static class DogboneTrack
{
    public const double StraightLength = 800.0;
    public const double LegSeparation = 25.0;
    public const double OriginLat = 45.0;
    public const double OriginLon = -75.0;
    private const double EarthR = 6_371_000.0;
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    public static double TotalLength => 2 * StraightLength + 2 * LegSeparation;

    /// <summary>A point at a local east/north offset in meters from the track origin.</summary>
    public static (double lat, double lon) PointAt(double eastMeters, double northMeters)
    {
        var lat = OriginLat + (northMeters / EarthR) * RadToDeg;
        var lon = OriginLon + (eastMeters / (EarthR * Math.Cos(OriginLat * DegToRad))) * RadToDeg;
        return (lat, lon);
    }

    /// <summary>The point a given distance along the closed path, wrapping at the origin.</summary>
    public static (double lat, double lon) AtDistance(double distanceMeters)
    {
        var d = ((distanceMeters % TotalLength) + TotalLength) % TotalLength;

        if (d <= StraightLength)
            return PointAt(d, 0);                                   // out leg, heading east
        d -= StraightLength;

        if (d <= LegSeparation)
            return PointAt(StraightLength, d);                      // far end
        d -= LegSeparation;

        if (d <= StraightLength)
            return PointAt(StraightLength - d, LegSeparation);      // return leg, heading west
        d -= StraightLength;

        return PointAt(0, LegSeparation - d);                       // near end, closing the loop
    }

    /// <summary>
    /// Feeds the service a partial join-in lap (discarded) plus the two agreeing full laps the
    /// builder needs before it trusts a length, so it learns and exposes a map. Uses car "9" as the
    /// map source.
    /// </summary>
    public static async Task FeedFullLapAsync(TrackMapService service)
    {
        for (double d = 0; d < 200; d += 10)
        {
            var (lat, lon) = AtDistance(d);
            await service.AddSampleAsync("9", lat, lon, 0, onTrack: true);
        }
        foreach (var lap in new[] { 1, 2 })
        {
            for (double d = 0; d < TotalLength; d += 10)
            {
                var (lat, lon) = AtDistance(d);
                await service.AddSampleAsync("9", lat, lon, lap, onTrack: true);
            }
        }
        var (closeLat, closeLon) = AtDistance(0);
        await service.AddSampleAsync("9", closeLat, closeLon, 3, onTrack: true);
    }
}
