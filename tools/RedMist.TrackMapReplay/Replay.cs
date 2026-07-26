using RedMist.TimingCommon.LapTiming;
using RedMist.TimingCommon.Models;

namespace RedMist.TrackMapReplay;

/// <summary>
/// Drives recorded GPS from a real event through the production track-map and track-position code
/// and reports what it produced. Everything here mirrors what the pipeline does per sample; the
/// value is in running it over hundreds of thousands of real fixes rather than a synthetic circle.
/// </summary>
public class Replay(IReadOnlyList<Sample> samples, LapIndex laps, double? declaredLengthMeters)
{
    /// <summary>Matches GpsLapPositionEnricher.</summary>
    private const double MaxLateralOffsetMeters = 30.0;
    private const int MaxOnTrackZone = 127;
    private const double MaxTrackSpeedMetersPerSecond = 90.0;
    private const double TeleportSlackMeters = 50.0;

    public TrackMap? Map { get; private set; }
    public string? MapCar { get; private set; }
    public double? StartFinishOffsetMeters { get; private set; }

    /// <summary>
    /// Learns a map the way TrackMapService does: every car feeds its own builder from positions
    /// taken on the racing surface, the first to corroborate a lap wins, and the result is checked
    /// against the length the timing system declares.
    /// </summary>
    public void LearnMap()
    {
        var builders = new Dictionary<string, TrackMapBuilder>();
        var anchors = new TeleportFilter();

        foreach (var s in samples)
        {
            if (!s.HasPosition || anchors.IsTeleport(s))
                continue;

            if (!builders.TryGetValue(s.Car, out var builder))
                builders[s.Car] = builder = new TrackMapBuilder(eventId: 0);

            builder.AddSample(s.Lat, s.Lon, laps.CompletedAt(s.Car, s.Ts), s.IsOnTrack(MaxOnTrackZone));
            if (!builder.IsComplete)
                continue;

            var map = builder.Build(sessionId: 0, DateTime.UnixEpoch.AddSeconds(s.Ts));
            if (map == null)
                continue;

            if (declaredLengthMeters is double declared && declared > 0
                && Math.Abs(map.TotalLengthMeters - declared) / declared > 0.25)
            {
                Console.WriteLine($"  rejected a {map.TotalLengthMeters:F0} m map from car {s.Car} against a declared {declared:F0} m");
                builders.Remove(s.Car);
                continue;
            }

            Map = map;
            MapCar = s.Car;
            MapLearnedAfterSeconds = s.Ts - samples[0].Ts;
            return;
        }
    }

    public long MapLearnedAfterSeconds { get; private set; }

    /// <summary>
    /// Locates start/finish from where cars are when their lap count increments, as the enricher
    /// does, and reports how tightly those sightings agreed.
    /// </summary>
    public void Calibrate()
    {
        if (Map == null)
            return;

        var lastLap = new Dictionary<string, int>();
        var window = new List<double>();
        var accepted = new List<double>();

        foreach (var s in samples)
        {
            if (!s.HasPosition)
                continue;
            var completed = laps.CompletedAt(s.Car, s.Ts);
            var isRollover = lastLap.TryGetValue(s.Car, out var previous) && completed == previous + 1;
            lastLap[s.Car] = completed;
            if (!isRollover || StartFinishOffsetMeters != null)
                continue;

            var snap = TrackGeometry.Snap(Map.Points, Map.TotalLengthMeters, s.Lat, s.Lon);
            if (snap == null || snap.Value.LateralOffsetMeters > MaxLateralOffsetMeters || !s.IsOnTrack(MaxOnTrackZone))
                continue;

            window.Add(snap.Value.DistanceAlongMeters);
            accepted.Add(snap.Value.DistanceAlongMeters);
            if (window.Count > 20)
                window.RemoveAt(0);

            StartFinishOffsetMeters = StartFinishCalibrator.EstimateOffsetMeters(window, Map.TotalLengthMeters);
        }

        CalibrationCrossings = accepted.Count;
        if (StartFinishOffsetMeters is double offset)
        {
            var spread = window
                .Select(d => TrackGeometry.CircularDistanceMeters(d, offset, Map.TotalLengthMeters))
                .OrderBy(d => d).ToList();
            CalibrationSpreadMeters = spread.Count > 0 ? spread[spread.Count / 2] : 0;
        }
    }

    public int CalibrationCrossings { get; private set; }
    public double CalibrationSpreadMeters { get; private set; }

    /// <summary>
    /// Measures every fix against the map: how far off the path cars sit, how much is rejected and
    /// by what, and how often a published position jumps in a way it should not.
    /// </summary>
    public PositionQuality MeasurePositions(TrackMap map)
    {
        var q = new PositionQuality();
        var lateral = new List<double>();
        var previous = new Dictionary<string, (double Along, long Ts)>();

        foreach (var s in samples)
        {
            if (!s.HasPosition)
                continue;
            var snap = TrackGeometry.Snap(map.Points, map.TotalLengthMeters, s.Lat, s.Lon);
            if (snap == null)
                continue;

            lateral.Add(snap.Value.LateralOffsetMeters);
            q.Fixes++;

            if (!s.IsOnTrack(MaxOnTrackZone)) { q.RejectedByZone++; continue; }
            if (snap.Value.LateralOffsetMeters > MaxLateralOffsetMeters) { q.RejectedByLateral++; continue; }
            q.Published++;

            if (previous.TryGetValue(s.Car, out var prev) && s.Ts - prev.Ts is > 0 and <= 5)
            {
                var delta = snap.Value.DistanceAlongMeters - prev.Along;
                if (delta < -map.TotalLengthMeters / 2) delta += map.TotalLengthMeters;
                if (delta > map.TotalLengthMeters / 2) delta -= map.TotalLengthMeters;
                if (delta < -20) q.BackwardsJumps++;
                if (Math.Abs(delta) > (s.Ts - prev.Ts) * MaxTrackSpeedMetersPerSecond + 25) q.ImplausibleJumps++;
            }
            previous[s.Car] = (snap.Value.DistanceAlongMeters, s.Ts);
        }

        lateral.Sort();
        q.LateralMedian = Percentile(lateral, 0.50);
        q.LateralP90 = Percentile(lateral, 0.90);
        q.LateralP99 = Percentile(lateral, 0.99);
        return q;
    }

    /// <summary>
    /// Builds a denser centerline by folding later clean laps into the learned one. A single lap can
    /// only ever be as dense as the feed's update rate allows - at a fix a second a car covers 40-60 m
    /// - so the polyline cuts corners the track really has, and cars on the true racing line read as
    /// far off it. Laps sample at different points around the circuit, so merging several closes
    /// that gap without inventing geometry.
    /// </summary>
    public TrackMap Densify(TrackMap reference, int lapsToFold, double minSpacingMeters = 10.0)
    {
        var byDistance = new SortedDictionary<double, TrackMapPoint>();
        foreach (var p in reference.Points)
            byDistance[p.CumulativeDistanceMeters] = p;

        var folded = 0;
        var lastLap = new Dictionary<string, int>();
        var lapPoints = new Dictionary<string, List<Sample>>();
        var anchors = new TeleportFilter();

        foreach (var s in samples)
        {
            if (!s.HasPosition || anchors.IsTeleport(s))
                continue;
            var completed = laps.CompletedAt(s.Car, s.Ts);
            var rollover = lastLap.TryGetValue(s.Car, out var previous) && completed != previous;
            lastLap[s.Car] = completed;

            if (rollover)
            {
                if (folded < lapsToFold && lapPoints.TryGetValue(s.Car, out var buffer) && buffer.Count >= 20
                    && buffer.All(b => b.IsOnTrack(MaxOnTrackZone)))
                {
                    foreach (var b in buffer)
                    {
                        var snap = TrackGeometry.Snap(reference.Points, reference.TotalLengthMeters, b.Lat, b.Lon);
                        if (snap == null || snap.Value.LateralOffsetMeters > MaxLateralOffsetMeters)
                            continue;
                        var along = snap.Value.DistanceAlongMeters;
                        // Keep it only where the line is not already described.
                        if (byDistance.Keys.Any(k => Math.Abs(k - along) < minSpacingMeters))
                            continue;
                        byDistance[along] = new TrackMapPoint { Latitude = b.Lat, Longitude = b.Lon };
                    }
                    folded++;
                }
                lapPoints[s.Car] = [];
            }

            if (!lapPoints.TryGetValue(s.Car, out var list))
                lapPoints[s.Car] = list = [];
            list.Add(s);
        }

        var ordered = byDistance.Values.ToList();
        var densified = new TrackMap { EventId = reference.EventId, StartFinishOffsetMeters = reference.StartFinishOffsetMeters };
        double cumulative = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i > 0)
                cumulative += TrackGeometry.DistanceMeters(ordered[i - 1].Latitude, ordered[i - 1].Longitude,
                    ordered[i].Latitude, ordered[i].Longitude);
            densified.Points.Add(new TrackMapPoint
            {
                Latitude = ordered[i].Latitude,
                Longitude = ordered[i].Longitude,
                CumulativeDistanceMeters = cumulative,
            });
        }
        densified.TotalLengthMeters = cumulative + TrackGeometry.DistanceMeters(
            ordered[^1].Latitude, ordered[^1].Longitude, ordered[0].Latitude, ordered[0].Longitude);
        return densified;
    }

    /// <summary>Reproduces TelemetrySignalTracker's scale over the recorded feed.</summary>
    public int[] SimulateSignalBars()
    {
        var counts = new int[6];
        var windows = new Dictionary<string, Queue<(long Ts, bool Faulted)>>();
        var firstSeen = new Dictionary<string, long>();

        foreach (var s in samples)
        {
            if (!windows.TryGetValue(s.Car, out var w))
            {
                windows[s.Car] = w = new Queue<(long, bool)>();
                firstSeen[s.Car] = s.Ts;
            }
            w.Enqueue((s.Ts, s.IsFaulted()));
            while (w.Count > 0 && w.Peek().Ts < s.Ts - 60)
                w.Dequeue();

            int bars;
            if (s.Ts - firstSeen[s.Car] < 15) bars = 5;
            else if (w.Count == 0) bars = 0;
            else
            {
                var clean = w.Count(x => !x.Faulted) / (double)w.Count;
                bars = clean >= 0.95 ? 5 : clean >= 0.80 ? 4 : clean >= 0.60 ? 3 : clean >= 0.35 ? 2 : clean > 0 ? 1 : 0;
            }
            counts[bars]++;
        }
        return counts;
    }

    public static double Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))];

    /// <summary>Mirrors FlagtronicsProcessor.IsTeleport so replayed fixes are filtered as live ones are.</summary>
    private sealed class TeleportFilter
    {
        private readonly Dictionary<string, (double Lat, double Lon, long At, int Rejected)> anchors = [];

        public bool IsTeleport(Sample s)
        {
            if (!s.HasPosition)
                return false;
            if (!anchors.TryGetValue(s.Car, out var a))
            {
                anchors[s.Car] = (s.Lat, s.Lon, s.Ts, 0);
                return false;
            }

            var elapsed = Math.Max(1.0, s.Ts - a.At);
            var travelled = TrackGeometry.DistanceMeters(a.Lat, a.Lon, s.Lat, s.Lon);
            if (travelled <= elapsed * MaxTrackSpeedMetersPerSecond + TeleportSlackMeters)
            {
                anchors[s.Car] = (s.Lat, s.Lon, s.Ts, travelled > TeleportSlackMeters ? 0 : a.Rejected);
                return false;
            }
            if (a.Rejected >= 2)
            {
                anchors[s.Car] = (s.Lat, s.Lon, s.Ts, 0);
                return false;
            }
            anchors[s.Car] = (a.Lat, a.Lon, a.At, a.Rejected + 1);
            return true;
        }
    }
}

public class PositionQuality
{
    public int Fixes;
    public int RejectedByZone;
    public int RejectedByLateral;
    public int Published;
    public int BackwardsJumps;
    public int ImplausibleJumps;
    public double LateralMedian;
    public double LateralP90;
    public double LateralP99;
}
