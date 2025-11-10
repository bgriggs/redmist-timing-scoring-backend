using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventProcessor.EventStatus.X2;
using RedMist.TimingCommon.Extensions;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.EventProcessor.EventStatus.LapData;

/// <summary>
/// Responsible for tracking the last lap for each car and logging completed laps.
/// </summary>
public class LapProcessor : IDisposable
{
    private ILogger Logger { get; }
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly SessionContext sessionContext;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly PitProcessorV2 pitProcessor;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, CarPosition> lastCarPositionLookup = [];
    private readonly Dictionary<(int evt, int sess), Dictionary<string, int>> eventCarLastLapLookup = [];

    // Add a buffer to collect lap completions and wait for potential pit messages
    private readonly Dictionary<string, Queue<(CarPosition position, DateTimeOffset timestamp)>> pendingLapCompletions = [];
    private readonly TimeSpan pitMessageWaitTime = TimeSpan.FromMilliseconds(1000);
    private readonly CancellationTokenSource backgroundTaskCts = new();


    public LapProcessor(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
        SessionContext sessionContext, IConnectionMultiplexer cacheMux, PitProcessorV2 pitProcessor, TimeProvider? timeProvider = null)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.sessionContext = sessionContext;
        this.cacheMux = cacheMux;
        this.pitProcessor = pitProcessor;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Start background task to process pending lap completions
        _ = Task.Run(ProcessPendingLapCompletionsAsync, backgroundTaskCts.Token);
    }


    public async Task ProcessAsync(List<CarPosition> carPositions)
    {
        var eventId = sessionContext.EventId;
        var sessionId = sessionContext.SessionState.SessionId;

        // Get the last laps for all cars in the event
        if (!eventCarLastLapLookup.TryGetValue((eventId, sessionId), out var carLastLapLookup))
        {
            carLastLapLookup = [];
            eventCarLastLapLookup[(eventId, sessionId)] = carLastLapLookup;
            await InitializeEventLastLapsAsync(eventId, sessionId, carLastLapLookup, sessionContext.CancellationToken);
        }

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
                    continue;

                Logger.LogTrace("Car {n} completed new lap {l} in event {e}. Buffering for pit message check...", position.Number, position.LastLapCompleted, eventId);

                // Protect both queue operations AND lap tracking updates with the same lock
                // to prevent race conditions
                lock (pendingLapCompletions)
                {
                    // Re-check inside lock to prevent duplicate enqueues from concurrent threads
                    if (!carLastLapLookup.TryGetValue(position.Number, out int lockedLastLap))
                    {
                        lockedLastLap = 0;
                    }

                    // Only enqueue if lap is still newer after acquiring lock
                    bool shouldEnqueue = position.LastLapCompleted > lockedLastLap ||
                             (position.LastLapCompleted == 0 && lockedLastLap > 0);

                    if (shouldEnqueue)
                    {
                        if (!pendingLapCompletions.TryGetValue(position.Number, out var queue))
                        {
                            queue = new Queue<(CarPosition position, DateTimeOffset timestamp)>();
                            pendingLapCompletions[position.Number] = queue;
                        }
                        
                        var copy = position.DeepCopy();
                        queue.Enqueue((copy, _timeProvider.GetUtcNow()));

                        // Update the last lap tracking immediately while still holding the lock
                        // Only update if this lap is greater than the current last lap
                        // Don't let lap 0 (starting grid) reset the counter backwards
                        if (position.LastLapCompleted > lockedLastLap)
                        {
                            carLastLapLookup[position.Number] = position.LastLapCompleted;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Background task that processes pending lap completions after waiting for potential pit messages
    /// </summary>
    private async Task ProcessPendingLapCompletionsAsync()
    {
        while (!backgroundTaskCts.Token.IsCancellationRequested && !sessionContext.CancellationToken.IsCancellationRequested)
        {
            try
            {
                var completionsToProcess = new List<(string carNumber, CarPosition position)>();

                lock (pendingLapCompletions)
                {
                    var cutoffTime = _timeProvider.GetUtcNow() - pitMessageWaitTime;

                    // Process each car's queue
                    foreach (var (carNumber, queue) in pendingLapCompletions.ToList())
                    {
                        // Dequeue all expired laps
                        while (queue.Count > 0 && queue.Peek().timestamp <= cutoffTime)
                        {
                            var (position, _) = queue.Dequeue();
                            completionsToProcess.Add((carNumber, position));
                        }

                        // Remove empty queues to keep dictionary clean
                        if (queue.Count == 0)
                        {
                            pendingLapCompletions.Remove(carNumber);
                        }
                    }
                }

                if (completionsToProcess.Count != 0)
                {
                    await LogCompletedLapsAsync(completionsToProcess);
                }

                // Check every 100ms for expired completions
                await Task.Delay(100, CancellationTokenSource.CreateLinkedTokenSource(
                    backgroundTaskCts.Token, sessionContext.CancellationToken).Token);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error processing pending lap completions");
                try
                {
                    await Task.Delay(1000, CancellationTokenSource.CreateLinkedTokenSource(
                 backgroundTaskCts.Token, sessionContext.CancellationToken).Token); // Throttle on error
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Method to force immediate processing of pending laps for a specific car
    /// This can be called when a pit message is received to immediately process any pending lap for that car
    /// </summary>
    public async Task ProcessPendingLapForCarAsync(string carNumber)
    {
        var positionsToProcess = new List<CarPosition>();

        lock (pendingLapCompletions)
        {
            if (pendingLapCompletions.TryGetValue(carNumber, out var queue))
            {
                // Process all pending laps for this car
                while (queue.Count > 0)
                {
                    var (position, _) = queue.Dequeue();
                    positionsToProcess.Add(position);
                }
                pendingLapCompletions.Remove(carNumber);
            }
        }

        if (positionsToProcess.Count > 0)
        {
            Logger.LogTrace("Processing {count} pending lap(s) for car {n} immediately due to pit message", positionsToProcess.Count, carNumber);
            await LogCompletedLapsAsync([.. positionsToProcess.Select(p => (carNumber, p))]);
        }
    }

    /// <summary>
    /// Logs the completed laps to Redis
    /// </summary>
    private async Task LogCompletedLapsAsync(List<(string carNumber, CarPosition position)> completions)
    {
        if (completions.Count == 0) return;

        var eventId = sessionContext.EventId;
        var sessionId = sessionContext.SessionState.SessionId;
        var lapLogs = new List<CarLapData>();

        foreach (var (carNumber, position) in completions)
        {
            Logger.LogTrace("Car {n} completed lap {l} in event {e}. Logging...", carNumber, position.LastLapCompleted, eventId);
            
            // Update pit stops - this will set LapIncludedPit if the lap included a pit stop
            pitProcessor?.UpdateCarPositionForLogging(position);

            var log = new CarLapLog
            {
                EventId = eventId,
                SessionId = sessionId,
                Timestamp = _timeProvider.GetUtcNow().UtcDateTime,
                CarNumber = carNumber,
                LapNumber = position.LastLapCompleted,
                Flag = (int)position.TrackFlag,
                LapData = JsonSerializer.Serialize(position),
            };
            lapLogs.Add(new CarLapData(log, position.LastLapCompleted, sessionId));
        }

        // Post the lap logs to Redis for logger service to consume
        var streamId = string.Format(Consts.EVENT_PROCESSOR_LOGGING_STREAM_KEY, eventId);
        var cache = cacheMux.GetDatabase();
        var json = JsonSerializer.Serialize(lapLogs);
        await cache.StreamAddAsync(streamId, "laps", json);
    }

    /// <summary>
    /// Load the last laps for all cars for the event from the database.
    /// This allows the service to recover when it is restarted and the in-memory cache is lost.
    /// </summary>
    private async Task InitializeEventLastLapsAsync(int eventId, int sessionId, Dictionary<string, int> carLastLapLookup, CancellationToken cancellationToken)
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

    /// <summary>
    /// Cleanup resources when the processor is disposed
    /// </summary>
    public void Dispose()
    {
        backgroundTaskCts.Cancel();
        backgroundTaskCts.Dispose();
        GC.SuppressFinalize(this);
    }
}
