using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.TimingCommon.Extensions;
using RedMist.TimingCommon.Models;
using System.Collections.Immutable;

namespace RedMist.EventProcessor.EventStatus;

/// <summary>
/// Holds context information shared across the processing pipeline.
/// </summary>
public class SessionContext
{
    private ILogger Logger { get; }
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
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly ICarLapHistoryService lapHistoryService;
    private readonly TimeProvider _timeProvider;

    public int EventId { get; }

    public virtual CancellationToken CancellationToken { get; set; } = CancellationToken.None;

    public virtual bool IsMultiloopActive { get; set; }

    /// <summary>
    /// True once Flagtronics vehicle data is flowing for this event. Flagtronics in-car pit
    /// detection is higher fidelity than loop-based X2 pit data, so X2 pit processing is
    /// suppressed while this is set.
    /// </summary>
    public virtual bool IsFlagtronicsPitActive { get; set; }

    /// <summary>
    /// Fewest signal bars at which a car's Flagtronics in-car pit data is still applied. At two
    /// bars or fewer the car falls back to X2 loop data.
    ///
    /// Measured over a live 8 hour race: pit episodes beginning at two bars or fewer were
    /// spurious 296 times against 118 genuine, so that bucket is wrong more often than it is
    /// right. At full bars the ratio inverts, 343 genuine to 118 spurious.
    /// </summary>
    public const int MIN_TRUSTED_PIT_SIGNAL_BARS = 3;

    /// <summary>
    /// Whether Flagtronics in-car pit data should drive this particular car's pit state. False
    /// when the feed is absent, when the car has no in-car device (<see cref="CarPosition.SignalBars"/>
    /// null), or when its telemetry has degraded past <see cref="MIN_TRUSTED_PIT_SIGNAL_BARS"/> -
    /// in which case X2 loop data takes the car back.
    ///
    /// This is per car rather than per event on purpose: device failures are individual and
    /// intermittent. In the reference race one car's device degraded for the last five hours
    /// while the rest of the field stayed clean, and another failed for a single hour and
    /// recovered. It also covers the whole feed dying, since every car then goes stale and drops
    /// to zero bars.
    /// </summary>
    public virtual bool IsFlagtronicsPitTrusted(string? carNumber)
    {
        if (!IsFlagtronicsPitActive || string.IsNullOrEmpty(carNumber))
            return false;

        return flagtronicsPitOwners.TryGetValue(carNumber, out var owned) && owned;
    }

    private readonly Dictionary<string, bool> flagtronicsPitOwners = [];

    /// <summary>
    /// Reassesses which source owns each car's pit state, from the telemetry health published as
    /// <see cref="CarPosition.SignalBars"/>. Driven from the pipeline so the answer is stable for
    /// a whole pass; <see cref="IsFlagtronicsPitTrusted"/> is a pure read of the result.
    ///
    /// Ownership only changes while a car is out of the pit. Handing a car over mid-stop splits
    /// one physical stop across two sources, and the source taking it on has nothing to say yet -
    /// X2 has no loop passing for a car sitting still in a box - so the car would be shown
    /// leaving the pit while stationary and then entering again on recovery. That is exactly the
    /// enter/exit flapping this split exists to remove. It matters because a long stop is a
    /// common cause of losing a GPS fix, which is what drives the bars down in the first place.
    /// </summary>
    public virtual void UpdatePitOwnership()
    {
        if (!IsFlagtronicsPitActive)
        {
            flagtronicsPitOwners.Clear();
            return;
        }

        foreach (var car in SessionState.CarPositions)
        {
            if (string.IsNullOrEmpty(car.Number))
                continue;

            // Null bars mean no in-car device, and null >= n is false, which is the wanted answer.
            bool eligible = car.SignalBars >= MIN_TRUSTED_PIT_SIGNAL_BARS;
            if (!flagtronicsPitOwners.TryGetValue(car.Number, out var owned))
                flagtronicsPitOwners[car.Number] = eligible;
            else if (owned != eligible && !car.IsInPit)
                flagtronicsPitOwners[car.Number] = eligible;
        }
    }

    /// <summary>
    /// Latest overall track flag reported by the RMonitor timing system. RMonitor is the
    /// authoritative source for the overall flag; see <see cref="GetEffectiveTrackFlag"/>.
    /// </summary>
    public virtual Flags RMonitorTrackFlag { get; set; }

    /// <summary>
    /// Latest full-course flag reported by Flagtronics (Unknown when none/unusable). Used
    /// only to upgrade an RMonitor Yellow to Purple; see <see cref="GetEffectiveTrackFlag"/>.
    /// </summary>
    public virtual Flags FlagtronicsFullCourseFlag { get; set; }

    /// <summary>
    /// The overall track flag. RMonitor is authoritative; the single Flagtronics override is
    /// that RMonitor cannot represent a purple (slow-zone) full-course condition, so when
    /// RMonitor shows Yellow and Flagtronics reports Purple, the flag is upgraded to Purple35.
    /// </summary>
    public Flags GetEffectiveTrackFlag()
    {
        if (RMonitorTrackFlag == Flags.Yellow && FlagtronicsFullCourseFlag == Flags.Purple35)
            return Flags.Purple35;
        return RMonitorTrackFlag;
    }

    private readonly Dictionary<string, CarPosition> numberToCarPositionLookup = [];
    private readonly Dictionary<uint, string> transponderToNumberLookup = [];

    // Car starting positions by car number
    private readonly Dictionary<string, int> startingPositions = [];
    private readonly Dictionary<string, int> inClassStartingPositions = [];


    public SessionContext(IConfiguration configuration, IDbContextFactory<TsContext> tsContext,
        ILoggerFactory loggerFactory, ICarLapHistoryService lapHistoryService, TimeProvider? timeProvider = null)
    {
        EventId = configuration.GetValue("event_id", 0);
        SessionState.EventId = EventId;
        this.tsContext = tsContext;
        this.lapHistoryService = lapHistoryService;
        _timeProvider = timeProvider ?? TimeProvider.System; // Use system time by default
        Logger = loggerFactory.CreateLogger(GetType().Name);
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

    /// <summary>
    /// Gets all car positions for a given class name.
    /// </summary>
    /// <param name="className"></param>
    /// <returns></returns>
    public virtual ImmutableList<CarPosition> GetClassCarPositions(string className)
    {
        return [.. SessionState.CarPositions.Where(c => c.Class == className)];
    }

    public virtual void ResetCommand()
    {
        // Prevent multiple resets from overwriting the previous session state, which there are 
        // typically 2-3 $I commands at the same time when a new session starts.
        var currentTime = _timeProvider.GetUtcNow().DateTime;
        if ((currentTime - lastPreviousSessionStateUpdate).TotalSeconds > 5)
        {
            // Save a copy of the current session state before clearing it if there is a session change
            PreviousSessionState = SessionState.DeepCopy();
            lastPreviousSessionStateUpdate = currentTime;
        }

        numberToCarPositionLookup.Clear();
        transponderToNumberLookup.Clear();
        SessionState.EventEntries.Clear();
        SessionState.CarPositions.Clear();

        Logger.LogDebug("Session state reset cleared car positions");
    }

    public virtual async Task NewSessionAsync(int sessionId, string sessionName)
    {
        var eventName = await LoadEventNameAsync();

        using (await SessionStateLock.AcquireWriteLockAsync(CancellationToken))
        {
            ResetCommand();
            startingPositions.Clear();
            inClassStartingPositions.Clear();
            await lapHistoryService.ClearLapsAsync();

            SessionState = new SessionState
            {
                EventId = EventId,
                EventName = eventName,
                SessionId = sessionId,
                SessionName = sessionName
            };

            // Reset the track-flag sources so a stale flag from the prior session cannot leak
            // into the fresh CurrentFlag before the new session's first heartbeat/feed arrives.
            RMonitorTrackFlag = Flags.Unknown;
            FlagtronicsFullCourseFlag = Flags.Unknown;
            flagtronicsPitOwners.Clear();
        }
    }

    #region Starting Positions

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

    /// <summary>
    /// Get whether there are any starting positions recorded in thread-safe manner.
    /// </summary>
    /// <returns>true if there are starting positions, number of starting positions, number of cars</returns>
    public virtual async Task<(bool hasPositions, int startingCount, int totalCars)> HasStartingPositions()
    {
        using (await SessionStateLock.AcquireReadLockAsync(CancellationToken))
        {
            return (startingPositions.Count > 0, startingPositions.Count, numberToCarPositionLookup.Count);
        }
    }

    #endregion

    public virtual async Task SetLastLapTimeBeforeResetAsync()
    {
        int timeSetCount = 0;
        foreach (var car in SessionState.CarPositions)
        {
            if (!string.IsNullOrEmpty(car.Number))
            {
                var laps = await lapHistoryService.GetLapsAsync(car.Number);
                if (laps.Count > 0)
                {
                    car.LastLapTime = laps[0].LastLapTime;
                    timeSetCount++;
                }
            }
        }

        Logger.LogInformation("Set last lap time before reset for {count} cars of {total}", timeSetCount, SessionState.CarPositions.Count);
    }

    public virtual void SetSessionClassMetadata()
    {
        using var db = tsContext.CreateDbContext();

        // Load organization by join on Event using EventId
        var organization = db.Events
            .Where(e => e.Id == EventId)
            .Join(db.Organizations, e => e.OrganizationId, o => o.Id, (e, o) => o)
            .FirstOrDefault();

        if (organization != null && organization.Classes != null)
        {
            SessionState.ClassColors = organization.Classes.ToDictionary(cm => cm.Name, cm => cm.ColorHex);
            SessionState.ClassOrder = organization.Classes.ToDictionary(cm => cm.Name, cm => cm.Order.ToString());
        }
    }

    /// <summary>
    /// Gets the current flag in thread-safe manner.
    /// </summary>
    /// <returns></returns>
    public virtual async Task<(Flags, int)> GetCurrentFlagAndLapAsync()
    {
        using (await SessionStateLock.AcquireReadLockAsync(CancellationToken))
        {
            int lastLap = 0;
            if (SessionState.CarPositions.Count > 0)
            {
                lastLap = SessionState.CarPositions.Max(cp => cp.LastLapCompleted);
            }
            return (SessionState.CurrentFlag, lastLap);
        }
    }

    private async Task<string> LoadEventNameAsync()
    {
        try
        {
            using var db = tsContext.CreateDbContext();
            var eventName = await db.Events.Where(e => e.Id == EventId).Select(e => e.Name).FirstOrDefaultAsync();
            return eventName ?? string.Empty;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error retrieving event name for event ID {eventId}", EventId);
        }
        return string.Empty;
    }
}
