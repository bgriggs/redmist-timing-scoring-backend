namespace RedMist.Backend.Shared.Models;

public class SendControlLogCommand
{
    public int EventId { get; set; }
    public string ConnectionId { get; set; } = string.Empty;
    public string CarNumber { get; set; } = string.Empty;
}