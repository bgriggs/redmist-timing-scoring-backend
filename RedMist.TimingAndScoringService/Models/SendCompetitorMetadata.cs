namespace RedMist.TimingAndScoringService.Models;

public class SendCompetitorMetadata
{
    public int EventId { get; set; }
    public string ConnectionId { get; set; } = string.Empty;
    public string CarNumber { get; set; } = string.Empty;
}
