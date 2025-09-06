using Microsoft.EntityFrameworkCore;
using RedMist.Database;
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
    private readonly Dictionary<uint, Passing> inPit = [];
    private readonly Dictionary<uint, Passing> pitEntrance = [];
    private readonly Dictionary<uint, Passing> pitExit = [];
    private readonly Dictionary<uint, Passing> pitSf = [];
    private readonly Dictionary<uint, Passing> pitOther = [];
    private readonly Dictionary<uint, Passing> other = [];


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

    private ImmutableDictionary<uint, LoopMetadata> GetLoopMetadata()
    {
        var loopMetadata = new Dictionary<uint, LoopMetadata>();
        if (eventConfiguration != null)
        {
            foreach (var loop in eventConfiguration.LoopsMetadata)
            {
                loopMetadata[loop.Id] = loop;
            }
        }

        return loopMetadata.ToImmutableDictionary();
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

    public async Task<SessionStateUpdate?> Process(TimingMessage message)
    {
        // Handle event configuration change notifications
        if (message.Type == "event-changed")
        {
            await RefreshEventConfiguration(message);
            return null;
        }

        if (message.Type != "x2pass")
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
        }

        var change = new PitStateUpdate(
            CarLapsWithPitStops: carLapsWithPitStops,
            InPit: inPit.ToImmutableDictionary(),
            PitEntrance: pitEntrance.ToImmutableDictionary(),
            PitExit: pitExit.ToImmutableDictionary(),
            PitSf: pitSf.ToImmutableDictionary(),
            PitOther: pitOther.ToImmutableDictionary(),
            Other: other.ToImmutableDictionary(),
            LoopMetadata: loopMetadata);
        return new SessionStateUpdate([], [change]);
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
