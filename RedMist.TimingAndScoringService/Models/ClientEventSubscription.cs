using System.Text.Json.Serialization;

namespace RedMist.TimingAndScoringService.Models;

public class ClientEventSubscription
{
    [JsonPropertyName("eid")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("sid")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("cid")]
    public string ClientId { get; set; } = string.Empty;
}