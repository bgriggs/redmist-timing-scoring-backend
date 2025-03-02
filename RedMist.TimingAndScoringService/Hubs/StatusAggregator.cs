using MediatR;
using Microsoft.AspNetCore.SignalR;
using RedMist.TimingAndScoringService.Models;

namespace RedMist.TimingAndScoringService.Hubs;

/// <summary>
/// Handles sending status updates to registered hub clients.
/// </summary>
public class StatusAggregator : INotificationHandler<StatusNotification>
{
    private readonly IHubContext<TimingAndScoringHub> hubContext;
    private ILogger Logger { get; }

    public StatusAggregator(IHubContext<TimingAndScoringHub> hubContext, ILoggerFactory loggerFactory)
    {
        this.hubContext = hubContext;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    public async Task Handle(StatusNotification notification, CancellationToken cancellationToken)
    {
        
        try
        {
            if (!string.IsNullOrEmpty(notification.ConnectionDestination))
            {
                Logger.LogTrace("StatusNotification: {0}", notification.ConnectionDestination);
                await hubContext.Clients.Client(notification.ConnectionDestination).SendAsync("ReceiveMessage", notification.StatusJson, cancellationToken);
            }
            else
            {
                Logger.LogTrace("StatusNotification Group: {0}", notification.EventId);
                await hubContext.Clients.Group(notification.EventId.ToString()).SendAsync("ReceiveMessage", notification.StatusJson, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error sending status update to client(s): {0}", ex.Message);
        }
    }
}
