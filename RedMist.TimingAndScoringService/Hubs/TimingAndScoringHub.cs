using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.X2;
using StackExchange.Redis;
using System.IO;
using System.Text.Json;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

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
            var streamId = await eventDistribution.GetStreamIdAsync(eventId.ToString());
            var cache = cacheMux.GetDatabase();

            // Send the command to the service responsible for the specific event
            await cache.StreamAddAsync(streamId, string.Format(Consts.EVENT_RMON_STREAM_FIELD, eventId, sessionId), command);
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

        try
        {
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
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing SendSessionChange");
        }
    }

    /// <summary>
    /// Sends a list of X2 passing data associated with a specific event and session.
    /// </summary>
    /// <param name="eventId">Identifies the specific event for which the passing data is being sent.</param>
    /// <param name="sessionId">Specifies the session related to the event for which the passings are recorded.</param>
    /// <param name="passings">Contains the list of passing data that needs to be sent to the service.</param>
    public async Task SendPassings(int eventId, int sessionId, List<Passing> passings)
    {
        Logger.LogTrace("SendPassings: evt:{eventId} passings:{passings.Count}", eventId, passings.Count);

        var clientId = GetClientId();
        if (clientId == null)
        {
            Logger.LogWarning("SendPassings: invalid client id, ignoring message");
            return;
        }

        var orgId = await eventDistribution.GetOrganizationId(clientId);
        foreach (var pass in passings)
        {
            pass.OrganizationId = orgId;
        }

        var streamId = await eventDistribution.GetStreamIdAsync(eventId.ToString());
        var cache = cacheMux.GetDatabase();

        var chunks = SplitIntoChunks(passings);

        foreach (var chunk in chunks)
        {
            var json = JsonSerializer.Serialize(chunk);
            // Send the command to the service responsible for the specific event
            await cache.StreamAddAsync(streamId, string.Format(Consts.EVENT_X2_PASSINGS_STREAM_FIELD, eventId, sessionId), json);
        }
    }

    /// <summary>
    /// Divides a list into smaller lists of a specified maximum size. Each smaller list contains a portion of the
    /// original list.
    /// </summary>
    /// <typeparam name="T">Represents the type of elements contained in the list being divided.</typeparam>
    /// <param name="source">The list to be split into smaller chunks.</param>
    /// <param name="chunkSize">Specifies the maximum number of elements each smaller list can contain.</param>
    /// <returns>An enumerable collection of smaller lists created from the original list.</returns>
    private static IEnumerable<List<T>> SplitIntoChunks<T>(List<T> source, int chunkSize = 25)
    {
        for (int i = 0; i < source.Count; i += chunkSize)
        {
            yield return source.GetRange(i, Math.Min(chunkSize, source.Count - i));
        }
    }

    /// <summary>
    /// Sends a change in X2 loop data associated with a specific event for processing.
    /// </summary>
    /// <param name="eventId">Identifies the specific event for which the loop changes are being sent.</param>
    /// <param name="loops">Contains the collection of loop data that is being updated and sent.</param>
    public async Task SendLoopChange(int eventId, List<Loop> loops)
    {
        Logger.LogTrace("SendLoopChange: evt:{eventId} loops:{loops.Count}", eventId, loops.Count);

        var clientId = GetClientId();
        if (clientId == null)
        {
            Logger.LogWarning("SendLoopChange: invalid client id, ignoring message");
            return;
        }

        var orgId = await eventDistribution.GetOrganizationId(clientId);
        foreach (var loop in loops)
        {
            loop.OrganizationId = orgId;
        }

        var streamId = await eventDistribution.GetStreamIdAsync(eventId.ToString());
        var cache = cacheMux.GetDatabase();
        var json = JsonSerializer.Serialize(loops);
        await cache.StreamAddAsync(streamId, string.Format(Consts.EVENT_X2_LOOPS_STREAM_FIELD, eventId), json);
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

    public async Task SubscribeToControlLogs(int eventId)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = $"{eventId}-cl";
        await Groups.AddToGroupAsync(connectionId, grpKey);

        if (eventId > 0)
        {
            // Send a full status update to the client
            var sub = cacheMux.GetSubscriber();
            var cmd = new SendControlLogCommand { EventId = eventId, ConnectionId = connectionId, CarNumber = string.Empty };
            var json = JsonSerializer.Serialize(cmd);
            // Tell the service responsible for this event to send a full status update
            await sub.PublishAsync(new RedisChannel(Consts.SEND_CONTROL_LOG, RedisChannel.PatternMode.Literal), json, CommandFlags.FireAndForget);
        }

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

    public async Task UnsubscribeFromCarControlLogs(int eventId, string carNum)
    {
        var connectionId = Context.ConnectionId;
        var grpKey = $"{eventId}-{carNum}";
        await Groups.RemoveFromGroupAsync(connectionId, grpKey);
        Logger.LogInformation("Client {0} unsubscribed from control log for car {1} event {2}", connectionId, carNum, eventId);
    }

    #endregion
}
