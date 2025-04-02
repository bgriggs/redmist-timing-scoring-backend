using MediatR;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Models;

public class CompetitorMetadataNotification(int eventId, CompetitorMetadata competitorMetadata) : INotification
{
    public int EventId { get; set; } = eventId;
    public string CarNumber { get; set; } = string.Empty;
    public CompetitorMetadata CompetitorMetadata { get; } = competitorMetadata;
    public string ConnectionDestination { get; set; } = string.Empty;
}
