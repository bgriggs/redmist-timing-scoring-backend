using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.Database.Models;
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
    private readonly Dictionary<int, Dictionary<string, int>> eventCarLastLapLookup = [];
    private readonly SemaphoreSlim eventCarLastLapLookupLock = new(1, 1);


    public LapLogger(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
    }


    public async Task LogCarPositionUpdates(int eventId, int sessionId, List<CarPosition> carPositions, CancellationToken cancellationToken)
    {
        // Get the last laps for all cars in the event
        Dictionary<string, int>? carLastLapLookup;
        await eventCarLastLapLookupLock.WaitAsync(cancellationToken);
        try
        {
            if (!eventCarLastLapLookup.TryGetValue(eventId, out carLastLapLookup))
            {
                carLastLapLookup = [];
                eventCarLastLapLookup[eventId] = carLastLapLookup;
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

                if (position.LastLap > lastLap)
                {
                    Logger.LogDebug("Car {0} completed new lap {1} in event {2}. Logging...", position.Number, position.LastLap, eventId);

                    // New lap completed
                    carLastLapLookup[position.Number] = position.LastLap;
                    var log = new CarLapLog
                    {
                        EventId = eventId,
                        Timestamp = DateTime.UtcNow,
                        CarNumber = position.Number,
                        LapNumber = position.LastLap,
                        Flag = (int)position.Flag,
                        LapData = JsonSerializer.Serialize(position),
                    };
                    lapLogs.Add((log, position.LastLap));
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
                var lastLapRef = await context.CarLastLaps.FirstOrDefaultAsync(x => x.EventId == eventId && x.CarNumber == log.l.CarNumber, cancellationToken: cancellationToken);
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
                Logger.LogError(ex, "Error logging new lap for car {0} in event {1}", log.lastLapNum, eventId);
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
        Logger.LogDebug("Loading last laps for event {0}...", eventId);
        using var context = tsContext.CreateDbContext();
        var lastLaps = await context.CarLastLaps.Where(x => x.EventId == eventId).ToListAsync(cancellationToken);
        foreach (var lastLap in lastLaps)
        {
            carLastLapLookup[lastLap.CarNumber] = lastLap.LastLapNumber;
        }
    }
}
