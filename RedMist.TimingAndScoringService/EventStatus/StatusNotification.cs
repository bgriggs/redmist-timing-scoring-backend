using MediatR;

namespace RedMist.TimingAndScoringService.EventStatus;

public class StatusNotification(int eventId, string statusJson) : INotification
{
    public int EventId { get; set; } = eventId;

    /// <summary>
    /// Status to send to clients.
    /// </summary>
    public string StatusJson { get; set; } = statusJson;
}
