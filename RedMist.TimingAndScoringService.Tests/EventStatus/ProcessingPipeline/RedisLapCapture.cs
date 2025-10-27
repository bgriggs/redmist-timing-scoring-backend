using RedMist.Backend.Shared.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.ProcessingPipeline;

/// <summary>
/// Helper class to capture lap data sent to Redis StreamAddAsync during tests
/// </summary>
public class RedisLapCapture
{
    private readonly List<CarLapData> _capturedLaps = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Gets all captured lap data
    /// </summary>
    public List<CarLapData> CapturedLaps
    {
        get
        {
            lock (_lock)
            {
                return [.. _capturedLaps];
            }
        }
    }

    /// <summary>
    /// Processes a StreamAddAsync call and extracts lap data
    /// </summary>
    /// <param name="field">The field name (should be "laps")</param>
    /// <param name="value">The JSON value containing lap data</param>
    public void CaptureStreamAdd(RedisValue field, RedisValue value)
    {
        if (field.ToString() == "laps" && value.HasValue)
        {
            try
            {
                var laps = JsonSerializer.Deserialize<List<CarLapData>>(value.ToString());
                if (laps != null)
                {
                    lock (_lock)
                    {
                        _capturedLaps.AddRange(laps);
                    }
                }
            }
            catch
            {
                // Ignore deserialization errors in tests
            }
        }
    }

    /// <summary>
    /// Gets laps for a specific car number
    /// </summary>
    public List<CarLapData> GetLapsForCar(string carNumber)
    {
        lock (_lock)
        {
            return [.. _capturedLaps
                .Where(lap => lap.Log.CarNumber == carNumber)
                .OrderBy(lap => lap.Log.Timestamp)];
        }
    }

    /// <summary>
    /// Gets the count of laps captured for a specific car
    /// </summary>
    public int GetLapCountForCar(string carNumber)
    {
        lock (_lock)
        {
            return _capturedLaps.Count(lap => lap.Log.CarNumber == carNumber);
        }
    }

    /// <summary>
    /// Clears all captured lap data
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _capturedLaps.Clear();
        }
    }

    /// <summary>
    /// Gets the most recent lap for a specific car
    /// </summary>
    public CarLapData? GetLatestLapForCar(string carNumber)
    {
        lock (_lock)
        {
            return _capturedLaps
                .Where(lap => lap.Log.CarNumber == carNumber)
                .OrderByDescending(lap => lap.Log.Timestamp)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Checks if a specific lap number was logged for a car
    /// </summary>
    public bool HasLap(string carNumber, int lapNumber)
    {
        lock (_lock)
        {
            return _capturedLaps.Any(lap =>
                lap.Log.CarNumber == carNumber &&
                lap.LastLapNum == lapNumber);
        }
    }

    /// <summary>
    /// Gets all unique car numbers that have logged laps
    /// </summary>
    public List<string> GetCarsWithLaps()
    {
        lock (_lock)
        {
            return [.. _capturedLaps
                .Select(lap => lap.Log.CarNumber)
                .Distinct()
                .OrderBy(num => num)];
        }
    }
}
