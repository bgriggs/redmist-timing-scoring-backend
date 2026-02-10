using RedMist.EventProcessor.EventStatus.PositionEnricher;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus.LapData;

/// <summary>
/// Determines when a car is "stale" - meaning they have not completed a lap in a 
/// reasonable amount of time compared to their last lap and the current track conditions.
/// </summary>
public class StaleCarEnricher
{
    private readonly SessionContext sessionContext;

    private ILogger Logger { get; }


    public StaleCarEnricher(ILoggerFactory loggerFactory, SessionContext sessionContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.sessionContext = sessionContext;
    }


    public async Task<List<CarPositionPatch>> ProcessAsync()
    {
        var patches = new List<CarPositionPatch>();
        var cars = sessionContext.SessionState.CarPositions;
        if (cars.Count == 0)
            return patches;

        var currentLapNumber = cars.Max(c => c.LastLapCompleted);

        // Let the race settle for a couple laps before marking cars as stale
        if (currentLapNumber < 3)
            return patches;

        var currentFlag = sessionContext.SessionState.CurrentFlag;
        var raceTime = PositionMetadataProcessor.ParseRMTime(sessionContext.SessionState.RunningRaceTime);

        foreach (var car in cars)
        {
            if (string.IsNullOrEmpty(car.Number))
                continue;

            // No lap history and never completed a lap, mark as stale
            if (car.LastLapCompleted == 0)
            {
                var patch = UpdateCar(car, true);
                if (patch != null)
                    patches.Add(patch);
            }
            else
            {
                var isStale = CheckForStale(car, currentFlag, raceTime);
                var patch = UpdateCar(car, isStale);
                if (patch != null)
                    patches.Add(patch);
            }
        }

        return patches;
    }

    public bool CheckForStale(CarPosition car, Flags trackFlag, DateTime raceTime)
    {
        if (trackFlag != Flags.Green && trackFlag != Flags.Yellow && trackFlag != Flags.White)
            return false; // Only consider green and yellow laps for staleness

        double percentOver = 0.3; // 30% slower than last lap is considered stale

        // When going from green to yellow, be more lenient since cars will naturally slow down
        if (trackFlag == Flags.Yellow && car.TrackFlag == Flags.Green)
            percentOver = 1.1; // 110% over = 2.1x the lap time
        // Check for change from yellow to green - be more strict
        else if (trackFlag == Flags.Green && car.TrackFlag == Flags.Yellow)
            percentOver = 0.05; // Only 5% over when transitioning from yellow to green
        
        if (car.TotalTime == null)
            return false;

        // Determine how much race time has passed since the car's last lap
        var lastTime = PositionMetadataProcessor.ParseRMTime(car.TotalTime);
        var diff = raceTime - lastTime;
        if (car.LastLapTime != null && diff > TimeSpan.FromSeconds(1))
        {
            var lastLapTime = FastestPaceEnricher.ParseRMTime(car.LastLapTime);
            // If the car's last lap time since their last lap exceeds the threshold, mark as stale
            return lastLapTime.TotalSeconds > 0 && diff.TotalSeconds > lastLapTime.TotalSeconds * (1 + percentOver);
        }
        return false;
    }

    private static CarPositionPatch? UpdateCar(CarPosition car, bool isStale)
    {
        var patch = new CarPositionPatch { Number = car.Number };
        if (car.IsStale != isStale)
        {
            patch.IsStale = isStale;
            car.IsStale = isStale;
            return patch;
        }

        return null;
    }
}
