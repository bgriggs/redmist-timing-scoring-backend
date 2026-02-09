using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus.LapData;

/// <summary>
/// Interface for managing a rolling window of the last 5 laps per car.
/// Provides fast lookup of recent lap history by event ID and car number.
/// </summary>
public interface ICarLapHistoryService
{
    /// <summary>
    /// Adds a lap to the rolling window for the specified car.
    /// Maintains only the last 5 laps.
    /// </summary>
    /// <param name="position">The CarPosition data for the completed lap</param>
    Task AddLapAsync(CarPosition position);

    /// <summary>
    /// Retrieves the last up to 5 laps for the specified car.
    /// Returns the laps in reverse chronological order (most recent first).
    /// </summary>
    /// <param name="carNumber">The car number</param>
    /// <returns>A list of CarPosition objects representing the last laps, or an empty list if none found</returns>
    Task<List<CarPosition>> GetLapsAsync(string carNumber);
}
