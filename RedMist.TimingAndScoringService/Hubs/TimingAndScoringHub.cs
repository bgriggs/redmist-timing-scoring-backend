using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
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
    private readonly IDbContextFactory<TsContext> tsContext;

    private ILogger Logger { get; }

    public TimingAndScoringHub(ILoggerFactory loggerFactory, EventDistribution eventDistribution, IConnectionMultiplexer cacheMux,
        IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.eventDistribution = eventDistribution;
        this.cacheMux = cacheMux;
        this.tsContext = tsContext;
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
    /// <param name="eventId">user select event on the relay</param>
    /// <param name="sessionId">timing system session</param>
    /// <param name="command">RMonitor command string</param>
    /// <see cref="https://github.com/bradfier/rmonitor/blob/master/docs/RMonitor%20Timing%20Protocol.pdf"/>
    public async Task SendRMonitor(int eventId, int sessionId, string command)
    {
        Logger.LogTrace("RX-RM: e:{evt} s:{ses} {c}", eventId, sessionId, command);
        if (eventId > 0)
        {
            // Security note: not checking that the event/session is valid for the user explicitly here for performance. Security is ensured by the
            // check in SendSessionChange that the event/session is committed to the database only when it passes the security check.
            var stream = await eventDistribution.GetStreamIdAsync(eventId.ToString());
            var cache = cacheMux.GetDatabase();

            // Send the command to the service responsible for the specific event
            await cache.StreamAddAsync(stream, string.Format(Consts.EVENT_RMON_STREAM_FIELD, eventId, sessionId), command);
        }
    }

    /// <summary>
    /// Receive and register a new session/run from the timing system.
    /// </summary>
    /// <param name="sessionId">ID received from the timing system</param>
    /// <param name="sessionName">Name of the event from the timing system</param>
    /// <param name="timeZoneOffset">Local time zone offset in hours</param>
    public async Task SendSessionChange(int eventId, int sessionId, string sessionName, double timeZoneOffset)
    {
        Logger.LogDebug("SendSessionChange: evt:{eventId} new session:{sessionId}, new name:{sessionName}", eventId, sessionId, sessionName);

        var clientId = GetClientId();
        if (clientId == null)
        {
            Logger.LogWarning("SendSessionChange: invalid client id, ignoring message");
            return;
        }

        // Verify that the event is under this client
        var orgId = await eventDistribution.GetOrganizationId(clientId);
        using var db = await tsContext.CreateDbContextAsync();
        var ev = await db.Events.FirstOrDefaultAsync(x => x.OrganizationId == orgId && x.Id == eventId);
        if (ev != null)
        {
            Logger.LogTrace("SendSessionChange: success, event {e} found for client {c}", eventId, clientId);
            var existingSession = await db.Sessions.FirstOrDefaultAsync(x => x.EventId == eventId && x.Id == sessionId);
            if (existingSession == null)
            {
                db.Sessions.Add(new Session
                {
                    Id = sessionId,
                    EventId = eventId,
                    Name = sessionName,
                    IsLive = true,
                    StartTime = DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow,
                    LocalTimeZoneOffset = timeZoneOffset
                });
                await db.SaveChangesAsync();
                Logger.LogInformation("New session {s} saved for event {e}", sessionId, eventId);
            }
            else
            {
                Logger.LogInformation("Session {s} already exists for event {e}. No modifications.", sessionId, eventId);
            }
        }
        else
        {
            Logger.LogWarning("Event {e} not found for client {c}. Session not registered.", eventId, clientId);
        }
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
            var cmd = new SendStatusCommand { EventId = eventId, ConnectionId = connectionId };
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

    public async Task SubscribeToControlLogs(int eventId, string carNum)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = $"{eventId}-{carNum}";
        await Groups.AddToGroupAsync(connectionId, grpKey);

        if (eventId > 0)
        {
            // Send a full status update to the client
            var sub = cacheMux.GetSubscriber();
            var cmd = new SendControlLogCommand { EventId = eventId, ConnectionId = connectionId, CarNumber = carNum };
            var json = JsonSerializer.Serialize(cmd);
            // Tell the service responsible for this event to send a full status update
            await sub.PublishAsync(new RedisChannel(Consts.SEND_CONTROL_LOG, RedisChannel.PatternMode.Literal), json, CommandFlags.FireAndForget);
        }

        Logger.LogInformation("Client {0} subscribed to control log for car {1} event {2}", connectionId, carNum, eventId);
    }

    public async Task UnsubscribeFromControlLogs(int eventId, string carNum)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = $"{eventId}-{carNum}";
        await Groups.RemoveFromGroupAsync(connectionId, grpKey);
        Logger.LogInformation("Client {0} unsubscribed from control log for car {1} event {2}", connectionId, carNum, eventId);
    }

    #endregion
}
