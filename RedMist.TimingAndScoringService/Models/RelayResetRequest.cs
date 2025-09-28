using RedMist.Backend.Shared.Services;

namespace RedMist.TimingAndScoringService.Models;

public class RelayResetRequest : INotification
{
    public int EventId { get; set; }
    public bool ForceTimingDataReset { get; set; } = false;
}
