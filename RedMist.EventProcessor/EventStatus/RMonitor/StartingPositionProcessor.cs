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

    /// <summary>
    /// How many times a session's grid is reconstructed from historic laps before giving up. The
    /// failures worth retrying are temporary - laps not queryable yet, session id not settled - and
    /// clear within a pass or two. The rest never resolve: a session whose green flag falls beyond the
    /// laps that get loaded cannot be reconstructed however often it is tried, and retrying it every
    /// interval would re-read and deserialize every car's early laps for the rest of the session.
    /// </summary>
    private const int MaxHistoricalCheckAttempts = 10;
    private int historicalCheckAttemptSession = -1;
    private int historicalCheckAttempts;


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
        var positionInfo = await sessionContext.HasStartingPositions();
        if (positionInfo.hasPositions && positionInfo.startingCount == positionInfo.totalCars)
        {
            lastCompletedSessionHistoricalCheck = currentSession;
            return false;
        }

        // See if the event is active
        var (flag, lap) = await sessionContext.GetCurrentFlagAndLapAsync();
        if (lap > 3 && (flag == Flags.Green || flag == Flags.Yellow || flag == Flags.Red || flag == Flags.Purple35))
        {
            Logger.LogInformation("Starting laps for session {sid} have not been determined. Performing historical check...", currentSession);
            var result = await UpdateStartingPositionsFromHistoricLapsAsync(currentSession);
            if (result)
            {
                // Only a successful reconstruction retires the check outright. Marking it done
                // regardless meant one failure disabled recovery for the rest of the session.
                lastCompletedSessionHistoricalCheck = currentSession;
                Logger.LogInformation("Starting positions for session {sid} have been determined from historical laps.", currentSession);
            }
            else if (RecordFailedHistoricalCheck(currentSession) >= MaxHistoricalCheckAttempts)
            {
                lastCompletedSessionHistoricalCheck = currentSession;
                Logger.LogWarning("Could not determine starting positions for session {sid} from historical laps after {n} attempts. Giving up.",
                    currentSession, MaxHistoricalCheckAttempts);
            }
            else
            {
                Logger.LogWarning("Could not determine starting positions for session {sid} from historical laps (attempt {n}). Will retry.",
                    currentSession, historicalCheckAttempts);
            }
        }

        return true;
    }

    /// <summary>
    /// Counts a failed reconstruction against the current session, restarting the count when the
    /// session changes so each session gets its own allowance.
    /// </summary>
    /// <returns>Attempts made for this session, including this one.</returns>
    private int RecordFailedHistoricalCheck(int sessionId)
    {
        if (historicalCheckAttemptSession != sessionId)
        {
            historicalCheckAttemptSession = sessionId;
            historicalCheckAttempts = 0;
        }
        return ++historicalCheckAttempts;
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

    /// <summary>
    /// Ranks each class by its cars' overall grid positions to give their in-class starting positions.
    ///
    /// Ranking is by grid position rather than by the car's current class position on purpose. The two
    /// agree while the field is still on the grid, which is the only time this used to run, but they
    /// diverge the moment the race does - so rebuilding mid-race (after a restart, or from historic
    /// laps) off the current order would hand every car an in-class starting position equal to where
    /// it is now, and the whole field would show zero positions gained.
    /// </summary>
    internal void UpdateInClassStartingPositionLookup()
    {
        var entries = new List<(string num, string @class, int pos)>();
        var startingPositions = sessionContext.GetStartingPositions();
        foreach (var regNum in startingPositions.Keys)
        {
            var startingPosition = startingPositions[regNum];
            var car = sessionContext.GetCarByNumber(regNum);
            if (car == null || car.Class == null)
            {
                //Logger.LogWarning("Car {rn} not found for starting position", regNum);
                continue;
            }
            entries.Add((regNum, car.Class, startingPosition));
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
    /// Determines the lap number that represents the starting grid order.
    /// If the green flag is on lap 0, lap 0 itself is the starting grid.
    /// Otherwise, it returns the lap just prior to the green flag lap.
    /// </summary>
    /// <param name="laps">starting number of car laps</param>
    /// <returns>lap number for starting order or -1 if cannot be determined</returns>
    internal static int GetLapNumberPriorToGreen(List<CarPosition> laps)
    {
        var leader = laps.Where(c => c.OverallPosition == 1).OrderBy(c => c.LastLapCompleted).ToArray();
        // Get the lap number just before the green flag lap, this will be the starting lineup
        var greenLap = leader.FirstOrDefault(c => c.TrackFlag == Flags.Green);
        if (greenLap == null)
            return -1;
        if (greenLap.LastLapCompleted == 0)
            return 0;
        return greenLap.LastLapCompleted - 1;
    }
}
