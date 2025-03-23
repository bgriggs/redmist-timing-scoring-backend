using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using RedMist.TimingCommon.Models.X2;

namespace RedMist.TimingAndScoringService.EventStatus.X2;

public class PitProcessor
{
    private ILogger Logger { get; }
    private readonly int eventId;
    private readonly IDbContextFactory<TsContext> tsContext;

    private readonly Dictionary<uint, Passing> inPit = [];
    private readonly Dictionary<uint, Passing> pitEntrance = [];
    private readonly Dictionary<uint, Passing> pitExit = [];
    private readonly Dictionary<uint, Passing> pitSf = [];
    private readonly Dictionary<uint, Passing> pitOther = [];
    private readonly Dictionary<uint, Passing> other = [];
    private readonly Lock passingLock = new();

    private TimingCommon.Models.Configuration.Event? eventConfiguration;
    private readonly Lock eventConfigurationLock = new();
    private DateTime lastEventLoopMetadataRefresh = DateTime.MinValue;


    public PitProcessor(int eventId, IDbContextFactory<TsContext> tsContext, ILoggerFactory loggerFactory)
    {
        this.eventId = eventId;
        this.tsContext = tsContext;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    public void UpdatePassings(List<Passing> passings)
    {
        // Initialize loop configuration
        var loopMetadata = GetLoopMetadata();

        lock (passingLock)
        {
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
        }

        RefreshEventLoopMetadata();
    }

    private Dictionary<uint, LoopMetadata> GetLoopMetadata()
    {
        var loopMetadata = new Dictionary<uint, LoopMetadata>();
        lock (eventConfigurationLock)
        {
            if (eventConfiguration != null)
            {
                foreach (var loop in eventConfiguration.LoopsMetadata)
                {
                    loopMetadata[loop.Id] = loop;
                }
            }
        }

        return loopMetadata;
    }

    private void RefreshEventLoopMetadata()
    {
        Task.Run(async () =>
        {
            if (DateTime.Now - lastEventLoopMetadataRefresh > TimeSpan.FromMinutes(3))
            {
                try
                {
                    using var db = await tsContext.CreateDbContextAsync();
                    var config = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId);
                    lock (eventConfigurationLock)
                    {
                        eventConfiguration = config;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error refreshing event loop metadata");
                }

                lastEventLoopMetadataRefresh = DateTime.Now;
            }
        });
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

    public void ApplyTransponderPassing(CarPosition[] carPositions)
    {
        var loopMetadata = GetLoopMetadata();

        lock (passingLock)
        {
            foreach (var pos in carPositions)
            {
                //ClearPositionLoopData(pos);

                if (inPit.TryGetValue(pos.TransponderId, out _))
                {
                    pos.IsInPit = true;
                }
                
                if (pitEntrance.TryGetValue(pos.TransponderId, out _))
                {
                    pos.IsEnteredPit = true;
                    pos.IsInPit = true;
                }
                
                if (pitExit.TryGetValue(pos.TransponderId, out _))
                {
                    pos.IsExistedPit = true;
                    pos.IsInPit = true;
                }

                if (pitSf.TryGetValue(pos.TransponderId, out _))
                {
                    pos.IsPitStartFinish = true;
                    pos.IsInPit = true;
                }

                if (pitOther.TryGetValue(pos.TransponderId, out _))
                {
                    pos.IsInPit = true;
                }

                if (other.TryGetValue(pos.TransponderId, out var otherPass) && loopMetadata.TryGetValue(otherPass.Id, out var lm))
                {
                    pos.LastLoopName = lm.Name;
                }
            }
        }
    }

    private static void ClearPositionLoopData(CarPosition pos)
    {
        pos.IsInPit = false;
        pos.IsEnteredPit = false;
        pos.IsExistedPit = false;
        pos.IsPitStartFinish = false;
        pos.LastLoopName = string.Empty;
    }
}
