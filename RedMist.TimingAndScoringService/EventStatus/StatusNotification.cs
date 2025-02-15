using MediatR;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// Final processed event information to send to clients.
/// </summary>
/// <param name="eventId"></param>
/// <param name="statusJson"></param>
public class StatusNotification(int eventId, string statusJson) : INotification
{
    public int EventId { get; set; } = eventId;

    /// <summary>
    /// Status to send to clients.
    /// </summary>
    public string StatusJson { get; set; } = statusJson;

    public string ConnectionDestination { get; set; } = string.Empty;
}
