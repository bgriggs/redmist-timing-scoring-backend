using System.Collections.Concurrent;

namespace RedMist.StatusLoadTest;

/// <summary>
/// Everything one virtual viewer saw. The websocket fields are written only by that client's hub
/// message loop and the poll fields only by its poll loop, so neither needs a lock. Most are read
/// only once every client has been stopped - PollStatuses is the exception, since the progress
/// reporter samples it while the run is going, which is why it alone is concurrent.
/// </summary>
public sealed class ClientStats
{
    public int Index;
    public string ClientIp = "";

    public int ConnectAttempts;
    public double ConnectMs = double.NaN;
    public bool EverConnected;

    /// <summary>
    /// Connected <em>and</em> subscribed. Only a subscribed client can receive a broadcast, so this
    /// - not <see cref="EverConnected"/> - is the audience the fan-out figures are measured against.
    /// </summary>
    public bool Subscribed;
    public string? ConnectError;
    public int Reconnects;
    public int Drops;

    public int SessionPatches;
    public int CarPatchMessages;
    public int CarPatchCars;
    public int Resets;
    public long LastPatchTicks;
    public readonly List<double> PatchGapsMs = [];

    public int Polls;
    public long PollBytes;
    public readonly List<double> PollMs = [];
    /// <summary>
    /// Concurrent because the progress reporter reads it on its own task mid-run. A plain
    /// Dictionary throws if it is enumerated while a new status code is being inserted, and the
    /// moment a first 429 or 503 appears across many clients at once is exactly when that would
    /// happen - taking the whole run's report down with it.
    /// </summary>
    public readonly ConcurrentDictionary<int, int> PollStatuses = new();
    public int PollErrors;

    /// <summary>
    /// Content already seen by this client. A repeat means two broadcasts carried identical
    /// payloads, which breaks the assumption that content identifies a broadcast, so the bucket is
    /// dropped from the fan-out figures rather than reported as a huge spread.
    /// </summary>
    public readonly HashSet<int> SeenContent = [];
}

/// <summary>
/// One server-side broadcast, as observed across every client that received it.
/// </summary>
public sealed class Broadcast
{
    public long FirstTicks;
    public long LastTicks;
    public int Receivers;
    public bool Poisoned;
}

/// <summary>
/// Collects what the run saw.
///
/// The figure that matters at this scale is fan-out spread: how long after the first client gets a
/// broadcast the last one does, and whether every client got it at all. A single client cannot show
/// that, and neither can an average - it is the tail across the audience.
///
/// Broadcasts are identified by their content rather than by arrival order, because clients join at
/// different times during the ramp and a reconnect would shift a client's ordinals permanently out
/// of step with everyone else's.
/// </summary>
public sealed class Recorder
{
    private readonly ConcurrentDictionary<string, Broadcast> broadcasts = new();
    private readonly ClientStats[] clients;

    public Recorder(int clientCount)
    {
        clients = new ClientStats[clientCount];
        for (var i = 0; i < clientCount; i++)
            clients[i] = new ClientStats { Index = i };
    }

    public ClientStats this[int index] => clients[index];
    public IReadOnlyList<ClientStats> Clients => clients;

    /// <summary>Ticks at the moment the last client was asked to connect.</summary>
    public long RampCompleteTicks { get; set; }

    public void RecordBroadcast(ClientStats client, string kind, string content, long ticks)
    {
        var key = kind + " " + content;
        var broadcast = broadcasts.GetOrAdd(key, _ => new Broadcast { FirstTicks = ticks, LastTicks = ticks });

        // Keyed by method as well as content: two hub methods can carry the same payload without
        // either being a repeat of the other.
        var repeat = !client.SeenContent.Add(key.GetHashCode());
        lock (broadcast)
        {
            if (repeat)
                broadcast.Poisoned = true;
            broadcast.Receivers++;
            if (ticks < broadcast.FirstTicks) broadcast.FirstTicks = ticks;
            if (ticks > broadcast.LastTicks) broadcast.LastTicks = ticks;
        }
    }

    public int ConnectedCount => clients.Count(c => c.EverConnected);
    public int SubscribedCount => clients.Count(c => c.Subscribed);
    public int TotalSessionPatches => clients.Sum(c => c.SessionPatches);
    public int TotalCarPatchMessages => clients.Sum(c => c.CarPatchMessages);
    public int TotalPolls => clients.Sum(c => c.Polls);

    /// <summary>
    /// Fan-out spread over the broadcasts that were in flight after every client had connected.
    /// Anything earlier reaches only part of the audience by definition, and would misreport both
    /// the spread and the delivery rate.
    /// </summary>
    public IReadOnlyList<Broadcast> SettledBroadcasts(long graceTicks)
    {
        return [.. broadcasts.Values.Where(b => !b.Poisoned && b.FirstTicks >= RampCompleteTicks + graceTicks)];
    }

    public int PoisonedBroadcasts => broadcasts.Values.Count(b => b.Poisoned);

    public static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
            return double.NaN;
        var rank = (int)Math.Ceiling(percentile / 100 * sortedValues.Count) - 1;
        return sortedValues[Math.Clamp(rank, 0, sortedValues.Count - 1)];
    }
}
