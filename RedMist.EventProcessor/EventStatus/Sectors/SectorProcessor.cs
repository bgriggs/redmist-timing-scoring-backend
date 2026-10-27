using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Mappers;

namespace RedMist.EventProcessor.EventStatus.Sectors;

/// <summary>
/// Owns every car's sector times for the session: the latest time through each sector, the bests,
/// and the flags clients color them by. Sources (an external feed today; X2 loops, Multiloop and
/// in-car GPS to follow) only report <see cref="SectorCrossing"/>s, so the bests and the purple and
/// green behave the same whichever source the event uses.
///
/// Only each car's own valid times are kept. Its personal best in a sector is the fastest of them,
/// and the overall and class bests are worked out from the personal bests whenever flags are built,
/// using each car's class as it stands then. Nothing is cached that a later correction could leave
/// behind: a time the source replaces, or marks invalid after the fact, simply stops counting.
///
/// Bests go to whoever set them first: a later time equal to a best does not take it. That keeps the
/// flags nested the way <see cref="SectorTime"/> documents - the overall best is also the best in its
/// class, and both are the holder's own best - since the first car to reach a time in the field is
/// also the first to reach it in its class and in its own laps.
///
/// State lives in memory only. After a processor restart the bests are rebuilt from whatever the
/// source sends next, so purple can briefly sit on a time slower than the session's real best until
/// the field has lapped again.
///
/// All calls happen under the pipeline write lock, so the state here needs no synchronization.
/// </summary>
public class SectorProcessor
{
    /// <summary>The most sectors a lap may be divided into.</summary>
    public const int MaxSectors = 8;

    private readonly SessionContext sessionContext;
    private ILogger Logger { get; }

    private int sessionId = -1;
    private long arrivals;
    private readonly Dictionary<string, CarSectors> cars = [];
    private (Dictionary<int, Best> Overall, Dictionary<(string Class, int Index), Best> InClass)? lastHolders;

    /// <summary>A valid time, and when it arrived, so ties go to whoever set the time first.</summary>
    private readonly record struct Timed(int TimeMs, long Arrival);

    /// <summary>A car's best through a sector and the lap it was set on.</summary>
    private sealed record Best(string CarNumber, int Lap, int TimeMs, long Arrival);

    private sealed class CarSectors
    {
        /// <summary>The latest time through each sector, by sector index.</summary>
        public Dictionary<int, SectorCrossing> Latest { get; } = [];
        /// <summary>Every valid time through each sector, by sector index and then lap.</summary>
        public Dictionary<int, Dictionary<int, Timed>> Valid { get; } = [];
        /// <summary>The fastest of <see cref="Valid"/> in each sector, kept up to date with it.</summary>
        public Dictionary<int, Best> PersonalBests { get; } = [];
        /// <summary>The sector the car completed most recently, or 0 before its first.</summary>
        public int LastIndex { get; set; }
        /// <summary>The lap that sector belonged to.</summary>
        public int LastLap { get; set; }
        /// <summary>The timing system's laps completed for the car when that sector arrived.</summary>
        public int LastLapCompletedSeen { get; set; }
    }

    public SectorProcessor(ILoggerFactory loggerFactory, SessionContext sessionContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.sessionContext = sessionContext;
    }

    /// <summary>
    /// Records sector crossings from <paramref name="source"/> and returns the patches for every car
    /// whose sectors or flags changed - which, when a crossing moves an overall or class best,
    /// includes the car that held it before - plus a session patch when the published layout has to
    /// grow.
    /// </summary>
    public PatchUpdates? Process(IReadOnlyCollection<SectorCrossing> crossings, SectorSource source)
    {
        var carPatches = ResetOnNewSession();

        var usable = new List<SectorCrossing>(crossings.Count);
        foreach (var crossing in crossings)
        {
            if (crossing.Index >= 1 && crossing.Index <= MaxSectors && crossing.TimeMs > 0 && !string.IsNullOrWhiteSpace(crossing.CarNumber))
                usable.Add(crossing);
            else
                Logger.LogDebug("Ignoring unusable sector crossing {Crossing}", crossing);
        }
        if (usable.Count == 0)
            return carPatches.Count == 0 ? null : new PatchUpdates([], [.. carPatches]);

        var sessionPatches = new List<SessionStatePatch>();
        var layoutPatch = EnsureLayout(usable.Max(c => c.Index), source);
        if (layoutPatch != null)
            sessionPatches.Add(layoutPatch);

        var touched = new HashSet<string>();
        foreach (var crossing in usable)
        {
            var car = sessionContext.GetCarByNumber(crossing.CarNumber);
            if (!cars.TryGetValue(crossing.CarNumber, out var state))
                cars[crossing.CarNumber] = state = new CarSectors();
            var lastLapCompleted = car?.LastLapCompleted ?? 0;

            if (IsRepeat(state, crossing, lastLapCompleted))
                continue;

            var lap = crossing.Lap ?? InferLap(state, crossing.Index, lastLapCompleted);
            var recorded = crossing with { Lap = lap };
            state.Latest[crossing.Index] = recorded;
            state.LastIndex = crossing.Index;
            state.LastLap = lap;
            state.LastLapCompletedSeen = lastLapCompleted;
            touched.Add(crossing.CarNumber);

            // The newest word on a lap's time replaces whatever was said before, including a valid
            // time the source has since corrected or thrown out.
            if (!state.Valid.TryGetValue(crossing.Index, out var byLap))
                state.Valid[crossing.Index] = byLap = [];
            if (recorded.IsInvalid)
                byLap.Remove(lap);
            else
                byLap[lap] = new Timed(recorded.TimeMs, ++arrivals);

            if (PersonalBest(crossing.CarNumber, byLap) is { } best)
                state.PersonalBests[crossing.Index] = best;
            else
                state.PersonalBests.Remove(crossing.Index);
        }

        // A change to an overall or class best can take a flag away from any car, so then every car
        // is looked at again; otherwise only the cars that just crossed can have changed. Compared
        // with the bests last published rather than those before this batch, so a car that has
        // changed class since is caught here too - on the next batch of crossings from any car, since
        // a class change alone does not come through here.
        var holdersAfter = FieldBests();
        IEnumerable<string> toCheck = lastHolders is { } published && SameHolders(published, holdersAfter) ? touched : cars.Keys;
        lastHolders = holdersAfter;
        foreach (var number in toCheck)
        {
            var car = sessionContext.GetCarByNumber(number);
            if (car == null)
                continue;

            var sectors = BuildSectors(number, car.Class ?? string.Empty, holdersAfter);
            if (car.Sectors != null && car.Sectors.SequenceEqual(sectors))
                continue;

            car.Sectors = sectors;
            carPatches.Add(new CarPositionPatch { Number = number, Sectors = sectors });
        }

        if (sessionPatches.Count == 0 && carPatches.Count == 0)
            return null;
        return new PatchUpdates([.. sessionPatches], [.. carPatches]);
    }

    /// <summary>
    /// Puts sector times back on cars that have lost them: a timing system reset clears the cars and
    /// re-sends them without sectors. Cars that still have theirs are left alone. The layout lives on
    /// the session, which a reset keeps.
    /// </summary>
    public List<CarPositionPatch> ApplyCarValues(IEnumerable<CarPosition> carPositions)
    {
        var patches = ResetOnNewSession();
        if (cars.Count == 0)
            return patches;

        // Runs with every timing update, and almost never finds a car to restore, so the field's bests
        // are only worked out once one turns up.
        (Dictionary<int, Best> Overall, Dictionary<(string Class, int Index), Best> InClass)? holders = null;
        foreach (var car in carPositions)
        {
            if (car.Number == null || !cars.ContainsKey(car.Number) || car.Sectors is { Count: > 0 })
                continue;

            holders ??= FieldBests();
            car.Sectors = BuildSectors(car.Number, car.Class ?? string.Empty, holders.Value);
            patches.Add(new CarPositionPatch { Number = car.Number, Sectors = car.Sectors });
        }
        return patches;
    }

    /// <summary>
    /// Whether a crossing only repeats what the source already said. Sources resend - a feed
    /// republishing a car's whole sector list whenever any of it changes - and a repeat is not a new
    /// time. Checked before a lap is inferred for it: a resent first sector arriving after the second
    /// would otherwise be read as the start of the next lap.
    ///
    /// When the source gives no lap, a resend cannot be told from a new lap that matched the previous
    /// one to the millisecond. The resend is far more common - the list carrying a car's last sector
    /// usually arrives with its lap count already ticked, and repeats the earlier sectors of the lap
    /// just completed - so the same time is a repeat unless the car has completed a lap past the one
    /// it belonged to. A true tie then shows the previous lap's number until the car's next time,
    /// which clients read as the sector not yet reached.
    /// </summary>
    private static bool IsRepeat(CarSectors state, SectorCrossing crossing, int lastLapCompleted)
    {
        if (!state.Latest.TryGetValue(crossing.Index, out var previous)
            || previous.TimeMs != crossing.TimeMs
            || previous.IsInvalid != crossing.IsInvalid)
            return false;

        if (crossing.Lap != null)
            return crossing.Lap == previous.Lap;
        return previous.Lap >= lastLapCompleted;
    }

    /// <summary>
    /// Which lap a sector belongs to when the source cannot say. A car works through its sectors in
    /// order, so a sector numbered after the last one it completed is on the same lap, and one
    /// numbered the same or lower starts the next. The timing system's lap count keeps that honest
    /// both ways: a car cannot be partway round a lap it has already completed, nor further on than
    /// the lap after it. The upper bound matters as much as the lower one - a source that resends a
    /// sector with a corrected time, or misses one, would otherwise push the count ahead a lap at a
    /// time and never come back. Once the source has a lap count, then, every sector but the last is
    /// on the lap after the last completed, and the order the sectors arrive in only decides the
    /// last sector, which ends on the line and may belong to the lap just completed.
    /// </summary>
    private int InferLap(CarSectors state, int index, int lastLapCompleted)
    {
        var floor = LapFloor(index, lastLapCompleted);
        var lap = state.LastIndex == 0
            ? floor
            : index > state.LastIndex ? state.LastLap
            // The same sector again with no lap completed in between is a corrected time, not a lap.
            : index == state.LastIndex && lastLapCompleted == state.LastLapCompletedSeen ? state.LastLap
            : state.LastLap + 1;

        return Math.Clamp(lap, floor, Math.Max(lastLapCompleted + 1, floor));
    }

    /// <summary>
    /// The earliest lap a sector can belong to: the lap after the last one completed, except for the
    /// last sector, which can be the one that just completed it. A layout still growing from its
    /// first sector says nothing yet about which sector is last.
    /// </summary>
    private int LapFloor(int index, int lastLapCompleted)
    {
        var isLastSector = index > 1 && index >= sessionContext.SessionState.SectorLayout.Count;
        return Math.Max(isLastSector ? lastLapCompleted : lastLapCompleted + 1, 1);
    }

    /// <summary>A car's best valid time through a sector, earliest first among equal times.</summary>
    private static Best? PersonalBest(string carNumber, Dictionary<int, Timed> byLap)
    {
        Best? best = null;
        foreach (var (lap, timed) in byLap)
        {
            if (best == null || timed.TimeMs < best.TimeMs || (timed.TimeMs == best.TimeMs && timed.Arrival < best.Arrival))
                best = new Best(carNumber, lap, timed.TimeMs, timed.Arrival);
        }
        return best;
    }

    /// <summary>
    /// The overall and class bests through every sector, from the personal bests of the cars in the
    /// session, each counted in the class it is in now. A car the session does not know is in no
    /// class and in no field.
    /// </summary>
    private (Dictionary<int, Best> Overall, Dictionary<(string Class, int Index), Best> InClass) FieldBests()
    {
        var overall = new Dictionary<int, Best>();
        var inClass = new Dictionary<(string, int), Best>();
        foreach (var (number, state) in cars)
        {
            var car = sessionContext.GetCarByNumber(number);
            if (car == null)
                continue;

            var carClass = car.Class ?? string.Empty;
            foreach (var (index, best) in state.PersonalBests)
            {
                if (!overall.TryGetValue(index, out var o) || IsBetter(best, o))
                    overall[index] = best;
                if (!inClass.TryGetValue((carClass, index), out var c) || IsBetter(best, c))
                    inClass[(carClass, index)] = best;
            }
        }
        return (overall, inClass);
    }

    private static bool IsBetter(Best candidate, Best current) =>
        candidate.TimeMs < current.TimeMs || (candidate.TimeMs == current.TimeMs && candidate.Arrival < current.Arrival);

    private static bool SameHolders(
        (Dictionary<int, Best> Overall, Dictionary<(string Class, int Index), Best> InClass) before,
        (Dictionary<int, Best> Overall, Dictionary<(string Class, int Index), Best> InClass) after) =>
        before.Overall.Count == after.Overall.Count && before.Overall.All(kv => after.Overall.TryGetValue(kv.Key, out var b) && b == kv.Value)
        && before.InClass.Count == after.InClass.Count && before.InClass.All(kv => after.InClass.TryGetValue(kv.Key, out var b) && b == kv.Value);

    private List<SectorTime> BuildSectors(string carNumber, string carClass,
        (Dictionary<int, Best> Overall, Dictionary<(string Class, int Index), Best> InClass) holders)
    {
        var state = cars[carNumber];
        var sectors = new List<SectorTime>(state.Latest.Count);
        foreach (var crossing in state.Latest.Values.OrderBy(c => c.Index))
        {
            var sector = new SectorTime { Index = crossing.Index, Lap = crossing.Lap!.Value, TimeMs = crossing.TimeMs, IsInvalid = crossing.IsInvalid };
            if (!crossing.IsInvalid)
            {
                sector.IsPersonalBest = Holds(state.PersonalBests.GetValueOrDefault(crossing.Index), carNumber, sector);
                sector.IsOverallBest = Holds(holders.Overall.GetValueOrDefault(crossing.Index), carNumber, sector);
                sector.IsClassBest = Holds(holders.InClass.GetValueOrDefault((carClass, crossing.Index)), carNumber, sector);
            }
            sectors.Add(sector);
        }
        return sectors;
    }

    private static bool Holds(Best? best, string carNumber, SectorTime sector) =>
        best != null && best.CarNumber == carNumber && best.Lap == sector.Lap && best.TimeMs == sector.TimeMs;

    /// <summary>
    /// Publishes a layout covering every sector seen so far. A source that declares its own layout
    /// sends it on the session; this only ever grows it, so a declared layout is left alone.
    /// </summary>
    private SessionStatePatch? EnsureLayout(int highestIndex, SectorSource source)
    {
        var session = sessionContext.SessionState;
        var count = Math.Clamp(highestIndex, 1, MaxSectors);
        if (session.SectorLayout.Count >= count && session.SectorSource == source)
            return null;

        var patch = new SessionStatePatch { SectorSource = source };
        if (session.SectorLayout.Count < count)
        {
            var layout = new List<SectorInfo>(session.SectorLayout);
            for (int index = layout.Count + 1; index <= count; index++)
                layout.Add(new SectorInfo { Index = index, Label = $"S{index}" });
            patch.SectorLayout = layout;
        }

        SessionStateMapper.ApplyPatch(patch, session);
        return patch;
    }

    /// <summary>
    /// Starts over when the session changes, clearing the sectors the cars still carry from the old
    /// one and returning the patches that clear them for clients. A session whose number is only now
    /// becoming known - a resumed session moving from 0 to its real number, its cars kept - is the
    /// same session, and keeps its times.
    /// </summary>
    private List<CarPositionPatch> ResetOnNewSession()
    {
        var patches = new List<CarPositionPatch>();
        var current = sessionContext.SessionState.SessionId;
        if (current == sessionId)
            return patches;

        var previous = sessionId;
        sessionId = current;
        if (previous <= 0)
            return patches;

        Logger.LogInformation("Session changed from {Last} to {Current}, clearing sector state", previous, current);
        foreach (var number in cars.Keys)
        {
            var car = sessionContext.GetCarByNumber(number);
            if (car?.Sectors is not { Count: > 0 })
                continue;

            car.Sectors = [];
            patches.Add(new CarPositionPatch { Number = number, Sectors = [] });
        }
        cars.Clear();
        lastHolders = null;
        return patches;
    }
}
