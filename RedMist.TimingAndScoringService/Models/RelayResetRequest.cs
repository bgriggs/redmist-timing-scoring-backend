using MediatR;

namespace RedMist.TimingAndScoringService.Models;

public class RelayResetRequest : INotification
{
    public int EventId { get; set; }
}
