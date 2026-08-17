using MessagePack;
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
    /// when the car has no in-car device (<see cref="CarPosition.SignalBars"/> null), and false
    /// once its telemetry degrades past <see cref="MIN_TRUSTED_PIT_SIGNAL_BARS"/> - in which case
    /// X2 loop data takes the car back.
    ///
    /// This is per car rather than per event on purpose: device failures are individual and
    /// intermittent. In the reference race one car's device degraded for the last five hours
    /// while the rest of the field stayed clean, and another failed for a single hour and
    /// recovered. Bars alone also cover an event with no in-car equipment at all, and the whole
    /// feed dying, since every car then goes stale and drops to zero.
    /// </summary>
    public virtual bool IsFlagtronicsPitTrusted(string? carNumber)
    {
        if (string.IsNullOrEmpty(carNumber))
            return false;

        return flagtronicsPitOwners.TryGetValue(carNumber, out var owned) && owned;
    }

    private readonly Dictionary<string, bool> flagtronicsPitOwners = [];

    /// <summary>
    /// Cars allowed to change pit-state owner despite currently being shown in the pit.
    /// </summary>
    private readonly HashSet<string> pitOwnershipHoldReleased = [];

    /// <summary>
    /// Lifts the in-pit hold on changing a car's pit-state owner. Called when the car completes a
    /// lap: crossing start/finish proves it is on track, so the mid-stop protection no longer
    /// applies. Without this, a car whose telemetry stopped while it was shown in the pit would
    /// keep its owner forever - that owner has nothing left to say and the other source is not
    /// allowed to take over, so the car would sit in the pit for the rest of the session.
    /// </summary>
    public virtual void ReleasePitOwnershipHold(string? carNumber)
    {
        if (!string.IsNullOrEmpty(carNumber))
            pitOwnershipHoldReleased.Add(carNumber);
    }

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
        foreach (var car in SessionState.CarPositions)
        {
            if (string.IsNullOrEmpty(car.Number))
                continue;

            // Null bars mean no in-car device, and null >= n is false, which is the wanted answer.
            bool eligible = car.SignalBars >= MIN_TRUSTED_PIT_SIGNAL_BARS;
            if (!flagtronicsPitOwners.TryGetValue(car.Number, out var owned))
            {
                // First sight of the car establishes ownership outright; any release recorded
                // before that has nothing to act on and must not stay armed for a later stop.
                flagtronicsPitOwners[car.Number] = eligible;
                pitOwnershipHoldReleased.Remove(car.Number);
                continue;
            }

            if (owned == eligible)
            {
                pitOwnershipHoldReleased.Remove(car.Number);
                continue;
            }

            if (!car.IsInPit || pitOwnershipHoldReleased.Contains(car.Number))
            {
                flagtronicsPitOwners[car.Number] = eligible;
                pitOwnershipHoldReleased.Remove(car.Number);
            }
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
        InvalidateSnapshot();

        Logger.LogDebug("Session state reset cleared car positions");
    }

    /// <summary>
    /// Starts a fresh session, dropping everything the previous one accumulated.
    ///
    /// For a caller that is already inside the write lock, which every caller is: sessions change on
    /// the processing pipeline, which holds the lock for the whole message. There is deliberately no
    /// locking overload - the lock is not reentrant, so one would be a hang waiting to be called by
    /// name. The reset must not be deferred to a background task either: the relay sends the new
    /// session's entry records right behind the session change, and a reset landing after those have
    /// been applied wipes the field it just rebuilt. Entries only arrive once per session, so
    /// nothing puts them back.
    /// </summary>
    public virtual async Task NewSessionWithLockHeldAsync(int sessionId, string sessionName)
    {
        var eventName = await LoadEventNameAsync();
        await ApplyNewSessionAsync(sessionId, sessionName, eventName);
    }

    private async Task ApplyNewSessionAsync(int sessionId, string sessionName, string eventName)
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
            SessionName = sessionName,
            // Derived from the name here rather than left to the feed. The RMonitor $B update that
            // would otherwise set it only produces a patch when the run number or the name differs
            // from the state's, and adopting the session has just made both match - so on an
            // RMonitor-only event nothing ever set it and every practice and qualifying session was
            // recorded, and served back, as a race. A Multiloop feed still corrects it from the run
            // type when its $R arrives, which is the better answer where there is one.
            IsPracticeQualifying = SessionHelper.IsPracticeOrQualifyingSession(sessionName)
        };

        // Reset the track-flag sources so a stale flag from the prior session cannot leak
        // into the fresh CurrentFlag before the new session's first heartbeat/feed arrives.
        RMonitorTrackFlag = Flags.Unknown;
        FlagtronicsFullCourseFlag = Flags.Unknown;
        flagtronicsPitOwners.Clear();
        pitOwnershipHoldReleased.Clear();

        // Again after the state object itself has been replaced: the reset above invalidated the
        // snapshot as it stood before any of this.
        InvalidateSnapshot();
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

    /// <summary>
    /// Restores each car's last lap time from the lap history. A reset does not resend lap times, and
    /// the relay's cached data set never carries them at all, so without this every car reads blank
    /// until it next crosses start/finish.
    /// </summary>
    /// <returns>
    /// Patches for the cars whose last lap time changed. These are returned rather than applied
    /// silently because clients are only sent what a patch tells them; a car restored in place here
    /// would stay blank on screen until its next change.
    /// </returns>
    public virtual async Task<List<CarPositionPatch>> SetLastLapTimeBeforeResetAsync()
    {
        var patches = new List<CarPositionPatch>();
        foreach (var car in SessionState.CarPositions)
        {
            if (!string.IsNullOrEmpty(car.Number))
            {
                var laps = await lapHistoryService.GetLapsAsync(car.Number);
                if (laps.Count > 0 && car.LastLapTime != laps[0].LastLapTime)
                {
                    car.LastLapTime = laps[0].LastLapTime;
                    patches.Add(new CarPositionPatch { Number = car.Number, LastLapTime = car.LastLapTime });
                }
            }
        }

        Logger.LogInformation("Set last lap time before reset for {count} cars of {total}", patches.Count, SessionState.CarPositions.Count);
        return patches;
    }

    /// <summary>
    /// Drops the cached lap history for the event. Exposed separately from the session reset so a
    /// caller that is not adopting a session - one handed a bare session id, with no name to adopt
    /// it under - can still retire the outgoing session's laps, which would otherwise be restored
    /// onto the new session's cars.
    /// </summary>
    public virtual Task ClearLapHistoryAsync() => lapHistoryService.ClearLapsAsync();

    /// <summary>
    /// Adopts a session that is already under way, as happens when this process restarts mid-session.
    /// Unlike <see cref="NewSessionWithLockHeldAsync"/> nothing is cleared: the cars rebuilt from the
    /// relay's cached data stay, and so does the lap history, which is the only record of each car's
    /// last lap time until it next crosses start/finish.
    ///
    /// For a caller already inside the write lock; there is no locking overload, since the only
    /// caller is the processing pipeline. See <see cref="NewSessionWithLockHeldAsync"/> for why the
    /// work cannot be deferred.
    /// </summary>
    public virtual async Task ResumeSessionWithLockHeldAsync(int sessionId, string sessionName)
    {
        var eventName = await LoadEventNameAsync();
        ApplyResumedSession(sessionId, sessionName, eventName);
    }

    private void ApplyResumedSession(int sessionId, string sessionName, string eventName)
    {
        // A resume only ever names the session that was running at startup, so it must never pull
        // the state back to it once a real session change has been adopted. Session changes are
        // serialized by the write lock this runs under, so nothing can get in front of the resume
        // today; the guard stands in case one ever can. Zero here means no session has been adopted
        // yet, not session 0 - an adopted session 0 matches the id and is let through.
        if (SessionState.SessionId != 0 && SessionState.SessionId != sessionId)
        {
            Logger.LogInformation("Skipping resume of session {sessionId}; session {current} has since been adopted",
                sessionId, SessionState.SessionId);
            return;
        }

        SessionState.EventId = EventId;
        SessionState.EventName = eventName;
        SessionState.SessionId = sessionId;
        SessionState.SessionName = sessionName;
        // Follows the name for the same reason a new session's does: a restart mid-session is not
        // told the run type either, and the $B that would have named it went by long before this
        // process started. Multiloop is the exception - its run type is the better answer, and it
        // only re-sends the run information when it changes, so there is nothing to correct a value
        // overwritten here. That is reachable: on a restart the backlog can carry the run
        // information in ahead of the relay's replayed session change, which is what brings us here.
        if (!IsMultiloopActive)
        {
            SessionState.IsPracticeQualifying = SessionHelper.IsPracticeOrQualifyingSession(sessionName);
        }

        Logger.LogInformation("Resumed session {sessionId} ({sessionName}) holding {cars} cars",
            sessionId, sessionName, SessionState.CarPositions.Count);
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

    #region Serialized state snapshot

    /// <summary>
    /// How old a snapshot handed to the status endpoint is allowed to be before it is taken again.
    /// Callers poll that endpoint every few seconds and receive changes over the patch feed in
    /// between, so a snapshot this fresh is indistinguishable to them from one taken per request.
    /// </summary>
    private static readonly TimeSpan SnapshotMaxAge = TimeSpan.FromMilliseconds(100);

    private readonly Lock snapshotGate = new();
    private byte[]? snapshot;
    private DateTimeOffset snapshotTakenUtc = DateTimeOffset.MinValue;
    private TaskCompletionSource<byte[]>? snapshotInFlight;

    /// <summary>
    /// Counts session changes, so a snapshot taken across one can be recognized and dropped.
    /// </summary>
    private int snapshotVersion;

    /// <summary>
    /// The session state, serialized, for the status endpoint to return.
    ///
    /// Serializing per request would put a read lock acquisition on the pipeline for every poller,
    /// so the cost of watching an event would scale with how many people were watching it - and
    /// the read lock genuinely excludes the pipeline now. Instead callers arriving while a snapshot
    /// is being taken wait for that one rather than starting their own, and a completed snapshot
    /// serves any caller arriving within <see cref="SnapshotMaxAge"/> of it. The pipeline is then
    /// interrupted at a fixed rate no matter how large the audience is.
    /// </summary>
    public virtual Task<byte[]> GetSerializedStateAsync()
    {
        TaskCompletionSource<byte[]> pending;
        lock (snapshotGate)
        {
            if (snapshot != null && _timeProvider.GetUtcNow() - snapshotTakenUtc < SnapshotMaxAge)
                return Task.FromResult(snapshot);

            if (snapshotInFlight != null)
                return snapshotInFlight.Task;

            pending = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            snapshotInFlight = pending;
        }

        // Started outside the gate: acquiring the read lock can complete synchronously, and the
        // serialization would then run with the gate held, blocking every other caller on it.
        _ = TakeSnapshotAsync(pending);
        return pending.Task;
    }

    private async Task TakeSnapshotAsync(TaskCompletionSource<byte[]> pending)
    {
        try
        {
            byte[] serialized;
            int version;
            using (await SessionStateLock.AcquireReadLockAsync(CancellationToken))
            {
                serialized = MessagePackSerializer.Serialize(SessionState);
                // Read under the read lock, where a session change - which happens under the write
                // lock - cannot be in progress, so the version and the bytes describe one another.
                lock (snapshotGate)
                {
                    version = snapshotVersion;
                }
            }

            lock (snapshotGate)
            {
                // The state is cached only if it still describes the current session. A session
                // change can land between the read lock being released above and this point, and
                // caching over it would go on handing out the previous session's field to callers
                // that have already been told to reset. That interleaving is too narrow to reach
                // from a test without a seam here, so this stands on the ordering rather than on
                // coverage - what the tests pin is the reachable case, a reset dropping the cache.
                if (snapshotVersion == version)
                {
                    snapshot = serialized;
                    snapshotTakenUtc = _timeProvider.GetUtcNow();
                }
                snapshotInFlight = null;
            }
            pending.TrySetResult(serialized);
        }
        catch (Exception ex)
        {
            // Clear the in-flight slot before completing, so the next caller starts a fresh attempt
            // instead of being handed this failure forever.
            lock (snapshotGate)
            {
                if (ReferenceEquals(snapshotInFlight, pending))
                    snapshotInFlight = null;
            }
            pending.TrySetException(ex);
        }
    }

    /// <summary>
    /// Drops the cached snapshot so the next caller serializes the state as it now stands.
    ///
    /// <see cref="SnapshotMaxAge"/> is a tolerance for ordinary field updates, which reach clients
    /// over the patch feed regardless. A session change is different in kind: it clears the field
    /// or replaces the state outright, and a client is told to reset and re-read at that moment -
    /// so serving it even a slightly old snapshot would repopulate it with the session that just
    /// ended.
    /// </summary>
    private void InvalidateSnapshot()
    {
        lock (snapshotGate)
        {
            snapshot = null;
            snapshotVersion++;
        }
    }

    #endregion

    /// <summary>
    /// Gets the current flag in thread-safe manner.
    /// </summary>
    /// <returns></returns>
    public virtual async Task<(Flags, int)> GetCurrentFlagAndLapAsync()
    {
        using (await SessionStateLock.AcquireReadLockAsync(CancellationToken))
        {
            return GetCurrentFlagAndLapWithLockHeld();
        }
    }

    /// <summary>
    /// <see cref="GetCurrentFlagAndLapAsync"/> for a caller already inside the lock - the pipeline
    /// runs its enrichers under the write lock, and the lock is not reentrant.
    /// </summary>
    public virtual (Flags, int) GetCurrentFlagAndLapWithLockHeld()
    {
        int lastLap = 0;
        if (SessionState.CarPositions.Count > 0)
        {
            lastLap = SessionState.CarPositions.Max(cp => cp.LastLapCompleted);
        }
        return (SessionState.CurrentFlag, lastLap);
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
