using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileSystemGlobbing;
using RedMist.Database;
using RedMist.Migrations;
using RedMist.TimingAndScoringService.EventStatus.X2.StateChanges;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using RedMist.TimingCommon.Models.X2;
using System.Collections.Immutable;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.EventStatus.X2;

/// <summary>
/// Processes X2 loop passings to determine pit status of cars.
/// </summary>
public class PitProcessorV2
{
    private ILogger Logger { get; }
    private int eventId;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly SessionContext sessionContext;
    private TimingCommon.Models.Configuration.Event? eventConfiguration;
    private readonly Dictionary<string, HashSet<int>> carLapsWithPitStops = [];
    public readonly Dictionary<uint, Passing> inPit = [];
    private readonly Dictionary<uint, Passing> pitEntrance = [];
    private readonly Dictionary<uint, Passing> pitExit = [];
    private readonly Dictionary<uint, Passing> pitSf = [];
    private readonly Dictionary<uint, Passing> pitOther = [];
    private readonly Dictionary<uint, Passing> other = [];

    /// <summary>
    /// Callback to notify when pit messages are received for specific cars
    /// This allows the lap processor to immediately process any pending laps for these cars
    /// </summary>
    public Func<HashSet<string>, Task>? NotifyLapProcessorOfPitMessages { get; set; }

    public PitProcessorV2(IDbContextFactory<TsContext> tsContext, ILoggerFactory loggerFactory, SessionContext sessionContext)
    {
        this.tsContext = tsContext ?? throw new ArgumentNullException(nameof(tsContext));
        Logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger(GetType().Name);
        this.sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
    }

    public async Task Initialize(int eventId)
    {
        this.eventId = eventId;
        eventConfiguration = await LoadEventLoopMetadata();
    }

    private async Task<TimingCommon.Models.Configuration.Event?> LoadEventLoopMetadata()
    {
        try
        {
            using var db = await tsContext.CreateDbContextAsync();
            return await db.Events.FirstOrDefaultAsync(e => e.Id == eventId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error refreshing event loop metadata");
        }

        return null;
    }

    private Dictionary<uint, LoopMetadata> GetLoopMetadata()
    {
        var loopMetadata = new Dictionary<uint, LoopMetadata>();
        if (eventConfiguration != null)
        {
            foreach (var loop in eventConfiguration.LoopsMetadata)
            {
                loopMetadata[loop.Id] = loop;
            }
        }

        return loopMetadata;
    }

    private void RemoveTransponderFromAllPassings(uint transponderId)
    {
        inPit.Remove(transponderId);
        pitEntrance.Remove(transponderId);
        pitExit.Remove(transponderId);
        pitOther.Remove(transponderId);
        pitSf.Remove(transponderId);
        other.Remove(transponderId);
    }

    public async Task<PatchUpdates?> Process(TimingMessage message)
    {
        // Handle event configuration change notifications
        if (message.Type == Backend.Shared.Consts.EVENT_CONFIGURATION_CHANGED)
        {
            await RefreshEventConfiguration(message);
            return null;
        }

        if (message.Type != Backend.Shared.Consts.X2PASS_TYPE)
            return null;

        if (sessionContext.IsMultiloopActive)
        {
            // Multiloop is active, do not process X2 pit data
            return null;
        }

        List<Passing>? passings;
        try
        {
            passings = JsonSerializer.Deserialize<List<Passing>>(message.Data);
        }
        catch (JsonException)
        {
            // Return null for invalid JSON data
            return null;
        }

        if (passings == null || passings.Count == 0)
            return null;

        var loopMetadata = GetLoopMetadata();
        var patches = new List<CarPositionPatch>();
        var carsWithPitMessages = new HashSet<string>();

        foreach (var pass in passings)
        {
            RemoveTransponderFromAllPassings(pass.TransponderId);

            var carNum = sessionContext.GetCarNumberForTransponder(pass.TransponderId);
            if (carNum != null)
            {
                var car = sessionContext.GetCarByNumber(carNum);
                if (car != null)
                {
                    if (pass.IsInPit)
                        Logger.LogInformation("**** Pass InPit Number {n}", car.Number);
                    else
                        Logger.LogTrace("Passing for car {c}", car.Number);
                }
            }

            if (pass.IsInPit)
            {
                inPit[pass.TransponderId] = pass;
            }

            if (loopMetadata.TryGetValue(pass.LoopId, out var lm))
            {
                if (lm.Type == LoopType.PitIn)
                {
                    pitEntrance[pass.TransponderId] = pass;
                }
                else if (lm.Type == LoopType.PitExit)
                {
                    pitExit[pass.TransponderId] = pass;
                }
                else if (lm.Type == LoopType.PitStartFinish)
                {
                    pitSf[pass.TransponderId] = pass;
                }
                else if (lm.Type == LoopType.PitOther)
                {
                    pitOther[pass.TransponderId] = pass;
                }
                else if (lm.Type == LoopType.Other)
                {
                    other[pass.TransponderId] = pass;
                }
            }

            var carNumber = sessionContext.GetCarNumberForTransponder(pass.TransponderId);
            if (carNumber == null)
                continue;

            var change = new PitStateUpdate(
                carNumber,
                CarLapsWithPitStops: carLapsWithPitStops,
                InPit: inPit,
                PitEntrance: pitEntrance,
                PitExit: pitExit,
                PitSf: pitSf,
                PitOther: pitOther,
                Other: other,
                LoopMetadata: loopMetadata);

            var p = change.ApplyCarChange(sessionContext);
            if (p != null)
            {
                patches.Add(p);

                if (p.IsInPit ?? false)
                    carsWithPitMessages.Add(carNumber);
            }
        }

        // Notify lap processor about cars that received pit messages
        // This allows immediate processing of any pending laps for these cars
        if (carsWithPitMessages.Count > 0 && NotifyLapProcessorOfPitMessages != null)
        {
            try
            {
                await NotifyLapProcessorOfPitMessages(carsWithPitMessages);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error notifying lap processor of pit messages for cars: {Cars}", 
                    string.Join(", ", carsWithPitMessages));
            }
        }
        
        return new PatchUpdates([], [.. patches]);
    }

    public CarPositionPatch? ProcessCar(string number)
    {
        var loopMetadata = GetLoopMetadata();
        var change = new PitStateUpdate(
                number,
                CarLapsWithPitStops: carLapsWithPitStops,
                InPit: inPit,
                PitEntrance: pitEntrance,
                PitExit: pitExit,
                PitSf: pitSf,
                PitOther: pitOther,
                Other: other,
                LoopMetadata: loopMetadata);

        return change.ApplyCarChange(sessionContext);
    }

    private async Task RefreshEventConfiguration(TimingMessage message)
    {
        if (int.TryParse(message.Data, out var configChangedEventId) && configChangedEventId == eventId)
        {
            Logger.LogInformation("Event configuration changed for event {EventId}, reloading...", eventId);
            eventConfiguration = await LoadEventLoopMetadata();
        }
    }

    public ImmutableDictionary<string, ImmutableHashSet<int>> GetCarLapsWithPitStops()
    {
        return carLapsWithPitStops.ToImmutableDictionary(entry => entry.Key, entry => entry.Value.ToImmutableHashSet());
    }

    public void UpdateCarPositionForLogging(CarPosition carPosition)
    {
        if (carPosition.Number != null && carLapsWithPitStops.TryGetValue(carPosition.Number, out var laps) && laps != null)
        {
            carPosition.LapIncludedPit = laps.Contains(carPosition.LastLapCompleted);
        }
    }
}
