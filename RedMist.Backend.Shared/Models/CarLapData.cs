using RedMist.Database.Models;

namespace RedMist.Backend.Shared.Models;

/// <summary>
/// Completed lap data for a car, including the log and the last lap number.
/// </summary>
public record CarLapData(CarLapLog Log, int LastLapNum, int SessionId) { }