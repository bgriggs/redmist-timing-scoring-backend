using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using RedMist.Backend.Shared.Models;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using RedMist.TimingCommon.Models.X2;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;

namespace RedMist.Backend.Shared.Hubs;

[Authorize]
public class RelayHub : Hub
{
    private ILogger Logger { get; }
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly HybridCache hcache;
    private static readonly ConcurrentDictionary<string, HashSet<string>> relayGroupTracker = new();


    public RelayHub(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, IDbContextFactory<TsContext> tsContext, HybridCache hcache)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.tsContext = tsContext;
        this.hcache = hcache;
    }


    public async override Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
        var clientId = GetClientId();
        if (clientId == null)
        {
            Logger.LogWarning("OnConnectedAsync: invalid client id, ignoring message");
            return;
        }
        await SetRelayConnectionAsync(clientId);
        Logger.LogInformation("Client {id} connected: {ConnectionId}", clientId, Context.ConnectionId);
    }

    public async override Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
        var clientId = GetClientId();
        if (clientId == null)
        {
            Logger.LogWarning("OnDisconnectedAsync: invalid client id, ignoring message");
            return;
        }

        await RemoveRelayConnectionAsync();
        Logger.LogInformation("Client {id} disconnected: {ConnectionId}", clientId, Context.ConnectionId);

        Logger.LogDebug("Removing relay connection from all groups for client {id}", clientId);
        RemoveRelayConnectionFromAllGroups(Context.ConnectionId);
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

    private async Task SetRelayConnectionAsync(string clientId)
    {
        var connEntry = new RelayConnectionStatus { ClientId = clientId, ConnectionId = Context.ConnectionId, ConnectedTimestamp = DateTime.UtcNow };
        var cache = cacheMux.GetDatabase();
        var hashKey = new RedisKey(Consts.RELAY_EVENT_CONNECTIONS);
        var fieldKey = string.Format(Consts.RELAY_CONNECTION, Context.ConnectionId);
        await cache.HashSetAsync(hashKey, fieldKey, JsonSerializer.Serialize(connEntry));
    }

    private async Task RemoveRelayConnectionAsync()
    {
        var cache = cacheMux.GetDatabase();
        var hashKey = new RedisKey(Consts.RELAY_EVENT_CONNECTIONS);
        var fieldKey = string.Format(Consts.RELAY_CONNECTION, Context.ConnectionId);
        await cache.HashDeleteAsync(hashKey, fieldKey);
    }

    #region Relay Clients

    public async Task SendHeartbeat(int eventId)
    {
        var clientId = GetClientId();
        if (clientId == null)
        {
            Logger.LogWarning("SendHeartbeat: invalid client id, ignoring message");
            return;
        }

        Logger.LogTrace("Heartbeat received from relay {c} for event {eventId}", clientId, eventId);

        var cache = cacheMux.GetDatabase();
        var hashKey = new RedisKey(Consts.RELAY_EVENT_CONNECTIONS);
        var orgId = await GetOrganizationId(clientId);
        var entry = new RelayConnectionEventEntry
        {
            EventId = eventId,
            ConnectionId = Context.ConnectionId,
            OrganizationId = orgId,
            Timestamp = DateTime.UtcNow
        };
        var entryKey = string.Format(Consts.RELAY_HEARTBEAT, eventId);
        var entryJson = JsonSerializer.Serialize(entry);
        await cache.HashSetAsync(hashKey, entryKey, entryJson);
    }

    /// <summary>
    /// Receives a message from an RMonitor relay.
    /// </summary>
    /// <param name="eventId">user select event on the relay</param>
    /// <param name="sessionId">timing system session</param>
    /// <param name="command">RMonitor command string</param>
    /// <see cref="https://github.com/bradfier/rmonitor/blob/master/docs/RMonitor%20Timing%20Protocol.pdf"/>
    public async Task SendRMonitor(int eventId, int sessionId, string command)
    {
        string commandStr = command.Replace("\r", "").Replace("\n", "");
        Logger.LogTrace("RX-RM: e:{evt} s:{ses} {c}", eventId, sessionId, commandStr);
        if (eventId > 0)
        {
            // Security note: not checking that the event/session is valid for the user explicitly here for performance. Security is ensured by the
            // check in SendSessionChange that the event/session is committed to the database only when it passes the security check.
            var streamId = string.Format(Consts.EVENT_STATUS_STREAM_KEY, eventId);
            var cache = cacheMux.GetDatabase();

            // Send the command to the service responsible for the specific event
            await cache.StreamAddAsync(streamId, string.Format(Consts.EVENT_RMON_STREAM_FIELD, eventId, sessionId), command);

            // Add the connection to the relay group for this event
            var connectionId = Context.ConnectionId;
            var groupName = string.Format(Consts.RELAY_GROUP_PREFIX, eventId);
            await SafeAddToRelayGroupAsync(connectionId, groupName);
        }
    }

    /// <summary>
    /// Reduces the number of relay group adds by keeping a local copy. When there are
    /// multiple instance of this service, there may occasional duplications of group adds.
    /// </summary>
    /// <param name="connectionId"></param>
    /// <param name="groupName"></param>
    private async Task SafeAddToRelayGroupAsync(string connectionId, string groupName)
    {
        var group = relayGroupTracker.GetOrAdd(groupName, _ => []);

        lock (group)
        {
            if (!group.Add(connectionId))
            {
                return; // Already in group, skip adding
            }
        }

        await Groups.AddToGroupAsync(connectionId, groupName);
    }

    public static void RemoveRelayConnectionFromAllGroups(string connectionId)
    {
        foreach (var kvp in relayGroupTracker)
        {
            var groupName = kvp.Key;
            var connectionSet = kvp.Value;

            lock (connectionSet)
            {
                if (connectionSet.Remove(connectionId) && connectionSet.Count == 0)
                {
                    relayGroupTracker.TryRemove(groupName, out _);
                }
            }
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
            var orgId = await GetOrganizationId(clientId);
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
                        LocalTimeZoneOffset = timeZoneOffset,
                        IsPracticeQualifying = SessionHelper.IsPracticeOrQualifyingSession(sessionName)
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
        Logger.LogDebug("SendPassings: evt:{eventId} passings:{passings.Count}", eventId, passings.Count);

        var clientId = GetClientId();
        if (clientId == null)
        {
            Logger.LogWarning("SendPassings: invalid client id, ignoring message");
            return;
        }

        var orgId = await GetOrganizationId(clientId);
        foreach (var pass in passings)
        {
            pass.OrganizationId = orgId;
        }

        var streamId = string.Format(Consts.EVENT_STATUS_STREAM_KEY, eventId);
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
        Logger.LogDebug("SendLoopChange: evt:{eventId} loops:{loops.Count}", eventId, loops.Count);

        var clientId = GetClientId();
        if (clientId == null)
        {
            Logger.LogWarning("SendLoopChange: invalid client id, ignoring message");
            return;
        }

        var orgId = await GetOrganizationId(clientId);
        foreach (var loop in loops)
        {
            loop.OrganizationId = orgId;
        }

        var streamId = string.Format(Consts.EVENT_STATUS_STREAM_KEY, eventId);
        var cache = cacheMux.GetDatabase();
        var json = JsonSerializer.Serialize(loops);
        await cache.StreamAddAsync(streamId, string.Format(Consts.EVENT_X2_LOOPS_STREAM_FIELD, eventId), json);
    }

    /// <summary>
    /// Sends a list of flag durations associated with a specific event to a streaming service.
    /// </summary>
    /// <param name="eventId">Identifies the specific event for which the flags are being sent.</param>
    /// <param name="sessionId"></param>
    /// <param name="flags">Contains the durations of flags that are associated with the event.</param>
    public async Task SendFlags(int eventId, int sessionId, List<FlagDuration> flags)
    {
        Logger.LogDebug("SendFlags: evt:{eventId} loops:{loops.Count}", eventId, flags.Count);

        var clientId = GetClientId();
        if (clientId == null)
        {
            Logger.LogWarning("SendFlags: invalid client id, ignoring message");
            return;
        }

        var streamId = string.Format(Consts.EVENT_STATUS_STREAM_KEY, eventId);
        var cache = cacheMux.GetDatabase();
        var json = JsonSerializer.Serialize(flags);
        await cache.StreamAddAsync(streamId, string.Format(Consts.EVENT_FLAGS_STREAM_FIELD, eventId, sessionId), json);
    }

    /// <summary>
    /// Sends metadata for competitors associated with a specific event.
    /// </summary>
    /// <param name="eventId">Identifies the specific event for which competitor metadata is being sent.</param>
    /// <param name="competitors">Contains the metadata of competitors such as name, make, model, club.</param>
    public async Task SendCompetitorMetadata(int eventId, List<CompetitorMetadata> competitors)
    {
        Logger.LogDebug("SendCompetitorMetadata: evt:{eventId} competitors:{competitors.Count}", eventId, competitors.Count);

        var clientId = GetClientId();
        if (clientId == null)
        {
            Logger.LogWarning("SendCompetitorMetadata: invalid client id, ignoring message");
            return;
        }

        // Ensure the event provided is valid for this client
        var orgId = await GetOrganizationId(clientId);
        using var db = await tsContext.CreateDbContextAsync();
        var ev = await db.Events.FirstOrDefaultAsync(x => x.OrganizationId == orgId && x.Id == eventId);
        if (ev?.Id != eventId)
        {
            Logger.LogWarning("SendCompetitorMetadata: event {e} not found for client {c}. Ignoring message.", eventId, clientId);
            return;
        }

        // Send the data to logger
        var streamId = string.Format(Consts.EVENT_STATUS_STREAM_KEY, eventId);
        var cache = cacheMux.GetDatabase();
        var json = JsonSerializer.Serialize(competitors);
        await cache.StreamAddAsync(streamId, string.Format(Consts.EVENT_COMPETITORS, eventId), json);

        // Save the metadata to the database
        await SaveCompetitorMetadata(eventId, competitors);

        // Invalidate cache
        Logger.LogDebug("Invalidating competitor metadata cache for event {EventId}", eventId);
        foreach (var cm in competitors)
        {
            var cacheKey = string.Format(Consts.COMPETITOR_METADATA, cm.CarNumber, eventId);
            await cache.KeyDeleteAsync(cacheKey, CommandFlags.FireAndForget);
        }
    }

    private async Task SaveCompetitorMetadata(int eventId, List<CompetitorMetadata> competitorMetadata)
    {
        Logger.LogInformation("Updating database with {Count} competitor metadata entries for event {EventId}", competitorMetadata.Count, eventId);
        try
        {
            using var db = await tsContext.CreateDbContextAsync();
            foreach (var competitor in competitorMetadata)
            {
                var existingCompetitor = await db.CompetitorMetadata
                    .FirstOrDefaultAsync(c => c.EventId == eventId && c.CarNumber == competitor.CarNumber);
                if (existingCompetitor != null)
                {
                    if (competitor.LastUpdated > existingCompetitor.LastUpdated)
                    {
                        db.Entry(existingCompetitor).CurrentValues.SetValues(competitor);
                    }
                }
                else
                {
                    await db.CompetitorMetadata.AddAsync(competitor);
                }
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error saving competitor metadata for event {EventId}", eventId);
        }
    }

    #endregion

    public async Task<int> GetOrganizationId(string clientId)
    {
        var key = string.Format(Consts.CLIENT_ID, clientId);
        return await hcache.GetOrCreateAsync(key,
            async cancel => await LoadOrganizationId(clientId));
    }

    private async Task<int> LoadOrganizationId(string clientId)
    {
        using var db = await tsContext.CreateDbContextAsync();
        var org = await db.Organizations.FirstOrDefaultAsync(x => x.ClientId == clientId);
        return org?.Id ?? 0;
    }
}
