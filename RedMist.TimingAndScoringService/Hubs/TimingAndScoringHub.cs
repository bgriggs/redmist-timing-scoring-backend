using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RedMist.TimingAndScoringService.EventStatus;
using StackExchange.Redis;

namespace RedMist.TimingAndScoringService.Hubs;

[Authorize]
public class TimingAndScoringHub : Hub, INotificationHandler<StatusNotification>
{
    private readonly EventDistribution eventDistribution;
    private readonly IConnectionMultiplexer cacheMux;

    private ILogger Logger { get; }

    public TimingAndScoringHub(ILoggerFactory loggerFactory, EventDistribution eventDistribution, IConnectionMultiplexer cacheMux)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.eventDistribution = eventDistribution;
        this.cacheMux = cacheMux;
    }

    public async override Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
        Logger.LogInformation("Client connected: {0}", Context.ConnectionId);
    }

    public async override Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
        Logger.LogInformation("Client disconnected: {0}", Context.ConnectionId);
    }

    private string? GetClientId() 
    {
        if (Context.User == null)
        {
            Logger.LogDebug("Invalid user context, ignoring message");
            return null;
        }

        var clientId = Context.User.Claims.First(c => c.Type == "azp").Value;
        if (clientId == null)
        {
            Logger.LogDebug("Invalid client id, ignoring message");
            return null;
        }
        return clientId;
    }

    #region Relay

    /// <summary>
    /// Receives a sent message from a RMonitor relay.
    /// </summary>
    /// <param name="command">RMonitor command string</param>
    /// <returns></returns>
    /// <see cref="https://github.com/bradfier/rmonitor/blob/master/docs/RMonitor%20Timing%20Protocol.pdf"/>
    public async Task SendRMonitor(int eventReference, string command)
    {
        //Debug.WriteLine($"RX-RM: {command}");
        Logger.LogTrace("RX-RM: {0}", command);

        var clientId = GetClientId();
        if (clientId == null)
        {
            return;
        }

        var orgId = await eventDistribution.GetOrganizationId(clientId);
        Logger.LogTrace("SendRMonitor user context clientId found: {0} Org: {1}", clientId, orgId);

        if (eventReference > 0)
        {
            var eventId = await eventDistribution.GetEventId(orgId, eventReference);

            if (eventId > 0)
            {
                var stream = await eventDistribution.GetStreamAsync(eventId.ToString());
                var db = cacheMux.GetDatabase();
                await db.StreamAddAsync(stream, string.Format("rmonitor-{0}", eventId), command);
            }
        }
    }

    public async Task SendEventUpdate(int eventReference, string name)
    {
        Logger.LogTrace("EventUpdate: {0}, {1}", eventReference, name);

        var clientId = GetClientId();
        if (clientId == null)
        {
            return;
        }

        var orgId = await eventDistribution.GetOrganizationId(clientId);
        await eventDistribution.SaveOrUpdateEvent(orgId, eventReference, name);
    }

    #endregion

    #region Clients

    /// <summary>
    /// Receives a status update from an event processor and forwards it to the subscribed client(s).
    /// </summary>
    public async Task Handle(StatusNotification notification, CancellationToken cancellationToken)
    {
        Logger.LogInformation("StatusNotification: {0}", notification.StatusJson);
        await Clients.Group(notification.EventId.ToString()).SendAsync("ReceiveMessage", notification.StatusJson);
    }

    public async Task SubscribeToEvent(int eventId)
    {
        var connectionId = Context.ConnectionId;
        await Groups.AddToGroupAsync(connectionId, eventId.ToString());
        Logger.LogInformation("Client {0} subscribed to event {1}", connectionId, eventId);
    }

    public async Task UnsubscribeFromEvent(int eventId)
    {
        var connectionId = Context.ConnectionId;
        await Groups.RemoveFromGroupAsync(connectionId, eventId.ToString());
        Logger.LogInformation("Client {0} unsubscribed from event {1}", connectionId, eventId);
    }

    #endregion
}
