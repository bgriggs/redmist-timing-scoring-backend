using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Prometheus;
using RedMist.Backend.Shared.Models;
using RedMist.Backend.Shared.Services;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using Microsoft.EntityFrameworkCore;
using RedMist.Database.Models;
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
//[Authorize]
public class StatusHub : Hub
{
    #region Metrics

    public static Gauge ClientConnectionsCount { get; } = Metrics.CreateGauge(Consts.CLIENT_CONNECTIONS_KEY, "Total client connections");
    public static Gauge ClientConnectionsByType { get; } = Metrics.CreateGauge(
        "client_connections_by_type", "Client connections by application type",
        new GaugeConfiguration { LabelNames = ["client_type"] });

    #endregion

    private readonly IConnectionMultiplexer cacheMux;
    private readonly IEventAccessValidator accessValidator;
    private readonly TimeProvider timeProvider;
    private readonly IDbContextFactory<TsContext>? tsContext;

    /// <summary>The most events one dashboard may watch at once.</summary>
    /// <remarks>
    /// Far above any real organizer - the largest account administers a handful of organizations
    /// with at most a few events running at once - and present only so a single connection cannot
    /// ask the server to walk an unbounded list.
    /// </remarks>
    public const int MaxWatchedEvents = 50;

    private ILogger Logger { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="StatusHub"/> class.
    /// </summary>
    /// <param name="loggerFactory">Factory to create loggers for this hub.</param>
    /// <param name="cacheMux">Redis connection multiplexer for caching and pub/sub.</param>
    /// <param name="accessValidator">Validator for per-event access codes (private events).</param>
    /// <param name="timeProvider">Clock for the timestamps written to the viewership stream.</param>
    /// <remarks>
    /// <paramref name="timeProvider"/> is optional because hubs are constructed through
    /// <c>ActivatorUtilities</c>, which honors a default rather than requiring a registration.
    /// </remarks>
    public StatusHub(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, IEventAccessValidator accessValidator,
        TimeProvider? timeProvider = null, IDbContextFactory<TsContext>? tsContext = null)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.accessValidator = accessValidator;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.tsContext = tsContext;
    }

    /// <summary>
    /// Subscribes an organizer's dashboard to live viewer counts for events they administer.
    /// </summary>
    /// <param name="eventIds">The events to watch. Ids the caller does not administer are omitted.</param>
    /// <returns>
    /// The counts as they stand now, keyed by event id, for the events actually joined. A joined event
    /// whose counts could not be read just now is left out rather than reported as zero, and its
    /// counts arrive with the next push if a processor is publishing for the event.
    /// </returns>
    /// <remarks>
    /// <para>
    /// THIS DOES NOT COUNT THE CALLER AS A VIEWER. It joins the counts group and deliberately does
    /// not call <c>AddOrUpdateConnectionTracking</c>, so the connection never enters the event's
    /// hash. That one omission covers both halves: the live counts read that hash, and the
    /// post-event report is built from the viewer sessions the same method publishes. An organizer
    /// with a dashboard open all weekend therefore appears in neither.
    /// </para>
    /// <para>
    /// Takes a list because the dashboard is cross-organization: an account administering three
    /// organizations watches every live event across all of them on one page. Each id is authorized
    /// independently against its own event's organization, so one call may span organizations.
    /// </para>
    /// <para>
    /// Returns the current snapshots rather than waiting for the first push, so a dashboard shows a
    /// real number immediately - including for an event nothing is publishing for, where the
    /// subscription would otherwise sit silent and look like a page that had failed to load.
    /// </para>
    /// </remarks>
    public async Task<Dictionary<int, ViewerCountSnapshot>> SubscribeToEventViewerCounts(int[] eventIds)
    {
        var permitted = await PermittedEventsAsync(eventIds);
        var snapshots = new Dictionary<int, ViewerCountSnapshot>();
        if (permitted.Count == 0)
        {
            return snapshots;
        }

        var cache = cacheMux.GetDatabase();
        var asOf = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var eventId in permitted)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, string.Format(Consts.EVENT_VIEWER_COUNTS_SUB, eventId));

            // Still joined when the read fails - the caller is entitled to the event, and the next push
            // carries its counts if a processor is publishing for it. Only the fabricated zero is
            // withheld.
            var snapshot = await ViewerCounts.ReadAsync(cache, eventId, asOf);
            if (snapshot != null)
            {
                snapshots[eventId] = snapshot;
            }
        }

        Logger.LogInformation("Client {connectionId} subscribed to viewer counts for {count} event(s)",
            Context.ConnectionId, permitted.Count);
        return snapshots;
    }

    /// <summary>
    /// Stops receiving viewer counts for the given events.
    /// </summary>
    /// <param name="eventIds">The events to stop watching.</param>
    /// <remarks>
    /// Not authorized: leaving a group the caller was never in is harmless, and refusing would only
    /// make a dashboard's teardown depend on permissions it may have lost in the meantime.
    /// </remarks>
    public async Task UnsubscribeFromEventViewerCounts(int[] eventIds)
    {
        foreach (var eventId in (eventIds ?? []).Distinct().Take(MaxWatchedEvents))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, string.Format(Consts.EVENT_VIEWER_COUNTS_SUB, eventId));
        }
    }

    /// <summary>
    /// Of the events asked for, those the caller administers.
    /// </summary>
    /// <remarks>
    /// The caller's organizations are resolved once and the events filtered against them, rather
    /// than asking per event - the list spans organizations, and one membership read answers for all
    /// of them. Unauthorized ids are dropped rather than failing the call, so a dashboard whose
    /// event list has drifted still gets the ones it may see.
    /// </remarks>
    private async Task<List<int>> PermittedEventsAsync(int[] eventIds)
    {
        var requested = (eventIds ?? []).Where(id => id > 0).Distinct().Take(MaxWatchedEvents).ToList();
        if (requested.Count == 0 || tsContext == null)
        {
            if (tsContext == null)
            {
                Logger.LogWarning("Viewer counts requested but no database is configured for this hub.");
            }
            return [];
        }

        await using var db = await tsContext.CreateDbContextAsync(Context.ConnectionAborted);
        var organizations = await CallerOrganizations.ResolveAsync(db, Context.User, Context.ConnectionAborted);
        if (organizations.Count == 0)
        {
            return [];
        }

        return await db.Events
            .AsNoTracking()
            .Where(e => requested.Contains(e.Id) && !e.IsDeleted && organizations.Contains(e.OrganizationId))
            .Select(e => e.Id)
            .ToListAsync(Context.ConnectionAborted);
    }

    private async Task EnsureAccessAsync(int eventId, string? accessCode)
    {
        if (eventId <= 0)
            return;
        if (!await accessValidator.ValidateAsync(eventId, accessCode, Context.ConnectionAborted))
        {
            Logger.LogWarning("Access code rejected for event {eventId} from {connectionId}", eventId, Context.ConnectionId);
            throw new HubException("Access code required or invalid for this event.");
        }
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
        var clientType = ClientTypeHelper.ResolveClientType(clientId);

        try
        {
            // Save off the connectionId in the cache
            var cache = cacheMux.GetDatabase();
            var conn = new StatusConnection { ConnectedTimestamp = UtcNow(), ClientId = clientId, SubscribedEventId = 0 };
            var json = JsonSerializer.Serialize(conn);
            await cache.HashSetAsync(Consts.STATUS_CONNECTIONS, Context.ConnectionId, json);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error adding connectionId {connectionId} to status connections cache", Context.ConnectionId);
        }

        ClientConnectionsCount.Inc();
        ClientConnectionsByType.WithLabels(clientType).Inc();
        Logger.LogInformation("Client {id} ({type}) connected: {ConnectionId}", clientId, clientType, Context.ConnectionId);
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
        var clientType = ClientTypeHelper.ResolveClientType(clientId);
        StatusConnection? conn = null;

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
                conn = JsonSerializer.Deserialize<StatusConnection>(json.ToString());
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

        // Close the viewer session on whichever event this connection was last subscribed to. Read
        // from the record above rather than from the hub context, which no longer knows the event.
        if (conn is { SubscribedEventId: > 0 })
        {
            await PublishViewerSessionEndAsync(conn.SubscribedEventId, ViewerSessionEndReason.Disconnected);
        }

        ClientConnectionsCount.Inc(-1);
        ClientConnectionsByType.WithLabels(clientType).Dec();
        Logger.LogInformation("Client {id} disconnected: {ConnectionId}", clientId, Context.ConnectionId);
    }

    private string? GetClientId()
    {
        if (Context.User == null)
        {
            //Logger.LogDebug("Invalid user context, ignoring message");
            return null;
        }

        var clientId = Context.User.Claims.FirstOrDefault(c => c.Type == "azp")?.Value;
        if (clientId == null)
        {
            //Logger.LogDebug("Invalid client id, ignoring message");
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
        await SubscribeToEventV2WithCode(eventId, null);
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
    /// <para>Throws <see cref="HubException"/> if the event is private and the access code is missing or wrong.</para>
    /// </remarks>
    /// <example>
    /// JavaScript:
    /// <code>
    /// await connection.invoke('SubscribeToEventV2', 123);
    /// </code>
    /// </example>
    public async Task SubscribeToEventV2(int eventId)
    {
        await SubscribeToEventV2WithCode(eventId, null);
    }

    /// <summary>
    /// Subscribes the client to receive V2 updates for a specific event using an access code.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event to subscribe to.</param>
    /// <param name="accessCode">The access code for private events.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// Version: V2
    /// </remarks>
    /// <example>
    /// JavaScript:
    /// <code>
    /// await connection.invoke('SubscribeToEventV2WithCode', 123, '1234567');
    /// </code>
    /// </example>
    public async Task SubscribeToEventV2WithCode(int eventId, string? accessCode)
    {
        await EnsureAccessAsync(eventId, accessCode);

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
            await AddOrUpdateConnectionTracking(connectionId, eventId, inCarDriverConnection: null, updateInCarDriver: false);
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

        if (eventId > 0)
        {
            await PublishViewerSessionEndAsync(eventId, ViewerSessionEndReason.Unsubscribed);

            // Clear the recorded subscription too, or the connection still looks subscribed to this
            // event. That matters on the ordinary path of going back to the event list and into the
            // same event again: the tracking update would see the event id unchanged, publish no
            // start, and leave the viewer uncounted until the reconciler inferred them back.
            await ClearSubscribedEventAsync(connectionId, eventId);
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
    /// <param name="accessCode">Required for private events; pass <c>null</c> for public events.</param>
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
    public async Task SubscribeToControlLogs(int eventId, string? accessCode = null)
    {
        await EnsureAccessAsync(eventId, accessCode);
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
    /// <param name="accessCode">Required for private events; pass <c>null</c> for public events.</param>
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
    public async Task SubscribeToCarControlLogs(int eventId, string carNum, string? accessCode = null)
    {
        await EnsureAccessAsync(eventId, accessCode);
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
    /// <param name="accessCode">Required for private events; pass <c>null</c> for public events.</param>
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
    public async Task SubscribeToInCarDriverEvent(int eventId, string car, string? accessCode = null)
    {
        await EnsureAccessAsync(eventId, accessCode);
        var connectionId = Context.ConnectionId;
        var grpKey = string.Format(Consts.IN_CAR_EVENT_SUB, eventId, car);
        await Groups.AddToGroupAsync(connectionId, grpKey);
        await AddOrUpdateConnectionTracking(connectionId, eventId,
            inCarDriverConnection: new InCarDriverConnection(eventId, car), updateInCarDriver: true);
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
        await AddOrUpdateConnectionTracking(connectionId, eventId: null, inCarDriverConnection: null, updateInCarDriver: true);
        Logger.LogInformation("Client {connectionId} unsubscribed from in-car driver event for car {car} event {eventId}", connectionId, car, eventId);
    }

    /// <summary>
    /// Subscribes the client to in-car driver mode V2 for a specific car in an event.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="car">The car number/identifier for the in-car view.</param>
    /// <param name="accessCode">Required for private events; pass <c>null</c> for public events.</param>
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
    public async Task SubscribeToInCarDriverEventV2(int eventId, string car, string? accessCode = null)
    {
        await EnsureAccessAsync(eventId, accessCode);
        var connectionId = Context.ConnectionId;
        var grpKey = string.Format(Consts.IN_CAR_EVENT_SUB_V2, eventId, car);
        await Groups.AddToGroupAsync(connectionId, grpKey);
        await AddOrUpdateConnectionTracking(connectionId, eventId,
            inCarDriverConnection: new InCarDriverConnection(eventId, car), updateInCarDriver: true);
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
        await AddOrUpdateConnectionTracking(connectionId, eventId: null, inCarDriverConnection: null, updateInCarDriver: true);
        Logger.LogInformation("Client {connectionId} unsubscribed from in-car V2 driver event for car {car} event {eventId}", connectionId, car, eventId);
    }

    #endregion

    #region Connection Status Management

    /// <summary>
    /// Records which event a connection is watching and whether it is in in-car driver mode, and
    /// emits the viewer session transitions that follow from the change.
    /// </summary>
    /// <param name="connectionId">The connection being tracked.</param>
    /// <param name="eventId">
    /// The event the connection is now watching, or <c>null</c> to leave the existing event
    /// subscription untouched.
    /// </param>
    /// <param name="inCarDriverConnection">The in-car driver subscription, or <c>null</c> for none.</param>
    /// <param name="updateInCarDriver">
    /// Whether <paramref name="inCarDriverConnection"/> should be written. <c>false</c> leaves the
    /// existing value alone.
    /// </param>
    /// <remarks>
    /// <para>
    /// The two pieces of state are settable independently because the callers genuinely need
    /// different combinations of them, and conflating the two was a bug. Subscribing to in-car driver
    /// mode used to pass event id 0, which took the "moved to a different event" branch below and
    /// deleted the connection from its event's hash: an in-car viewer stopped being counted as
    /// watching the event at all, and on disconnect the cleanup no longer knew which event to tidy.
    /// In-car mode is a phone feature, so what that lost was mobile viewers specifically.
    /// </para>
    /// <para>
    /// Leaving in-car mode passes a null event id rather than the event, because a driver switching
    /// back to the timing screen is still watching. If they do then leave, the unsubscribe, the
    /// disconnect handler, or failing both the logger's reconciler closes the session.
    /// </para>
    /// <para>
    /// This read-modify-write of the connection's record is NOT serialized per connection. SignalR's
    /// <c>MaximumParallelInvocationsPerClient</c> defaults to 1, but AddRedMistSignalR raises it to 3,
    /// so two calls a client sends without awaiting the first can interleave: one reads the record,
    /// the other changes it, and the first writes its stale copy back. The first-party apps await
    /// each call, and the result is bounded by disconnect, which clears the record - but a client
    /// firing an unsubscribe and an in-car unsubscribe together can leave itself counted against an
    /// event it has left until it disconnects.
    /// </para>
    /// </remarks>
    private async Task AddOrUpdateConnectionTracking(string connectionId, int? eventId,
        InCarDriverConnection? inCarDriverConnection, bool updateInCarDriver)
    {
        int? switchedAwayFrom = null;
        int? startedWatching = null;
        int? trackedEventId = null;
        int? inCarEventId = inCarDriverConnection?.EventId;
        string clientType = ClientTypeHelper.ResolveClientType(null);
        bool isInCar = inCarDriverConnection != null;
        string? carNumber = inCarDriverConnection?.CarNumber;

        try
        {
            // Save off the connectionId in the cache for ability to send messages to this client individually
            var cache = cacheMux.GetDatabase();

            // Get the cache entry for this connectionId that would have been created in OnConnectedAsync
            var connJson = await cache.HashGetAsync(Consts.STATUS_CONNECTIONS, connectionId);
            if (!connJson.IsNullOrEmpty)
            {
                var conn = JsonSerializer.Deserialize<StatusConnection>(connJson.ToString());
                if (conn != null)
                {
                    if (eventId is { } newEventId)
                    {
                        // If the connection was previously subscribed to a different event, remove it from that event's connection cache
                        if (conn.SubscribedEventId > 0 && conn.SubscribedEventId != newEventId)
                        {
                            var oldConnKey = string.Format(Consts.STATUS_EVENT_CONNECTIONS, conn.SubscribedEventId);
                            await cache.HashDeleteAsync(oldConnKey, connectionId, CommandFlags.FireAndForget);
                            switchedAwayFrom = conn.SubscribedEventId;
                        }

                        if (conn.SubscribedEventId != newEventId && newEventId > 0)
                        {
                            startedWatching = newEventId;
                        }

                        // Always update SubscribedEventId so OnDisconnectedAsync can clean up the event hash entry
                        conn.SubscribedEventId = newEventId;
                    }

                    if (updateInCarDriver)
                    {
                        conn.InCarDriverConnection = inCarDriverConnection;
                    }
                    else
                    {
                        isInCar = conn.InCarDriverConnection != null;
                        carNumber = conn.InCarDriverConnection?.CarNumber;
                        inCarEventId = conn.InCarDriverConnection?.EventId;
                    }

                    if (conn.ClientId != null)
                    {
                        clientType = ClientTypeHelper.ResolveClientType(conn.ClientId);
                    }

                    // The event the connection is on after this update. Not the eventId passed in:
                    // leaving driver mode passes none, because it must not touch the event
                    // subscription - but the hash entry for that same event still has to change.
                    trackedEventId = conn.SubscribedEventId;

                    var updatedJson = JsonSerializer.Serialize(conn);
                    await cache.HashSetAsync(Consts.STATUS_CONNECTIONS, connectionId, updatedJson);
                }
            }

            // Save off the connectionId in the event connections cache with its live-count bucket.
            //
            // Written against the event the connection is on, which is the passed eventId when one was
            // given and otherwise the one it was already subscribed to. Keying this on the passed
            // eventId alone meant leaving driver mode - which passes none - never rewrote the entry,
            // and a phone that had exited driver mode stayed counted as InCar until it disconnected.
            //
            // InCar replaces the device here rather than accompanying it, so a phone in driver mode is
            // counted once. clientType itself is left alone for the viewer session published below,
            // which carries isInCar separately and has to keep the device for the report.
            var hashEventId = trackedEventId ?? eventId;
            if (hashEventId is > 0)
            {
                var connKey = string.Format(Consts.STATUS_EVENT_CONNECTIONS, hashEventId.Value);
                // In driver mode for THIS event, not merely in driver mode. A client can be driving at
                // one event and also watching another; counting it "In Vehicle" at the second would
                // show a car on track that is actually somewhere else.
                var bucket = isInCar && inCarEventId == hashEventId ? ClientTypeHelper.InCar : clientType;
                await cache.HashSetAsync(connKey, connectionId, bucket, When.Always, CommandFlags.FireAndForget);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error adding connection tracking for connectionId {connectionId} to event {eventId}", connectionId, eventId);
        }

        // Emitted after the tracking write, and outside its try/catch, so a viewership failure can
        // never cost a client its subscription. A switch is two transitions on two different streams,
        // each read by that event's own logger pod.
        if (switchedAwayFrom is { } oldEventId)
        {
            await PublishViewerSessionEndAsync(oldEventId, ViewerSessionEndReason.Switched);
        }
        if (startedWatching is { } watchedEventId)
        {
            await PublishViewerSessionStartAsync(watchedEventId, clientType, isInCar, carNumber);
        }
    }

    /// <summary>
    /// Forgets which event a connection was watching, leaving everything else about it alone.
    /// </summary>
    /// <remarks>
    /// Only clears when the recorded event still matches, so an unsubscribe that arrives after the
    /// client has already moved on cannot wipe the newer subscription.
    /// </remarks>
    private async Task ClearSubscribedEventAsync(string connectionId, int eventId)
    {
        try
        {
            var cache = cacheMux.GetDatabase();
            var connJson = await cache.HashGetAsync(Consts.STATUS_CONNECTIONS, connectionId);
            if (connJson.IsNullOrEmpty)
            {
                return;
            }

            var conn = JsonSerializer.Deserialize<StatusConnection>(connJson.ToString());
            if (conn == null || conn.SubscribedEventId != eventId)
            {
                return;
            }

            conn.SubscribedEventId = 0;
            await cache.HashSetAsync(Consts.STATUS_CONNECTIONS, connectionId, JsonSerializer.Serialize(conn));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error clearing the subscribed event for connectionId {connectionId}", connectionId);
        }
    }

    #endregion

    #region Viewership

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private Task PublishViewerSessionStartAsync(int eventId, string clientType, bool isInCar, string? carNumber) =>
        PublishViewerSessionEventAsync(new ViewerSessionEvent
        {
            Kind = ViewerSessionEventKind.Start,
            EventId = eventId,
            ConnectionId = Context.ConnectionId,
            ClientType = clientType,
            TimestampUtc = UtcNow(),
            IsInCar = isInCar,
            CarNumber = carNumber,
        });

    private Task PublishViewerSessionEndAsync(int eventId, ViewerSessionEndReason reason) =>
        PublishViewerSessionEventAsync(new ViewerSessionEvent
        {
            Kind = ViewerSessionEventKind.End,
            EventId = eventId,
            ConnectionId = Context.ConnectionId,
            TimestampUtc = UtcNow(),
            Reason = reason,
        });

    /// <summary>
    /// Writes one viewer session transition to the event's viewership stream.
    /// </summary>
    /// <remarks>
    /// Failures are logged and swallowed. This is telemetry riding alongside the live feed, and no
    /// viewership write is worth failing a subscribe, an unsubscribe or a disconnect over - the
    /// logger's reconciler recovers whatever a Redis outage loses here.
    /// </remarks>
    private async Task PublishViewerSessionEventAsync(ViewerSessionEvent viewerEvent)
    {
        try
        {
            var cache = cacheMux.GetDatabase();
            var streamKey = string.Format(Consts.EVENT_VIEWERSHIP_STREAM_KEY, viewerEvent.EventId);
            await cache.StreamAddAsync(streamKey, Consts.VIEWER_SESSION_TYPE, JsonSerializer.Serialize(viewerEvent),
                maxLength: Consts.EVENT_VIEWERSHIP_STREAM_MAX_LENGTH, useApproximateMaxLength: true);

            // Sliding expiry rather than a one-shot TTL: viewers can sit on an event whose relay has
            // dropped and whose logger pod has already been deleted, and nothing else would ever
            // delete the stream that keeps collecting their transitions.
            await cache.KeyExpireAsync(streamKey, Consts.VIEWERSHIP_STREAM_TTL, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error publishing viewer session {kind} for connection {connectionId} on event {eventId}",
                viewerEvent.Kind, viewerEvent.ConnectionId, viewerEvent.EventId);
        }
    }

    #endregion
}
