namespace RedMist.TrackMapReplay;

/// <summary>One car's telemetry record at one instant, as exported from EventStatusLogs.</summary>
/// <param name="Ts">Unix seconds; the log's whole-second resolution is all the feed records.</param>
/// <param name="Car">Car number.</param>
/// <param name="Lat">Latitude, or 0 when the device reported no position.</param>
/// <param name="Lon">Longitude, or 0 when the device reported no position.</param>
/// <param name="Speed">Reported speed in mph; 255 is the device's bad-GPS sentinel, 254 is stopped.</param>
/// <param name="Zone">Flagging zone: 1-127 on the racing surface, 128+ pit/paddock, 0 no fix.</param>
public readonly record struct Sample(long Ts, string Car, double Lat, double Lon, int Speed, int Zone)
{
    public bool HasPosition => Lat != 0 || Lon != 0;

    public bool IsOnTrack(int maxOnTrackZone) => Zone <= maxOnTrackZone;

    /// <summary>Mirrors FlagtronicsProcessor.IsPositionFaulted.</summary>
    public bool IsFaulted()
    {
        var hasValidZone = Zone >= 1;
        if (!hasValidZone && !HasPosition)
            return true;
        if (Speed == 255)
            return true;
        return hasValidZone && Zone > 127 && Speed is < 254 and >= 80;
    }
}

/// <summary>
/// Completed-lap counts over time, which is what the map builder keys its lap boundaries off.
/// </summary>
public class LapIndex
{
    private readonly Dictionary<string, List<(long Ts, int Lap)>> byCar = [];

    public int Count { get; private set; }

    public void Add(string car, int lap, long ts)
    {
        if (!byCar.TryGetValue(car, out var list))
            byCar[car] = list = [];
        list.Add((ts, lap));
        Count++;
    }

    public void Seal()
    {
        foreach (var list in byCar.Values)
            list.Sort((a, b) => a.Ts.CompareTo(b.Ts));
    }

    /// <summary>The car's completed-lap count as at <paramref name="ts"/>.</summary>
    public int CompletedAt(string car, long ts)
    {
        if (!byCar.TryGetValue(car, out var list))
            return 0;

        int lo = 0, hi = list.Count - 1, best = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (list[mid].Ts <= ts) { best = list[mid].Lap; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best;
    }
}

public static class Exports
{
    /// <summary>
    /// Reads the pipe-separated output of the queries in README.md, skipping the header and the
    /// trailing row count.
    /// </summary>
    public static List<Sample> LoadSamples(string path)
    {
        var list = new List<Sample>();
        foreach (var line in File.ReadLines(path))
        {
            var p = line.Split('|');
            if (p.Length != 7 || !long.TryParse(p[1].Trim(), out var ts))
                continue;
            list.Add(new Sample(ts, p[2].Trim(),
                double.Parse(p[3].Trim()), double.Parse(p[4].Trim()),
                int.Parse(p[5].Trim()), int.Parse(p[6].Trim())));
        }
        return list;
    }

    public static LapIndex LoadLaps(string path)
    {
        var index = new LapIndex();
        foreach (var line in File.ReadLines(path))
        {
            var p = line.Split('|');
            if (p.Length != 3 || !int.TryParse(p[1].Trim(), out var lap) || !long.TryParse(p[2].Trim(), out var ts))
                continue;
            index.Add(p[0].Trim(), lap, ts);
        }
        index.Seal();
        return index;
    }
}
