using RedMist.Backend.Shared.Utilities;
using RedMist.TimingCommon.Extensions;
using RedMist.TimingCommon.Models;
using System.Collections.Immutable;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// Holds context information shared across the processing pipeline.
/// </summary>
public class SessionContext
{
    public SessionState SessionState { get; private set; } = new SessionState();
    private readonly AsyncReaderWriterLock sessionStateLock = new();
    public AsyncReaderWriterLock SessionStateLock => sessionStateLock;

    /// <summary>
    /// Session state before the last reset. This can be used to save the session's results
    /// when a new session starts since the reset command happens before the $B run command 
    /// and will clear the current session state.
    /// </summary>
    public SessionState PreviousSessionState { get; private set; } = new SessionState();
    private DateTime lastPreviousSessionStateUpdate = DateTime.MinValue;
    private readonly TimeProvider _timeProvider;

    public int EventId { get; }

    public virtual CancellationToken CancellationToken { get; set; } = CancellationToken.None;

    public virtual bool IsMultiloopActive { get; set; }

    private readonly Dictionary<string, CarPosition> numberToCarPositionLookup = [];
    private readonly Dictionary<uint, string> transponderToNumberLookup = [];

    // Car starting positions by car number
    private readonly Dictionary<string, int> startingPositions = [];
    private readonly Dictionary<string, int> inClassStartingPositions = [];

    // Last lap time before reset for each car number
    private readonly Dictionary<string, string> lastLapTimesBeforeReset = [];


    public SessionContext(IConfiguration configuration, TimeProvider? timeProvider = null)
    {
        EventId = configuration.GetValue("event_id", 0);
        SessionState.EventId = EventId;
        _timeProvider = timeProvider ?? TimeProvider.System; // Use system time by default
    }


    /// <summary>
    /// This will add or update the car positions in the session state.
    /// </summary>
    /// <param name="carPositions"></param>
    /// <returns></returns>
    public virtual void UpdateCars(IEnumerable<CarPosition> carPositions)
    {
        foreach (var incomingCarPosition in carPositions)
        {
            if (string.IsNullOrEmpty(incomingCarPosition.Number))
                continue;

            // Check if car already exists
            if (numberToCarPositionLookup.TryGetValue(incomingCarPosition.Number, out var existingCarPosition))
            {
                // Update existing car position in the list
                var index = SessionState.CarPositions.IndexOf(existingCarPosition);
                if (index >= 0)
                {
                    var c = SessionState.CarPositions[index];
                    c.TransponderId = incomingCarPosition.TransponderId;
                    c.DriverName = incomingCarPosition.DriverName;
                    c.Class = incomingCarPosition.Class;
                }

                // Remove old transponder mapping if it changed
                if (existingCarPosition.TransponderId != 0 && existingCarPosition.TransponderId != incomingCarPosition.TransponderId)
                {
                    transponderToNumberLookup.Remove(existingCarPosition.TransponderId);
                }
            }
            else
            {
                // Add new car position
                SessionState.CarPositions.Add(incomingCarPosition);
                numberToCarPositionLookup[incomingCarPosition.Number] = incomingCarPosition;
            }

            // Update transponder lookup if transponder ID is valid
            if (incomingCarPosition.TransponderId != 0)
            {
                transponderToNumberLookup[incomingCarPosition.TransponderId] = incomingCarPosition.Number!;
            }
        }
    }

    public virtual CarPosition? GetCarByNumber(string carNumber)
    {
        if (numberToCarPositionLookup.TryGetValue(carNumber, out var carPosition))
        {
            return carPosition;
        }
        return null;
    }

    public virtual string? GetCarNumberForTransponder(uint transponderId)
    {
        if (transponderToNumberLookup.TryGetValue(transponderId, out var carNumber))
        {
            return carNumber;
        }
        return null;
    }

    public virtual void ResetCommand()
    {
        // Prevent multiple resets from overwriting the previous session state, which there are 
        // typically 2-3 $I commands at the same time when a new session starts.
        var currentTime = _timeProvider.GetUtcNow().DateTime;
        if ((currentTime - lastPreviousSessionStateUpdate).TotalSeconds > 5)
        {
            // Save a copy of the current session state before clearing it if there is a session change
            var p = TimingCommon.Models.Mappers.SessionStateMapper.ToPatch(SessionState);
            var copy = TimingCommon.Models.Mappers.SessionStateMapper.PatchToEntity(p);
            copy.CarPositions = [.. copy.CarPositions.DeepCopy()];
            copy.EventEntries = [.. SessionState.EventEntries];
            copy.Announcements = [.. SessionState.Announcements];
            copy.Sections = [.. SessionState.Sections];
            copy.ClassColors = SessionState.ClassColors.ToDictionary();
            copy.FlagDurations = [.. SessionState.FlagDurations];
            PreviousSessionState = copy;

            // Update last lap times before reset
            lastLapTimesBeforeReset.Clear();
            foreach (var car in SessionState.CarPositions)
            {
                if (!string.IsNullOrEmpty(car.Number) && !string.IsNullOrEmpty(car.LastLapTime))
                {
                    lastLapTimesBeforeReset[car.Number] = car.LastLapTime;
                }
            }

            lastPreviousSessionStateUpdate = currentTime;
        }

        numberToCarPositionLookup.Clear();
        transponderToNumberLookup.Clear();
        SessionState.EventEntries.Clear();
        SessionState.CarPositions.Clear();
    }

    public virtual async Task NewSession(int sessionId, string sessionName)
    {
        using (await SessionStateLock.AcquireWriteLockAsync(CancellationToken))
        {
            ResetCommand();
            startingPositions.Clear();
            inClassStartingPositions.Clear();
            lastLapTimesBeforeReset.Clear();
            SessionState = new SessionState { EventId = EventId, SessionId = sessionId, SessionName = sessionName };
        }
    }

    public virtual void SetStartingPosition(string number, int position)
    {
        startingPositions[number] = position;
    }

    public virtual void SetInClassStartingPosition(string number, int position)
    {
        inClassStartingPositions[number] = position;
    }

    public virtual int? GetStartingPosition(string number)
    {
        if (IsMultiloopActive && numberToCarPositionLookup.TryGetValue(number, out var car))
            return car.OverallStartingPosition;
        if (startingPositions.TryGetValue(number, out var pos))
            return pos;
        return null;
    }

    public virtual int? GetInClassStartingPosition(string number)
    {
        if (IsMultiloopActive)
            throw new InvalidOperationException(nameof(GetInClassStartingPosition) + " not supported in Multiloop mode");
        if (inClassStartingPositions.TryGetValue(number, out var pos))
            return pos;
        return null;
    }

    public virtual ImmutableDictionary<string, int> GetStartingPositions() => startingPositions.ToImmutableDictionary();
    public virtual ImmutableDictionary<string, int> GetInClassStartingPositions() => inClassStartingPositions.ToImmutableDictionary();

    public virtual void ClearStartingPositions()
    {
        startingPositions.Clear();
        inClassStartingPositions.Clear();
    }

    public virtual void SetLastLapTimeBeforeReset()
    {
        foreach(var car in SessionState.CarPositions)
        {
            if (!string.IsNullOrEmpty(car.Number) && lastLapTimesBeforeReset.TryGetValue(car.Number, out var lastLapTime))
            {
                car.LastLapTime = lastLapTime;
            }
        }
    }
}
