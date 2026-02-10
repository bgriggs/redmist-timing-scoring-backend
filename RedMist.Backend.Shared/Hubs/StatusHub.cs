using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Prometheus;
using RedMist.Backend.Shared.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.Backend.Shared.Hubs;

/// <summary>
/// SignalR hub that provides real-time event status updates to connected clients.
/// Manages subscriptions for live timing data, control logs, and in-car driver information.
/// </summary>
/// <remarks>
/// <para>Hub Route: /status/event-status</para>
/// <para>Authentication: Required (Bearer token)</para>
/// <para>This hub supports multiple subscription types including event updates, control logs, and in-car driver mode.</para>
/// </remarks>
[Authorize]
public class StatusHub : Hub
{
    #region Metrics

    public static Gauge ClientConnectionsCount { get; } = Metrics.CreateGauge(Consts.CLIENT_CONNECTIONS_KEY, "Total client connections");

    #endregion

    private readonly IConnectionMultiplexer cacheMux;

    private ILogger Logger { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="StatusHub"/> class.
    /// </summary>
    /// <param name="loggerFactory">Factory to create loggers for this hub.</param>
    /// <param name="cacheMux">Redis connection multiplexer for caching and pub/sub.</param>
    public StatusHub(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
    }

    /// <summary>
    /// Called when a new client connects to the hub.
    /// Initializes connection tracking and updates metrics.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
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

    /// <summary>
    /// Called when a client disconnects from the hub.
    /// Cleans up connection tracking and subscriptions, and updates metrics.
    /// </summary>
    /// <param name="exception">Optional exception that caused the disconnection.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
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
                var conn = JsonSerializer.Deserialize<StatusConnection>(json.ToString());
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
    /// Subscribes the client to receive real-time updates for a specific event.
    /// Triggers an immediate full status update to be sent to the client.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event to subscribe to.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <example>
    /// JavaScript:
    /// <code>
    /// await connection.invoke('SubscribeToEvent', 123);
    /// </code>
    /// Python:
    /// <code>
    /// await hub.server.invoke('SubscribeToEvent', 123)
    /// </code>
    /// </example>
    public async Task SubscribeToEvent(int eventId)
    {
        await SubscribeToEventV2(eventId);
    }

    /// <summary>
    /// Unsubscribes the client from receiving updates for a specific event.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event to unsubscribe from.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <example>
    /// JavaScript:
    /// <code>
    /// await connection.invoke('UnsubscribeFromEvent', 123);
    /// </code>
    /// </example>
    public async Task UnsubscribeFromEvent(int eventId)
    {
        await UnsubscribeFromEventV2(eventId);
    }

    /// <summary>
    /// Subscribes the client to receive real-time updates for a specific event using V2 protocol.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event to subscribe to.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// <para>Version: V2</para>
    /// <para>This version uses an improved subscription model with enhanced data structures.</para>
    /// </remarks>
    /// <example>
    /// JavaScript:
    /// <code>
    /// await connection.invoke('SubscribeToEventV2', 123);
    /// </code>
    /// </example>
    public async Task SubscribeToEventV2(int eventId)
    {
        var connectionId = Context.ConnectionId;
        var subKey = string.Format(Consts.EVENT_SUB_V2, eventId);

        try
        {
            await Groups.AddToGroupAsync(connectionId, subKey);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "FAILED to add connectionId {connectionId} to group {subKey}", connectionId, subKey);
            throw;
        }

        if (eventId > 0)
        {
            // Update connection tracking for this event
            await AddOrUpdateConnectionTracking(connectionId, eventId, inCarDriverConnection: null);
        }

        Logger.LogInformation("Client {connectionId} subscribed v2 to event {eventId}", connectionId, eventId);
    }

    /// <summary>
    /// Unsubscribes the client from receiving V2 updates for a specific event.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event to unsubscribe from.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// Version: V2
    /// </remarks>
    /// <example>
    /// JavaScript:
    /// <code>
    /// await connection.invoke('UnsubscribeFromEventV2', 123);
    /// </code>
    /// </example>
    public async Task UnsubscribeFromEventV2(int eventId)
    {
        var connectionId = Context.ConnectionId;
        var subKey = string.Format(Consts.EVENT_SUB_V2, eventId);
        await Groups.RemoveFromGroupAsync(connectionId, subKey);

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

    /// <summary>
    /// Subscribes the client to receive control log updates for a specific event.
    /// Control logs include race control decisions, penalties, and incident reports.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// Control logs are only available if configured by the event organizer.
    /// </remarks>
    /// <example>
    /// JavaScript:
    /// <code>
    /// await connection.invoke('SubscribeToControlLogs', 123);
    /// </code>
    /// </example>
    public async Task SubscribeToControlLogs(int eventId)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = $"{eventId}-cl";
        await Groups.AddToGroupAsync(connectionId, grpKey);
        Logger.LogInformation("Client {connectionId} subscribed to control log for event {eventId}", connectionId, eventId);
    }

    /// <summary>
    /// Unsubscribes the client from receiving control log updates for a specific event.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <example>
    /// JavaScript:
    /// <code>
    /// await connection.invoke('UnsubscribeFromControlLogs', 123);
    /// </code>
    /// </example>
    public async Task UnsubscribeFromControlLogs(int eventId)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = $"{eventId}-cl";
        await Groups.RemoveFromGroupAsync(connectionId, grpKey);
        Logger.LogInformation("Client {connectionId} unsubscribed from control log for event {eventId}", connectionId, eventId);
    }

    /// <summary>
    /// Subscribes the client to receive control log updates for a specific car in an event.
    /// Provides filtered control log entries relevant only to the specified car.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="carNum">The car number to subscribe to.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// Useful for drivers or teams who only want to see control log entries affecting their car.
    /// </remarks>
    /// <example>
    /// JavaScript:
    /// <code>
    /// await connection.invoke('SubscribeToCarControlLogs', 123, '42');
    /// </code>
    /// </example>
    public async Task SubscribeToCarControlLogs(int eventId, string carNum)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = $"{eventId}-{carNum}";
        await Groups.AddToGroupAsync(connectionId, grpKey);
        Logger.LogInformation("Client {connectionId} subscribed to control log for car {carNum} event {eventId}", connectionId, carNum, eventId);
    }

    /// <summary>
    /// Unsubscribes the client from receiving control log updates for a specific car.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="carNum">The car number to unsubscribe from.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <example>
    /// JavaScript:
    /// <code>
    /// await connection.invoke('UnsubscribeFromCarControlLogs', 123, '42');
    /// </code>
    /// </example>
    public async Task UnsubscribeFromCarControlLogs(int eventId, string carNum)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = $"{eventId}-{carNum}";
        await Groups.RemoveFromGroupAsync(connectionId, grpKey);
        Logger.LogInformation("Client {connectionId} unsubscribed from control log for car {carNum} event {eventId}", connectionId, carNum, eventId);
    }

    #endregion

    #region In-Car Driver Mode

    /// <summary>
    /// Subscribes the client to in-car driver mode for a specific car in an event.
    /// Provides real-time data optimized for in-car display to drivers.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="car">The car number/identifier for the in-car view.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// <para>Version: V1 (Legacy)</para>
    /// <para>In-car mode provides driver-specific information including:</para>
    /// <list type="bullet">
    /// <item>Current position and lap time</item>
    /// <item>Gap to cars ahead/behind</item>
    /// <item>Best lap comparison</item>
    /// <item>Flag status</item>
    /// </list>
    /// </remarks>
    /// <example>
    /// JavaScript:
    /// <code>
    /// await connection.invoke('SubscribeToInCarDriverEvent', 123, '42');
    /// </code>
    /// </example>
    public async Task SubscribeToInCarDriverEvent(int eventId, string car)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = string.Format(Consts.IN_CAR_EVENT_SUB, eventId, car);
        await Groups.AddToGroupAsync(connectionId, grpKey);
        await AddOrUpdateConnectionTracking(connectionId, 0, inCarDriverConnection: new InCarDriverConnection(eventId, car));
        Logger.LogInformation("Client {connectionId} subscribed to in-car driver event for car {car} event {eventId}", connectionId, car, eventId);
    }

    /// <summary>
    /// Unsubscribes the client from in-car driver mode for a specific car.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="car">The car number/identifier to unsubscribe from.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// Version: V1 (Legacy)
    /// </remarks>
    /// <example>
    /// JavaScript:
    /// <code>
    /// await connection.invoke('UnsubscribeFromInCarDriverEvent', 123, '42');
    /// </code>
    /// </example>
    public async Task UnsubscribeFromInCarDriverEvent(int eventId, string car)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = string.Format(Consts.IN_CAR_EVENT_SUB, eventId, car);
        await Groups.RemoveFromGroupAsync(connectionId, grpKey);
        await AddOrUpdateConnectionTracking(connectionId, 0, inCarDriverConnection: null);
        Logger.LogInformation("Client {connectionId} unsubscribed from in-car driver event for car {car} event {eventId}", connectionId, car, eventId);
    }

    /// <summary>
    /// Subscribes the client to in-car driver mode V2 for a specific car in an event.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="car">The car number/identifier for the in-car view.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// <para>Version: V2</para>
    /// <para>V2 includes enhanced data structures and improved update frequency.</para>
    /// </remarks>
    /// <example>
    /// JavaScript:
    /// <code>
    /// await connection.invoke('SubscribeToInCarDriverEventV2', 123, '42');
    /// </code>
    /// </example>
    public async Task SubscribeToInCarDriverEventV2(int eventId, string car)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = string.Format(Consts.IN_CAR_EVENT_SUB_V2, eventId, car);
        await Groups.AddToGroupAsync(connectionId, grpKey);
        await AddOrUpdateConnectionTracking(connectionId, 0, inCarDriverConnection: new InCarDriverConnection(eventId, car));
        Logger.LogInformation("Client {connectionId} subscribed to in-car V2 driver event for car {car} event {eventId}", connectionId, car, eventId);
    }

    /// <summary>
    /// Unsubscribes the client from in-car driver mode V2 for a specific car.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="car">The car number/identifier to unsubscribe from.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// Version: V2
    /// </remarks>
    /// <example>
    /// JavaScript:
    /// <code>
    /// await connection.invoke('UnsubscribeFromInCarDriverEventV2', 123, '42');
    /// </code>
    /// </example>
    public async Task UnsubscribeFromInCarDriverEventV2(int eventId, string car)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = string.Format(Consts.IN_CAR_EVENT_SUB_V2, eventId, car);
        await Groups.RemoveFromGroupAsync(connectionId, grpKey);
        await AddOrUpdateConnectionTracking(connectionId, 0, inCarDriverConnection: null);
        Logger.LogInformation("Client {connectionId} unsubscribed from in-car V2 driver event for car {car} event {eventId}", connectionId, car, eventId);
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
                var conn = JsonSerializer.Deserialize<StatusConnection>(connJson.ToString());
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
