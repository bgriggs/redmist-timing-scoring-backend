using Microsoft.EntityFrameworkCore;
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
        if (fs != null)
        {
            await ProcessFlags(sessionContext.SessionState.SessionId, fs, sessionContext.CancellationToken);
            var flagDurations = await GetFlagsAsync(sessionContext.CancellationToken);
            var flagChange = new FlagsStateChange(flagDurations);
            var sp = flagChange.ApplySessionChange(sessionContext.SessionState);
            if (sp != null)
                return new PatchUpdates([sp], []);
        }
        return null;
    }


    public async Task ProcessFlags(int sessionId, List<FlagDuration> fs, CancellationToken cancellationToken)
    {
        if (SessionId != sessionId)
        {
            Logger.LogDebug("SessionId changed from {s1} to {s2}. Attempting flag reload.", SessionId, sessionId);
            SessionId = sessionId;
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
