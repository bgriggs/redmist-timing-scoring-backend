using System.Text.Json.Serialization;

namespace RedMist.Backend.Shared.Models;

public class RelayConnectionStatus
{
    [JsonPropertyName("c")]
    public string ConnectionId { get; set; } = string.Empty;
    [JsonPropertyName("cl")]
    public string ClientId { get; set; } = string.Empty;
    [JsonPropertyName("t")]
    public DateTime ConnectedTimestamp { get; set; }
}
