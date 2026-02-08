using RedMist.Backend.Shared;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.EventProcessor.EventStatus.LapData;

/// <summary>
/// Service for managing a rolling window of the last 5 laps per car in Redis.
/// Provides fast lookup of recent lap history by event ID and car number.
/// </summary>
public class CarLapHistoryService
{
    private const int MaxLapsPerCar = 5;
    
    private readonly ILogger _logger;
    private readonly IConnectionMultiplexer _cacheMux;
    private readonly SessionContext _sessionContext;

    public CarLapHistoryService(
        ILoggerFactory loggerFactory,
        IConnectionMultiplexer cacheMux,
        SessionContext sessionContext)
    {
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger(GetType().Name);
        _cacheMux = cacheMux ?? throw new ArgumentNullException(nameof(cacheMux));
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
    }

    /// <summary>
    /// Adds a lap to the rolling window for the specified car.
    /// Maintains only the last 5 laps in Redis.
    /// </summary>
    /// <param name="position">The CarPosition data for the completed lap</param>
    public virtual async Task AddLapAsync(CarPosition position)
    {
        if (string.IsNullOrEmpty(position.Number))
            throw new ArgumentException("Car number cannot be null or empty", nameof(position.Number));

        ArgumentNullException.ThrowIfNull(position);

        try
        {
            var eventId = _sessionContext.EventId;
            var key = string.Format(Consts.CAR_LAP_HISTORY, eventId, position.Number);
            var cache = _cacheMux.GetDatabase();

            // Serialize the CarPosition to JSON
            var json = JsonSerializer.Serialize(position);

            // Add to the left of the list (most recent first)
            await cache.ListLeftPushAsync(key, json);

            // Trim the list to keep only the last MaxLapsPerCar laps
            await cache.ListTrimAsync(key, 0, MaxLapsPerCar - 1);

            _logger.LogTrace("Added lap {lap} for car {car} in event {event}. Key: {key}", 
                position.LastLapCompleted, position.Number, eventId, key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding lap for car {car} in event {event}", 
                position.Number, _sessionContext.EventId);
            throw;
        }
    }

    /// <summary>
    /// Retrieves the last up to 5 laps for the specified car.
    /// Returns the laps in reverse chronological order (most recent first).
    /// </summary>
    /// <param name="carNumber">The car number</param>
    /// <returns>A list of CarPosition objects representing the last laps, or an empty list if none found</returns>
    public virtual async Task<List<CarPosition>> GetLapsAsync(string carNumber)
    {
        if (string.IsNullOrEmpty(carNumber))
            throw new ArgumentException("Car number cannot be null or empty", nameof(carNumber));

        try
        {
            var eventId = _sessionContext.EventId;
            var key = string.Format(Consts.CAR_LAP_HISTORY, eventId, carNumber);
            var cache = _cacheMux.GetDatabase();

            // Get all laps from the list
            var values = await cache.ListRangeAsync(key, 0, MaxLapsPerCar - 1);

            var laps = new List<CarPosition>();
            foreach (var value in values)
            {
                if (!value.IsNullOrEmpty)
                {
                    var position = JsonSerializer.Deserialize<CarPosition>(value.ToString());
                    if (position != null)
                    {
                        laps.Add(position);
                    }
                }
            }

            _logger.LogTrace("Retrieved {count} lap(s) for car {car} in event {event}", 
                laps.Count, carNumber, eventId);

            return laps;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving laps for car {car} in event {event}", 
                carNumber, _sessionContext.EventId);
            throw;
        }
    }
}
