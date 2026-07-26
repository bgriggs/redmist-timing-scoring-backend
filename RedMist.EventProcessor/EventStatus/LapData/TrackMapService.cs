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
    private readonly List<(string Car, double Distance)> startFinishObservations = [];
    private const int MaxStartFinishObservations = 20;

    /// <summary>
    /// Crossings needed to move a line that has already been located, against the five that settle
    /// it in the first place. Overturning a working calibration should take more evidence than
    /// establishing one, and all of it gathered since.
    /// </summary>
    private const int MinObservationsToRecalibrate = 10;

    /// <summary>
    /// Distinct cars those crossings must come from. Ten crossings by one car circulating alone are
    /// not ten measurements, they are one systematic bias repeated - and that car would otherwise
    /// capture the event's start/finish line for the whole field.
    /// </summary>
    private const int MinCarsToRecalibrate = 3;

    /// <summary>
    /// How far the crossings must sit from the located line before it is worth moving, as a fraction
    /// of the lap, floored by <see cref="MinRecalibrationDistanceMeters"/>.
    /// </summary>
    private const double RecalibrationThreshold = 0.05;

    /// <summary>
    /// Crossings scatter in normal running - cars cross at different speeds and their laps are
    /// reported with different delays - and the calibrator calls anything inside 100 m agreement.
    /// A purely proportional threshold falls below that on a track under a mile and a quarter, which
    /// would have the line chasing its own noise, so it never goes under half as much again.
    /// </summary>
    private const double MinRecalibrationDistanceMeters = 150.0;

    /// <summary>
    /// A revised line waiting on a second, independent window of crossings to say the same thing.
    /// Without that, a field whose crossings fall into two clusters - a mixed grid whose classes
    /// report laps a few seconds apart - moves the line back and forth between them indefinitely,
    /// each move a database write and a jump in every car's reported position.
    /// </summary>
    private double? pendingRevision;

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
    /// The lap length the timing system declares for this track, when it declares one. Corroborating
    /// laps against each other cannot tell a circuit from one whose every buffer covers two laps -
    /// a feed reporting lap counts consistently late produces exactly that, and the doubled laps
    /// agree with each other perfectly. Only something outside the GPS can settle it.
    ///
    /// Setting it also re-examines the map already in hand. A map learned before this check existed,
    /// or before the declared length arrived, is otherwise reloaded and trusted every session for
    /// the rest of the event.
    /// </summary>
    public double? DeclaredLapLengthMeters => declaredLapLengthMeters;

    private double? declaredLapLengthMeters;

    /// <summary>
    /// Records the length the timing system declares, and re-examines the map already in hand
    /// against it.
    /// </summary>
    public async Task SetDeclaredLapLengthAsync(double meters, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(meters) || meters <= 0 || declaredLapLengthMeters == meters)
            return;

        declaredLapLengthMeters = meters;
        if (IsLongerThanDeclared(currentMap?.TotalLengthMeters))
            await DiscardMapAsync(cancellationToken);
    }

    /// <summary>
    /// Whether a map is too long to be this track. Deliberately one-sided: the failure this guards
    /// against is a buffer that swallowed a lap boundary, which is always too long, while a map
    /// reading short is the ordinary result of a polyline cutting every corner. Treating short as
    /// wrong would let a track length entered in the wrong units destroy a perfectly good map and
    /// then reject every replacement for the rest of the event.
    /// </summary>
    private bool IsLongerThanDeclared(double? lengthMeters)
    {
        if (lengthMeters is not double length)
            return false;
        if (declaredLapLengthMeters is not double declared || !double.IsFinite(declared) || declared <= 0)
            return false;
        return (length - declared) / declared > DeclaredLengthTolerance;
    }

    /// <summary>
    /// Throws away the current map, and the stored copies of it, so the next clean laps replace it
    /// rather than the event carrying a map it cannot use for the rest of its life.
    /// </summary>
    private async Task DiscardMapAsync(CancellationToken cancellationToken)
    {
        Logger.LogWarning(
            "Discarding the {len:F0} m map held for event {event}: the timing system declares {declared:F0} m. Relearning.",
            currentMap!.TotalLengthMeters, sessionContext.EventId, declaredLapLengthMeters);

        currentMap = null;
        builders.Clear();
        startFinishObservations.Clear();
        pendingRevision = null;

        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var record = await db.TrackMaps.FirstOrDefaultAsync(t => t.EventId == sessionContext.EventId, cancellationToken);
            if (record != null)
            {
                db.TrackMaps.Remove(record);
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete the discarded track map for event {event}", sessionContext.EventId);
        }

        try
        {
            // Consumers draw the outline from here, and would otherwise keep drawing the one just
            // discarded while every car reports its position as unknown.
            await redis.GetDatabase().KeyDeleteAsync(string.Format(Consts.TRACK_MAP_KEY, sessionContext.EventId));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to remove the discarded track map for event {event} from the cache", sessionContext.EventId);
        }
    }

    /// <summary>
    /// How far a learned map may sit from the declared length. Generous, because a polyline through
    /// sampled fixes cuts every corner and reads short, and the declared figure is its own
    /// measurement - but nowhere near enough to admit a map covering two laps.
    /// </summary>
    private const double DeclaredLengthTolerance = 0.25;

    /// <summary>
    /// True once the map's start/finish line has been located. Until then a position on the map can
    /// be measured, but not reliably expressed as a distance into the lap.
    /// </summary>
    public bool IsStartFinishCalibrated => currentMap?.StartFinishOffsetMeters != null;

    /// <summary>
    /// Records where a car was on the path at the moment its completed-lap count incremented - that
    /// is, a sighting of the start/finish line. Enough agreeing sightings locate the line; the map
    /// is then persisted and published so a reload starts calibrated.
    ///
    /// Crossings keep being collected afterwards. A line settled from bad evidence - a session that
    /// began with a handful of degraded devices, a course whose timing line has since moved - would
    /// otherwise be wrong for the rest of the event, and the whole event is what the map is kept
    /// for. Moving it takes more evidence than settling it did, all of it gathered since, agreeing
    /// with itself and disagreeing with where the line currently sits by more than crossings
    /// normally scatter.
    /// </summary>
    public async Task AddStartFinishObservationAsync(string carNumber, double distanceAlongMeters,
        CancellationToken cancellationToken = default)
    {
        if (currentMap == null)
            return;

        startFinishObservations.Add((carNumber, distanceAlongMeters));
        if (startFinishObservations.Count > MaxStartFinishObservations)
            startFinishObservations.RemoveAt(0);

        if (currentMap.StartFinishOffsetMeters is double located)
            await ReviseStartFinishAsync(located, cancellationToken);
        else
            await LocateStartFinishAsync(cancellationToken);
    }

    private async Task LocateStartFinishAsync(CancellationToken cancellationToken)
    {
        var offset = StartFinishCalibrator.EstimateOffsetMeters(Distances(), currentMap!.TotalLengthMeters);
        if (offset == null)
            return;

        currentMap.StartFinishOffsetMeters = offset;
        Logger.LogInformation(
            "Calibrated start/finish for event {event} at {offset:F0} m along the map ({count} crossings)",
            sessionContext.EventId, offset.Value, startFinishObservations.Count);

        // Start the next window fresh, so nothing that settled this line counts towards moving it.
        startFinishObservations.Clear();
        await PersistAsync(currentMap, cancellationToken);
        await PublishAsync(currentMap, cancellationToken);
    }

    private async Task ReviseStartFinishAsync(double located, CancellationToken cancellationToken)
    {
        if (startFinishObservations.Count < MinObservationsToRecalibrate)
            return;
        if (startFinishObservations.Select(o => o.Car).Distinct().Count() < MinCarsToRecalibrate)
            return;

        var revised = StartFinishCalibrator.EstimateOffsetMeters(Distances(),
            currentMap!.TotalLengthMeters, MinObservationsToRecalibrate);
        if (revised == null)
            return;

        var threshold = Math.Max(currentMap.TotalLengthMeters * RecalibrationThreshold, MinRecalibrationDistanceMeters);
        var drift = TrackGeometry.CircularDistanceMeters(revised.Value, located, currentMap.TotalLengthMeters);
        if (drift <= threshold)
        {
            // These crossings say the line is where it already is, which retires any revision that
            // was waiting on a second opinion.
            pendingRevision = null;
            startFinishObservations.Clear();
            return;
        }

        if (pendingRevision is not double pending
            || TrackGeometry.CircularDistanceMeters(pending, revised.Value, currentMap.TotalLengthMeters) > threshold)
        {
            // First window to point somewhere else. Hold it, and see whether an independent one
            // says the same before moving anything.
            Logger.LogInformation(
                "Crossings for event {event} put start/finish {drift:F0} m from where it sits, at {revised:F0} m. Waiting for a second window to agree.",
                sessionContext.EventId, drift, revised.Value);
            pendingRevision = revised;
            startFinishObservations.Clear();
            return;
        }

        currentMap.StartFinishOffsetMeters = revised;
        Logger.LogWarning(
            "Moved start/finish for event {event} from {old:F0} m to {new:F0} m along the map: two windows of crossings since it was located agree it is {drift:F0} m away",
            sessionContext.EventId, located, revised.Value, drift);

        pendingRevision = null;
        startFinishObservations.Clear();
        await PersistAsync(currentMap, cancellationToken);
        await PublishAsync(currentMap, cancellationToken);
    }

    private List<double> Distances() => [.. startFinishObservations.Select(o => o.Distance)];

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

                // A stored map is only as good as the checks that existed when it was learned, so
                // it is re-examined before anything is claimed about having loaded one.
                if (IsLongerThanDeclared(currentMap.TotalLengthMeters))
                {
                    await DiscardMapAsync(cancellationToken);
                }
                else
                {
                    Logger.LogInformation("Loaded track map for event {event}: {points} points, {len:F0} m, {sf}",
                        sessionContext.EventId, currentMap.Points.Count, currentMap.TotalLengthMeters,
                        currentMap.StartFinishOffsetMeters is double sf
                            ? $"start/finish at {sf:F0} m"
                            : "start/finish not yet calibrated");
                }
            }
            loaded = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load track map for event {event}", sessionContext.EventId);
        }
    }

    /// <summary>
    /// Feeds a corrected GPS position for a car into the map learner. Once a car has turned two
    /// clean laps that agree on the track's length a map is built, persisted, and published;
    /// subsequent samples are ignored while a map exists.
    /// </summary>
    /// <param name="carNumber">Car the sample belongs to.</param>
    /// <param name="latitude">Sample latitude in decimal degrees.</param>
    /// <param name="longitude">Sample longitude in decimal degrees.</param>
    /// <param name="completedLaps">The car's completed-lap count when the sample was taken.</param>
    /// <param name="onTrack">
    /// Whether the car was on the racing surface. Pit and paddock positions are not the racing line,
    /// and a lap containing them is not a lap of the track.
    /// </param>
    /// <param name="cancellationToken">Cancels the persist and publish that follow a completed map.</param>
    public async Task AddSampleAsync(string carNumber, double latitude, double longitude, int completedLaps,
        bool onTrack, CancellationToken cancellationToken = default)
    {
        if (currentMap != null || string.IsNullOrEmpty(carNumber))
            return;

        if (!builders.TryGetValue(carNumber, out var builder))
            builders[carNumber] = builder = new TrackMapBuilder(sessionContext.EventId);

        builder.AddSample(latitude, longitude, completedLaps, onTrack);
        if (!builder.IsComplete)
            return;

        var map = builder.Build(sessionContext.SessionState.SessionId, timeProvider.GetUtcNow().UtcDateTime);
        if (map == null)
            return;

        if (IsLongerThanDeclared(map.TotalLengthMeters))
        {
            // Learned from laps that agree with each other but not with the track. Start that car
            // over rather than persisting a map the event is then stuck with.
            Logger.LogWarning(
                "Discarding a {len:F0} m map learned for event {event} from car {car}: the timing system declares {declared:F0} m",
                map.TotalLengthMeters, sessionContext.EventId, carNumber, declaredLapLengthMeters);
            builders.Remove(carNumber);
            return;
        }

        currentMap = map;
        startFinishObservations.Clear();
        pendingRevision = null;
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