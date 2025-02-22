using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RedMist.TimingAndScoringService.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Hubs;

/// <summary>
/// SignalR hub for timing and scoring relay service and UI clients.
/// </summary>
[Authorize]
public class TimingAndScoringHub : Hub
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

    #region Relay Clients

    /// <summary>
    /// Receives a message from an RMonitor relay.
    /// </summary>
    /// <param name="eventReference">ID received from the timing system</param>
    /// <param name="command">RMonitor command string</param>
    /// <see cref="https://github.com/bradfier/rmonitor/blob/master/docs/RMonitor%20Timing%20Protocol.pdf"/>
    public async Task SendRMonitor(int eventReference, string command)
    {
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

                // Send the command to the service responsible for the specific event
                await db.StreamAddAsync(stream, string.Format("rmonitor-{0}", eventId), command);
            }
        }
    }

    /// <summary>
    /// Receive and register a new event from the timing system.
    /// </summary>
    /// <param name="eventReference">ID received from the timing system</param>
    /// <param name="name">Name of the event from the timing system</param>
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

    #region UI Clients

    /// <summary>
    /// UI is registering to receive updates for a specific event.
    /// </summary>
    /// <param name="eventId"></param>
    public async Task SubscribeToEvent(int eventId)
    {
        var connectionId = Context.ConnectionId;
        await Groups.AddToGroupAsync(connectionId, eventId.ToString());

        if (eventId > 0)
        {
            // Send a full status update to the client
            var sub = cacheMux.GetSubscriber();
            var cmd = new SendStatusCommand{ EventId = eventId, ConnectionId = connectionId };
            var json = JsonSerializer.Serialize(cmd);
            // Tell the service responsible for this event to send a full status update
            await sub.PublishAsync(new RedisChannel(Consts.SEND_FULL_STATUS, RedisChannel.PatternMode.Literal), json, CommandFlags.FireAndForget);
        }

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
