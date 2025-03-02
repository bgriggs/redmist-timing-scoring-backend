using MediatR;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Models;

public class ControlLogNotification(int eventId, CarControlLogs controlLogEntries) : INotification
{
    public int EventId { get; set; } = eventId;
    public string CarNumber { get; set; } = string.Empty;
    public CarControlLogs ControlLogEntries { get; set; } = controlLogEntries;
    public string ConnectionDestination { get; set; } = string.Empty;
}
