using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.EventProcessor.Models;
using RedMist.TimingCommon.Models;
using System.Globalization;
using System.Text.Json;

namespace RedMist.EventProcessor.EventStatus.LapData;

/// <summary>
/// Responsible for calculating a moving average lap time (5) laps and determining the fastest paceed car in each class.
/// </summary>
public class FastestPaceEnricher
{
    const int LapsForPace = 5;
    private readonly ICarLapHistoryService carLapHistoryService;
    private readonly SessionContext sessionContext;

    private ILogger Logger { get; }

    public FastestPaceEnricher(ILoggerFactory loggerFactory, ICarLapHistoryService carLapHistoryService, SessionContext sessionContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.carLapHistoryService = carLapHistoryService;
        this.sessionContext = sessionContext;
    }

    public async Task<List<CarPositionPatch>> ProcessAsync(TimingMessage tm)
    {
        var patches = new List<CarPositionPatch>();
        if (tm.Type != Consts.LAP_COMPLETED_TYPE)
            return patches;

        var lapCompleted = JsonSerializer.Deserialize<LapCompleted>(tm.Data);
        if (lapCompleted != null)
        {
            // Get all cars in the class of the completed lap
            var carsInClass = sessionContext.GetClassCarPositions(lapCompleted.Class);

            string fastestCarNumber = string.Empty;
            int fastestTimeMs = int.MaxValue;
            foreach (var car in carsInClass)
            {
                if (string.IsNullOrEmpty(car.Number))
                    continue;

                // Load lap history for the car and calculate the average lap time
                var laps = await carLapHistoryService.GetLapsAsync(car.Number);
                if (laps != null)
                {
                    var avgLapTime = CalculateAverageLapTime(laps);
                    if (avgLapTime != null)
                    {
                        if (avgLapTime < fastestTimeMs)
                        {
                            fastestTimeMs = avgLapTime.Value;
                            fastestCarNumber = car.Number;
                        }
                    }
                }
            }

            // Update all cars in the class with whether they are the fastest or not
            foreach (var car in carsInClass)
            {
                var patch = UpdateCar(car, fastestCarNumber);
                if (patch != null)
                    patches.Add(patch);
            }
        }

        return patches;
    }

    public static int? CalculateAverageLapTime(List<CarPosition> laps)
    {
        if (laps.Count < LapsForPace)
            return null;

        // Take the last 5 laps (or fewer if not available)
        var recentLaps = laps.Take(LapsForPace).ToList();

        // Calculate the average lap time
        int totalMs = 0;
        int usableTimes = 0;
        foreach (var car in recentLaps)
        {
            var carLapTime = ParseRMTime(car.LastLapTime ?? string.Empty);
            if (carLapTime != TimeSpan.Zero)
            {
                totalMs += (int)carLapTime.TotalMilliseconds;
                usableTimes++;
            }
        }

        int avg = usableTimes > 0 ? totalMs / usableTimes : 0;
        return avg;
    }

    public static TimeSpan ParseRMTime(string time)
    {
        if (TimeSpan.TryParseExact(time, @"hh\:mm\:ss\.fff", null, TimeSpanStyles.None, out var result))
            return result;
        if (TimeSpan.TryParseExact(time, @"hh\:mm\:ss", null, TimeSpanStyles.None, out result))
            return result;
        return TimeSpan.Zero;
    }

    private static CarPositionPatch? UpdateCar(CarPosition car, string fastestCarNumber)
    {
        var patch = new CarPositionPatch { Number = car.Number };
        bool shouldBeFastest = car.Number == fastestCarNumber;

        if (car.InClassFastestAveragePace != shouldBeFastest)
        {
            patch.InClassFastestAveragePace = shouldBeFastest;
            car.InClassFastestAveragePace = shouldBeFastest;
            return patch;
        }

        return null;
    }
}
