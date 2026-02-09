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
    private readonly ICarLapHistoryService carLapHistoryService;
    private readonly SessionContext sessionContext;

    private ILogger Logger { get; }


    public ProjectedLapTimeEnricher(ILoggerFactory loggerFactory, ICarLapHistoryService carLapHistoryService, SessionContext sessionContext)
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

        // Prefer same-flag laps, but fall back to all clean laps if insufficient same-flag data
        var flagFilteredLaps = cleanLaps.Where(l => l.TrackFlag == currentFlag).ToList();

        // Use most recent 5 laps maximum (don't go too far back in history)
        var recentLaps = flagFilteredLaps.Take(5).ToList();

        // If we don't have enough same-flag laps, consider all recent clean laps
        if (recentLaps.Count < 3 && cleanLaps.Count >= 3)
        {
            recentLaps = [.. cleanLaps.Take(5)];
        }

        // CRITICAL: Require at least 3 laps for reliable prediction
        if (recentLaps.Count < 3)
            return 0;

        // Remove outliers using MAD-based detection
        var filtered = FilterOutliers(recentLaps);

        if (filtered.Count < 2)
            return 0;

        // Check consistency - if variance is too high, don't predict
        var lapTimes = filtered
            .Select(l => FastestPaceEnricher.ParseRMTime(l.LastLapTime ?? string.Empty))
            .Where(t => t != TimeSpan.Zero)
            .Select(t => t.TotalMilliseconds)
            .ToList();

        if (lapTimes.Count < 2)
            return 0;

        var average = lapTimes.Average();
        var variance = lapTimes.Sum(t => Math.Pow(t - average, 2)) / lapTimes.Count;
        var stdDev = Math.Sqrt(variance);

        // If coefficient of variation > 10%, data is too inconsistent
        if (stdDev / average > 0.10)
            return 0;

        // Use weighted average giving preference to recent laps
        var projectedTime = CalculateWeightedAverage(filtered);

        // ABSOLUTE MINIMUM: No lap should ever be projected under 10 seconds (likely data corruption)
        if (projectedTime < 10000)
        {
            Logger.LogWarning(
                "Projection {projected}ms for car {car} below absolute minimum of 10s. Returning 0.",
                projectedTime, car.Number);
            return 0;
        }

        // Apply relative sanity bounds based on flag-appropriate reference time
        var referenceTime = GetReferenceLapTime(car, average, currentFlag);

        // Projection should be between 0.7x and 3.0x of reference time
        // (allows for track conditions but prevents extreme outliers)
        var minBound = (int)(referenceTime * 0.7);
        var maxBound = (int)(referenceTime * 3.0);

        if (projectedTime < minBound || projectedTime > maxBound)
        {
            Logger.LogWarning(
                "Projection {projected}ms for car {car} outside relative bounds [{min}, {max}] based on reference {ref}ms for flag {flag}. Returning 0.",
                projectedTime, car.Number, minBound, maxBound, referenceTime, currentFlag);
            return 0;
        }

        return projectedTime;
    }

    /// <summary>
    /// Gets a reference lap time for sanity checking. Uses flag-appropriate references.
    /// For yellow flags, prefers recent average. For green flags, prefers best lap time.
    /// </summary>
    private static double GetReferenceLapTime(CarPosition car, double recentAverage, Flags currentFlag)
    {
        // For yellow flags, prefer using recent average since best time is usually from green flag conditions
        // Yellow laps are typically slower, so using green flag best time could inappropriately reject valid projections
        if (currentFlag == Flags.Yellow && recentAverage > 0)
        {
            return recentAverage;
        }

        // For green flags or fallback, try to use best lap time as reference
        if (!string.IsNullOrEmpty(car.BestTime))
        {
            var bestTime = FastestPaceEnricher.ParseRMTime(car.BestTime);
            if (bestTime != TimeSpan.Zero)
            {
                return bestTime.TotalMilliseconds;
            }
        }

        // Fall back to recent average, or a conservative default
        return recentAverage > 0 ? recentAverage : 120000; // 2 minutes default
    }

    /// <summary>
    /// Filters outliers using Median Absolute Deviation (MAD), which is more robust than standard deviation.
    /// </summary>
    /// <param name="laps">List of laps to filter</param>
    /// <param name="madThreshold">Number of MAD deviations to use as threshold (default 3.0)</param>
    /// <returns>Filtered list without outliers</returns>
    private static List<CarPosition> FilterOutliers(List<CarPosition> laps, double madThreshold = 3.0)
    {
        if (laps.Count < 3)
            return laps;

        // Parse all lap times
        var lapTimes = laps
            .Select(l => FastestPaceEnricher.ParseRMTime(l.LastLapTime ?? string.Empty))
            .Where(t => t != TimeSpan.Zero)
            .Select(t => t.TotalMilliseconds)
            .ToList();

        if (lapTimes.Count < 3)
            return laps;

        // Calculate median
        var sorted = lapTimes.OrderBy(x => x).ToList();
        double median = sorted.Count % 2 == 0
            ? (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0
            : sorted[sorted.Count / 2];

        // Calculate MAD (Median Absolute Deviation)
        var deviations = lapTimes.Select(t => Math.Abs(t - median)).OrderBy(x => x).ToList();
        double mad = deviations.Count % 2 == 0
            ? (deviations[deviations.Count / 2 - 1] + deviations[deviations.Count / 2]) / 2.0
            : deviations[deviations.Count / 2];

        // Avoid division by zero
        if (mad < 1.0)
            mad = 1.0;

        // Filter based on modified Z-score (more robust than standard deviation)
        // 1.4826 is constant to make MAD consistent with standard deviation for normal distribution
        var threshold = median + (mad * madThreshold * 1.4826);
        var minThreshold = median - (mad * madThreshold * 1.4826);

        // Also add absolute filters for extreme cases
        var absoluteMaxThreshold = median * 2.0; // No lap should be >2x median

        var filtered = new List<CarPosition>();
        for (int i = 0; i < laps.Count; i++)
        {
            var lapTime = FastestPaceEnricher.ParseRMTime(laps[i].LastLapTime ?? string.Empty);
            if (lapTime != TimeSpan.Zero &&
                lapTime.TotalMilliseconds >= minThreshold &&
                lapTime.TotalMilliseconds <= threshold &&
                lapTime.TotalMilliseconds <= absoluteMaxThreshold)
            {
                filtered.Add(laps[i]);
            }
        }

        // Keep original if filtering was too aggressive
        return filtered.Count >= 2 ? filtered : laps;
    }

    /// <summary>
    /// Calculates weighted average with linear weighting favoring recent laps.
    /// Most recent lap gets highest weight, oldest lap gets weight of 1.
    /// </summary>
    /// <param name="laps">List of laps ordered by recency (most recent first)</param>
    /// <returns>Weighted average lap time in milliseconds</returns>
    private static int CalculateWeightedAverage(List<CarPosition> laps)
    {
        double totalWeightedTime = 0;
        double totalWeight = 0;

        for (int i = 0; i < laps.Count; i++)
        {
            var lapTime = FastestPaceEnricher.ParseRMTime(laps[i].LastLapTime ?? string.Empty);
            if (lapTime != TimeSpan.Zero)
            {
                // Linear weighting: most recent lap gets weight = count, oldest gets weight = 1
                double weight = laps.Count - i;
                totalWeightedTime += lapTime.TotalMilliseconds * weight;
                totalWeight += weight;
            }
        }

        return totalWeight > 0 ? (int)(totalWeightedTime / totalWeight) : 0;
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
