using System.Diagnostics;

namespace RedMist.StatusLoadTest;

/// <summary>A distribution, reported by its tail rather than its average.</summary>
public sealed record Distribution(int Count, double P50, double P95, double P99, double Max)
{
    public static Distribution Of(IEnumerable<double> values)
    {
        var sorted = values.Where(v => !double.IsNaN(v)).Order().ToArray();
        if (sorted.Length == 0)
            return new Distribution(0, double.NaN, double.NaN, double.NaN, double.NaN);
        return new Distribution(
            sorted.Length,
            Recorder.Percentile(sorted, 50),
            Recorder.Percentile(sorted, 95),
            Recorder.Percentile(sorted, 99),
            sorted[^1]);
    }

    public override string ToString() => Count == 0
        ? "no samples"
        : $"p50 {P50,8:0.#}   p95 {P95,8:0.#}   p99 {P99,8:0.#}   max {Max,8:0.#}";
}

/// <summary>
/// The run's outcome. Kept separate from collection so the same numbers can be printed and written
/// to JSON without recomputing them.
/// </summary>
public sealed class Report
{
    public required int RequestedClients { get; init; }
    public required int ConnectedClients { get; init; }
    public required int SubscribedClients { get; init; }
    public required Distribution ConnectMs { get; init; }
    public required int Drops { get; init; }
    public required int Reconnects { get; init; }
    public required List<string> ConnectErrors { get; init; }

    public required int SettledBroadcasts { get; init; }
    public required int BroadcastsDeliveredToEveryone { get; init; }
    public required Distribution FanOutSpreadMs { get; init; }
    public required Distribution DeliveryShortfall { get; init; }
    public required Distribution PatchGapMs { get; init; }
    public required int SessionPatches { get; init; }
    public required int CarPatchMessages { get; init; }
    public required int CarPatchesCars { get; init; }
    public required int Resets { get; init; }
    public required int AmbiguousBroadcasts { get; init; }

    public required int Polls { get; init; }
    public required Dictionary<int, int> PollStatuses { get; init; }
    public required int PollErrors { get; init; }
    public required Distribution PollMs { get; init; }
    public required long PollBytes { get; init; }

    public static Report Build(Options options, Recorder recorder)
    {
        // Broadcasts that were already in flight when the last client connected reach only part of
        // the audience however healthy the server is, so the fan-out figures start a beat later.
        var grace = (long)(Stopwatch.Frequency * 2);
        var settled = recorder.SettledBroadcasts(grace);
        var connected = recorder.ConnectedCount;

        // The audience a broadcast could have reached. A connected-but-unsubscribed client receives
        // nothing however healthy the hub is, so counting it here would turn a handful of failed
        // subscribes into a report of total fan-out collapse.
        var audience = recorder.SubscribedCount;

        return new Report
        {
            RequestedClients = options.Clients,
            ConnectedClients = connected,
            SubscribedClients = audience,
            ConnectMs = Distribution.Of(recorder.Clients.Select(c => c.ConnectMs)),
            Drops = recorder.Clients.Sum(c => c.Drops),
            Reconnects = recorder.Clients.Sum(c => c.Reconnects),
            ConnectErrors = [.. recorder.Clients
                .Where(c => c.ConnectError != null)
                .GroupBy(c => c.ConnectError!)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count(),5} x {g.Key}")],

            SettledBroadcasts = settled.Count,
            BroadcastsDeliveredToEveryone = settled.Count(b => b.Receivers >= audience),
            FanOutSpreadMs = Distribution.Of(settled.Select(b => Stopwatch.GetElapsedTime(b.FirstTicks, b.LastTicks).TotalMilliseconds)),
            DeliveryShortfall = Distribution.Of(settled.Select(b => (double)(audience - b.Receivers))),
            PatchGapMs = Distribution.Of(recorder.Clients.SelectMany(c => c.PatchGapsMs)),
            SessionPatches = recorder.TotalSessionPatches,
            CarPatchMessages = recorder.TotalCarPatchMessages,
            CarPatchesCars = recorder.Clients.Sum(c => c.CarPatchCars),
            Resets = recorder.Clients.Sum(c => c.Resets),
            AmbiguousBroadcasts = recorder.PoisonedBroadcasts,

            Polls = recorder.TotalPolls,
            PollStatuses = recorder.Clients
                .SelectMany(c => c.PollStatuses)
                .GroupBy(s => s.Key)
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.Sum(s => s.Value)),
            PollErrors = recorder.Clients.Sum(c => c.PollErrors),
            PollMs = Distribution.Of(recorder.Clients.SelectMany(c => c.PollMs)),
            PollBytes = recorder.Clients.Sum(c => c.PollBytes),
        };
    }

    public void Print()
    {
        Console.WriteLine("=== Result ===");
        Console.WriteLine();
        Console.WriteLine("Connections");
        Console.WriteLine($"  connected            {ConnectedClients} / {RequestedClients}");
        if (SubscribedClients != ConnectedClients)
            Console.WriteLine($"  subscribed           {SubscribedClients} / {ConnectedClients}   <- the audience below is measured against this");
        Console.WriteLine($"  connect ms           {ConnectMs}");
        Console.WriteLine($"  drops                {Drops}");
        Console.WriteLine($"  reconnects           {Reconnects}");
        foreach (var error in ConnectErrors)
            Console.WriteLine($"  error              {error}");

        Console.WriteLine();
        Console.WriteLine("Broadcast delivery");
        Console.WriteLine($"  session patches      {SessionPatches}");
        Console.WriteLine($"  car patch messages   {CarPatchMessages}  ({CarPatchesCars} cars)");
        if (Resets > 0)
            Console.WriteLine($"  resets               {Resets}");
        Console.WriteLine($"  broadcasts measured  {SettledBroadcasts}");
        if (SettledBroadcasts > 0)
        {
            var complete = 100.0 * BroadcastsDeliveredToEveryone / SettledBroadcasts;
            Console.WriteLine($"  reached all clients  {BroadcastsDeliveredToEveryone} / {SettledBroadcasts}  ({complete:0.0}%)");
            Console.WriteLine($"  clients missed       {DeliveryShortfall}");
            Console.WriteLine($"  fan-out spread ms    {FanOutSpreadMs}");
        }
        Console.WriteLine($"  per-client gap ms    {PatchGapMs}");
        if (AmbiguousBroadcasts > 0)
            Console.WriteLine($"  excluded (repeated payload)  {AmbiguousBroadcasts}");

        Console.WriteLine();
        Console.WriteLine("Full-state poll");
        Console.WriteLine($"  requests             {Polls}");
        foreach (var (status, count) in PollStatuses)
            Console.WriteLine($"  http {status}             {count}");
        if (PollErrors > 0)
            Console.WriteLine($"  transport errors     {PollErrors}");
        Console.WriteLine($"  latency ms           {PollMs}");
        if (Polls > 0)
            Console.WriteLine($"  bytes                {PollBytes / 1024.0 / 1024:0.0} MB  (avg {PollBytes / (double)Math.Max(1, Polls) / 1024:0.0} KB)");
    }
}
