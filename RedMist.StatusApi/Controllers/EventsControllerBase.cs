using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarDriverMode;
using StackExchange.Redis;
using System.Diagnostics;
using System.Text.Json;

namespace RedMist.StatusApi.Controllers;

/// <summary>
/// Base controller containing all shared logic across API versions.
/// Not directly routable - inherited by versioned controllers.
/// </summary>
[ApiController]
[Authorize]
public abstract class EventsControllerBase : ControllerBase
{
    protected readonly IDbContextFactory<TsContext> tsContext;
    protected readonly HybridCache hcache;
    protected readonly IConnectionMultiplexer cacheMux;
    protected readonly IMemoryCache memoryCache;
    protected readonly IHttpClientFactory httpClientFactory;
    protected ILogger Logger { get; }


    protected EventsControllerBase(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
        HybridCache hcache, IConnectionMultiplexer cacheMux, IMemoryCache memoryCache,
        IHttpClientFactory httpClientFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.hcache = hcache;
        this.cacheMux = cacheMux;
        this.memoryCache = memoryCache;
        this.httpClientFactory = httpClientFactory;
    }


    [HttpGet]
    [ProducesResponseType<Event[]>(StatusCodes.Status200OK)]
    public virtual async Task<Event[]> LoadEvents(DateTime startDateUtc)
    {
        Logger.LogTrace("LoadEvents");

        using var context = await tsContext.CreateDbContextAsync();
        var dbEvents = await context.Events
            .Join(context.Organizations, e => e.OrganizationId, o => o.Id, (e, o) => new { e, o })
            .Where(x => x.e.StartDate >= startDateUtc && !x.e.IsDeleted).ToArrayAsync();

        // Map to Event model
        List<Event> eventDtos = [];
        foreach (var dbEvent in dbEvents)
        {
            var sessions = await context.Sessions.Where(x => x.EventId == dbEvent.e.Id).ToArrayAsync();
            var eventDto = new Event
            {
                EventId = dbEvent.e.Id,
                EventName = dbEvent.e.Name,
                EventDate = dbEvent.e.StartDate.ToString(),
                EventUrl = dbEvent.e.EventUrl,
                Sessions = sessions,
                OrganizationName = dbEvent.o.Name,
                OrganizationWebsite = dbEvent.o.Website,
                OrganizationLogo = dbEvent.o.Logo,
                TrackName = dbEvent.e.TrackName,
                CourseConfiguration = dbEvent.e.CourseConfiguration,
                Distance = dbEvent.e.Distance,
                Broadcast = dbEvent.e.Broadcast,
                Schedule = dbEvent.e.Schedule,
                HasControlLog = !string.IsNullOrEmpty(dbEvent.o.ControlLogType),
                IsLive = dbEvent.e.IsLive,
            };
            eventDtos.Add(eventDto);
        }

        return [.. eventDtos];
    }

    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType<List<EventListSummary>>(StatusCodes.Status200OK)]
    public virtual async Task<List<EventListSummary>> LoadLiveEvents()
    {
        Logger.LogTrace("LoadLiveEvents");
        using var db = await tsContext.CreateDbContextAsync();
        var summaries = await (
        from e in db.Events
        join o in db.Organizations on e.OrganizationId equals o.Id
        where e.IsActive && !e.IsDeleted && e.IsLive
        group new { e, o } by new
        {
            e.Id,
            e.OrganizationId,
            OrganizationName = o.Name,
            EventName = e.Name,
            EventDate = e.StartDate,
            TrackName = e.TrackName,
            Schedule = e.Schedule
        } into g
        select new EventListSummary
        {
            Id = g.Key.Id,
            OrganizationId = g.Key.OrganizationId,
            OrganizationName = g.Key.OrganizationName,
            EventName = g.Key.EventName,
            EventDate = g.Key.EventDate.ToString("yyyy-MM-dd"),
            IsLive = true,
            TrackName = g.Key.TrackName,
            Schedule = g.Key.Schedule
        }).ToListAsync();

        return summaries;
    }

    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType<List<EventListSummary>>(StatusCodes.Status200OK)]
    public virtual async Task<List<EventListSummary>> LoadLiveAndRecentEvents()
    {
        Logger.LogTrace("LoadLiveAndRecentEvents");

        using var db1 = await tsContext.CreateDbContextAsync();
        var recentEvents = await (
            from e in db1.Events
            join o in db1.Organizations on e.OrganizationId equals o.Id
            where !e.IsDeleted && !e.IsLive
            orderby e.StartDate descending
            select new EventListSummary
            {
                Id = e.Id,
                OrganizationId = e.OrganizationId,
                OrganizationName = o.Name,
                EventName = e.Name,
                EventDate = e.StartDate.ToString("yyyy-MM-dd"),
                IsLive = false,
                TrackName = e.TrackName,
            })
            .Take(100)
            .ToListAsync();
        var liveEvents = await LoadLiveEvents();

        return [.. liveEvents, .. recentEvents];
    }

    [HttpGet]
    [ProducesResponseType<Event>(StatusCodes.Status200OK)]
    public virtual async Task<Event?> LoadEvent(int eventId)
    {
        Logger.LogTrace("LoadEvent");

        using var context = await tsContext.CreateDbContextAsync();
        var dbEvent = await context.Events
            .Join(context.Organizations, e => e.OrganizationId, o => o.Id, (e, o) => new { e, o })
            .Where(x => x.e.Id == eventId && !x.e.IsDeleted).FirstOrDefaultAsync();

        if (dbEvent == null)
            return null;

        var sessions = await context.Sessions.Where(x => x.EventId == dbEvent.e.Id).ToArrayAsync();
        return new Event
        {
            EventId = dbEvent.e.Id,
            EventName = dbEvent.e.Name,
            EventDate = dbEvent.e.StartDate.ToString(),
            EventUrl = dbEvent.e.EventUrl,
            Sessions = sessions,
            OrganizationName = dbEvent.o.Name,
            OrganizationWebsite = dbEvent.o.Website,
            OrganizationLogo = dbEvent.o.Logo,
            TrackName = dbEvent.e.TrackName,
            CourseConfiguration = dbEvent.e.CourseConfiguration,
            Distance = dbEvent.e.Distance,
            Broadcast = dbEvent.e.Broadcast,
            Schedule = dbEvent.e.Schedule,
            HasControlLog = !string.IsNullOrEmpty(dbEvent.o.ControlLogType),
            IsLive = dbEvent.e.IsLive,
        };
    }

    [HttpGet]
    [ProducesResponseType<List<CarPosition>>(StatusCodes.Status200OK)]
    public virtual async Task<List<CarPosition>> LoadCarLaps(int eventId, int sessionId, string carNumber)
    {
        Logger.LogTrace("GetCarPositions for event {eventId}", eventId);
        using var context = tsContext.CreateDbContext();
        var laps = await context.CarLapLogs
            .Where(c => c.EventId == eventId && c.SessionId == sessionId && c.CarNumber == carNumber && c.LapNumber > 0 && c.Timestamp == context.CarLapLogs
                .Where(r => r.Id == c.Id)
                .Max(r => r.Timestamp))
            .ToListAsync();

        var carPositions = new List<CarPosition>();
        foreach (var lap in laps)
        {
            try
            {
                var cp = JsonSerializer.Deserialize<CarPosition>(lap.LapData);
                if (cp != null)
                {
                    carPositions.Add(cp);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error deserializing car position data for event {eventId}, car {carNumber}", eventId, carNumber);
            }
        }

        return carPositions;
    }

    #region Sessions

    [HttpGet]
    [ProducesResponseType<List<Session>>(StatusCodes.Status200OK)]
    public virtual async Task<List<Session>> LoadSessions(int eventId)
    {
        Logger.LogTrace("GetSessions for event {eventId}", eventId);
        using var context = await tsContext.CreateDbContextAsync();
        return await context.Sessions.Where(s => s.EventId == eventId).ToListAsync();
    }

    [HttpGet]
    [ProducesResponseType(typeof(SessionState), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<IActionResult> GetCurrentSessionState(int eventId)
    {
        var url = await GetEventProcessorEndpointAsync(eventId);
        if (string.IsNullOrEmpty(url))
            return NotFound("Event processor endpoint not found");

        url = url.TrimEnd('/') + "/status/GetStatus";
        var sw = Stopwatch.StartNew();
        
        using var httpClient = httpClientFactory.CreateClient("EventProcessor");
        
        try
        {
            var stream = await httpClient.GetStreamAsync(url);
            return File(stream, "application/x-msgpack");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            Logger.LogWarning("HTTP request to {url} timed out after {elapsed} ms", url, sw.ElapsedMilliseconds);
            return StatusCode(408, "Request timeout");
        }
        catch (HttpRequestException ex)
        {
            Logger.LogError(ex, "HTTP request to {url} failed after {elapsed} ms", url, sw.ElapsedMilliseconds);
            return StatusCode(503, "Service unavailable");
        }
    }

    protected async Task<string?> GetEventProcessorEndpointAsync(int eventId)
    {
        var key = string.Format(Consts.EVENT_SERVICE_ENDPOINT, eventId);
        if (memoryCache.TryGetValue(key, out string? cachedValue))
        {
            return cachedValue;
        }

        try
        {
            var cache = cacheMux.GetDatabase();
            var redisValue = await cache.StringGetAsync(key);

            if (redisValue.HasValue)
            {
                var value = redisValue.ToString();
                if (!value.StartsWith("http"))
                    value = "http://" + value;
                var cacheOptions = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1) };
                memoryCache.Set(key, value, cacheOptions);
                return value;
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error retrieving service endpoint for event {eventId}", eventId);
            return null;
        }
    }

    #endregion

    #region Competitor Metadata

    [HttpGet]
    [ProducesResponseType<CompetitorMetadata>(StatusCodes.Status200OK)]
    public virtual async Task<CompetitorMetadata?> LoadCompetitorMetadata(int eventId, string car)
    {
        Logger.LogTrace("LoadCompetitorMetadata for event {eventId}, car {car}", eventId, car);
        return await GetCompetitorMetadata(eventId, car);
    }

    protected async Task<CompetitorMetadata?> GetCompetitorMetadata(int eventId, string car)
    {
        var key = string.Format(Consts.COMPETITOR_METADATA, car, eventId);
        return await hcache.GetOrCreateAsync(key,
            async cancel => await LoadDbCompetitorMetadata(eventId, car));
    }

    protected async Task<CompetitorMetadata?> LoadDbCompetitorMetadata(int eventId, string car)
    {
        using var db = await tsContext.CreateDbContextAsync();
        return await db.CompetitorMetadata.FirstOrDefaultAsync(x => x.EventId == eventId && x.CarNumber == car);
    }

    #endregion

    #region Control Logs

    [HttpGet]
    [ProducesResponseType<List<ControlLogEntry>>(StatusCodes.Status200OK)]
    public virtual async Task<List<ControlLogEntry>> LoadControlLog(int eventId)
    {
        Logger.LogTrace("LoadControlLog for event {eventId}", eventId);
        var logCacheKey = string.Format(Consts.CONTROL_LOG, eventId);
        var cache = cacheMux.GetDatabase();
        var json = await cache.StringGetAsync(logCacheKey);
        if (json.IsNullOrEmpty)
        {
            Logger.LogWarning("Control log for event {eventId} not found in cache", eventId);
            return [];
        }
        var ccl = JsonSerializer.Deserialize<CarControlLogs>(json!);
        return ccl?.ControlLogEntries ?? [];
    }

    [HttpGet]
    [ProducesResponseType<CarControlLogs>(StatusCodes.Status200OK)]
    public virtual async Task<CarControlLogs?> LoadCarControlLogs(int eventId, string car)
    {
        Logger.LogTrace("LoadCarControlLog for event {eventId} car {c}", eventId, car);
        var carLogEntryKey = string.Format(Consts.CONTROL_LOG_CAR, eventId, car);
        var cache = cacheMux.GetDatabase();
        var json = await cache.StringGetAsync(carLogEntryKey);
        if (json.IsNullOrEmpty)
        {
            Logger.LogWarning("Control log for event {eventId} car {c} not found in cache", eventId, car);
            return null;
        }
        return JsonSerializer.Deserialize<CarControlLogs>(json!);
    }

    #endregion

    #region In-Car Data

    [HttpGet]
    [ProducesResponseType<InCarPayload>(StatusCodes.Status200OK)]
    public virtual async Task<InCarPayload?> LoadInCarPayload(int eventId, string car)
    {
        Logger.LogTrace("LoadInCarPayload for event {eventId}, car {car}", eventId, car);
        var cache = cacheMux.GetDatabase();
        var cacheKey = string.Format(Consts.IN_CAR_DATA, eventId, car);
        var json = await cache.StringGetAsync(cacheKey);
        if (json.IsNullOrEmpty)
        {
            Logger.LogWarning("In-car data for event {eventId} car {car} not found in cache", eventId, car);
            return null;
        }
        return JsonSerializer.Deserialize<InCarPayload>(json!);
    }

    #endregion

    #region Flags

    [HttpGet]
    [ProducesResponseType<List<FlagDuration>>(StatusCodes.Status200OK)]
    public virtual async Task<List<FlagDuration>> LoadFlags(int eventId, int sessionId)
    {
        Logger.LogTrace("LoadFlags for event {eventId}, session {sessionId}", eventId, sessionId);
        using var context = await tsContext.CreateDbContextAsync();
        var flagLogs = await context.FlagLog
            .Where(f => f.EventId == eventId && f.SessionId == sessionId)
            .ToListAsync();

        var flagDurations = flagLogs.Select(f => new FlagDuration
        {
            Flag = f.Flag,
            StartTime = f.StartTime,
            EndTime = f.EndTime,
        }).ToList();

        return flagDurations;
    }

    #endregion
}
