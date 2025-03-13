using MediatR;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Models;

/// <summary>
/// Final processed event information to send to clients.
/// </summary>
/// <param name="eventId"></param>
/// <param name="statusJson"></param>
public class StatusNotification(int eventId, int sessionId, string statusJson) : INotification
{
    public int EventId { get; set; } = eventId;
    public int SessionId { get; } = sessionId;
    public Payload? Payload { get; set; }

    /// <summary>
    /// Status to send to clients.
    /// </summary>
    public string StatusJson { get; set; } = statusJson;

    public string ConnectionDestination { get; set; } = string.Empty;
}
