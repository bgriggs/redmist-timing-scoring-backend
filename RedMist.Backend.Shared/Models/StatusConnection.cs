namespace RedMist.Backend.Shared.Models;

/// <summary>
/// Represents a SignalR client connection to status API data.
/// </summary>
public class StatusConnection
{
    public DateTime ConnectedTimestamp { get; set; }
    public string? ClientId { get; set; } = string.Empty;
    public int SubscribedEventId { get; set; }
    public InCarDriverConnection? InCarDriverConnection { get; set; } = null;

    /// <summary>
    /// The status API process serving this connection, or null for a record written before
    /// processes were identified.
    /// </summary>
    /// <remarks>
    /// A process killed outright never runs its disconnect handler, so the connections it served stay
    /// in the cache. This is how the sweeper tells which records belong to a process whose heartbeat
    /// has stopped. See <see cref="Services.StatusConnectionSweeper"/>.
    /// </remarks>
    public string? InstanceId { get; set; }
}
