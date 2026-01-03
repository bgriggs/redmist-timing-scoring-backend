using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingCommon.Models;
using System.Text.Json;

namespace RedMist.EventProcessor.EventStatus.RMonitor;

/// <summary>
/// Tracks starting positions based on order of cars as they cross S/F during the starting green lap.
/// </summary>
public class StartingPositionProcessor : BackgroundService
{
    private ILogger Logger { get; }
    private readonly SessionContext sessionContext;
    private readonly IDbContextFactory<TsContext> tsContext;
    private int lastCompletedSessionHistoricalCheck = -1;


    public StartingPositionProcessor(SessionContext sessionContext, ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext)
    {
        this.sessionContext = sessionContext;
        this.tsContext = tsContext;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    /// <summary>
    /// Service check to see if starting positions need to be determined after a service restart.
    /// This will check for event conditions where the starting positions are not present and the 
    /// event session is running. It will then use the saved laps to determine starting positions.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("StartingPositionProcessor starting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                await CheckHistoricLapStartingPositionsAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "An error occurred in checking starting positions.");
            }
        }
    }

    internal async Task<bool> CheckHistoricLapStartingPositionsAsync()
    {
        var currentSession = sessionContext.SessionState.SessionId;
        if (lastCompletedSessionHistoricalCheck == currentSession)
            return false;

        // See if starting positions have already been determined
        var hasStartingPositions = await sessionContext.HasStartingPositions();
        if (hasStartingPositions)
        {
            lastCompletedSessionHistoricalCheck = currentSession;
            return false;
        }

        // See if the event is active
        var (flag, lap) = await sessionContext.GetCurrentFlagAndLap();
        if (lap > 3 && (flag == Flags.Green || flag == Flags.Yellow || flag == Flags.Red || flag == Flags.Purple35))
        {
            Logger.LogInformation("Starting laps for session {sid} have not been determined. Performing historical check...", currentSession);
            var result = await UpdateStartingPositionsFromHistoricLapsAsync(currentSession);
            lastCompletedSessionHistoricalCheck = currentSession;
            if (result)
            {
                Logger.LogInformation("Starting positions for session {sid} have been determined from historical laps.", currentSession);
            }
            else
            {
                Logger.LogWarning("Could not determine starting positions for session {sid} from historical laps.", currentSession);
            }
        }

        return true;
    }

    public virtual void UpdateStartingPosition(string[] parts, string regNum, Flags flag)
    {
        // Allow capture of starting positions during lap 0 up to and including the green flag
        if (flag == Flags.Unknown || flag == Flags.Yellow || flag == Flags.Green)
        {
            // Make a copy for storing off
            var sp = new RaceInformation();
            sp.ProcessG(parts);
            sessionContext.SetStartingPosition(regNum, sp.Position);
            UpdateInClassStartingPositionLookup();
        }
    }

    internal void UpdateInClassStartingPositionLookup()
    {
        var entries = new List<(string num, string @class, int pos)>();
        var startingPositions = sessionContext.GetStartingPositions();
        foreach (var regNum in startingPositions.Keys)
        {
            var ri = startingPositions[regNum];
            var car = sessionContext.GetCarByNumber(regNum);
            if (car == null || car.Class == null)
            {
                Logger.LogWarning("Car {rn} not found for starting position", regNum);
                continue;
            }
            entries.Add((regNum, car.Class, car.ClassPosition));
        }

        var classGroups = entries.GroupBy(x => x.@class);
        foreach (var classGroup in classGroups)
        {
            var positions = classGroup.OrderBy(x => x.pos).ToList();
            for (int i = 0; i < positions.Count; i++)
            {
                var entry = positions[i];
                sessionContext.SetInClassStartingPosition(entry.num, i + 1);
            }
        }
    }

    internal async Task<bool> UpdateStartingPositionsFromHistoricLapsAsync(int sessionId)
    {
        var laps = await LoadStartingLapsAsync(sessionId);
        var lapNumberPriorToGreen = GetLapNumberPriorToGreen(laps);
        if (lapNumberPriorToGreen < 0)
        {
            Logger.LogWarning("Cannot determine lap number prior to green flag for session {sid}", sessionId);
            return false;
        }
        var startingLaps = laps.Where(c => c.LastLapCompleted == lapNumberPriorToGreen).OrderBy(c => c.OverallPosition).ToList();
        using (await sessionContext.SessionStateLock.AcquireWriteLockAsync(sessionContext.CancellationToken))
        {
            foreach (var carPos in startingLaps)
            {
                if (string.IsNullOrEmpty(carPos.Number)) continue;
                sessionContext.SetStartingPosition(carPos.Number, carPos.OverallPosition);
            }
            UpdateInClassStartingPositionLookup();
        }

        return true;
    }

    internal async Task<List<CarPosition>> LoadStartingLapsAsync(int sessionId)
    {
        using var db = await tsContext.CreateDbContextAsync();
        var laps = await db.CarLapLogs
            .AsNoTracking()
            .Where(cl => cl.SessionId == sessionId && cl.LapNumber >= 0 && cl.LapNumber <= 4)
            .Select(cl => JsonSerializer.Deserialize<CarPosition>(cl.LapData)!)
            .ToListAsync();
        return laps;
    }

    /// <summary>
    /// Determines the lap number just prior to the green flag lap.
    /// </summary>
    /// <param name="laps">starting number of car laps</param>
    /// <returns>lap number prior to green or -1 if cannot be determined or is invalid</returns>
    internal static int GetLapNumberPriorToGreen(List<CarPosition> laps)
    {
        var leader = laps.Where(c => c.OverallPosition == 1).OrderBy(c => c.LastLapCompleted).ToArray();
        // Get the lap number just before the green flag lap, this will be the starting lineup
        var greenLap = leader.FirstOrDefault(c => c.TrackFlag == Flags.Green);
        if (greenLap == null || greenLap.LastLapCompleted == 0)
            return -1;
        return greenLap.LastLapCompleted - 1;
    }
}
