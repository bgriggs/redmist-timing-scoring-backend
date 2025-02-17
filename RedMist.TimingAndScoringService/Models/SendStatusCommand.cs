namespace RedMist.TimingAndScoringService.Models;

public class SendStatusCommand
{
    public int EventId { get; set; }
    public string ConnectionId { get; set; } = string.Empty;
}
