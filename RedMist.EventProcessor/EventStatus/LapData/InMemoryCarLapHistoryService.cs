using RedMist.Backend.Shared;
using RedMist.TimingCommon.Models;
using System.Text.Json;

namespace RedMist.EventProcessor.EventStatus.LapData;

/// <summary>
/// In-memory implementation of ICarLapHistoryService for testing purposes.
/// Maintains lap history in memory without requiring Redis.
/// </summary>
public class InMemoryCarLapHistoryService : ICarLapHistoryService
{
    private const int MaxLapsPerCar = 5;
    
    private readonly SessionContext _sessionContext;
    private readonly Dictionary<string, List<CarPosition>> _storage = new();

    public InMemoryCarLapHistoryService(SessionContext sessionContext)
    {
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
    }

    /// <summary>
    /// Adds a lap to the rolling window for the specified car.
    /// Maintains only the last 5 laps in memory.
    /// </summary>
    /// <param name="position">The CarPosition data for the completed lap</param>
    public virtual Task AddLapAsync(CarPosition position)
    {
        if (string.IsNullOrEmpty(position?.Number))
            throw new ArgumentException("Car number cannot be null or empty", nameof(position));

        var eventId = _sessionContext.EventId;
        var key = string.Format(Consts.CAR_LAP_HISTORY, eventId, position.Number);

        if (!_storage.ContainsKey(key))
        {
            _storage[key] = [];
        }

        // Insert at the beginning (most recent first)
        _storage[key].Insert(0, position);

        // Trim to keep only the last MaxLapsPerCar laps
        if (_storage[key].Count > MaxLapsPerCar)
        {
            _storage[key].RemoveRange(MaxLapsPerCar, _storage[key].Count - MaxLapsPerCar);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Retrieves the last up to 5 laps for the specified car.
    /// Returns the laps in reverse chronological order (most recent first).
    /// </summary>
    /// <param name="carNumber">The car number</param>
    /// <returns>A list of CarPosition objects representing the last laps, or an empty list if none found</returns>
    public virtual Task<List<CarPosition>> GetLapsAsync(string carNumber)
    {
        if (string.IsNullOrEmpty(carNumber))
            throw new ArgumentException("Car number cannot be null or empty", nameof(carNumber));

        var eventId = _sessionContext.EventId;
        var key = string.Format(Consts.CAR_LAP_HISTORY, eventId, carNumber);

        if (_storage.TryGetValue(key, out var laps))
        {
            // Return deep copies to prevent external modification (matching Redis behavior)
            var deepCopies = laps.Select(lap =>
            {
                var json = JsonSerializer.Serialize(lap);
                return JsonSerializer.Deserialize<CarPosition>(json)!;
            }).ToList();

            return Task.FromResult(deepCopies);
        }

        return Task.FromResult(new List<CarPosition>());
    }

    /// <summary>
    /// Clears all lap history from memory. Useful for test cleanup.
    /// </summary>
    public void Clear()
    {
        _storage.Clear();
    }
}
