using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RedMist.Backend.Shared.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.Backend.Shared.Hubs;

[Authorize]
public class StatusHub : Hub
{
    private readonly IConnectionMultiplexer cacheMux;

    private ILogger Logger { get; }


    public StatusHub(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
    }


    public async override Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
        var clientId = GetClientId();
        Logger.LogInformation("Client {id} connected: {ConnectionId}", clientId, Context.ConnectionId);
    }

    public async override Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
        var clientId = GetClientId();
        Logger.LogInformation("Client {id} disconnected: {ConnectionId}", clientId, Context.ConnectionId);
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
            var cmd = new SendStatusCommand { EventId = eventId, ConnectionId = connectionId };
            var json = JsonSerializer.Serialize(cmd);
            // Tell the service responsible for this event to send a full status update
            await sub.PublishAsync(new RedisChannel(Consts.SEND_FULL_STATUS, RedisChannel.PatternMode.Literal), json, CommandFlags.FireAndForget);
        }

        Logger.LogInformation("Client {connectionId} subscribed to event {eventId}", connectionId, eventId);
    }

    public async Task UnsubscribeFromEvent(int eventId)
    {
        var connectionId = Context.ConnectionId;
        await Groups.RemoveFromGroupAsync(connectionId, eventId.ToString());
        Logger.LogInformation("Client {connectionId} unsubscribed from event {eventId}", connectionId, eventId);
    }

    public async Task SubscribeToControlLogs(int eventId)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = $"{eventId}-cl";
        await Groups.AddToGroupAsync(connectionId, grpKey);

        Logger.LogInformation("Client {connectionId} subscribed to control log for event {eventId}", connectionId, eventId);
    }

    public async Task UnsubscribeFromControlLogs(int eventId)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = $"{eventId}-cl";
        await Groups.RemoveFromGroupAsync(connectionId, grpKey);
        Logger.LogInformation("Client {connectionId} unsubscribed from control log for event {eventId}", connectionId, eventId);
    }

    public async Task SubscribeToCarControlLogs(int eventId, string carNum)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = $"{eventId}-{carNum}";
        await Groups.AddToGroupAsync(connectionId, grpKey);

        Logger.LogInformation("Client {connectionId} subscribed to control log for car {carNum} event {eventId}", connectionId, carNum, eventId);
    }

    public async Task UnsubscribeFromCarControlLogs(int eventId, string carNum)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = $"{eventId}-{carNum}";
        await Groups.RemoveFromGroupAsync(connectionId, grpKey);
        Logger.LogInformation("Client {connectionId} unsubscribed from control log for car {carNum} event {eventId}", connectionId, carNum, eventId);
    }

    public async Task SubscribeToCompetitorMetadata(int eventId, string carNum)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = $"{eventId}-{carNum}";
        await Groups.AddToGroupAsync(connectionId, grpKey);

        if (eventId > 0)
        {
            // Send a full status update to the client
            var sub = cacheMux.GetSubscriber();
            var cmd = new SendCompetitorMetadata { EventId = eventId, ConnectionId = connectionId, CarNumber = carNum };
            var json = JsonSerializer.Serialize(cmd);
            // Tell the service responsible for this event to send a full status update
            await sub.PublishAsync(new RedisChannel(Consts.SEND_COMPETITOR_METADATA, RedisChannel.PatternMode.Literal), json, CommandFlags.FireAndForget);
        }

        Logger.LogInformation("Client {connectionId} subscribed from competitor metadata for car {carNum} event {eventId}", connectionId, carNum, eventId);
    }

    public async Task UnsubscribeFromCompetitorMetadata(int eventId, string carNum)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = $"{eventId}-{carNum}";
        await Groups.RemoveFromGroupAsync(connectionId, grpKey);
        Logger.LogInformation("Client {connectionId} unsubscribed from competitor metadata for car {carNum} event {eventId}", connectionId, carNum, eventId);
    }

    #endregion
}
