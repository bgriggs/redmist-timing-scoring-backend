using Microsoft.EntityFrameworkCore.Metadata.Internal;
using RedMist.Backend.Shared;
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
            driverInfo = JsonSerializer.Deserialize<DriverInfo>(message.Data);
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
        var patches = new List<CarPositionPatch>();
        var cache = cacheMux.GetDatabase();
        var cars = sessionContext.SessionState.CarPositions.ToArray();
        foreach (var car in cars)
        {
            if (!string.IsNullOrEmpty(car.Number))
            {
                var patch = await ProcessCarAsync(car.Number, cache);
                if (patch != null)
                {
                    patches.Add(patch);
                }
            }
        }

        if (patches.Count > 0)
        {
            return new PatchUpdates([], [.. patches]);
        }
        return null;
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
        if (string.IsNullOrEmpty(carNumber))
        {
            Logger.LogWarning("Car number is null or empty in ProcessCarAsync");
            return null;
        }

        DriverInfo? driverInfo = null;
        var car = sessionContext.GetCarByNumber(carNumber);
        if (car == null)
        {
            Logger.LogWarning("Car not found for number {CarNumber} in ProcessCarAsync", carNumber);
            return null;
        }
        CarPositionPatch? patch;
        cache ??= cacheMux.GetDatabase();

        // Event and Car Number
        var key1 = string.Format(Consts.EVENT_DRIVER_KEY, sessionContext.EventId, car.Number);
        var json = await cache.StringGetAsync(key1);
        if (json.HasValue)
        {
            try
            {
                driverInfo = JsonSerializer.Deserialize<DriverInfo>(json!.ToString());
            }
            catch (JsonException ex)
            {
                Logger.LogWarning(ex, "Unable to deserialize DriverInfo from cache for car {CarNumber}, key {Key}", car.Number, key1);
            }
        }
        else if (car.TransponderId > 0)
        {
            // Transponder only
            var key2 = string.Format(Consts.DRIVER_TRANSPONDER_KEY, car.TransponderId);
            json = await cache.StringGetAsync(key2);
            if (json.HasValue)
            {
                try
                {
                    driverInfo = JsonSerializer.Deserialize<DriverInfo>(json!.ToString());
                }
                catch (JsonException ex)
                {
                    Logger.LogWarning(ex, "Unable to deserialize DriverInfo from cache for car {CarNumber}, key {Key}", car.Number, key2);
                }
            }
        }

        if (driverInfo != null)
        {
            patch = UpdateCar(driverInfo, car);
        }
        else
        {
            // No driver info found, clear out any existing status

            car.DriverId = string.Empty;
            car.DriverName = string.Empty;

            // Send "empty" status since null will be ignored
            patch = new CarPositionPatch()
            {
                DriverId = string.Empty,
                DriverName = string.Empty
            };
        }

        return patch;
    }

    private static CarPositionPatch? UpdateCar(DriverInfo driverInfo, CarPosition car)
    {
        bool changed = false;
        var patch = new CarPositionPatch();
        if (car.DriverId != driverInfo.DriverId)
        {
            car.DriverId = driverInfo.DriverId;
            changed = true;
        }
        if (car.DriverName != driverInfo.DriverName)
        {
            car.DriverName = driverInfo.DriverName;
            changed = true;
        }

        if (changed)
            return patch;
        return null;
    }
}
