using MediatR;
using Microsoft.AspNetCore.SignalR;
using RedMist.Backend.Shared.Hubs;
using RedMist.TimingAndScoringService.Models;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// Process request to resend data from the the relay such as to attempt resolution to data consistency issues.
/// </summary>
public class RelayResetAggregator : INotificationHandler<RelayResetRequest>
{
    private readonly IHubContext<RelayHub> hubContext;
    private ILogger Logger { get; }


    public RelayResetAggregator(IHubContext<RelayHub> hubContext, ILoggerFactory loggerFactory)
    {
        this.hubContext = hubContext;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    public Task Handle(RelayResetRequest notification, CancellationToken cancellationToken)
    {
        var groupName = string.Format(Backend.Shared.Consts.RELAY_GROUP_PREFIX, notification.EventId);
        Logger.LogInformation("Sending request to relay group to resend event data: {groupName}", groupName);
        return hubContext.Clients.Group(groupName).SendAsync("SendEventData", notification.ForceTimingDataReset, cancellationToken);
    }
}
