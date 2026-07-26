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
/// Also owns the map's start/finish calibration. A learned polyline starts wherever the first sample
/// after a lap rollover landed, not on the line itself, so the line is located separately from where
/// cars actually are when their lap count increments (see <see cref="AddStartFinishObservationAsync"/>).
/// The resulting offset is stored on the map and persisted with it, so a reloaded map is calibrated
/// from the moment it loads.
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
    private DateTime? lastLoadAttemptUtc;
    private static readonly TimeSpan LoadRetryInterval = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, TrackMapBuilder> builders = [];

    /// <summary>
    /// Where cars were on the path when their completed-lap count last incremented. Bounded so a
    /// long event cannot grow it without limit, and so the estimate follows the recent field rather
    /// than crossings from hours ago.
    /// </summary>
    private readonly List<double> startFinishObservations = [];
    private const int MaxStartFinishObservations = 20;

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
    /// True once the map's start/finish line has been located. Until then a position on the map can
    /// be measured, but not reliably expressed as a distance into the lap.
    /// </summary>
    public bool IsStartFinishCalibrated => currentMap?.StartFinishOffsetMeters != null;

    /// <summary>
    /// Records where a car was on the path at the moment its completed-lap count incremented - that
    /// is, a sighting of the start/finish line. Once enough sightings agree the offset is fixed on
    /// the map and persisted; further observations are ignored.
    /// </summary>
    public async Task AddStartFinishObservationAsync(double distanceAlongMeters,
        CancellationToken cancellationToken = default)
    {
        if (currentMap == null || currentMap.StartFinishOffsetMeters != null)
            return;

        startFinishObservations.Add(distanceAlongMeters);
        if (startFinishObservations.Count > MaxStartFinishObservations)
            startFinishObservations.RemoveAt(0);

        var offset = StartFinishCalibrator.EstimateOffsetMeters(startFinishObservations, currentMap.TotalLengthMeters);
        if (offset == null)
            return;

        currentMap.StartFinishOffsetMeters = offset;
        Logger.LogInformation(
            "Calibrated start/finish for event {event} at {offset:F0} m along the map ({count} crossings)",
            sessionContext.EventId, offset.Value, startFinishObservations.Count);

        await PersistAsync(currentMap, cancellationToken);
        await PublishAsync(currentMap, cancellationToken);
    }

    /// <summary>
    /// Loads the event's persisted map once. Idempotent and cheap after the first call.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (loaded)
            return;

        // A failed read must not count as "there is no stored map": the service would go on to learn
        // a fresh one and overwrite the stored row, discarding its start/finish calibration with it.
        // Retry instead, spaced out so a database that is down does not get hammered by every batch.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (lastLoadAttemptUtc != null && now - lastLoadAttemptUtc < LoadRetryInterval)
            return;
        lastLoadAttemptUtc = now;

        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var record = await db.TrackMaps.AsNoTracking()
                .FirstOrDefaultAsync(t => t.EventId == sessionContext.EventId, cancellationToken);
            if (record?.Map is { Points.Count: > 1 })
            {
                currentMap = record.Map;
                Logger.LogInformation("Loaded track map for event {event}: {points} points, {len:F0} m, {sf}",
                    sessionContext.EventId, currentMap.Points.Count, currentMap.TotalLengthMeters,
                    currentMap.StartFinishOffsetMeters is double sf
                        ? $"start/finish at {sf:F0} m"
                        : "start/finish not yet calibrated");
            }
            loaded = true;
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