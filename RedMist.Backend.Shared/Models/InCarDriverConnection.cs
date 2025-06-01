namespace RedMist.Backend.Shared.Models;

public class InCarDriverConnection(int eventId, string carNumber)
{
    public int EventId { get; } = eventId;
    public string CarNumber { get; } = carNumber;
}
