using RedMist.TrackMapReplay;

// Replays recorded GPS from a real event through the production track-map and track-position code.
// See README.md for the queries that produce the two input files.

if (args.Length < 2)
{
    Console.WriteLine("usage: TrackMapReplay <ftcar.txt> <laps.txt> [declared-track-miles]");
    return 1;
}

var samples = Exports.LoadSamples(args[0]);
var laps = Exports.LoadLaps(args[1]);
double? declaredMeters = args.Length > 2 && double.TryParse(args[2], out var miles) ? miles * 1609.344 : null;

if (samples.Count == 0)
{
    Console.WriteLine($"No samples read from {args[0]}");
    return 1;
}

Console.WriteLine($"{samples.Count:N0} GPS samples, {laps.Count:N0} lap completions" +
                  (declaredMeters is double d ? $", track declared as {d:F0} m" : ", no declared track length"));

var replay = new Replay(samples, laps, declaredMeters);

Console.WriteLine("\n=== MAP ===");
replay.LearnMap();
if (replay.Map == null)
{
    Console.WriteLine("No map was learned. Nothing downstream can be measured.");
    return 1;
}
Console.WriteLine($"Learned from car {replay.MapCar} after {replay.MapLearnedAfterSeconds / 60.0:F1} min: " +
                  $"{replay.Map.Points.Count} points, {replay.Map.TotalLengthMeters:F0} m " +
                  $"({replay.Map.TotalLengthMeters / 1609.344:F3} mi)");
if (declaredMeters is double declared)
    Console.WriteLine($"  {(replay.Map.TotalLengthMeters - declared) / declared * 100:+0.0;-0.0}% against the declared length");

Console.WriteLine("\n=== START/FINISH ===");
replay.Calibrate();
if (replay.StartFinishOffsetMeters is double offset)
{
    Console.WriteLine($"Located {offset:F0} m along the path ({offset / replay.Map.TotalLengthMeters * 100:F2}% of the lap) " +
                      $"from {replay.CalibrationCrossings} crossings");
    Console.WriteLine($"Crossings agreed to within {replay.CalibrationSpreadMeters:F0} m (median)");
    replay.Map.StartFinishOffsetMeters = offset;
}
else
{
    Console.WriteLine($"Never located; {replay.CalibrationCrossings} crossings did not agree");
}

Console.WriteLine("\n=== POSITION QUALITY ===");
Report("as learned", replay.Map, replay.MeasurePositions(replay.Map));

// A map from one lap is only as dense as the feed allows, so it cuts corners the track really has.
// Folding later laps in tests whether that is what the lateral rejections are actually measuring.
foreach (var lapsToFold in new[] { 2, 5, 10 })
{
    var densified = replay.Densify(replay.Map, lapsToFold);
    Report($"+{lapsToFold} laps folded in", densified, replay.MeasurePositions(densified));
}

Console.WriteLine("\n=== SIGNAL BARS ===");
var bars = replay.SimulateSignalBars();
var total = bars.Sum();
for (int b = 5; b >= 0; b--)
    Console.WriteLine($"  {b} bars: {bars[b],9:N0}  {100.0 * bars[b] / total,6:F2}%");
Console.WriteLine($"  a >=4 gate admits {100.0 * (bars[4] + bars[5]) / total:F2}% (track position is shown above this)");
Console.WriteLine($"  a >=3 gate admits {100.0 * (bars[3] + bars[4] + bars[5]) / total:F2}% (pit data stays with telemetry above two bars)");

return 0;

static void Report(string label, RedMist.TimingCommon.Models.TrackMap map, PositionQuality q)
{
    var spacing = map.Points.Count > 0 ? map.TotalLengthMeters / map.Points.Count : 0;
    Console.WriteLine($"{label,-24} {map.Points.Count,5} points ({spacing,5:F0} m apart)  " +
                      $"lateral p50 {q.LateralMedian,5:F1} p90 {q.LateralP90,6:F1} p99 {q.LateralP99,6:F1}  " +
                      $"published {100.0 * q.Published / Math.Max(1, q.Fixes),5:F1}%  " +
                      $"lateral-rejected {100.0 * q.RejectedByLateral / Math.Max(1, q.Fixes),5:F1}%  " +
                      $"bad jumps {100.0 * q.ImplausibleJumps / Math.Max(1, q.Published),5:F2}%");
}
