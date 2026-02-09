using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.EventProcessor.Models;
using RedMist.TimingCommon.Models;
using System.Text.Json;

namespace RedMist.EventProcessor.EventStatus.LapData;

/// <summary>
/// Provides an estimation of the car's next lap time based on recent lap history and current track conditions.
/// </summary>
public class ProjectedLapTimeEnricher
{
    private readonly CarLapHistoryService carLapHistoryService;
    private readonly SessionContext sessionContext;

    private ILogger Logger { get; }


    public ProjectedLapTimeEnricher(ILoggerFactory loggerFactory, CarLapHistoryService carLapHistoryService, SessionContext sessionContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.carLapHistoryService = carLapHistoryService;
        this.sessionContext = sessionContext;
    }


    public async Task<CarPositionPatch?> ProcessAsync(TimingMessage tm)
    {
        if (tm.Type != Consts.LAP_COMPLETED_TYPE)
            return null;

        var lapCompleted = JsonSerializer.Deserialize<LapCompleted>(tm.Data);
        if (lapCompleted != null)
        {
            var car = sessionContext.GetCarByNumber(lapCompleted.CarNumber);
            if (car != null)
            {
                var projectedTime = await CalculateProjectedLapTimeAsync(car);
                return UpdateCar(car, projectedTime);
            }
        }

        return null;
    }

    /// <summary>
    /// Determines an estimated lap time for the car based on previous laps.
    /// </summary>
    /// <param name="car"></param>
    /// <returns>time in milliseconds or 0 if no projection is available</returns>
    public async Task<int> CalculateProjectedLapTimeAsync(CarPosition car)
    {
        // Get current track flag
        var (currentFlag, _) = await sessionContext.GetCurrentFlagAndLap();

        // Only process for green or yellow flags
        if (currentFlag != Flags.Green && currentFlag != Flags.Yellow)
            return 0;

        // Get lap history
        if (string.IsNullOrEmpty(car.Number))
            return 0;

        var laps = await carLapHistoryService.GetLapsAsync(car.Number);
        if (laps == null || laps.Count == 0)
            return 0;

        // Filter out laps with pit stops
        var cleanLaps = laps.Where(l => !l.LapIncludedPit).ToList();

        // Filter by track flag (green only uses green laps, yellow only uses yellow laps)
        var flagFilteredLaps = cleanLaps.Where(l => l.TrackFlag == currentFlag).ToList();

        // For green laps, perform outlier check if we have at least 3 laps
        if (currentFlag == Flags.Green && flagFilteredLaps.Count >= 3)
        {
            // Parse all lap times
            var lapTimes = flagFilteredLaps
                .Select(l => FastestPaceEnricher.ParseRMTime(l.LastLapTime ?? string.Empty))
                .Where(t => t != TimeSpan.Zero)
                .Select(t => (int)t.TotalMilliseconds)
                .ToList();

            if (lapTimes.Count >= 3)
            {
                // Calculate average of all green laps
                var average = lapTimes.Average();

                // Filter out outliers (laps > 150% of average)
                var threshold = average * 1.5;
                var nonOutlierLaps = new List<CarPosition>();

                foreach (var lap in flagFilteredLaps)
                {
                    var lapTime = FastestPaceEnricher.ParseRMTime(lap.LastLapTime ?? string.Empty);
                    if (lapTime != TimeSpan.Zero && lapTime.TotalMilliseconds <= threshold)
                    {
                        nonOutlierLaps.Add(lap);
                    }
                }

                flagFilteredLaps = nonOutlierLaps;
            }
        }

        // Check minimum lap requirement (at least 1 lap after filtering)
        if (flagFilteredLaps.Count < 1)
            return 0;

        // Calculate average lap time of filtered laps
        int totalMs = 0;
        int usableLaps = 0;

        foreach (var lap in flagFilteredLaps)
        {
            var lapTime = FastestPaceEnricher.ParseRMTime(lap.LastLapTime ?? string.Empty);
            if (lapTime != TimeSpan.Zero)
            {
                totalMs += (int)lapTime.TotalMilliseconds;
                usableLaps++;
            }
        }

        if (usableLaps == 0)
            return 0;

        return totalMs / usableLaps;
    }

    private static CarPositionPatch? UpdateCar(CarPosition car, int projectedTime)
    {
        var patch = new CarPositionPatch { Number = car.Number };

        if (car.ProjectedLapTimeMs != projectedTime)
        {
            patch.ProjectedLapTimeMs = projectedTime;
            car.ProjectedLapTimeMs = projectedTime;
            return patch;
        }

        return null;
    }
}
