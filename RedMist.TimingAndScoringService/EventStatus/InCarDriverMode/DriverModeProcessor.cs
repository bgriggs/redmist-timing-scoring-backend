using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Hubs;
using RedMist.Database;
using RedMist.TimingAndScoringService.EventStatus.RMonitor;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarDriverMode;
using StackExchange.Redis;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.EventStatus.InCarDriverMode;

public class DriverModeProcessor
{
    private const string MinTimeFormat = @"m\:ss\.fff";
    private const string SecTimeFormat = @"s\.fff";

    private readonly int eventId;
    private ILogger Logger { get; }

    private readonly IHubContext<StatusHub> hubContext;
    private readonly HybridCache hcache;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly Dictionary<string, CarPosition> carPositionsLookup = [];
    private readonly Dictionary<string, CarSet> carSetsLookup = [];
    private Flags lastFlag = Flags.Unknown;


    public DriverModeProcessor(int eventId, IHubContext<StatusHub> hubContext, ILoggerFactory loggerFactory, HybridCache hcache, 
        IDbContextFactory<TsContext> tsContext, IConnectionMultiplexer cacheMux)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.eventId = eventId;
        this.hubContext = hubContext;
        this.hcache = hcache;
        this.tsContext = tsContext;
        this.cacheMux = cacheMux;
    }


    public async Task UpdateCarPositions(List<CarPosition> positions, Dictionary<string, Competitor> competitors, Flags currentFlag)
    {
        foreach (var position in positions)
        {
            if (position.Number != null)
            {
                carPositionsLookup[position.Number] = position;
            }
        }

        bool flagChanged = lastFlag != currentFlag;
        lastFlag = currentFlag;

        var changes = new List<InCarPayload>();
        foreach (var number in carPositionsLookup.Keys)
        {
            if (!carSetsLookup.TryGetValue(number, out CarSet? value))
            {
                value = new CarSet();
                carSetsLookup[number] = value;
            }

            var driver = carPositionsLookup[number];
            value.UpdateDriver(driver);
            value.CarAhead.Update(GetCarAhead(driver));
            value.CarBehind.Update(GetCarBehind(driver));
            value.CarAheadOutOfClass.Update(GetCarAheadOutOfClass(driver));

            if (value.IsDirty || flagChanged)
            {
                var payload = value.GetPayloadPartial();
                payload.Flag = currentFlag;
                changes.Add(payload);
            }
        }

        await SendUpdatesAsync(changes, competitors);
    }

    private CarPosition? GetCarAhead(CarPosition driver)
    {
        if (driver.ClassPosition > 1)
        {
            return carPositionsLookup.Values.FirstOrDefault(c => c.ClassPosition == driver.ClassPosition - 1 && c.Class == driver.Class);
        }
        return null;
    }

    private CarPosition? GetCarBehind(CarPosition driver)
    {
        return carPositionsLookup.Values.FirstOrDefault(c => c.ClassPosition == driver.ClassPosition + 1 && c.Class == driver.Class);
    }

    private CarPosition? GetCarAheadOutOfClass(CarPosition driver)
    {
        if (driver.OverallPosition > 1)
        {
            return carPositionsLookup.Values.FirstOrDefault(c => c.OverallPosition == driver.OverallPosition - 1 && c.Class != driver.Class);
        }
        return null;
    }

    private async Task SendUpdatesAsync(List<InCarPayload> changes, Dictionary<string, Competitor> competitors, CancellationToken cancellationToken = default)
    {
        foreach (var change in changes)
        {
            foreach (var car in change.Cars)
            {
                if (car?.Number == null)
                {
                    continue;
                }

                try
                {
                    var metadata = await GetCompetitorMetadata(eventId, car.Number);
                    if (metadata != null)
                    {
                        car.CarType = (metadata.Make + " " + metadata.ModelEngine).Trim();
                    }

                    var carPos = carPositionsLookup.GetValueOrDefault(car.Number);
                    if (carPos != null)
                    {
                        car.Class = carPos.Class ?? string.Empty;
                        car.TransponderId = carPos.TransponderId;

                        if (competitors.TryGetValue(car.Number, out Competitor? competitor))
                        {
                            var entry = competitor.ToEventEntry();
                            car.Team = entry.Name;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error processing car metadata for {CarNumber}", car.Number);
                }
            }

            var grpKey = string.Format(Consts.IN_CAR_EVENT_SUB, eventId, change.CarNumber);
            try
            {
                var json = JsonSerializer.Serialize(change);
                var bytes = Encoding.UTF8.GetBytes(json);
                using var output = new MemoryStream();
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
                {
                    gzip.Write(bytes, 0, bytes.Length);
                }
                var b64 = Convert.ToBase64String(output.ToArray());
                await hubContext.Clients.Group(grpKey).SendAsync("ReceiveInCarUpdate", b64, cancellationToken);

                var cache = cacheMux.GetDatabase();
                var cacheKey = string.Format(Consts.IN_CAR_DATA, eventId, change.CarNumber);
                await cache.StringSetAsync(cacheKey, json, TimeSpan.FromMinutes(5));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error sending in-car update to clients.");
            }
        }
    }

    private async Task<CompetitorMetadata?> GetCompetitorMetadata(int eventId, string car)
    {
        var key = string.Format(Consts.COMPETITOR_METADATA, car, eventId);
        return await hcache.GetOrCreateAsync(key,
            async cancel => await LoadDbCompetitorMetadata(eventId, car));
    }

    private async Task<CompetitorMetadata?> LoadDbCompetitorMetadata(int eventId, string car)
    {
        using var db = await tsContext.CreateDbContextAsync();
        return await db.CompetitorMetadata.FirstOrDefaultAsync(x => x.EventId == eventId && x.CarNumber == car);
    }
}
