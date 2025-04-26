using MediatR;
using Microsoft.AspNetCore.SignalR;
using RedMist.TimingAndScoringService.Hubs;
using RedMist.TimingAndScoringService.Models;

namespace RedMist.TimingAndScoringService.EventStatus;

public class RelayResetAggregator : INotificationHandler<RelayResetRequest>
{
    private readonly IHubContext<TimingAndScoringHub> hubContext;
    private ILogger Logger { get; }


    public RelayResetAggregator(IHubContext<TimingAndScoringHub> hubContext, ILoggerFactory loggerFactory)
    {
        this.hubContext = hubContext;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    public Task Handle(RelayResetRequest notification, CancellationToken cancellationToken)
    {
        var groupName = string.Format(Consts.RELAY_GROUP_PREFIX, notification.EventId);
        Logger.LogInformation("Sending request to relay group to resend event data: {groupName}", groupName);
        return hubContext.Clients.Group(groupName).SendAsync("SendEventData", cancellationToken);
    }
}
