using MediatR;
using Microsoft.AspNetCore.SignalR;
using RedMist.Backend.Shared.Hubs;
using RedMist.TimingAndScoringService.Models;

namespace RedMist.TimingAndScoringService.Hubs;

/// <summary>
/// Aggregates and sends competitor metadata, e.g. name, make, model, updates to requesting 
/// client such as when user opens a car details in the UI.
/// </summary>
public class CompetitorMetadataAggregator : INotificationHandler<CompetitorMetadataNotification>
{
    private readonly IHubContext<StatusHub> hubContext;
    private ILogger Logger { get; }


    public CompetitorMetadataAggregator(IHubContext<StatusHub> hubContext, ILoggerFactory loggerFactory)
    {
        this.hubContext = hubContext;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    public async Task Handle(CompetitorMetadataNotification notification, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrEmpty(notification.ConnectionDestination))
            {
                string grpKey = $"{notification.EventId}-{notification.CarNumber}";
                Logger.LogTrace("CompetitorMetadataNotification: car {CarNumber} group {grpKey}", notification.CarNumber, grpKey);
                await hubContext.Clients.Client(notification.ConnectionDestination).SendAsync("ReceiveCompetitorMetadata", notification.CompetitorMetadata, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error sending competitor metadata update to clients.");
        }
    }
}