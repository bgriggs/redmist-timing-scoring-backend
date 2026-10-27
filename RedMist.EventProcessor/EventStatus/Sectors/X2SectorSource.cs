using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using RedMist.TimingCommon.Models.X2;

namespace RedMist.EventProcessor.EventStatus.Sectors;

/// <summary>
/// Sector times from X2 timing loops, using the event's sector setup: the loop on the start/finish
/// line and, for each sector but the last, the loop where it ends. Each car's time through a sector
/// is the gap between its passings of the loops at either end, both taken from the X2 decoder's own
/// clock.
///
/// Only consecutive loops make a sector. A car whose transponder is not read at a loop has no time
/// for the sectors either side of it - the combined time is known but cannot be split - rather than
/// a wrong one. Missing a whole lap of loops brings the car back round to a loop that looks
/// consecutive; that is caught by the time it took, which is then longer than a lap. Resent passings
/// only fill in what a car has not done yet; they never move it back. A car that
/// crosses a pit loop partway round gets its time for that sector marked invalid: it is shown, but
/// never a best. The lap is left to <see cref="SectorProcessor"/>, which follows each car round from
/// the timing system's lap count.
///
/// All calls happen under the pipeline write lock, so the state here needs no synchronization.
/// </summary>
public class X2SectorSource
{
    /// <summary>
    /// The longest a car may take between two loops for the time to count as a sector. Past this it
    /// stopped - a red flag, a trip to the paddock - and the time says nothing about its pace.
    /// </summary>
    public static readonly TimeSpan MaxSectorTime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a sector may take, as a share of the car's best lap. A sector is a part of the lap,
    /// so a time longer than a whole best lap means the loops of a lap were missed in between - the
    /// time is then a lap plus a sector, and no lap is faster than the best. A sector that really is
    /// that slow, a long one run at a crawl under a full-course yellow, is lost along with them: no
    /// time rather than a wrong one. A setup with a single sector times the lap itself, which under a
    /// yellow can be well over the best; a missed read there is two laps, so it has
    /// <see cref="MaxSingleSectorToLapRatio"/> instead.
    /// </summary>
    public const double MaxSectorToLapRatio = 1.0;

    /// <summary>The <see cref="MaxSectorToLapRatio"/> for a setup with a single sector.</summary>
    public const double MaxSingleSectorToLapRatio = 1.9;

    /// <summary>
    /// A second read of the same loop this soon after the first is the same pass read twice, not a
    /// car that has come round again.
    /// </summary>
    public static readonly TimeSpan DoubleReadWindow = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan LoadRetryInterval = TimeSpan.FromSeconds(30);

    private readonly IDbContextFactory<TsContext> dbContextFactory;
    private readonly SessionContext sessionContext;
    private readonly TimeProvider timeProvider;
    private ILogger Logger { get; }

    private bool loaded;
    private DateTime? lastLoadAttemptUtc;
    private int sessionId = -1;
    private Dictionary<uint, LoopMetadata> loops = [];
    private Dictionary<uint, int> boundaries = [];
    private List<SectorInfo> layout = [];

    /// <summary>
    /// The boundaries of the last setup that could be used. Kept while a setup is set aside, so that a
    /// loop list saved without one of its loops for a moment, and then put back, is not taken for a
    /// change of sectors that throws every time away.
    /// </summary>
    private Dictionary<uint, int> lastGoodBoundaries = [];
    private readonly Dictionary<uint, TransponderState> transponders = [];

    private sealed class TransponderState
    {
        /// <summary>The boundary last crossed: 0 for start/finish, k for the end of sector k.</summary>
        public int Boundary { get; set; } = -1;
        public DateTime TimeUtc { get; set; }
        /// <summary>Whether the car has been through the pit lane since that boundary.</summary>
        public bool ThroughPit { get; set; }
    }

    public X2SectorSource(IDbContextFactory<TsContext> dbContextFactory, ILoggerFactory loggerFactory,
        SessionContext sessionContext, TimeProvider? timeProvider = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.sessionContext = sessionContext;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }

    /// <summary>Whether the event has a usable sector setup.</summary>
    public bool IsConfigured => layout.Count > 0;

    /// <summary>The sectors the setup declares, with their labels and lengths. Empty without one.</summary>
    public IReadOnlyList<SectorInfo> Layout => layout;

    /// <summary>
    /// Loads the event's sector setup once. Idempotent and cheap afterwards; a failed read is retried,
    /// spaced out so a database that is down is not asked on every batch of passings. Returns whether
    /// the sectors changed, as <see cref="RefreshAsync"/> does - a retry can be the read that finally
    /// picks up an organizer's change - and false when nothing was read.
    /// </summary>
    public async Task<bool> EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (loaded)
            return false;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (lastLoadAttemptUtc != null && now - lastLoadAttemptUtc < LoadRetryInterval)
            return false;
        lastLoadAttemptUtc = now;

        return await RefreshAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the event's sector setup and loop metadata again, as after the organizer saves either.
    /// Returns whether the times already recorded no longer mean what they did - see
    /// <see cref="Apply"/>. A failed read keeps the setup in force and is retried from
    /// <see cref="EnsureLoadedAsync"/>, so an organizer's save is not lost to a passing database fault.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var eventId = sessionContext.EventId;
            var loopsMetadata = await db.Events.AsNoTracking()
                .Where(e => e.Id == eventId)
                .Select(e => e.LoopsMetadata)
                .FirstOrDefaultAsync(cancellationToken) ?? [];
            var record = await db.EventSectorConfigurations.AsNoTracking()
                .FirstOrDefaultAsync(s => s.EventId == eventId, cancellationToken);

            loaded = true;
            return Apply(record?.Configuration ?? new SectorConfiguration(), loopsMetadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            loaded = false;
            return false;
        }
        catch (Exception ex)
        {
            loaded = false;
            lastLoadAttemptUtc = timeProvider.GetUtcNow().UtcDateTime;
            Logger.LogError(ex, "Failed to load the sector setup for event {event}", sessionContext.EventId);
            return false;
        }
    }

    /// <summary>
    /// Takes a sector setup and the loops it refers to. A setup that no longer fits the loops - one
    /// of its loops has since been removed from the event - is set aside rather than half applied.
    ///
    /// Returns true when the sectors themselves changed: a boundary moved, a sector was added or
    /// taken away, or the organizer removed the setup. The times already recorded were then taken over
    /// different stretches of track, so they and the bests built on them have to go. A change of
    /// label or length alone returns false and keeps everything, and so does a setup set aside or
    /// restored: its sectors are the same ones while it is out of use.
    /// </summary>
    public bool Apply(SectorConfiguration configuration, IReadOnlyCollection<LoopMetadata> loopsMetadata)
    {
        var newLoops = loopsMetadata.GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.First());
        var newBoundaries = new Dictionary<uint, int>();
        var newLayout = new List<SectorInfo>();

        var problem = configuration.Validate(loopsMetadata);
        if (problem != null)
        {
            Logger.LogWarning("Sector setup for event {event} cannot be used: {problem}", sessionContext.EventId, problem);
            loops = newLoops;
            boundaries = [];
            layout = [];
            return false;
        }

        if (configuration.Sectors.Count > 0)
        {
            newBoundaries[configuration.StartFinishLoopId!.Value] = 0;
            for (int i = 0; i < configuration.Sectors.Count; i++)
            {
                var sector = configuration.Sectors[i];
                if (sector.EndLoopId is uint end)
                    newBoundaries[end] = i + 1;
                newLayout.Add(new SectorInfo
                {
                    Index = i + 1,
                    Label = string.IsNullOrWhiteSpace(sector.Label) ? $"S{i + 1}" : sector.Label.Trim(),
                    LengthMeters = sector.LengthMeters,
                });
            }
        }

        // Boundaries include start/finish and one loop for each sector but the last, so they also
        // say how many sectors there are.
        var sectorsChanged = !newBoundaries.OrderBy(b => b.Key).SequenceEqual(lastGoodBoundaries.OrderBy(b => b.Key));
        if (sectorsChanged)
            transponders.Clear();
        loops = newLoops;
        boundaries = newBoundaries;
        lastGoodBoundaries = newBoundaries;
        layout = newLayout;
        return sectorsChanged;
    }

    /// <summary>
    /// Turns a batch of passings into sector crossings. Passings are taken in the decoder's time
    /// order; a resent or out-of-order passing older than the car's last one is ignored.
    /// </summary>
    public List<SectorCrossing> Process(IEnumerable<Passing> passings)
    {
        ResetOnNewSession();
        var crossings = new List<SectorCrossing>();
        if (!IsConfigured)
            return crossings;

        var sectorCount = layout.Count;
        foreach (var pass in passings.OrderBy(p => p.TimestampUtc))
        {
            if (!transponders.TryGetValue(pass.TransponderId, out var state))
                transponders[pass.TransponderId] = state = new TransponderState();

            if (state.Boundary >= 0 && pass.TimestampUtc <= state.TimeUtc)
            {
                // Older than what the car last did: a resend, or a batch arriving late. Unless a
                // live passing is far older - the decoder's clock was set back - when the car starts
                // afresh rather than ignoring every passing until the clock catches up. A resend can
                // be from long before, so it never counts as that.
                if (pass.IsResend || state.TimeUtc - pass.TimestampUtc <= MaxSectorTime)
                    continue;
                state.Boundary = -1;
                state.ThroughPit = false;
            }

            var isPit = pass.IsInPit || (loops.TryGetValue(pass.LoopId, out var loop) && SectorConfiguration.IsPitLoop(loop.Type));
            if (isPit)
            {
                state.ThroughPit = true;
                continue;
            }

            if (!boundaries.TryGetValue(pass.LoopId, out var boundary))
                continue;
            if (state.Boundary == boundary && pass.TimestampUtc - state.TimeUtc < DoubleReadWindow)
                continue;

            var previousBoundary = boundary == 0 ? sectorCount - 1 : boundary - 1;
            if (state.Boundary >= 0 && state.Boundary == previousBoundary)
            {
                var elapsed = pass.TimestampUtc - state.TimeUtc;
                var carNumber = sessionContext.GetCarNumberForTransponder(pass.TransponderId);
                if (carNumber != null && IsPlausible(elapsed, carNumber))
                {
                    var index = boundary == 0 ? sectorCount : boundary;
                    crossings.Add(new SectorCrossing(carNumber, index, (int)elapsed.TotalMilliseconds, IsInvalid: state.ThroughPit));
                }
            }

            state.Boundary = boundary;
            state.TimeUtc = pass.TimestampUtc;
            state.ThroughPit = false;
        }

        return crossings;
    }

    /// <summary>
    /// Whether a time between consecutive loops can be a sector: not so long the car stopped, and not
    /// longer than the car's best lap, which would mean a whole lap of loops went unread in between.
    ///
    /// A best set on lap 1 is not used: a first lap can be a partial one from a grid behind the line,
    /// short enough to refuse every real sector. The last lap stands in for it, once there is a lap
    /// after the first. Until then only the stopped-car test applies.
    /// </summary>
    private bool IsPlausible(TimeSpan elapsed, string carNumber)
    {
        if (elapsed > MaxSectorTime)
            return false;

        var car = sessionContext.GetCarByNumber(carNumber);
        if (car == null)
            return true;
        var lap = car.BestLap >= 2
            ? RaceTimeParser.Parse(car.BestTime).TotalMilliseconds
            : car.LastLapCompleted >= 2 ? RaceTimeParser.Parse(car.LastLapTime).TotalMilliseconds : 0;
        if (lap <= 0)
            return true;

        var ratio = layout.Count == 1 ? MaxSingleSectorToLapRatio : MaxSectorToLapRatio;
        return elapsed.TotalMilliseconds <= lap * ratio;
    }

    /// <summary>
    /// Forgets where cars were when the session changes. A resumed session whose number is only now
    /// becoming known, from 0 to its real one, is the same session, as <see cref="SectorProcessor"/>
    /// treats it.
    /// </summary>
    private void ResetOnNewSession()
    {
        var current = sessionContext.SessionState.SessionId;
        if (current == sessionId)
            return;

        var previous = sessionId;
        sessionId = current;
        if (previous > 0)
            transponders.Clear();
    }
}
