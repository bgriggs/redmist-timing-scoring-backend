using RedMist.TimingCommon.Models.Configuration;
using System.Text.Json.Serialization;

namespace RedMist.TimingAndScoringService.Models;

public class EventListSummary
{
    [JsonPropertyName("eid")]
    public int Id { get; set; }
    [JsonPropertyName("oid")]
    public int OrganizationId { get; set; }
    [JsonPropertyName("on")]
    public string OrganizationName { get; set; } = string.Empty;
    [JsonPropertyName("en")]
    public string EventName { get; set; } = string.Empty;
    [JsonPropertyName("ed")]
    public string EventDate { get; set; } = string.Empty;
    [JsonPropertyName("l")]
    public bool IsLive { get; set; }
    [JsonPropertyName("t")]
    public string TrackName { get; set; } = string.Empty;
    [JsonPropertyName("s")]
    public EventSchedule? Schedule { get; set; }
}
