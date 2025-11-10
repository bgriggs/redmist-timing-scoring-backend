using RedMist.Backend.Shared.Services;

namespace RedMist.EventProcessor.Models;

public class RelayResetRequest : INotification
{
    public int EventId { get; set; }
    public bool ForceTimingDataReset { get; set; } = false;
}
