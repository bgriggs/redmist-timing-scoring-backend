using System.Collections.Concurrent;

namespace RedMist.Backend.Shared.Services;

/// <summary>
/// Identifies this status API process, and knows which connections it is actually serving.
/// </summary>
/// <remarks>
/// <para>
/// Per process rather than per pod. A container that is OOM-killed and restarted keeps its pod name,
/// and the new process must not inherit the old one's records: those connections died with it.
/// The pod name is kept in the id only so a person reading the cache can tell where a record came from.
/// </para>
/// <para>
/// The live set is what lets the sweeper clean up after this process too. A disconnect that runs
/// while Redis is unreachable deletes nothing, and a subscribe still in flight when its connection
/// drops writes the records back after the disconnect removed them. Either way the record names a
/// process that is alive, so the heartbeat alone would keep it counted for as long as the pod runs.
/// </para>
/// </remarks>
public sealed class StatusHubInstance
{
    private readonly ConcurrentDictionary<string, byte> live = new(StringComparer.Ordinal);

    public StatusHubInstance() : this($"{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..12]}")
    {
    }

    public StatusHubInstance(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    /// <summary>The id written into each connection record this process creates.</summary>
    public string Id { get; }

    /// <summary>Records that this process is serving the connection. Call before writing its record.</summary>
    public void Connected(string connectionId) => live.TryAdd(connectionId, 0);

    /// <summary>Records that this process has stopped serving the connection.</summary>
    public void Disconnected(string connectionId) => live.TryRemove(connectionId, out _);

    /// <summary>Whether this process is serving the connection.</summary>
    public bool IsServing(string connectionId) => live.ContainsKey(connectionId);
}
