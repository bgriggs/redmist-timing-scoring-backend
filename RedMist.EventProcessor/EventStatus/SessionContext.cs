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
    /// Cars whose pit state has moved from Flagtronics to X2 since <see cref="TakeCarsHandedToX2"/>
    /// was last called, for whatever reason.
    /// </summary>
    private readonly HashSet<string> carsHandedToX2 = [];

    /// <summary>
    /// Returns, and forgets, the cars whose pit state has moved from Flagtronics to X2 since the last
    /// call. Whatever Flagtronics last published for them - a pit phase in particular - stays on the
    /// car until something replaces it, and X2 may have nothing to say until the car's next passing,
    /// or ever, at an event without loops.
    /// </summary>
    public virtual List<string> TakeCarsHandedToX2()
    {
        var cars = carsHandedToX2.ToList();
        carsHandedToX2.Clear();
        return cars;
    }

    /// <summary>
    /// Cars allowed to change pit-state owner despite currently being shown in the pit.
    /// </summary>
    private readonly HashSet<string> pitOwnershipHoldReleased = [];

    /// <summary>
    /// The highest completed lap each car has been seen with this session. It survives
    /// <see cref="ResetCommand"/>, so a reset re-sending the field's lap counts is not taken for
    /// every car completing a lap. It only rises: after a scoring edit takes a lap away, the next
    /// lap counts once the car is past its old count.
    /// </summary>
    private readonly Dictionary<string, int> completedLapsSeen = [];

    /// <summary>
    /// Records a change to a car's completed lap count and says whether it is a new lap - one above
    /// the highest already seen for the car - as opposed to a count re-sent or taken back. A
    /// mid-race reset rebuilds every car and re-sends its lap count; treated as laps, those lifted
    /// the in-pit ownership hold on every car parked in the pit, handing any whose bars happened
    /// to be low to X2 mid-stop.
    /// </summary>
    /// <param name="carNumber">The car whose lap count changed.</param>
    /// <param name="lapCompleted">The car's completed lap count after the change.</param>
    public virtual bool RecordCompletedLap(string? carNumber, int lapCompleted)
    {
        if (string.IsNullOrEmpty(carNumber))
            return false;

        if (completedLapsSeen.TryGetValue(carNumber, out var seen) && lapCompleted <= seen)
            return false;

        completedLapsSeen[carNumber] = lapCompleted;
        return true;
    }

    /// <summary>
    /// Lifts the in-pit hold on changing a car's pit-state owner. Called when the car completes a
    /// lap (see <see cref="RecordCompletedLap"/>): crossing start/finish proves it is on track, so
    /// the mid-stop protection no longer applies. Without this, a car whose telemetry stopped while
    /// it was shown in the pit would keep its owner forever - that owner has nothing left to say
    /// and the other source is not allowed to take over, so the car would sit in the pit for the
    /// rest of the session.
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
    /// <returns>
    /// Cars just taken off Flagtronics because loop evidence showed their in-car pit data to be
    /// wrong. X2 has to republish these straight away: nothing else will prompt it to until the
    /// car's next passing or lap.
    /// </returns>
    public virtual List<string> UpdatePitOwnership()
    {
        var now = _timeProvider.GetUtcNow();
        List<string>? demotedNow = null;

        foreach (var car in SessionState.CarPositions)
        {
            if (string.IsNullOrEmpty(car.Number))
                continue;

            ObservePitAndPosition(car, now);
            if (EvaluatePitContradiction(car, now))
            {
                // The hold on changing owner mid-stop protects a car whose state is right. This
                // one's is provably wrong, and leaving it with Flagtronics is the failure itself.
                flagtronicsPitDemoted[car.Number] = now;
                flagtronicsPitDemotionCount[car.Number] = flagtronicsPitDemotionCount.GetValueOrDefault(car.Number) + 1;
                flagtronicsPitOwners[car.Number] = false;
                carsHandedToX2.Add(car.Number);
                pitOwnershipHoldReleased.Remove(car.Number);
                (demotedNow ??= []).Add(car.Number);
                continue;
            }

            // Null bars mean no in-car device, and null >= n is false, which is the wanted answer.
            // A demoted car's recovery is re-tested on every pass rather than settled once, so a
            // device that freezes again while the handover waits out a pit stop is not given back.
            bool eligible = car.SignalBars >= MIN_TRUSTED_PIT_SIGNAL_BARS
                && (!flagtronicsPitDemoted.ContainsKey(car.Number) || HasRecoveredFromDemotion(car.Number, now));
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
                if (!eligible)
                    carsHandedToX2.Add(car.Number);
                pitOwnershipHoldReleased.Remove(car.Number);
                if (eligible && flagtronicsPitDemoted.Remove(car.Number))
                    Logger.LogInformation("Car {Car} Flagtronics GPS has recovered; returning its pit state to Flagtronics", car.Number);
            }
        }

        return demotedNow ?? [];
    }

    /// <summary>
    /// How long Flagtronics gets to agree with an X2 loop passing before the disagreement counts.
    /// Comfortably longer than its own pit confirm window, so a car crossing a pit loop a few
    /// seconds before its device commits the stop is not mistaken for a broken device.
    /// </summary>
    public static readonly TimeSpan PitContradictionGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How far before a pit loop passing an in-pit reading from Flagtronics still counts as
    /// agreeing with it. Covers a pit exit loop, which the car crosses just after its device
    /// has already moved it back on track.
    /// </summary>
    public static readonly TimeSpan PitAgreementLookback = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a car's GPS has to keep moving before a car taken off Flagtronics is given back
    /// to it. Long, because handing a car back to a device that is still faulty costs another stop
    /// or lap shown wrong before it is caught again, while staying on X2 a little longer costs
    /// nothing - X2 is right, only coarser. Doubles with each further demotion in the session, up
    /// to <see cref="MaxFlagtronicsRecoveryWindow"/>, so an intermittent device is not handed back
    /// every half hour of a 24 hour race.
    /// </summary>
    public static readonly TimeSpan FlagtronicsRecoveryWindow = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaxFlagtronicsRecoveryWindow = TimeSpan.FromHours(4);

    /// <summary>How many times each car has been taken off Flagtronics this session.</summary>
    private readonly Dictionary<string, int> flagtronicsPitDemotionCount = [];

    /// <summary>
    /// How long a position can stay identical before the device counts as frozen rather than
    /// between updates. Above the gap between records of even a slow device, and far below the
    /// recovery window, so one stall anywhere in that window resets it.
    /// </summary>
    public static readonly TimeSpan GpsFrozenAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Cars whose Flagtronics pit data was shown wrong by X2 loop passings, and when. The fault
    /// behind this is a device whose GPS has frozen while it keeps transmitting, which leaves
    /// signal bars full and so is never caught by them. A car stays on X2 until its device has
    /// been healthy for <see cref="FlagtronicsRecoveryWindow"/>; see <see cref="HasRecoveredFromDemotion"/>.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> flagtronicsPitDemoted = [];

    private readonly Dictionary<string, PitObservation> pitObservations = [];
    private readonly Dictionary<string, PitContradiction> pitContradictions = [];

    private sealed class PitObservation
    {
        public DateTimeOffset? LastInPit;
        public DateTimeOffset? LastOutOfPit;
        public double? Latitude;
        public double? Longitude;
        public DateTimeOffset? LastMoved;

        /// <summary>
        /// Last time the position had been identical for longer than <see cref="GpsFrozenAfter"/>.
        /// A device that stops sending altogether also lands here, since its position then stops
        /// changing too.
        /// </summary>
        public DateTimeOffset? LastFrozen;
    }

    private sealed record PitContradiction(bool LoopSaysInPit, uint LoopId, DateTimeOffset Since, int Count);

    /// <summary>
    /// Records an X2 loop passing as evidence about a Flagtronics-owned car's pit state. Loop
    /// passings are physical: a car cannot cross a pit loop without being in the pit lane, nor a
    /// track loop while sitting in its box. When the in-car data disagrees and keeps disagreeing
    /// (see <see cref="EvaluatePitContradiction"/>), the car is handed to X2 on the next
    /// <see cref="UpdatePitOwnership"/>.
    /// </summary>
    /// <param name="loopId">The loop crossed, so a repeat can be told from one transit over two loops.</param>
    /// <param name="loopSaysInPit">True for a pit-lane loop, false for a loop on the racing surface.</param>
    public virtual void RecordLoopPitEvidence(string? carNumber, uint loopId, bool loopSaysInPit)
    {
        if (string.IsNullOrEmpty(carNumber) || !IsFlagtronicsPitTrusted(carNumber))
            return;

        var car = GetCarByNumber(carNumber);
        if (car == null)
            return;

        var now = _timeProvider.GetUtcNow();
        ObservePitAndPosition(car, now);

        if (pitContradictions.TryGetValue(carNumber, out var existing))
        {
            if (existing.LoopSaysInPit == car.IsInPit)
            {
                // The device has come round to what the loops said, so it was only slow.
                pitContradictions.Remove(carNumber);
            }
            else if (existing.LoopSaysInPit == loopSaysInPit)
            {
                // Only the same loop again, after the grace, counts as a repeat: one transit of the
                // pit lane can cross-read two different loops, but only a car out lapping can cross
                // the same one twice.
                if (existing.LoopId == loopId && now - existing.Since >= PitContradictionGrace)
                    pitContradictions[carNumber] = existing with { Count = existing.Count + 1 };
                return;
            }
            else
            {
                // The device matches this loop only because its state has not moved since the
                // earlier one; keep the evidence rather than let a stuck state clear it.
                return;
            }
        }

        if (car.IsInPit == loopSaysInPit)
            return;

        pitContradictions[carNumber] = new PitContradiction(loopSaysInPit, loopId, now, 1);
    }

    private void ObservePitAndPosition(CarPosition car, DateTimeOffset now)
    {
        if (!pitObservations.TryGetValue(car.Number!, out var obs))
        {
            obs = new PitObservation();
            pitObservations[car.Number!] = obs;
        }

        if (car.IsInPit)
            obs.LastInPit = now;
        else
            obs.LastOutOfPit = now;

        // Exact comparison on purpose: a live receiver jitters in the last digits even standing
        // still, whereas a frozen one repeats the identical value.
        if (obs.LastMoved == null || obs.Latitude != car.Latitude || obs.Longitude != car.Longitude)
        {
            obs.Latitude = car.Latitude;
            obs.Longitude = car.Longitude;
            obs.LastMoved = now;
        }

        // Bars are deliberately not folded in: they gate ownership on their own at the moment of
        // handover, and a brief dip at some GPS-shadowed spot each lap would otherwise keep
        // restarting the window and never let a working device back.
        if (now - obs.LastMoved > GpsFrozenAfter)
            obs.LastFrozen = now;
    }

    /// <summary>
    /// Whether a car taken off Flagtronics has had its position changing for the whole recovery
    /// window. The window starts no earlier than the demotion, so a device that was fine right up
    /// to the moment it was caught out does not walk straight back in.
    /// </summary>
    private bool HasRecoveredFromDemotion(string carNumber, DateTimeOffset now)
    {
        if (!flagtronicsPitDemoted.TryGetValue(carNumber, out var demotedAt))
            return false;

        var healthySince = demotedAt;
        if (pitObservations.TryGetValue(carNumber, out var obs) && obs.LastFrozen is DateTimeOffset frozen && frozen > healthySince)
            healthySince = frozen;

        return now - healthySince >= GetRecoveryWindow(carNumber);
    }

    private TimeSpan GetRecoveryWindow(string carNumber)
    {
        int priorDemotions = Math.Max(0, flagtronicsPitDemotionCount.GetValueOrDefault(carNumber) - 1);
        var window = FlagtronicsRecoveryWindow * Math.Pow(2, Math.Min(priorDemotions, 8));
        return window < MaxFlagtronicsRecoveryWindow ? window : MaxFlagtronicsRecoveryWindow;
    }

    /// <summary>
    /// Whether a recorded loop contradiction has held long enough to prove the car's in-car pit
    /// data wrong. The two directions need different proof, because each has an innocent
    /// look-alike:
    ///   - Pit loop, device says on track: a strong transponder on the racing surface can be read
    ///     by a pit loop laid alongside it. So it only counts when the device's position has not
    ///     moved either - a frozen GPS - and it never showed the car in the pit around the time.
    ///   - Track loop, device says in pit: the same cross-read from the pit lane, once. So it
    ///     takes a second track passing at least a grace period later with the car shown in the
    ///     pit throughout, which only a car out lapping can produce.
    /// </summary>
    private bool EvaluatePitContradiction(CarPosition car, DateTimeOffset now)
    {
        var number = car.Number!;
        if (!pitContradictions.TryGetValue(number, out var c))
            return false;

        if (!IsFlagtronicsPitTrusted(number))
        {
            pitContradictions.Remove(number);
            return false;
        }

        if (now - c.Since < PitContradictionGrace)
            return false;

        pitObservations.TryGetValue(number, out var obs);
        if (c.LoopSaysInPit)
        {
            pitContradictions.Remove(number);
            bool shownInPit = obs?.LastInPit is DateTimeOffset inPit && inPit >= c.Since - PitAgreementLookback;
            bool frozen = obs?.LastMoved is DateTimeOffset moved && moved < c.Since;
            if (shownInPit || !frozen)
                return false;

            Logger.LogWarning("Car {Car} crossed a pit loop but its Flagtronics data never showed it in the pit and its GPS position is frozen; using X2 for its pit state", number);
            return true;
        }

        if (obs?.LastOutOfPit is DateTimeOffset outOfPit && outOfPit > c.Since)
        {
            // The device did take the car out of the pit, so it was only slow.
            pitContradictions.Remove(number);
            return false;
        }

        if (c.Count < 2)
            return false;

        pitContradictions.Remove(number);
        Logger.LogWarning("Car {Car} kept crossing track loops while its Flagtronics data held it in the pit; using X2 for its pit state", number);
        return true;
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

    /// <summary>
    /// When timing data last reached this processor, or null if none has since it started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept here, in memory, because nothing else records it. Sessions.LastUpdated sounds like it
    /// and is not: it moves only when the relay re-announces the session it is already on, which it
    /// does on connect, on reconnect and whenever Red Mist asks it to resend - never on its own
    /// through a healthy race, so for three hours it stays where it was when the session started.
    /// </para>
    /// <para>
    /// Only live data moves it, which is decided in SessionStateProcessingPipeline. A resend of the
    /// relay's cache is not live data: Red Mist asks for one on every processor start, and while car
    /// positions fail the consistency check, so counting it would show a dead feed as fresh.
    /// </para>
    /// <para>
    /// On the context rather than the session state, because data arriving is not a property of a
    /// session: it carries across a session change, and a reset that replaces the state must not
    /// make a live feed look like it has never sent anything.
    /// </para>
    /// <para>
    /// The time this processor handled the message, not the time it reached Red Mist. Normally those
    /// are milliseconds apart. A restarted processor drains whatever queued while it was down, which
    /// can make a feed that stopped during the outage look fresher by up to the length of it. The
    /// relay's own send queue does the same after it loses its hub connection, replaying up to fifteen
    /// minutes of real but old heartbeats when it reconnects. Both happen once, bounded by the outage.
    /// </para>
    /// <para>
    /// Written and read under <see cref="SessionStateLock"/>.
    /// </para>
    /// </remarks>
    public DateTime? LastTimingDataUtc { get; private set; }

    /// <summary>
    /// Records that timing data has just arrived. The caller must hold the write lock.
    /// </summary>
    public void MarkTimingDataReceived() => LastTimingDataUtc = _timeProvider.GetUtcNow().UtcDateTime;

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
    /// Moves a car to a new number, keeping its laps, positions and starting position. Timing systems
    /// renumber a car mid-session (216 to 216x for championship points, or a typo corrected) by sending
    /// a competitor record under the new number with nothing to retire the old one, so without this the
    /// car ends up listed twice. The caller must hold the write lock.
    /// </summary>
    /// <returns>The car now under the new number, or null when there was no car under the old one.</returns>
    public virtual CarPosition? RenumberCar(string oldNumber, string newNumber)
    {
        if (oldNumber == newNumber || !numberToCarPositionLookup.Remove(oldNumber, out var car))
            return null;

        // An entry already under the new number - a $COMP can create one ahead of the $A - is replaced:
        // the car being moved is the one that has been timed.
        if (numberToCarPositionLookup.TryGetValue(newNumber, out var existing))
            SessionState.CarPositions.Remove(existing);

        car.Number = newNumber;
        numberToCarPositionLookup[newNumber] = car;

        if (car.TransponderId != 0)
            transponderToNumberLookup[car.TransponderId] = newNumber;

        MoveByNumber(startingPositions, oldNumber, newNumber);
        MoveByNumber(inClassStartingPositions, oldNumber, newNumber);
        MoveByNumber(flagtronicsPitOwners, oldNumber, newNumber);
        if (carsHandedToX2.Remove(oldNumber))
            carsHandedToX2.Add(newNumber);
        if (pitOwnershipHoldReleased.Remove(oldNumber))
            pitOwnershipHoldReleased.Add(newNumber);
        MoveByNumber(completedLapsSeen, oldNumber, newNumber);
        MoveByNumber(flagtronicsPitDemoted, oldNumber, newNumber);
        MoveByNumber(flagtronicsPitDemotionCount, oldNumber, newNumber);
        MoveByNumber(pitObservations, oldNumber, newNumber);
        MoveByNumber(pitContradictions, oldNumber, newNumber);
        SessionState.EventEntries.RemoveAll(e => e.Number == oldNumber);

        // Clients are told to reset and re-read, and must not be served the field with the old number.
        InvalidateSnapshot();
        return car;
    }

    private static void MoveByNumber<T>(Dictionary<string, T> byNumber, string oldNumber, string newNumber)
    {
        if (byNumber.Remove(oldNumber, out var value))
            byNumber[newNumber] = value;
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
        carsHandedToX2.Clear();
        pitOwnershipHoldReleased.Clear();
        completedLapsSeen.Clear();
        flagtronicsPitDemoted.Clear();
        flagtronicsPitDemotionCount.Clear();
        pitObservations.Clear();
        pitContradictions.Clear();

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

    /// <summary>
    /// How long a snapshot may wait for the read lock before the callers waiting on it are handed
    /// the previous one instead. The pipeline holds the write lock across database and Redis work
    /// for the whole of each message, so one slow message is enough to hold a snapshot off - and
    /// holding every poller with it, when a serviceable copy is already in hand, is the worst of
    /// both.
    /// </summary>
    private static readonly TimeSpan SnapshotStallTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The oldest a snapshot may be and still stand in for one that has stalled. Past this the
    /// callers wait after all: they apply the full state over patches they have already been sent,
    /// and nothing carries a sequence number to reconcile the two by, so state old enough to
    /// contradict those patches would visibly rewind the timing screen. No age is provably safe -
    /// this is a judgment about how much rewind is worth avoiding a wait, not a derived bound.
    /// </summary>
    private static readonly TimeSpan SnapshotMaxServedAge = TimeSpan.FromSeconds(2);

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
    ///
    /// A snapshot that cannot get the read lock degrades rather than holding everyone: see
    /// <see cref="SnapshotStallTimeout"/> and <see cref="SnapshotMaxServedAge"/>. That releases
    /// every caller waiting on it, not only those arriving after the bound - a caller that has
    /// been waiting longer is the last one that should go on waiting.
    /// </summary>
    public virtual Task<byte[]> GetSerializedStateAsync()
    {
        TaskCompletionSource<byte[]> pending;
        lock (snapshotGate)
        {
            if (snapshot != null && _timeProvider.GetUtcNow() - snapshotTakenUtc < SnapshotMaxAge)
                return Task.FromResult(snapshot);

            // Already completed, if the one in flight has stalled and given its waiters the
            // previous snapshot; this caller is then served from it immediately too.
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
            // Kept as a task rather than awaited straight away so the wait can be given up on.
            // The acquisition itself is always awaited below whatever happens: walking away from a
            // grant still in flight would leave the lock held by someone who never takes it.
            var acquisition = SessionStateLock.AcquireReadLockAsync(CancellationToken);
            if (!acquisition.IsCompleted)
                await ServeStaleIfStalledAsync(acquisition, pending);

            byte[] serialized;
            int version;
            using (await acquisition)
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
                if (ReferenceEquals(snapshotInFlight, pending))
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
    /// Hands the callers waiting on a snapshot the previous one, once this one has been queued for
    /// the read lock for <see cref="SnapshotStallTimeout"/>.
    ///
    /// The snapshot itself carries on regardless - it still takes the lock, still serializes, and
    /// still replaces the cached copy. Only the waiting is given up on, and the acquisition is
    /// still awaited by the caller of this, so no grant is ever abandoned.
    /// </summary>
    private async Task ServeStaleIfStalledAsync(Task<IDisposable> acquisition, TaskCompletionSource<byte[]> pending)
    {
        using var stallTimer = new CancellationTokenSource();
        var stalled = Task.Delay(SnapshotStallTimeout, _timeProvider, stallTimer.Token);
        if (await Task.WhenAny(acquisition, stalled) != stalled)
        {
            // Cancels the timer rather than letting it run out. That leaves the delay canceled
            // rather than faulted, so there is no exception for anyone to observe.
            stallTimer.Cancel();
            return;
        }

        byte[]? previous;
        DateTimeOffset takenUtc;
        lock (snapshotGate)
        {
            previous = snapshot;
            takenUtc = snapshotTakenUtc;
        }

        // Nothing to fall back on before the first snapshot - and nothing after a session change,
        // which drops the copy precisely so the field it describes cannot be handed out once that
        // session is over.
        if (previous == null)
            return;

        var age = _timeProvider.GetUtcNow() - takenUtc;
        if (age > SnapshotMaxServedAge)
        {
            Logger.LogWarning(
                "Session state snapshot has been waiting {wait:F0} ms on the pipeline and the previous one is {age:F0} ms old, " +
                "past the {max:F0} ms it may stand in for; callers are waiting for a fresh one",
                SnapshotStallTimeout.TotalMilliseconds, age.TotalMilliseconds, SnapshotMaxServedAge.TotalMilliseconds);
            return;
        }

        if (pending.TrySetResult(previous))
        {
            Logger.LogWarning(
                "Session state snapshot has been waiting {wait:F0} ms on the pipeline; serving the previous one, {age:F0} ms old",
                SnapshotStallTimeout.TotalMilliseconds, age.TotalMilliseconds);
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
