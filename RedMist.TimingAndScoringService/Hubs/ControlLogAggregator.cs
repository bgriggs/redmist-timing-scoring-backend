using MediatR;
using Microsoft.AspNetCore.SignalR;
using RedMist.TimingAndScoringService.Models;

namespace RedMist.TimingAndScoringService.Hubs;

/// <summary>
/// Handles sending control log updates to registered hub clients.
/// </summary>
public class ControlLogAggregator : INotificationHandler<ControlLogNotification>
{
    private readonly IHubContext<TimingAndScoringHub> hubContext;
    private ILogger Logger { get; }

    
    public ControlLogAggregator(IHubContext<TimingAndScoringHub> hubContext, ILoggerFactory loggerFactory)
    {
        this.hubContext = hubContext;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    public async Task Handle(ControlLogNotification notification, CancellationToken cancellationToken)
    {
        
        try
        {
            if (!string.IsNullOrEmpty(notification.ConnectionDestination))
            {
                Logger.LogTrace("ControlLogNotification: car {0} client {1}", notification.CarNumber, notification.ConnectionDestination);
                await hubContext.Clients.Client(notification.ConnectionDestination).SendAsync("ReceiveControlLog", notification.ControlLogEntries, cancellationToken);
            }
            else if (!string.IsNullOrEmpty(notification.CarNumber))
            {
                string grpKey = $"{notification.EventId}-{notification.CarNumber}";
                Logger.LogTrace("ControlLogNotification: car {0} group {1}", notification.CarNumber, grpKey);
                await hubContext.Clients.Group(grpKey).SendAsync("ReceiveControlLog", notification.ControlLogEntries, cancellationToken);
            }
            else
            {
                string grpKey = $"{notification.EventId}-cl";
                Logger.LogTrace("ControlLogNotification: event {0} group {1}", notification.EventId, grpKey);
                await hubContext.Clients.Group(grpKey).SendAsync("ReceiveControlLog", notification.ControlLogEntries, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error sending control log update to clients: {0}", ex.Message);
        }
    }
}
