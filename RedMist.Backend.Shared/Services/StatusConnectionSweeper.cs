using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RedMist.Backend.Shared.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.Backend.Shared.Services;

/// <summary>
/// Keeps this status API process's heartbeat alive, and removes connection records nobody is serving.
/// </summary>
/// <remarks>
/// <para>
/// A connection is listed in <see cref="Consts.STATUS_CONNECTIONS"/> and in its event's
/// <see cref="Consts.STATUS_EVENT_CONNECTIONS"/> hash, and only <c>StatusHub.OnDisconnectedAsync</c>
/// removes it. A process that is OOM-killed or killed by a failed liveness probe never runs that
/// handler, so every connection it served stays counted as a viewer, and its clients reconnect to a
/// healthy pod and are counted a second time. A pod crash-looping during an event inflated the live
/// viewer count from a few dozen to several hundred this way.
/// </para>
/// <para>
/// Each record carries the <see cref="StatusConnection.InstanceId"/> of the process that wrote it,
/// and each process refreshes a short-lived heartbeat key. A record is orphaned when it is missing on
/// two sweeps in a row from wherever it should be: its owner's heartbeat for another process's
/// record, or this process's live set (<see cref="StatusHubInstance"/>) for one of its own. Two
/// sweeps rather than one so a Redis blip that delays a heartbeat, or a connect still writing its
/// record, cannot take a live viewer off the count. Removing the event hash entry is also what lets
/// the event logger's reconciler close the viewer session, because absence from that hash closes it.
/// </para>
/// <para>
/// The heartbeat runs on its own loop. A sweep that takes long - the first one after deploy walks
/// everything already orphaned - must never delay it, or the other pods would sweep this one's
/// viewers. A sweep only runs when this process's own heartbeat succeeded recently: otherwise Redis is
/// unreachable or only just back, and the other pods' heartbeats may be just as late.
/// </para>
/// <para>
/// Every replica sweeps. The work is idempotent - deleting a field that is already gone does nothing -
/// so a lock would add a failure mode without saving anything.
/// </para>
/// <para>
/// Records with no owner were written before processes were identified. They are removed once they
/// are older than <see cref="LegacyRecordMaxAge"/>, which clears what is already orphaned in the cache.
/// During the rollout that introduces this, a viewer connected that long to a pod still on the old
/// version is taken off the count early; it is counted again once that pod stops and the client
/// reconnects.
/// </para>
/// </remarks>
public sealed class StatusConnectionSweeper : BackgroundService
{
    /// <summary>How often the heartbeat is refreshed.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>How long a heartbeat outlives its last refresh.</summary>
    public static readonly TimeSpan HeartbeatTtl = TimeSpan.FromSeconds(45);

    /// <summary>How often the connection records are checked.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    /// <summary>The age past which a record with no owner is treated as orphaned.</summary>
    /// <remarks>Matches the longest viewer session the event logger's reconciler allows.</remarks>
    public static readonly TimeSpan LegacyRecordMaxAge = TimeSpan.FromHours(6);

    private readonly ILogger logger;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly StatusHubInstance instance;
    private readonly TimeProvider timeProvider;

    /// <summary>When this process's heartbeat last reached Redis.</summary>
    private long lastHeartbeatTicks;

    /// <summary>Other processes whose heartbeat was missing on the previous sweep.</summary>
    private HashSet<string> suspectedDeadOwners = new(StringComparer.Ordinal);

    /// <summary>This process's records that were missing from its live set on the previous sweep.</summary>
    private HashSet<string> suspectedOwnLeaks = new(StringComparer.Ordinal);

    public StatusConnectionSweeper(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, StatusHubInstance instance,
        TimeProvider? timeProvider = null)
    {
        logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.instance = instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Writes the heartbeat before the host starts serving, so no connection record of this process
    /// exists without one.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await TryHeartbeatAsync();
        await base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Connection sweeper starting for instance {instance}", instance.Id);
        return Task.WhenAll(HeartbeatLoopAsync(stoppingToken), SweepLoopAsync(stoppingToken));
    }

    private async Task HeartbeatLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TryHeartbeatAsync();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SweepLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepIfHealthyAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error sweeping orphaned status connections");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Sweeps if this process's own heartbeat reached Redis within the last two intervals. Otherwise
    /// forgets what earlier sweeps suspected, so the two sweeps that condemn a record are never
    /// either side of an outage.
    /// </summary>
    /// <returns>How many connection records were removed, or null if the sweep was skipped.</returns>
    public async Task<int?> SweepIfHealthyAsync()
    {
        var sinceHeartbeat = timeProvider.GetUtcNow().UtcTicks - Interlocked.Read(ref lastHeartbeatTicks);
        if (sinceHeartbeat > (HeartbeatInterval * 2).Ticks)
        {
            suspectedDeadOwners.Clear();
            suspectedOwnLeaks.Clear();
            return null;
        }

        return await SweepAsync();
    }

    /// <summary>Refreshes this process's heartbeat. Returns false if Redis could not be written.</summary>
    public async Task<bool> TryHeartbeatAsync()
    {
        try
        {
            var now = timeProvider.GetUtcNow();
            var cache = cacheMux.GetDatabase();
            await cache.StringSetAsync(HeartbeatKey(instance.Id), now.UtcDateTime.ToString("O"), expiry: HeartbeatTtl);
            Interlocked.Exchange(ref lastHeartbeatTicks, now.UtcTicks);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to refresh the status instance heartbeat for {instance}", instance.Id);
            return false;
        }
    }

    /// <summary>
    /// Runs one sweep, regardless of this process's own heartbeat.
    /// </summary>
    /// <returns>How many connection records were removed.</returns>
    public async Task<int> SweepAsync()
    {
        var cache = cacheMux.GetDatabase();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entries = await cache.HashGetAllAsync(Consts.STATUS_CONNECTIONS);

        var alive = new Dictionary<string, bool>(StringComparer.Ordinal) { [instance.Id] = true };
        var deadOwnersNow = new HashSet<string>(StringComparer.Ordinal);
        var ownLeaksNow = new HashSet<string>(StringComparer.Ordinal);
        var orphans = new List<RedisValue>();
        var orphansByEvent = new Dictionary<int, List<RedisValue>>();

        foreach (var entry in entries)
        {
            var connectionId = entry.Name.ToString();
            StatusConnection? conn;
            try
            {
                conn = JsonSerializer.Deserialize<StatusConnection>(entry.Value.ToString());
            }
            catch (JsonException)
            {
                conn = null;
            }

            if (conn == null)
            {
                continue;
            }

            bool orphaned;
            if (string.IsNullOrEmpty(conn.InstanceId))
            {
                orphaned = now - conn.ConnectedTimestamp > LegacyRecordMaxAge;
            }
            else if (conn.InstanceId == instance.Id)
            {
                if (instance.IsServing(connectionId))
                {
                    continue;
                }
                ownLeaksNow.Add(connectionId);
                orphaned = suspectedOwnLeaks.Contains(connectionId);
            }
            else
            {
                if (!alive.TryGetValue(conn.InstanceId, out var isAlive))
                {
                    isAlive = await cache.KeyExistsAsync(HeartbeatKey(conn.InstanceId));
                    alive[conn.InstanceId] = isAlive;
                }

                if (isAlive)
                {
                    continue;
                }
                deadOwnersNow.Add(conn.InstanceId);
                orphaned = suspectedDeadOwners.Contains(conn.InstanceId);
            }

            if (!orphaned)
            {
                continue;
            }

            orphans.Add(connectionId);
            if (conn.SubscribedEventId > 0)
            {
                if (!orphansByEvent.TryGetValue(conn.SubscribedEventId, out var forEvent))
                {
                    forEvent = [];
                    orphansByEvent[conn.SubscribedEventId] = forEvent;
                }
                forEvent.Add(connectionId);
            }
        }

        suspectedDeadOwners = deadOwnersNow;
        suspectedOwnLeaks = ownLeaksNow;

        if (orphans.Count == 0)
        {
            return 0;
        }

        // One multi-field delete per hash: the first sweep after deploy can find a large backlog.
        // Event hashes first, so a failure part way never leaves a counted entry with no record.
        await Task.WhenAll(orphansByEvent.Select(e =>
            cache.HashDeleteAsync(string.Format(Consts.STATUS_EVENT_CONNECTIONS, e.Key), e.Value.ToArray())));
        await cache.HashDeleteAsync(Consts.STATUS_CONNECTIONS, orphans.ToArray());

        logger.LogInformation("Removed {n} orphaned status connections across {events} events", orphans.Count, orphansByEvent.Count);
        return orphans.Count;
    }

    public static string HeartbeatKey(string instanceId) => string.Format(Consts.STATUS_INSTANCE_ALIVE, instanceId);
}
