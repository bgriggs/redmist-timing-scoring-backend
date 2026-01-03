using System.Text.Json.Serialization;

namespace RedMist.Backend.Shared.Models;

public class RelayConnectionEventEntry
{
    [JsonPropertyName("c")]
    public string ConnectionId { get; set; } = string.Empty;
    [JsonPropertyName("e")]
    public int EventId { get; set; }
    [JsonPropertyName("o")]
    public int OrganizationId { get; set; }
    [JsonPropertyName("t")]
    public DateTime Timestamp { get; set; }
    [JsonPropertyName("rv")]
    public string RelayVersion { get; set; } = string.Empty;
}
