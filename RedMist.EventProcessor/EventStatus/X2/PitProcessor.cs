using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus.X2.StateChanges;
using RedMist.EventProcessor.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using RedMist.TimingCommon.Models.X2;
using System.Collections.Immutable;
using System.Text.Json;

namespace RedMist.EventProcessor.EventStatus.X2;

/// <summary>
/// Processes X2 loop passings to determine pit status of cars.
/// </summary>
public class PitProcessor
{
    private ILogger Logger { get; }
    private int eventId;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly SessionContext sessionContext;
    private TimingCommon.Models.Configuration.Event? eventConfiguration;
    private readonly Dictionary<string, HashSet<int>> carLapsWithPitStops = [];

    /// <summary>
    /// Guards <see cref="carLapsWithPitStops"/>, which PitStateUpdate mutates on the pipeline
    /// thread while <see cref="WasPitLap"/> reads it from the lap-logging background thread.
    /// Needed since per-car ownership: this map used to stay empty whenever in-car telemetry was
    /// running, so there was no concurrent writer.
    /// </summary>
    private readonly object pitLapsLock = new();
    private readonly Dictionary<uint, Passing> inPit = [];
    private readonly Dictionary<uint, Passing> pitEntrance = [];
    private readonly Dictionary<uint, Passing> pitExit = [];
    private readonly Dictionary<uint, Passing> pitSf = [];
    private readonly Dictionary<uint, Passing> pitOther = [];
    private readonly Dictionary<uint, Passing> other = [];
    private int lastSessionId = -1;

    /// <summary>
    /// Callback to notify when pit messages are received for specific cars
    /// This allows the lap processor to immediately process any pending laps for these cars
    /// </summary>
    public Func<HashSet<string>, Task>? NotifyLapProcessorOfPitMessages { get; set; }

    public PitProcessor(IDbContextFactory<TsContext> tsContext, ILoggerFactory loggerFactory, SessionContext sessionContext)
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

    private bool HasAnyPassing(uint transponderId) =>
        inPit.ContainsKey(transponderId) || pitEntrance.ContainsKey(transponderId)
        || pitExit.ContainsKey(transponderId) || pitSf.ContainsKey(transponderId)
        || pitOther.ContainsKey(transponderId) || other.ContainsKey(transponderId);

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

        // Flagtronics is no longer an all-or-nothing gate here. Loop state is tracked for every
        // car regardless, so a car handed back by a failing in-car device has usable history
        // waiting; only the decision to publish is taken per car, further down.

        // Check for session change and clear out old data
        if (lastSessionId != sessionContext.SessionState.SessionId)
        {
            Logger.LogInformation("Session changed from {LastSessionId} to {CurrentSessionId}, clearing pit processor state",
                lastSessionId, sessionContext.SessionState.SessionId);
            lock (pitLapsLock)
            {
                carLapsWithPitStops.Clear();
            }
            inPit.Clear();
            pitEntrance.Clear();
            pitExit.Clear();
            pitSf.Clear();
            pitOther.Clear();
            other.Clear();
            lastSessionId = sessionContext.SessionState.SessionId;
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

            // A car whose in-car pit data is trusted is owned by Flagtronics; publishing loop
            // state for it as well would have the two sources fight over IsInPit.
            if (sessionContext.IsFlagtronicsPitTrusted(carNumber))
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

            CarPositionPatch? p;
            lock (pitLapsLock)
            {
                p = change.ApplyCarChange(sessionContext);
            }
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
        if (sessionContext.IsFlagtronicsPitTrusted(number))
        {
            // Flagtronics in-car pit data is trusted for this car and takes precedence
            return null;
        }

        // Nothing has ever been seen for this transponder, so there is no basis to say anything
        // about its pit state. Publishing anyway would drive every field false and clear the pit
        // badge of a car handed over in an event with no X2 coverage.
        var carPosition = sessionContext.GetCarByNumber(number);
        if (carPosition != null && !HasAnyPassing(carPosition.TransponderId))
            return null;

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

        lock (pitLapsLock)
        {
            return change.ApplyCarChange(sessionContext);
        }
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

    /// <summary>
    /// Whether this source recorded the given lap as including a pit stop. A query rather than a
    /// stamp, so the caller can take the union across sources: pit ownership can move mid-session,
    /// leaving each source with only part of a car's history, and a source that writes its own
    /// answer wholesale erases the other's.
    ///
    /// Called from the lap processor's background logging loop, hence <see cref="pitLapsLock"/>.
    /// </summary>
    public bool WasPitLap(string? carNumber, int lapNumber)
    {
        if (string.IsNullOrEmpty(carNumber))
            return false;

        lock (pitLapsLock)
        {
            return carLapsWithPitStops.TryGetValue(carNumber, out var laps) && laps.Contains(lapNumber);
        }
    }
}
