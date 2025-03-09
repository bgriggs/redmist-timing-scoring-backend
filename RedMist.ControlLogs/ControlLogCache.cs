using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RedMist.Database;
using RedMist.TimingCommon.Models;

namespace RedMist.ControlLogs;

public class ControlLogCache
{
    private readonly int eventId;
    private ILogger Logger { get; }
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly IControlLogFactory controlLogFactory;
    private readonly Dictionary<string, List<ControlLogEntry>> controlLogCache = [];
    private readonly SemaphoreSlim cacheLock = new(1, 1);


    public ControlLogCache(int eventId, ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IControlLogFactory controlLogFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.eventId = eventId;
        this.tsContext = tsContext;
        this.controlLogFactory = controlLogFactory;
    }


    public async Task<List<string>> RequestControlLogChanges(CancellationToken stoppingToken = default)
    {
        await cacheLock.WaitAsync(stoppingToken);
        try
        {
            Logger.LogDebug($"Checking control log for event {eventId}");
            using var db = await tsContext.CreateDbContextAsync(stoppingToken);
            var org = await db.Events.Where(db => db.Id == eventId)
                .Join(db.Organizations, e => e.OrganizationId, o => o.Id, (e, o) => new { e, o })
                .Select(x => x.o)
                .FirstOrDefaultAsync(stoppingToken);
            if (org != null && !string.IsNullOrEmpty(org.ControlLogType))
            {
                var controlLog = controlLogFactory.CreateControlLog(org.ControlLogType);
                var logEntries = await controlLog.LoadControlLogAsync(org.ControlLogParams, stoppingToken);

                var oldLogs = controlLogCache.ToDictionary(x => x.Key, x => x.Value);
                var car1Grp = logEntries.GroupBy(x => x.Car1);
                var car2Grp = logEntries.GroupBy(x => x.Car2);

                controlLogCache.Clear();
                foreach (var l in car1Grp)
                {
                    controlLogCache[l.Key.ToLower()] = [.. l];
                }

                foreach (var l in car2Grp)
                {
                    if (!controlLogCache.TryGetValue(l.Key, out List<ControlLogEntry>? value))
                    {
                        controlLogCache[l.Key.ToLower()] = [.. l];
                    }
                    else
                    {
                        value.AddRange(l);
                    }
                }

                var changes = GetChangedCars(oldLogs, controlLogCache);
                return changes;
            }
            else if (org == null)
            {
                Logger.LogWarning($"No event {eventId} found for Control Log in database.");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading control log for event {0}", eventId);
        }
        finally
        {
            cacheLock.Release();
        }

        return [];
    }

    private List<string> GetChangedCars(Dictionary<string, List<ControlLogEntry>> old, Dictionary<string, List<ControlLogEntry>> @new)
    {
        var changedCars = new List<string>();

        foreach (var car in @new.Keys)
        {
            if (!old.TryGetValue(car, out List<ControlLogEntry>? oldEntries))
            {
                changedCars.Add(car);
            }
            else
            {
                var newEntries = @new[car];
                if (oldEntries.Count != newEntries.Count)
                {
                    changedCars.Add(car);
                }
                else
                {
                    foreach (var ne in newEntries)
                    {
                        var oe = oldEntries.FirstOrDefault(x => x.OrderId == ne.OrderId);
                        if (oe != null)
                        {
                            var changed = CompareControlLogEntries(oe, ne);
                            if (changed)
                            {
                                changedCars.Add(car);
                            }
                        }
                        else
                        {
                            changedCars.Add(car);
                        }
                    }
                }
            }
        }
        foreach (var car in old.Keys)
        {
            if (!@new.ContainsKey(car))
            {
                changedCars.Add(car);
            }
        }
        return changedCars;
    }

    private static bool CompareControlLogEntries(ControlLogEntry old, ControlLogEntry @new)
    {
        if (old.OrderId != @new.OrderId)
            return false;
        if (old.Car1 != @new.Car1)
            return false;
        if (old.Car2 != @new.Car2)
            return false;
        if (old.Timestamp != @new.Timestamp)
            return false;
        if (old.Status != @new.Status)
            return false;
        if (old.Corner != @new.Corner)
            return false;
        if (old.Note != @new.Note)
            return false;
        if (old.OtherNotes != @new.OtherNotes)
            return false;
        return true;
    }

    public async Task<Dictionary<string, List<ControlLogEntry>>> GetCarControlEntries(string[] cars)
    {
        Dictionary<string, List<ControlLogEntry>> entries = [];
        await cacheLock.WaitAsync();
        try
        {
            foreach (var car in cars)
            {
                if (controlLogCache.TryGetValue(car, out List<ControlLogEntry>? value))
                {
                    entries[car] = value;
                }
            }
        }
        finally
        {
            cacheLock.Release();
        }
        return entries;
    }
}
