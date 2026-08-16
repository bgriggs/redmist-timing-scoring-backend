using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventProcessor.EventStatus.FlagData.StateChanges;
using RedMist.EventProcessor.Models;
using RedMist.TimingCommon.Models;
using System.Text.Json;

namespace RedMist.EventProcessor.EventStatus.FlagData;

public class FlagProcessor
{
    public int SessionId { get; private set; }
    private ILogger Logger { get; }
    private readonly int eventId;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly SessionContext? sessionContext;
    private readonly List<FlagDuration> flags = [];
    private readonly SemaphoreSlim flagsLock = new(1, 1);

    /// <summary>
    /// A change in the override that has not yet held for <see cref="OverrideConfirmWindow"/>, and
    /// the timing system clock reading it was first seen at. Also throttles the paths that give up
    /// after reading the log, which would otherwise re-read it for every record.
    /// </summary>
    private (bool OverrideActive, DateTime Since)? pendingOverride;

    /// <summary>
    /// How long a change in the purple override has to hold before it is written to the log. The
    /// full-course flag is taken from whichever car reported one last in a message, so it flickers
    /// either side of a real transition much as the same feed's pit readings do. The overall flag
    /// absorbs that because it is a level clients re-read; the log is a record, and a flicker
    /// written into it leaves a permanent pair of entries behind. Waiting costs no accuracy: the
    /// boundary is back-dated to when the change was first seen.
    /// </summary>
    private static readonly TimeSpan OverrideConfirmWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How soon after a caution starts the override has to become applicable for the purple to take
    /// the caution's entry over rather than split it. The timing system and Flagtronics are not
    /// synchronized, so the two arrive moments apart in whichever order; within this window they
    /// describe one flag change rather than two, and the yellow half of it is not a flag the field
    /// ever ran under. The same length as <see cref="OverrideConfirmWindow"/>, so a caution the slow
    /// zone never confirms over is also one too short to have been absorbed.
    /// </summary>
    /// <remarks>
    /// Measured from the caution to the override becoming applicable, not to the slow zone being
    /// called: a slow zone already running when the caution is called was seen at the same moment as
    /// far as the log is concerned, and takes the caution over however long it has been up. That
    /// also means a purple stuck on for an hour takes over the next caution entirely rather than
    /// leaving a sliver of it, which is the same exposure the override itself carries.
    /// </remarks>
    private static readonly TimeSpan CautionAbsorbWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How far past the entry being split the timing system's clock may read before its time of
    /// day is treated as unusable. The clock carries no date, so it is anchored to that entry and
    /// rolled forward for a session that has passed midnight; a clock that has jumped backwards
    /// instead would be rolled a full day forward and would reorder the whole list.
    /// </summary>
    private static readonly TimeSpan MaxTimingSystemClockLead = TimeSpan.FromHours(12);


    public FlagProcessor(IDbContextFactory<TsContext> tsContext, ILoggerFactory loggerFactory, SessionContext sessionContext)
    {
        this.sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        eventId = sessionContext.SessionState.EventId;
        this.tsContext = tsContext;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }

    // Legacy constructor for backward compatibility (e.g., tests)
    public FlagProcessor(int eventId, IDbContextFactory<TsContext> tsContext, ILoggerFactory loggerFactory)
    {
        this.eventId = eventId;
        this.tsContext = tsContext;
        sessionContext = null;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    public async Task<PatchUpdates?> Process(TimingMessage message)
    {
        if (sessionContext == null)
            throw new InvalidOperationException("SessionContext is required for Process method. Use the constructor that accepts SessionContext.");

        if (message.Type != Backend.Shared.Consts.FLAGS_TYPE)
            return null;

        var fs = JsonSerializer.Deserialize<List<FlagDuration>>(message.Data);
        if (fs == null)
            return null;

        await ProcessFlags(sessionContext.SessionState.SessionId, fs, sessionContext.CancellationToken);

        // A caution logged while Flagtronics is already reporting purple has to be split as soon as
        // the relay logs it, and a relay flag change may equally have ended one.
        await ApplyPurpleOverrideAsync(sessionContext.CancellationToken);

        return await PublishFlagsAsync(sessionContext.CancellationToken);
    }

    /// <summary>
    /// Brings the flag log in line with the Flagtronics purple override that
    /// <see cref="SessionContext.GetEffectiveTrackFlag"/> applies to the overall flag. RMonitor has
    /// no purple full-course flag to send, so a purple slow zone reaches the system on the
    /// Flagtronics feed alone while the relay's flag list keeps showing the yellow underneath it.
    /// The log is what the flags tab, the session results and the archive are all read from, so the
    /// caution is split there the same way the overall flag is overridden: the yellow ends where
    /// purple takes over, a Purple35 entry covers the override, and a fresh yellow entry resumes
    /// the caution when purple releases.
    /// </summary>
    /// <returns>The flag durations to publish, or null when the log was left unchanged.</returns>
    public async Task<PatchUpdates?> ReconcilePurpleOverrideAsync(CancellationToken cancellationToken)
    {
        if (!await ApplyPurpleOverrideAsync(cancellationToken))
            return null;

        return await PublishFlagsAsync(cancellationToken);
    }

    /// <summary>
    /// Writes the flag log side of the purple override, returning whether it changed anything.
    /// </summary>
    private async Task<bool> ApplyPurpleOverrideAsync(CancellationToken cancellationToken)
    {
        if (sessionContext == null)
            return false;

        // A purple effective flag can only be the override: a timing system's own purple, where one
        // exists, arrives on the relay's flag list like any other flag and needs no help here.
        var underCaution = sessionContext.RMonitorTrackFlag == Flags.Yellow;
        var overrideActive = underCaution && sessionContext.GetEffectiveTrackFlag() == Flags.Purple35;

        // The cached list belongs to the session the relay last sent flags for; until it has sent
        // this session's there is nothing to override.
        var sessionId = sessionContext.SessionState.SessionId;
        if (sessionId != SessionId)
        {
            pendingOverride = null;
            return false;
        }

        var logged = await GetFlagsAsync(cancellationToken);

        // Read from the log rather than remembered, so that a restart part-way through an override
        // picks the entry back up. An open purple entry is the override's only while the timing
        // system itself shows yellow: one logged by a timing system that sends its own purple
        // belongs to that system, and once RMonitor has left yellow the entry is the relay's next
        // flag to close, through the auto-complete in SaveAndUpdateFlagsAsync, which dates it
        // against the flag that actually replaced the caution.
        var openPurple = underCaution ? LatestOpen(logged, Flags.Purple35) : null;
        if (overrideActive == (openPurple != null))
        {
            pendingOverride = null;
            return false;
        }

        // The entry the boundary falls inside: the caution when purple takes over, the purple
        // itself when it releases.
        var split = overrideActive ? LatestOpen(logged, Flags.Yellow) : openPurple;
        if (split == null)
        {
            // Nothing to override: a purple entry with no caution under it would put a flag change
            // in the log that the timing system never made.
            Logger.LogDebug("Flagtronics reports purple with no open yellow in the flag log for event {eventId} session {sessionId}",
                eventId, sessionId);
            return false;
        }

        if (GetTimingSystemNow(split.StartTime) is not DateTime now)
            return false;

        if (pendingOverride is not { } pending || pending.OverrideActive != overrideActive)
        {
            pendingOverride = (overrideActive, now);
            return false;
        }

        if (now - pending.Since < OverrideConfirmWindow)
            return false;

        // The two systems are not synchronized, so a caution and the slow zone called with it arrive
        // moments apart in either order. A caution the slow zone took over within a few seconds was
        // never a flag anyone ran under, and logging it leaves a sliver of yellow in front of every
        // purple that reads as a flag change of its own. The purple takes the caution's entry over
        // instead of splitting it, which is also why it can start where the caution did.
        var absorbsCaution = overrideActive
            && pending.Since - split.StartTime <= CautionAbsorbWindow
            && !IsResumedCaution(logged, split);

        // Back-dated to when the change was first seen, and never dated past the clock: the relay
        // ends an entry the second before the next one begins, and an entry ahead of the timing
        // system's own flags could not be closed by an auto-complete that only looks backwards.
        var boundary = absorbsCaution ? split.StartTime : Later(pending.Since, split.StartTime.AddSeconds(1));
        if (boundary > now)
            return false;

        try
        {
            using var context = await tsContext.CreateDbContextAsync(cancellationToken);
            var dbFlags = await context.FlagLog
                .Where(f => f.EventId == eventId && f.SessionId == sessionId)
                .ToListAsync(cancellationToken);

            var splitRow = dbFlags.FirstOrDefault(f => f.Flag == split.Flag && f.StartTime == split.StartTime && f.EndTime == null);

            // Anything the relay has logged at or past the boundary owns the timeline from there,
            // and an entry written behind it would never be closed.
            if (splitRow == null || dbFlags.Any(f => f != splitRow && f.StartTime >= boundary))
            {
                // Re-confirmed before the log is read again, rather than on every record.
                pendingOverride = null;
                return false;
            }

            // Purple takes over the caution; when it releases the caution resumes, the override
            // being open only while RMonitor still shows yellow.
            var resumingFlag = overrideActive ? Flags.Purple35 : Flags.Yellow;
            if (absorbsCaution)
            {
                // The purple stands in its place, from the same moment. The relay goes on sending
                // the entry in every list it publishes; SaveAndUpdateFlagsAsync reads the start time
                // now being a purple's as the mark of an entry that was taken over.
                //
                // Starting the purple at exactly the caution's start matters beyond reading well:
                // the relay's entry stays forever new to the auto-complete in SaveAndUpdateFlagsAsync,
                // which closes the latest open entry starting strictly before it. A purple even a
                // second later would be closed and reopened by every list the relay sends.
                context.FlagLog.Remove(splitRow);
            }
            else
            {
                splitRow.EndTime = boundary.AddSeconds(-1);
            }

            await context.FlagLog.AddAsync(new FlagLog
            {
                EventId = eventId,
                SessionId = sessionId,
                Flag = resumingFlag,
                StartTime = boundary,
            }, cancellationToken);

            await context.SaveChangesAsync(cancellationToken);
            await ReloadFlagsAsync(context, sessionId, cancellationToken);
            pendingOverride = null;

            Logger.LogInformation("Flagtronics purple {change} at {boundary}: {disposition} the {splitFlag} started at {splitStart} and logged a {resumingFlag} for event {eventId} session {sessionId}",
                overrideActive ? "took over the caution" : "released", boundary,
                absorbsCaution ? "replaced" : "ended", split.Flag, split.StartTime, resumingFlag, eventId, sessionId);
            return true;
        }
        catch (DbUpdateException ex)
        {
            // Concurrent updates, as in SaveAndUpdateFlagsAsync. Cleared so the change has to be
            // re-confirmed before it is retried, rather than retried on every record.
            Logger.LogWarning(ex, "Database update exception applying the Flagtronics purple override for event {eventId} session {sessionId}",
                eventId, sessionId);
            pendingOverride = null;
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error applying the Flagtronics purple override to the flag log for event {eventId} session {sessionId}",
                eventId, sessionId);
            pendingOverride = null;
            return false;
        }
    }

    /// <summary>
    /// "Now" on the clock the flag log is kept in. The relay stamps each flag with the timing
    /// system's own wall clock rather than with UTC - a production feed was observed running three
    /// hours from UTC and the better part of an hour from the track's local time - so a purple
    /// boundary taken from any other clock would land hours away from the entries around it. The
    /// heartbeat carries a time of day with no date, so it is anchored to the entry being split.
    /// </summary>
    /// <param name="anchor">Start of the entry the new boundary falls inside.</param>
    private DateTime? GetTimingSystemNow(DateTime anchor)
    {
        var timeOfDay = RaceTimeParser.Parse(sessionContext!.SessionState.LocalTimeOfDay);
        if (timeOfDay == TimeSpan.Zero)
        {
            // No heartbeat read yet: there is no clock to place the boundary on. Midnight exactly
            // is indistinguishable from that and is given up on for the second it lasts.
            Logger.LogDebug("No usable timing system time of day for event {eventId}; leaving the flag log unchanged", eventId);
            return null;
        }

        var now = anchor.Date + timeOfDay;
        if (now < anchor)
            now = now.AddDays(1);

        if (now - anchor > MaxTimingSystemClockLead)
        {
            Logger.LogWarning("Timing system time of day {timeOfDay} cannot be placed against the flag starting {anchor} for event {eventId}; leaving the flag log unchanged",
                sessionContext.SessionState.LocalTimeOfDay, anchor, eventId);
            return null;
        }

        return now;
    }

    private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>
    /// The entry of the given flag that is still running, i.e. the latest one with no end time.
    /// </summary>
    private static FlagDuration? LatestOpen(List<FlagDuration> flagDurations, Flags flag)
        => flagDurations.Where(f => f.Flag == flag && f.EndTime == null).MaxBy(f => f.StartTime);

    /// <summary>
    /// Whether a caution entry is one this override resumed when a slow zone lifted, rather than one
    /// the timing system called. The purple it resumed from ends the second before it begins.
    /// </summary>
    /// <remarks>
    /// Only a caution the timing system called is taken over: a slow zone that comes back moments
    /// after lifting would otherwise delete the resumption and leave two purple entries with a gap
    /// between them, reading as two slow zones where the field saw its caution come back briefly.
    /// </remarks>
    private static bool IsResumedCaution(List<FlagDuration> flagDurations, FlagDuration caution)
        => flagDurations.Any(f => f.Flag == Flags.Purple35 && f.EndTime == caution.StartTime.AddSeconds(-1));

    /// <summary>
    /// Applies the current flag list to the session state and returns it for the clients, or null
    /// when they already have it.
    /// </summary>
    private async Task<PatchUpdates?> PublishFlagsAsync(CancellationToken cancellationToken)
    {
        var flagDurations = await GetFlagsAsync(cancellationToken);
        var sp = new FlagsStateChange(flagDurations).ApplySessionChange(sessionContext!.SessionState);
        return sp != null ? new PatchUpdates([sp], []) : null;
    }


    public async Task ProcessFlags(int sessionId, List<FlagDuration> fs, CancellationToken cancellationToken)
    {
        if (SessionId != sessionId)
        {
            Logger.LogDebug("SessionId changed from {s1} to {s2}. Attempting flag reload.", SessionId, sessionId);
            SessionId = sessionId;

            // An override change the previous session was part-way through says nothing about this
            // one, whose flags are a fresh list.
            pendingOverride = null;
        }

        await SaveAndUpdateFlagsAsync(sessionId, fs, cancellationToken);
    }

    public async Task<List<FlagDuration>> GetFlagsAsync(CancellationToken cancellationToken)
    {
        await flagsLock.WaitAsync(cancellationToken);
        try
        {
            return [.. flags];
        }
        finally
        {
            flagsLock.Release();
        }
    }

    public async Task SaveAndUpdateFlagsAsync(int sessionId, List<FlagDuration> fs, CancellationToken cancellationToken)
    {
        using var context = await tsContext.CreateDbContextAsync(cancellationToken);

        // Load flags that are not finished
        var dbFlags = await context.FlagLog
            .Where(f => f.EventId == eventId && f.SessionId == sessionId)
            .OrderBy(f => f.StartTime)
            .ToListAsync(cancellationToken);

        // Attempt to finish flags that lack end time
        var flagsUpdated = 0;
        foreach (var dbf in dbFlags.Where(f => f.EndTime == null).ToList())
        {
            var sourceFlag = fs.FirstOrDefault(x => x.Flag == dbf.Flag &&
                                                   x.StartTime == dbf.StartTime &&
                                                   x.EndTime.HasValue);
            if (sourceFlag != null)
            {
                dbf.EndTime = sourceFlag.EndTime;
                flagsUpdated++;
                Logger.LogDebug("Setting end time for flag {flag} from {start} to {end}", dbf.Flag, dbf.StartTime, dbf.EndTime);
            }
        }

        // Auto-complete previous flags when a new flag starts
        // This handles the case where the timing system starts a new flag without explicitly ending the previous one
        foreach (var newFlag in fs)
        {
            // Check if this is truly a new flag (not already in database)
            var isNewFlag = !dbFlags.Any(existing => existing.Flag == newFlag.Flag && existing.StartTime == newFlag.StartTime);

            if (isNewFlag)
            {
                // Find any existing flag with EndTime = NULL that started before this new flag
                var previousIncompleteFlag = dbFlags
                    .Where(dbf => dbf.EndTime == null && dbf.StartTime < newFlag.StartTime)
                    .OrderByDescending(dbf => dbf.StartTime)
                    .FirstOrDefault();

                if (previousIncompleteFlag != null)
                {
                    previousIncompleteFlag.EndTime = newFlag.StartTime;
                    flagsUpdated++;
                    Logger.LogDebug("Auto-completing previous flag {flag} started at {start} with end time {end} due to new flag {newFlag} starting",
                        previousIncompleteFlag.Flag, previousIncompleteFlag.StartTime, previousIncompleteFlag.EndTime, newFlag.Flag);
                }
            }
        }

        // Save changes to end times
        if (flagsUpdated > 0)
        {
            var saved = await context.SaveChangesAsync(cancellationToken);
            Logger.LogDebug("Saved {cnt} flag end timestamps for event {eventId} session {sessionId}",
                saved, eventId, sessionId);
        }

        // Save new flags
        var newFlagsAdded = 0;
        foreach (var f in fs)
        {
            // Check if this flag already exists in database (by Flag, StartTime only - EndTime can be updated)
            var exists = dbFlags.Any(x => x.Flag == f.Flag && x.StartTime == f.StartTime);
            if (exists)
                continue;

            // A caution the purple override replaced keeps its start time, so a start time that now
            // belongs to a purple is one that was taken over rather than a flag change still to be
            // logged. The relay sends its whole list on every change and would otherwise put the
            // caution back beside the purple that stands in its place. Read from the log rather than
            // remembered, so a restart cannot resurrect it either.
            if (f.Flag == Flags.Yellow && dbFlags.Any(x => x.Flag == Flags.Purple35 && x.StartTime == f.StartTime))
            {
                Logger.LogDebug("Skipping the caution at {start} for event {eventId} session {sessionId}: the purple override stands in its place",
                    f.StartTime, eventId, sessionId);
                continue;
            }

            try
            {
                var dbFlag = new FlagLog
                {
                    EventId = eventId,
                    SessionId = sessionId,
                    Flag = f.Flag,
                    StartTime = f.StartTime,
                    EndTime = f.EndTime
                };
                await context.FlagLog.AddAsync(dbFlag, cancellationToken);
                newFlagsAdded++;
                Logger.LogDebug("Adding new flag {flag} for event {eventId} session {sessionId} from {start} to {end}",
                    f.Flag, eventId, sessionId, f.StartTime, f.EndTime);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error preparing flag {flag} for event {eventId} session {sessionId}",
                    f.Flag, eventId, sessionId);
            }
        }

        // Save new flags in batch
        if (newFlagsAdded > 0)
        {
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                Logger.LogDebug("Saved {cnt} new flags for event {eventId} session {sessionId}",
                    newFlagsAdded, eventId, sessionId);
            }
            catch (DbUpdateException ex)
            {
                Logger.LogWarning(ex, "Database update exception when saving {cnt} new flags for event {eventId} session {sessionId}. This may be due to concurrent updates.",
                    newFlagsAdded, eventId, sessionId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unexpected error saving {cnt} new flags for event {eventId} session {sessionId}",
                    newFlagsAdded, eventId, sessionId);
            }
        }

        await ReloadFlagsAsync(context, sessionId, cancellationToken);
    }

    private async Task ReloadFlagsAsync(TsContext context, int sessionId, CancellationToken cancellationToken)
    {
        // Reload flags from database
        Logger.LogDebug("Reloading flags for event {eventId} session {sessionId}", eventId, sessionId);
        var reloadedFlags = await context.FlagLog
            .Where(f => f.EventId == eventId && f.SessionId == sessionId)
            .ToListAsync(cancellationToken: cancellationToken);

        await flagsLock.WaitAsync(cancellationToken);
        try
        {
            flags.Clear();
            flags.AddRange(ToFlagsDuration(reloadedFlags));
            Logger.LogDebug("Loaded {cnt} flags into memory for event {eventId} session {sessionId}",
                flags.Count, eventId, sessionId);
        }
        finally
        {
            flagsLock.Release();
        }
    }

    private static List<FlagDuration> ToFlagsDuration(List<FlagLog> dbFlags)
    {
        var flags = new List<FlagDuration>();
        foreach (var dbf in dbFlags)
        {
            flags.Add(new FlagDuration
            {
                Flag = dbf.Flag,
                StartTime = dbf.StartTime,
                EndTime = dbf.EndTime
            });
        }
        return flags;
    }
}
