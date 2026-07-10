using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.TimingCommon.LapTiming;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.EventProcessor.EventStatus.LapData;

/// <summary>
/// Owns the event's <see cref="TrackMap"/>: loads a previously-learned map from the database, or learns
/// one from live GPS positions (one clean lap), then persists it (per event) and publishes it to Redis
/// for other consumers (e.g. the UI). The map is reused across sessions within the event, so once it
/// exists the projection works from lap one.
///
/// Built positions arrive snapped to the track path (the source corrects them), so a single car's clean
/// lap is enough to learn the geometry. All mutating calls happen under the pipeline's write lock, so
/// the in-memory state needs no additional synchronisation.
/// </summary>
public class TrackMapService
{
    private readonly SessionContext sessionContext;
    private readonly IDbContextFactory<TsContext> dbContextFactory;
    private readonly IConnectionMultiplexer redis;
    private readonly TimeProvider timeProvider;
    private ILogger Logger { get; }

    private TrackMap? currentMap;
    private bool loaded;
    private readonly Dictionary<string, TrackMapBuilder> builders = [];

    public TrackMapService(SessionContext sessionContext, IDbContextFactory<TsContext> dbContextFactory,
        IConnectionMultiplexer redis, ILoggerFactory loggerFactory, TimeProvider? timeProvider = null)
    {
        this.sessionContext = sessionContext;
        this.dbContextFactory = dbContextFactory;
        this.redis = redis;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }

    /// <summary>The current track map for the event, or null if none has been learned/loaded yet.</summary>
    public TrackMap? CurrentMap => currentMap;

    /// <summary>
    /// Loads the event's persisted map once. Idempotent and cheap after the first call.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (loaded)
            return;
        loaded = true;

        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var record = await db.TrackMaps.AsNoTracking()
                .FirstOrDefaultAsync(t => t.EventId == sessionContext.EventId, cancellationToken);
            if (record?.Map is { Points.Count: > 1 })
            {
                currentMap = record.Map;
                Logger.LogInformation("Loaded track map for event {event}: {points} points, {len:F0} m",
                    sessionContext.EventId, currentMap.Points.Count, currentMap.TotalLengthMeters);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load track map for event {event}", sessionContext.EventId);
        }
    }

    /// <summary>
    /// Feeds a corrected GPS position for a car into the map learner. Once a car completes a clean lap a
    /// map is built, persisted, and published; subsequent samples are ignored while a map exists.
    /// </summary>
    public async Task AddSampleAsync(string carNumber, double latitude, double longitude, int completedLaps,
        CancellationToken cancellationToken = default)
    {
        if (currentMap != null || string.IsNullOrEmpty(carNumber))
            return;

        if (!builders.TryGetValue(carNumber, out var builder))
            builders[carNumber] = builder = new TrackMapBuilder(sessionContext.EventId);

        builder.AddSample(latitude, longitude, completedLaps);
        if (!builder.IsComplete)
            return;

        var map = builder.Build(sessionContext.SessionState.SessionId, timeProvider.GetUtcNow().UtcDateTime);
        if (map == null)
            return;

        currentMap = map;
        builders.Clear();
        Logger.LogInformation("Learned track map for event {event} from car {car}: {points} points, {len:F0} m",
            sessionContext.EventId, carNumber, map.Points.Count, map.TotalLengthMeters);

        await PersistAsync(map, cancellationToken);
        await PublishAsync(map, cancellationToken);
    }

    private async Task PersistAsync(TrackMap map, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var record = await db.TrackMaps.FirstOrDefaultAsync(t => t.EventId == map.EventId, cancellationToken);
            if (record == null)
            {
                record = new TrackMapRecord { EventId = map.EventId };
                db.TrackMaps.Add(record);
            }
            record.Map = map;
            record.UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to persist track map for event {event}", map.EventId);
        }
    }

    private async Task PublishAsync(TrackMap map, CancellationToken cancellationToken)
    {
        try
        {
            var key = string.Format(Consts.TRACK_MAP_KEY, map.EventId);
            var json = JsonSerializer.Serialize(map);
            await redis.GetDatabase().StringSetAsync(key, json);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to publish track map for event {event}", map.EventId);
        }
    }
}