using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Utilities;
using RedMist.Backend.Shared.Models;
using RedMist.EventProcessor.Models;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;
using DriverInfo = RedMist.TimingCommon.Models.DriverInfo;

namespace RedMist.EventProcessor.EventStatus.DriverInformation;

public class DriverEnricher
{
    private ILogger Logger { get; }
    private readonly SessionContext sessionContext;
    private readonly IConnectionMultiplexer cacheMux;


    public DriverEnricher(SessionContext context, ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux)
    {
        sessionContext = context;
        this.cacheMux = cacheMux;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    /// <summary>
    /// Handles an incoming timing message to update car position video status.
    /// </summary>
    public PatchUpdates? Process(TimingMessage message)
    {
        if (string.IsNullOrEmpty(message.Data))
        {
            Logger.LogWarning("Unable to deserialize DriverInfo from message data: message data is null or empty");
            return null;
        }

        DriverInfo? driverInfo;
        try
        {
            var dis = JsonSerializer.Deserialize<DriverInfoSource>(message.Data);
            driverInfo = dis?.DriverInfo;
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Unable to deserialize DriverInfo from message data: {Data}", message.Data);
            return null;
        }

        if (driverInfo == null)
        {
            Logger.LogWarning("Unable to deserialize DriverInfo from message data: {Data}", message.Data);
            return null;
        }

        CarPositionPatch? patch = null;
        if (!string.IsNullOrWhiteSpace(driverInfo.CarNumber))
        {
            if (sessionContext.EventId != driverInfo.EventId)
            {
                Logger.LogTrace("DriverInfo event ID {DriverEventId} is not this event, ignoring.", driverInfo.EventId);
                return null;
            }

            var car = sessionContext.GetCarByNumber(driverInfo.CarNumber);
            if (car != null)
            {
                patch = UpdateCar(driverInfo, car);
            }
        }
        else if (driverInfo.TransponderId > 0)
        {
            var number = sessionContext.GetCarNumberForTransponder(driverInfo.TransponderId);
            if (number != null)
            {
                var car = sessionContext.GetCarByNumber(number);
                if (car != null)
                {
                    patch = UpdateCar(driverInfo, car);
                }
            }
        }
        else
        {
            Logger.LogTrace("Unable to resolve car for DriverInfo event:{e}, car:{c}, transponder:{t}",
                driverInfo.EventId, driverInfo.CarNumber, driverInfo.TransponderId);
        }

        if (patch != null)
        {
            return new PatchUpdates([], [patch]);
        }
        return null;
    }

    /// <summary>
    /// Gets all current entries from cache to apply current status and clear out 
    /// expired entries. This should be called periodically to ensure car positions
    /// are up to date, such as every 60 seconds.
    /// </summary>
    public async Task<PatchUpdates?> ProcessApplyFullAsync()
    {
        var carNumbers = sessionContext.SessionState.CarPositions
            .Select(c => c.Number)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToArray();

        var patches = await ProcessCarsAsync(carNumbers!);
        if (patches.Count > 0)
        {
            return new PatchUpdates([], [.. patches]);
        }
        return null;
    }

    /// <summary>
    /// Resolves and applies driver information for a set of cars.
    ///
    /// The cache reads are issued a tier at a time rather than a car at a time. This runs inside
    /// the pipeline's write lock on every message that touches cars, so a round trip per car put
    /// the whole field's worth of cache latency on the critical path - one lookup per car, and
    /// another for each car falling back to its transponder. Batching each tier costs at most two
    /// round trips however many cars are in the set.
    /// </summary>
    public async Task<List<CarPositionPatch>> ProcessCarsAsync(IEnumerable<string> carNumbers, IDatabase? cache = null)
    {
        var cars = new List<CarPosition>();
        foreach (var carNumber in carNumbers)
        {
            if (string.IsNullOrEmpty(carNumber))
            {
                Logger.LogWarning("Car number is null or empty in ProcessCarAsync");
                continue;
            }

            var car = sessionContext.GetCarByNumber(carNumber);
            if (car == null)
            {
                Logger.LogWarning("Car not found for number {CarNumber} in ProcessCarAsync", carNumber);
                continue;
            }
            cars.Add(car);
        }

        if (cars.Count == 0)
            return [];

        cache ??= cacheMux.GetDatabase();
        var drivers = new DriverInfo?[cars.Count];

        // Event and Car Number
        var keys = cars
            .Select(c => string.Format(Consts.EVENT_DRIVER_KEY, sessionContext.EventId, c.Number))
            .ToArray();
        var values = await cache.StringGetAllAsync(keys);

        var byTransponder = new List<int>();
        for (var i = 0; i < cars.Count; i++)
        {
            if (values[i].HasValue)
            {
                // Only a key that is absent falls through to the transponder. One that is present
                // but unreadable leaves the car without a driver, as it always has.
                drivers[i] = Deserialize(values[i], cars[i].Number, keys[i]);
            }
            else if (cars[i].TransponderId > 0)
            {
                byTransponder.Add(i);
            }
        }

        // Transponder only
        if (byTransponder.Count > 0)
        {
            var transponderKeys = byTransponder
                .Select(i => string.Format(Consts.DRIVER_TRANSPONDER_KEY, cars[i].TransponderId))
                .ToArray();
            var transponderValues = await cache.StringGetAllAsync(transponderKeys);

            for (var j = 0; j < byTransponder.Count; j++)
            {
                if (transponderValues[j].HasValue)
                {
                    var i = byTransponder[j];
                    drivers[i] = Deserialize(transponderValues[j], cars[i].Number, transponderKeys[j]);
                }
            }
        }

        var patches = new List<CarPositionPatch>();
        for (var i = 0; i < cars.Count; i++)
        {
            var patch = ApplyDriver(drivers[i], cars[i]);
            if (patch != null)
                patches.Add(patch);
        }
        return patches;
    }

    private DriverInfo? Deserialize(RedisValue json, string? carNumber, string key)
    {
        try
        {
            return JsonSerializer.Deserialize<DriverInfoSource>(json!.ToString())?.DriverInfo;
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Unable to deserialize DriverInfo from cache for car {CarNumber}, key {Key}", carNumber, key);
            return null;
        }
    }

    private static CarPositionPatch? ApplyDriver(DriverInfo? driverInfo, CarPosition car)
    {
        if (driverInfo != null)
        {
            return UpdateCar(driverInfo, car);
        }

        // No driver info found, clear out any existing status
        car.DriverId = string.Empty;
        car.DriverName = string.Empty;

        // Send "empty" status since null will be ignored. The car number is required:
        // the consolidator drops any patch without one, so it would never reach clients.
        return new CarPositionPatch()
        {
            Number = car.Number,
            DriverId = string.Empty,
            DriverName = string.Empty
        };
    }

    /// <summary>
    /// Processes the specified car and retrieves its current driver information, returning a patch representing the
    /// car's position and driver status.
    /// </summary>
    /// <remarks>If driver information cannot be found in the cache, the returned patch will contain empty
    /// driver fields to indicate no active driver. The method uses the provided cache for lookups, falling back to a
    /// default cache if none is supplied.</remarks>
    /// <param name="carNumber">The unique identifier or number of the car to process. Cannot be null or empty.</param>
    /// <param name="cache">An optional cache database instance used to retrieve driver information. If not provided, a default database
    /// will be used.</param>
    /// <returns>A <see cref="CarPositionPatch"/> containing the car's driver information and position status, or <see
    /// langword="null"/> if the car number is invalid or the car cannot be found.</returns>
    public async Task<CarPositionPatch?> ProcessCarAsync(string carNumber, IDatabase? cache = null)
    {
        var patches = await ProcessCarsAsync([carNumber], cache);
        return patches.Count > 0 ? patches[0] : null;
    }

    private static CarPositionPatch? UpdateCar(DriverInfo driverInfo, CarPosition car)
    {
        bool changed = false;
        // The car number is required: the consolidator drops any patch without one, so a patch
        // that omits it updates server state and never reaches a client.
        var patch = new CarPositionPatch { Number = car.Number };
        if (car.DriverId != driverInfo.DriverId)
        {
            car.DriverId = driverInfo.DriverId;
            patch.DriverId = driverInfo.DriverId;
            changed = true;
        }
        if (car.DriverName != driverInfo.DriverName)
        {
            car.DriverName = driverInfo.DriverName;
            patch.DriverName = driverInfo.DriverName;
            changed = true;
        }

        if (changed)
            return patch;
        return null;
    }
}
