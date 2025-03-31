using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.TimingCommon.Models;
using System.Threading;

namespace RedMist.TimingAndScoringService.EventStatus;

public class FlagProcessor
{
    public int SessionId { get; private set; }
    private ILogger Logger { get; }
    private readonly int eventId;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly List<FlagDuration> flags = [];
    private readonly SemaphoreSlim flagsLock = new(1, 1);


    public FlagProcessor(int eventId, IDbContextFactory<TsContext> tsContext, ILoggerFactory loggerFactory)
    {
        this.eventId = eventId;
        this.tsContext = tsContext;
        Logger = loggerFactory.CreateLogger(GetType().Name);
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
            .Where(f => f.EventId == eventId && f.SessionId == SessionId)
            .ToListAsync(cancellationToken);

        // Attempt to finish flags that lack end time
        foreach (var dbf in dbFlags.Where(f => f.EndTime == null).ToList())
        {
            var sourceFlag = fs.FirstOrDefault(x => x.Flag == dbf.Flag && dbf.StartTime == x.StartTime && x.EndTime != null);
            if (sourceFlag != null)
            {
                dbf.EndTime = sourceFlag.EndTime;
            }
        }
        // Save changes to end times
        var saved = await context.SaveChangesAsync(cancellationToken);
        Logger.LogDebug("Saving {cnt} flag end timestamps for event {eventId} session {sessionId}", saved, eventId, sessionId);

        // Save new flags
        foreach (var f in fs)
        {
            var exists = dbFlags.Any(x => x.Flag == f.Flag && x.StartTime == f.StartTime && x.EndTime == f.EndTime);
            if (exists)
                continue;

            using var db = await tsContext.CreateDbContextAsync(cancellationToken);
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
                await db.FlagLog.AddAsync(dbFlag, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                Logger.LogDebug("Saved new flag {flag} for event {eventId} session {sessionId}", f.Flag, eventId, sessionId);
            }
            catch (DbUpdateException) { }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error saving flag {flag} for event {eventId} session {sessionId}", f.Flag, eventId, sessionId);
            }
        }

        // Load full set of flags
        using var db2 = await tsContext.CreateDbContextAsync(cancellationToken);
        Logger.LogInformation("Reloading flags for event {eventId} session {sessionId}", eventId, sessionId);
        dbFlags = await db2.FlagLog
            .Where(f => f.EventId == eventId && f.SessionId == SessionId)
            .ToListAsync(cancellationToken: cancellationToken);

        await flagsLock.WaitAsync(cancellationToken);
        try
        {
            flags.Clear();
            flags.AddRange(ToFlagsDuration(dbFlags));
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
