namespace RedMist.Backend.Shared.Models;

/// <summary>
/// Represents a SignalR client connection to status API data.
/// </summary>
public class StatusConnection
{
    public DateTime ConnectedTimestamp { get; set; }
    public string? ClientId { get; set; } = string.Empty;
    public int SubscribedEventId { get; set; }
}
