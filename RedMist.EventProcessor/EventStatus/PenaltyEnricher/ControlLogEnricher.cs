using RedMist.Backend.Shared.Models;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Collections.Immutable;
using System.Text.Json;

namespace RedMist.EventProcessor.EventStatus.PenaltyEnricher;

/// <summary>
/// Gets penalties from redis and applies to car positions.
/// </summary>
public class ControlLogEnricher : BackgroundService
{
    private ILogger Logger { get; }
    private readonly IConnectionMultiplexer cacheMux;
    private readonly SessionContext sessionContext;
    private readonly int eventId;
    private volatile ImmutableDictionary<string, CarPenalty> penaltyLookup;
    private volatile bool updateReset = true;


    public ControlLogEnricher(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux,
        IConfiguration configuration, SessionContext sessionContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.sessionContext = sessionContext;
        eventId = configuration.GetValue("event_id", 0);
        penaltyLookup = [];
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cache = cacheMux.GetDatabase();
                var carLogCacheKey = string.Format(Backend.Shared.Consts.CONTROL_LOG_CAR_PENALTIES, eventId);
                var controlLogHash = await cache.HashGetAllAsync(carLogCacheKey);

                var penalties = new Dictionary<string, CarPenalty>();
                foreach (var entry in controlLogHash!)
                {
                    var penalty = JsonSerializer.Deserialize<CarPenalty>(entry.Value.ToString()!);
                    if (penalty != null && entry.Name.HasValue)
                    {
                        penalties[entry.Name!] = penalty;
                    }
                }
                penaltyLookup = penalties.ToImmutableDictionary();
                //Logger.LogTrace("Updated penalty lookup with {count} entries", penalties.Count);
                updateReset = true;
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error reading event status stream");
            }
        }
    }

    /// <summary>
    /// Get penalties from redis and apply to car positions.
    /// </summary>
    public List<CarPositionPatch> Process()
    {
        // Only update once per session state update
        if (!updateReset)
            return [];

        var cars = sessionContext.SessionState.CarPositions;

        var patches = new List<CarPositionPatch>();
        try
        {
            foreach (var car in cars)
            {
                if (car.Number == null)
                    continue;

                if (penaltyLookup.TryGetValue(car.Number, out var penalty))
                {
                    // Car has penalties - update if different
                    if (car != null && penalty != null)
                    {
                        var patch = new CarPositionPatch();
                        if (car.PenalityWarnings != penalty.Warnings)
                        {
                            car.PenalityWarnings = penalty.Warnings;
                            patch.PenalityWarnings = penalty.Warnings;
                        }
                        if (car.PenalityLaps != penalty.Laps)
                        {
                            car.PenalityLaps = penalty.Laps;
                            patch.PenalityLaps = penalty.Laps;
                        }
                        if (TimingCommon.Models.Mappers.CarPositionMapper.IsValidPatch(patch))
                        {
                            patch.Number = car.Number;
                            patches.Add(patch);
                        }
                    }
                }
                else
                {
                    // Car not in penalty list - clear penalties if they were previously set
                    if (car.PenalityWarnings > 0 || car.PenalityLaps > 0)
                    {
                        var patch = new CarPositionPatch();
                        if (car.PenalityWarnings > 0)
                        {
                            car.PenalityWarnings = 0;
                            patch.PenalityWarnings = 0;
                        }
                        if (car.PenalityLaps > 0)
                        {
                            car.PenalityLaps = 0;
                            patch.PenalityLaps = 0;
                        }
                        if (TimingCommon.Models.Mappers.CarPositionMapper.IsValidPatch(patch))
                        {
                            patch.Number = car.Number;
                            patches.Add(patch);
                            Logger.LogDebug("Cleared penalties for car {carNumber} (no longer in penalty list)", car.Number);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error retrieving control log penalties");
        }
        return patches;
    }
}
