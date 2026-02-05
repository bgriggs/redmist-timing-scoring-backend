using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Hubs;
using RedMist.Database;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarDriverMode;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.EventProcessor.EventStatus.InCarDriverMode;

public interface IDriverModeProcessor
{
    Task ProcessAsync(CancellationToken cancellationToken = default);
}

public interface ICarPositionProvider
{
    IReadOnlyList<CarPosition> GetCarPositions();
    CarPosition? GetCarByNumber(string carNumber);
}

public interface ICompetitorMetadataProvider
{
    Task<CompetitorMetadata?> GetCompetitorMetadataAsync(int eventId, string carNumber);
}

public interface IInCarUpdateSender
{
    Task SendUpdatesAsync(List<InCarPayload> changes, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation that uses SessionContext for car positions
/// </summary>
public class SessionContextCarPositionProvider(SessionContext sessionContext) : ICarPositionProvider
{
    public IReadOnlyList<CarPosition> GetCarPositions()
    {
        return sessionContext.SessionState.CarPositions.AsReadOnly();
    }

    public CarPosition? GetCarByNumber(string carNumber)
    {
        return sessionContext.GetCarByNumber(carNumber);
    }
}

/// <summary>
/// Default implementation that uses HybridCache and database
/// </summary>
public class CachedCompetitorMetadataProvider(HybridCache hcache, IDbContextFactory<TsContext> tsContext) : ICompetitorMetadataProvider
{
    public async Task<CompetitorMetadata?> GetCompetitorMetadataAsync(int eventId, string carNumber)
    {
        var key = string.Format(Consts.COMPETITOR_METADATA, carNumber, eventId);
        return await hcache.GetOrCreateAsync(key,
            async cancel => await LoadDbCompetitorMetadataAsync(eventId, carNumber));
    }

    private async Task<CompetitorMetadata?> LoadDbCompetitorMetadataAsync(int eventId, string carNumber)
    {
        using var db = await tsContext.CreateDbContextAsync();
        return await db.CompetitorMetadata.FirstOrDefaultAsync(x => x.EventId == eventId && x.CarNumber == carNumber);
    }
}

/// <summary>
/// Default implementation that uses SignalR and Redis
/// </summary>
public class SignalRInCarUpdateSender : IInCarUpdateSender
{
    private readonly IHubContext<StatusHub> hubContext;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly SessionContext sessionContext;
    private readonly ILogger logger;

    public SignalRInCarUpdateSender(
        IHubContext<StatusHub> hubContext,
        IConnectionMultiplexer cacheMux,
        SessionContext sessionContext,
        ILogger logger)
    {
        this.hubContext = hubContext;
        this.cacheMux = cacheMux;
        this.sessionContext = sessionContext;
        this.logger = logger;
    }

    public async Task SendUpdatesAsync(List<InCarPayload> changes, CancellationToken cancellationToken = default)
    {
        var cache = cacheMux.GetDatabase();

        foreach (var change in changes)
        {
            var subKey = string.Format(Consts.IN_CAR_EVENT_SUB_V2, sessionContext.EventId, change.CarNumber);
            await hubContext.Clients.Group(subKey).SendAsync("ReceiveInCarUpdateV2", change, sessionContext.CancellationToken);

            try
            {
                var json = JsonSerializer.Serialize(change);

                // Legacy, send to in-car hub. To be removed in future.
                //var grpKey = string.Format(Consts.IN_CAR_EVENT_SUB, sessionContext.EventId, change.CarNumber);
                //var bytes = Encoding.UTF8.GetBytes(json);
                //using var output = new MemoryStream();
                //using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
                //{
                //    gzip.Write(bytes, 0, bytes.Length);
                //}
                //var b64 = Convert.ToBase64String(output.ToArray());
                //await hubContext.Clients.Group(grpKey).SendAsync("ReceiveInCarUpdate", b64, cancellationToken);

                // Update cache for initial payload loading
                var cacheKey = string.Format(Consts.IN_CAR_DATA, sessionContext.EventId, change.CarNumber);
                await cache.StringSetAsync(cacheKey, json, TimeSpan.FromMinutes(5));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error updating in-car cache.");
            }
        }
    }
}

public class DriverModeProcessor : IDriverModeProcessor
{
    private const string MinTimeFormat = @"m\:ss\.fff";
    private const string SecTimeFormat = @"s\.fff";

    private readonly ILogger logger;
    private readonly SessionContext sessionContext;
    private readonly ICarPositionProvider carPositionProvider;
    private readonly ICompetitorMetadataProvider competitorMetadataProvider;
    private readonly IInCarUpdateSender updateSender;
    
    private readonly Dictionary<string, CarSet> carSetsLookup = [];
    private Flags lastFlag = Flags.Unknown;

    public DriverModeProcessor(
        ILoggerFactory loggerFactory, 
        SessionContext sessionContext,
        ICarPositionProvider carPositionProvider,
        ICompetitorMetadataProvider competitorMetadataProvider,
        IInCarUpdateSender updateSender)
    {
        logger = loggerFactory.CreateLogger(GetType().Name);
        this.sessionContext = sessionContext;
        this.carPositionProvider = carPositionProvider;
        this.competitorMetadataProvider = competitorMetadataProvider;
        this.updateSender = updateSender;
    }

    // Convenience constructor for existing code that uses direct dependencies
    public DriverModeProcessor(IHubContext<StatusHub> hubContext, ILoggerFactory loggerFactory, HybridCache hcache,
        IDbContextFactory<TsContext> tsContext, IConnectionMultiplexer cacheMux, SessionContext sessionContext)
        : this(loggerFactory, sessionContext,
               new SessionContextCarPositionProvider(sessionContext),
               new CachedCompetitorMetadataProvider(hcache, tsContext),
               new SignalRInCarUpdateSender(hubContext, cacheMux, sessionContext, loggerFactory.CreateLogger<SignalRInCarUpdateSender>()))
    {
    }

    public async Task ProcessAsync(CancellationToken cancellationToken = default)
    {
        bool flagChanged = lastFlag != sessionContext.SessionState.CurrentFlag;
        lastFlag = sessionContext.SessionState.CurrentFlag;

        var carPositions = carPositionProvider.GetCarPositions();
        var changes = new List<InCarPayload>();
        
        foreach (var driver in carPositions)
        {
            if (driver.Number == null)
                continue;

            if (!carSetsLookup.TryGetValue(driver.Number, out CarSet? value))
            {
                value = new CarSet();
                carSetsLookup[driver.Number] = value;
            }

            value.UpdateDriver(driver);
            value.CarAhead.Update(GetCarAhead(driver, carPositions));
            value.CarBehind.Update(GetCarBehind(driver, carPositions));
            value.CarAheadOutOfClass.Update(GetCarAheadOutOfClass(driver, carPositions));

            if (value.IsDirty || flagChanged)
            {
                var payload = value.GetPayloadPartial();
                payload.Flag = sessionContext.SessionState.CurrentFlag;
                
                // Enrich car data with metadata
                await EnrichCarDataAsync(payload, cancellationToken);
                
                changes.Add(payload);
            }
        }

        _ = updateSender.SendUpdatesAsync(changes, cancellationToken);
    }

    /// <summary>
    /// Gets the car ahead in the same class. Made public for testing.
    /// </summary>
    public CarPosition? GetCarAhead(CarPosition driver, IReadOnlyList<CarPosition> carPositions)
    {
        if (driver.ClassPosition > 1)
        {
            return carPositions.FirstOrDefault(c => c.ClassPosition == driver.ClassPosition - 1 && c.Class == driver.Class);
        }
        return null;
    }

    /// <summary>
    /// Gets the car behind in the same class. Made public for testing.
    /// </summary>
    public CarPosition? GetCarBehind(CarPosition driver, IReadOnlyList<CarPosition> carPositions)
    {
        return carPositions.FirstOrDefault(c => c.ClassPosition == driver.ClassPosition + 1 && c.Class == driver.Class);
    }

    /// <summary>
    /// Gets the car ahead overall but in a different class. Made public for testing.
    /// </summary>
    public CarPosition? GetCarAheadOutOfClass(CarPosition driver, IReadOnlyList<CarPosition> carPositions)
    {
        if (driver.OverallPosition > 1)
        {
            return carPositions.FirstOrDefault(c => c.OverallPosition == driver.OverallPosition - 1 && c.Class != driver.Class);
        }
        return null;
    }

    /// <summary>
    /// Enriches car payload with metadata and position information. Made public for testing.
    /// </summary>
    public async Task EnrichCarDataAsync(InCarPayload payload, CancellationToken cancellationToken = default)
    {
        foreach (var car in payload.Cars)
        {
            if (car?.Number == null)
                continue;

            try
            {
                var metadata = await competitorMetadataProvider.GetCompetitorMetadataAsync(sessionContext.EventId, car.Number);
                if (metadata != null)
                {
                    car.CarType = (metadata.Make + " " + metadata.ModelEngine).Trim();
                }

                var carPos = carPositionProvider.GetCarByNumber(car.Number);
                if (carPos != null)
                {
                    car.Class = carPos.Class ?? string.Empty;
                    car.TransponderId = carPos.TransponderId;
                    car.Driver = carPos.DriverName ?? string.Empty;

                    var entry = sessionContext.SessionState.EventEntries.FirstOrDefault(e => e.Number == car.Number);
                    if (entry != null)
                        car.Team = entry.Name ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing car metadata for {CarNumber}", car.Number);
            }
        }
    }

    /// <summary>
    /// Gets the current car sets lookup for testing purposes
    /// </summary>
    public IReadOnlyDictionary<string, CarSet> GetCarSetsLookup()
    {
        return carSetsLookup.AsReadOnly();
    }

    /// <summary>
    /// Gets the last flag for testing purposes
    /// </summary>
    public Flags GetLastFlag()
    {
        return lastFlag;
    }
}
