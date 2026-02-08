namespace RedMist.Backend.Shared.Models;

/// <summary>
/// Data model representing the completion of a lap by a car in an event.
/// </summary>
/// <param name="CarNumber">123</param>
/// <param name="LapNumber">1</param>
/// <param name="Class">GT3</param>
/// <param name="Timestamp">2024-06-01T12:34:56Z</param>
public record LapCompleted(string CarNumber, int LapNumber, string Class, DateTime Timestamp) { }
