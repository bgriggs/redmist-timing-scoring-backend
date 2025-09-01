using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.TimingAndScoringService.EventStatus.X2;
using RedMist.TimingCommon.Models;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// Responsible for tracking the last lap for each car in an event and logging new laps.
/// </summary>
public class LapLogger
{
    private readonly IDbContextFactory<TsContext> tsContext;

    private ILogger Logger { get; }
    private readonly Dictionary<(int evt, int sess), Dictionary<string, int>> eventCarLastLapLookup = [];
    private readonly SemaphoreSlim eventCarLastLapLookupLock = new(1, 1);
    private readonly Dictionary<string, CarPosition> lastCarPositionLookup = [];

    public LapLogger(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
    }


    public async Task LogCarPositionUpdates(int eventId, int sessionId, List<CarPosition> carPositions, PitProcessor? pitProcessor, CancellationToken cancellationToken)
    {
        if (pitProcessor != null)
        {
            // Wait for 1 seconds to allow for transponder passing updates to come in for a pit stop at start / finish
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        // Get the last laps for all cars in the event
        Dictionary<string, int>? carLastLapLookup;
        await eventCarLastLapLookupLock.WaitAsync(cancellationToken);
        try
        {
            if (!eventCarLastLapLookup.TryGetValue((eventId, sessionId), out carLastLapLookup))
            {
                carLastLapLookup = [];
                eventCarLastLapLookup[(eventId, sessionId)] = carLastLapLookup;
                await InitializeEventLastLaps(eventId, sessionId, carLastLapLookup, cancellationToken);
            }
        }
        finally
        {
            eventCarLastLapLookupLock.Release();
        }

        var lapLogs = new List<(CarLapLog l, int lastLapNum)>();
        lock (carLastLapLookup)
        {
            foreach (var position in carPositions)
            {
                if (string.IsNullOrEmpty(position.Number))
                    continue;

                if (!carLastLapLookup.TryGetValue(position.Number, out int lastLap))
                {
                    lastLap = 0;
                }

                // Check if the car has completed a new lap or include lap 0 so the starting grid can be restored
                // on service restarts and qualifying or practice can be captured
                if (position.LastLapCompleted > lastLap || position.LastLapCompleted == 0)
                {
                    if (position.LastLapCompleted == 0 && !IsLapNewerThanLastEntryWithReplace(position))
                        return;

                    Logger.LogTrace("Car {n} completed new lap {l} in event {e}. Logging...", position.Number, position.LastLapCompleted, eventId);

                    // Update pit stops
                    pitProcessor?.UpdateCarPositionForLogging(position);
                    //if (position.LapIncludedPit)
                    //{
                    //    Logger.LogTrace("Logging Car {0} is in pit", position.Number);
                    //}

                    // New lap completed
                    carLastLapLookup[position.Number] = position.LastLapCompleted;

                    var log = new CarLapLog
                    {
                        EventId = eventId,
                        SessionId = sessionId,
                        Timestamp = DateTime.UtcNow,
                        CarNumber = position.Number,
                        LapNumber = position.LastLapCompleted,
                        Flag = (int)position.TrackFlag,
                        LapData = JsonSerializer.Serialize(position),
                    };
                    lapLogs.Add((log, position.LastLapCompleted));
                }
            }
        }

        using var context = tsContext.CreateDbContext();
        foreach (var log in lapLogs)
        {
            try
            {
                context.CarLapLogs.Add(log.l);

                // Save the last lap reference
                var lastLapRef = await context.CarLastLaps.FirstOrDefaultAsync(x => x.EventId == eventId && x.SessionId == sessionId && x.CarNumber == log.l.CarNumber, cancellationToken: cancellationToken);
                if (lastLapRef == null)
                {
                    lastLapRef = new CarLastLap { EventId = eventId, SessionId = sessionId, CarNumber = log.l.CarNumber, LastLapNumber = log.lastLapNum, LastLapTimestamp = DateTime.UtcNow };
                    context.CarLastLaps.Add(lastLapRef);
                }
                else
                {
                    lastLapRef.LastLapNumber = log.lastLapNum;
                    lastLapRef.LastLapTimestamp = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error logging new lap for car {c} in event {e}", log.lastLapNum, eventId);
            }
        }

        // Save the changes
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Load the last laps for all cars for the event from the database.
    /// This allows the service to recover when it is restarted and the in-memory cache is lost.
    /// </summary>
    private async Task InitializeEventLastLaps(int eventId, int sessionId, Dictionary<string, int> carLastLapLookup, CancellationToken cancellationToken)
    {
        Logger.LogDebug("Loading last laps for event {evt}...", eventId);
        using var context = tsContext.CreateDbContext();
        var lastLaps = await context.CarLastLaps.Where(x => x.EventId == eventId && x.SessionId == sessionId).ToListAsync(cancellationToken);
        foreach (var lastLap in lastLaps)
        {
            carLastLapLookup[lastLap.CarNumber] = lastLap.LastLapNumber;
        }
    }

    /// <summary>
    /// Determine if the lap is newer or has changed from the last entry in the database for the car.
    /// Typically, the last lap will not have changed in this case.
    /// </summary>
    /// <param name="carPosition"></param>
    /// <returns></returns>
    private bool IsLapNewerThanLastEntryWithReplace(CarPosition carPosition)
    {
        if (string.IsNullOrEmpty(carPosition.Number)) 
            return false;

        if (lastCarPositionLookup.TryGetValue(carPosition.Number, out var old))
        {
            if (carPosition.LastLapCompleted > old.LastLapCompleted)
            {
                lastCarPositionLookup[carPosition.Number] = carPosition;
                return true;
            }
            else if (carPosition.LastLapCompleted == old.LastLapCompleted && 
                (carPosition.OverallPosition != old.OverallPosition ||
                 carPosition.LastLapTime != old.LastLapTime))
            {
                lastCarPositionLookup[carPosition.Number] = carPosition;
                return true;
            }
        }
        else
        {
            lastCarPositionLookup[carPosition.Number] = carPosition;
            return true;
        }

        return false;
    }
}
