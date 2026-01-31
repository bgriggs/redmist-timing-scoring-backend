using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RedMist.Database;
using RedMist.TimingCommon.Models;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RedMist.ControlLogs;

public partial class ControlLogCache : IDisposable
{
    private readonly int eventId;
    private ILogger Logger { get; }
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly IControlLogFactory controlLogFactory;
    private readonly Dictionary<string, List<ControlLogEntry>> controlLogCache = [];
    private Dictionary<string, (int warnings, int laps)> penalityCounts = [];
    private readonly SemaphoreSlim cacheLock = new(1, 1);
    private bool disposed;


    public ControlLogCache(int eventId, ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IControlLogFactory controlLogFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.eventId = eventId;
        this.tsContext = tsContext;
        this.controlLogFactory = controlLogFactory;
    }


    public async Task<List<string>> RequestControlLogChangesAsync(CancellationToken stoppingToken = default)
    {
        //var sw = Stopwatch.StartNew();
        await cacheLock.WaitAsync(stoppingToken);
        //Logger.LogInformation("cacheLock.WaitAsync in {t}ms", sw.ElapsedMilliseconds);
        try
        {
            Logger.LogDebug("Checking control log for event {eventId} cached size {controlLogCacheCount}", eventId, controlLogCache.Count);
            using var db = await tsContext.CreateDbContextAsync(stoppingToken);
            var orgId = await db.Events
                .Where(e => e.Id == eventId)
                .Select(e => e.OrganizationId)
                .FirstOrDefaultAsync(stoppingToken);

            var org = orgId != default
                ? await db.Organizations.FirstOrDefaultAsync(o => o.Id == orgId, stoppingToken)
                : null;
            //Logger.LogInformation("DB load in {t}ms", sw.ElapsedMilliseconds);

            if (org != null && !string.IsNullOrEmpty(org.ControlLogType))
            {
                penalityCounts.Clear();
                var controlLog = controlLogFactory.CreateControlLog(org.ControlLogType);
                var logEntries = await controlLog.LoadControlLogAsync(org.ControlLogParams, stoppingToken);
                var logsList = logEntries.logs.ToList();
                Logger.LogDebug("Control log loaded for event {eventId} with {Count} entries", eventId, logsList.Count);
                //Logger.LogInformation("LoadControlLogAsync in {t}ms", sw.ElapsedMilliseconds);
                var oldLogs = controlLogCache.ToDictionary(x => x.Key, x => x.Value);
                var car1Grp = logsList.GroupBy(x => x.Car1);
                var car2Grp = logsList.GroupBy(x => x.Car2);

                controlLogCache.Clear();
                foreach (var l in car1Grp)
                {
                    if (l.Key != null)
                    {
                        controlLogCache[l.Key.ToLower()] = [.. l];
                    }
                }

                foreach (var l in car2Grp)
                {
                    if (l.Key != null)
                    {
                        if (!controlLogCache.TryGetValue(l.Key.ToLower(), out List<ControlLogEntry>? value))
                        {
                            controlLogCache[l.Key.ToLower()] = [.. l];
                        }
                        else
                        {
                            value.AddRange(l);
                        }
                    }
                }

                // Entries not associated with a car
                foreach (var entry in logEntries.logs)
                {
                    if (string.IsNullOrWhiteSpace(entry.Car1) && string.IsNullOrWhiteSpace(entry.Car2))
                    {
                        if (!controlLogCache.TryGetValue(string.Empty, out List<ControlLogEntry>? value))
                        {
                            controlLogCache[string.Empty] = [entry];
                        }
                        else
                        {
                            value.Add(entry);
                        }
                    }
                }

                // Determine the number of penalties (laps and warnings)
                penalityCounts = GetWarningsAndPenalties(controlLogCache);
                //Logger.LogInformation("GetWarningsAndPenalties in {t}ms", sw.ElapsedMilliseconds);
                var changes = GetChangedCars(oldLogs, controlLogCache);
                //Logger.LogInformation("GetChangedCars in {t}ms", sw.ElapsedMilliseconds);
                return changes;
            }
            else if (org == null)
            {
                Logger.LogWarning("No event {eventId} found for Control Log in database.", eventId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading control log for event {eventId}", eventId);
        }
        finally
        {
            cacheLock.Release();
        }

        return [];
    }

    private static List<string> GetChangedCars(Dictionary<string, List<ControlLogEntry>> old, Dictionary<string, List<ControlLogEntry>> @new)
    {
        var changedCars = new HashSet<string>();

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
                    bool hasChanges = false;
                    foreach (var ne in newEntries)
                    {
                        var oe = oldEntries.FirstOrDefault(x => x.OrderId == ne.OrderId);
                        if (oe == null || !AreEntriesEqual(oe, ne))
                        {
                            hasChanges = true;
                            break;
                        }
                    }
                    if (hasChanges)
                    {
                        changedCars.Add(car);
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
        return [.. changedCars];
    }

    private static bool AreEntriesEqual(ControlLogEntry old, ControlLogEntry @new)
    {
        return old.OrderId == @new.OrderId
            && old.Car1 == @new.Car1
            && old.Car2 == @new.Car2
            && old.Timestamp == @new.Timestamp
            && old.Status == @new.Status
            && old.Corner == @new.Corner
            && old.Note == @new.Note
            && old.OtherNotes == @new.OtherNotes;
    }

    public async Task<Dictionary<string, List<ControlLogEntry>>> GetCarControlEntriesAsync(string[]? cars = null)
    {
        Dictionary<string, List<ControlLogEntry>> entries = [];
        await cacheLock.WaitAsync();
        try
        {
            if (cars == null || cars.Length == 0)
            {
                return controlLogCache.ToDictionary(x => x.Key, x => x.Value.ToList());
            }

            foreach (var car in cars)
            {
                if (controlLogCache.TryGetValue(car.ToLower(), out List<ControlLogEntry>? value))
                {
                    entries[car] = [.. value];
                }
            }
        }
        finally
        {
            cacheLock.Release();
        }
        return entries;
    }

    public async Task<List<ControlLogEntry>> GetControlEntriesAsync()
    {
        await cacheLock.WaitAsync();
        try
        {
            return [.. controlLogCache.Values.SelectMany(x => x)];
        }
        finally
        {
            cacheLock.Release();
        }
    }

    /// <summary>
    /// Parse through the penalty column and count the number of warnings and laps.
    /// </summary>
    public static Dictionary<string, (int warnings, int laps)> GetWarningsAndPenalties(Dictionary<string, List<ControlLogEntry>> logs)
    {
        var results = new Dictionary<string, (int warnings, int laps)>();
        var warningRegex = WarningRegex();
        var lapPenaltyRegex = LapPenaltyRegex();

        foreach (var car in logs)
        {
            if (string.IsNullOrWhiteSpace(car.Key))
            {
                continue;
            }
            int laps = 0;
            int warnings = 0;
            foreach (var entry in car.Value)
            {
                if (string.IsNullOrWhiteSpace(entry.PenaltyAction))
                {
                    continue;
                }

                var isWarning = warningRegex.IsMatch(entry.PenaltyAction);
                if (isWarning && ApplyToCar(car.Key, entry))
                {
                    warnings++;
                    continue;
                }
                var lapPenalties = lapPenaltyRegex.Match(entry.PenaltyAction);
                if (lapPenalties.Success && ApplyToCar(car.Key, entry))
                {
                    laps += int.Parse(lapPenalties.Groups[1].Value);
                }
            }
            results[car.Key] = (warnings, laps);
        }
        return results;
    }

    [GeneratedRegex(@".*Warning.*", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex WarningRegex();

    [GeneratedRegex(@"(\d+)\s+(Lap|Laps)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex LapPenaltyRegex();

    /// <summary>
    /// When there are two cars, check if the car is highlighted to determine who to apply the penalty to.
    /// </summary>
    private static bool ApplyToCar(string car, ControlLogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Car2))
        {
            return true;
        }
        if (car.Equals(entry.Car1, StringComparison.OrdinalIgnoreCase) && entry.IsCar1Highlighted)
        {
            return true;
        }
        if (car.Equals(entry.Car2, StringComparison.OrdinalIgnoreCase) && entry.IsCar2Highlighted)
        {
            return true;
        }
        // Default to car 1 if no highlighting is specified
        if (!entry.IsCar1Highlighted && !entry.IsCar2Highlighted && car.Equals(entry.Car1, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    public async Task<Dictionary<string, (int warnings, int laps)>> GetPenaltiesAsync(CancellationToken stoppingToken = default)
    {
        await cacheLock.WaitAsync(stoppingToken);
        try
        {
            return penalityCounts.ToDictionary();
        }
        finally
        {
            cacheLock.Release();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposed)
        {
            if (disposing)
            {
                cacheLock?.Dispose();
            }
            disposed = true;
        }
    }
}
