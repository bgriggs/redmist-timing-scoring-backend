using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Prometheus;
using RedMist.Backend.Shared.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.Backend.Shared.Hubs;

[Authorize]
public class StatusHub : Hub
{
    #region Metrics

    public static Gauge ClientConnectionsCount { get; } = Metrics.CreateGauge(Consts.CLIENT_CONNECTIONS_KEY, "Total client connections");
    public static Gauge InCarConnectionsCount { get; } = Metrics.CreateGauge(Consts.IN_CAR_CONNECTIONS_KEY, "Total client in-car connections");

    #endregion

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

        try
        {
            // Save off the connectionId in the cache
            var cache = cacheMux.GetDatabase();
            var conn = new StatusConnection { ConnectedTimestamp = DateTime.UtcNow, ClientId = clientId, SubscribedEventId = 0 };
            var json = JsonSerializer.Serialize(conn);
            await cache.HashSetAsync(Consts.STATUS_CONNECTIONS, Context.ConnectionId, json);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error adding connectionId {connectionId} to status connections cache", Context.ConnectionId);
        }

        ClientConnectionsCount.Inc();
        Logger.LogInformation("Client {id} connected: {ConnectionId}", clientId, Context.ConnectionId);
    }

    public async override Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
        var clientId = GetClientId();

        try
        {
            var cache = cacheMux.GetDatabase();

            // Get the cache entry for this connectionId
            var json = await cache.HashGetAsync(Consts.STATUS_CONNECTIONS, Context.ConnectionId);
            // Remove the connectionId from the cache
            await cache.HashDeleteAsync(Consts.STATUS_CONNECTIONS, Context.ConnectionId, CommandFlags.FireAndForget);

            // If the connection had an event subscription, remove it from the event connections cache
            if (!json.IsNullOrEmpty)
            {
                var conn = JsonSerializer.Deserialize<StatusConnection>(json!);
                if (conn != null && conn.SubscribedEventId > 0)
                {
                    var connKey = string.Format(Consts.STATUS_EVENT_CONNECTIONS, conn.SubscribedEventId);
                    await cache.HashDeleteAsync(connKey, Context.ConnectionId, CommandFlags.FireAndForget);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error removing connectionId {connectionId} from status connections cache", Context.ConnectionId);
        }

        ClientConnectionsCount.Inc(-1);
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

    #region Event Subscriptions

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

            // Update connection tracking for this event
            await AddOrUpdateConnectionTracking(connectionId, eventId, inCarDriverConnection: null);
        }

        Logger.LogInformation("Client {connectionId} subscribed to event {eventId}", connectionId, eventId);
    }

    public async Task UnsubscribeFromEvent(int eventId)
    {
        var connectionId = Context.ConnectionId;
        await Groups.RemoveFromGroupAsync(connectionId, eventId.ToString());

        try
        {
            var cache = cacheMux.GetDatabase();
            var connKey = string.Format(Consts.STATUS_EVENT_CONNECTIONS, eventId);
            await cache.HashDeleteAsync(connKey, connectionId, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error removing connectionId {connectionId} from event {eventId}", connectionId, eventId);
        }

        Logger.LogInformation("Client {connectionId} unsubscribed from event {eventId}", connectionId, eventId);
    }

    #endregion

    #region Control Logs

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

    #endregion

    #region In-Car Driver Mode

    public async Task SubscribeToInCarDriverEvent(int eventId, string car)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = string.Format(Consts.IN_CAR_EVENT_SUB, eventId, car);
        await Groups.AddToGroupAsync(connectionId, grpKey);
        await AddOrUpdateConnectionTracking(connectionId, 0, inCarDriverConnection: new InCarDriverConnection(eventId, car));
        InCarConnectionsCount.Inc();
        Logger.LogInformation("Client {connectionId} subscribed to in-car driver event for car {car} event {eventId}", connectionId, car, eventId);
    }

    public async Task UnsubscribeFromInCarDriverEvent(int eventId, string car)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = string.Format(Consts.IN_CAR_EVENT_SUB, eventId, car);
        await Groups.RemoveFromGroupAsync(connectionId, grpKey);
        await AddOrUpdateConnectionTracking(connectionId, 0, inCarDriverConnection: null);
        InCarConnectionsCount.Dec();
        Logger.LogInformation("Client {connectionId} unsubscribed from in-car driver event for car {car} event {eventId}", connectionId, car, eventId);
    }

    #endregion

    #region Connection Status Management

    private async Task AddOrUpdateConnectionTracking(string connectionId, int eventId, InCarDriverConnection? inCarDriverConnection)
    {
        try
        {
            // Save off the connectionId in the cache for ability to send messages to this client individually
            var cache = cacheMux.GetDatabase();

            // Get the cache entry for this connectionId that would have been created in OnConnectedAsync
            var connJson = await cache.HashGetAsync(Consts.STATUS_CONNECTIONS, connectionId);
            if (!connJson.IsNullOrEmpty)
            {
                var conn = JsonSerializer.Deserialize<StatusConnection>(connJson!);
                if (conn != null && conn.ClientId != null)
                {
                    // Update the connectionId with the eventId
                    conn.SubscribedEventId = eventId;
                    conn.InCarDriverConnection = inCarDriverConnection;
                    var updatedJson = JsonSerializer.Serialize(conn);
                    await cache.HashSetAsync(Consts.STATUS_CONNECTIONS, connectionId, updatedJson);
                }
            }

            // Save off the connectionId in the event connections cache
            if (eventId > 0)
            {
                var connKey = string.Format(Consts.STATUS_EVENT_CONNECTIONS, eventId);
                await cache.HashSetAsync(connKey, connectionId, DateTime.UtcNow.ToString(), When.Always, CommandFlags.FireAndForget);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error adding connection tracking for connectionId {connectionId} to event {eventId}", connectionId, eventId);
        }
    }

    #endregion
}
