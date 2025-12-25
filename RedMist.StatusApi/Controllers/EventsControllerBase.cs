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
/// Base controller providing event status and timing data endpoints.
/// Contains shared logic across all API versions for loading events, sessions, car positions, and related data.
/// </summary>
/// <remarks>
/// This is an abstract base controller that is inherited by versioned controllers (V1, V2, etc.).
/// It is not directly routable and requires authentication via Bearer token.
/// </remarks>
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

    private static readonly HybridCacheEntryOptions liveEventsCacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(1),
        LocalCacheExpiration = TimeSpan.FromMinutes(1)
    };


    /// <summary>
    /// Initializes a new instance of the <see cref="EventsControllerBase"/> class.
    /// </summary>
    /// <param name="loggerFactory">Factory to create loggers.</param>
    /// <param name="tsContext">Database context factory for timing and scoring data.</param>
    /// <param name="hcache">Hybrid cache for distributed caching.</param>
    /// <param name="cacheMux">Redis connection multiplexer.</param>
    /// <param name="memoryCache">In-memory cache for frequently accessed data.</param>
    /// <param name="httpClientFactory">Factory to create HTTP clients for inter-service communication.</param>
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


    /// <summary>
    /// Retrieves all events starting from a specified date.
    /// </summary>
    /// <param name="startDateUtc">The start date (UTC) to filter events from.</param>
    /// <returns>An array of events with their associated sessions and organization details.</returns>
    /// <response code="200">Returns the array of events.</response>
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<Event[]>(StatusCodes.Status200OK)]
    public virtual async Task<Event[]> LoadEvents(DateTime startDateUtc)
    {
        Logger.LogTrace("{m} for {d}", nameof(LoadEvents), startDateUtc);

        using var context = await tsContext.CreateDbContextAsync();
        var dbEvents = await context.Events
            .Join(context.Organizations, e => e.OrganizationId, o => o.Id, (e, o) => new { e, o })
            .Where(x => x.e.StartDate >= startDateUtc && !x.e.IsDeleted).ToArrayAsync();

        var noLogo = dbEvents.Any(x => x.o.Logo == null);
        byte[] defaultLogo = [];
        if (noLogo)
        {
            defaultLogo = context.DefaultOrgImages.FirstOrDefault()?.ImageData ?? [];
        }

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
                OrganizationLogo = dbEvent.o.Logo ?? defaultLogo,
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

    /// <summary>
    /// Retrieves all currently live events.
    /// </summary>
    /// <returns>A list of event summaries for active live events.</returns>
    /// <response code="200">Returns the list of live events.</response>
    /// <remarks>
    /// This endpoint is publicly accessible (no authentication required).
    /// </remarks>
    [AllowAnonymous]
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<List<EventListSummary>>(StatusCodes.Status200OK)]
    public virtual async Task<List<EventListSummary>> LoadLiveEvents()
    {
        Logger.LogTrace("LoadLiveEvents");

        var cacheKey = "live-events";
        return await hcache.GetOrCreateAsync(cacheKey,
            async cancel => await LoadLiveEventsFromDbAsync(),
            liveEventsCacheOptions);
    }

    /// <summary>
    /// Loads live events from the database.
    /// </summary>
    /// <returns>A list of live event summaries.</returns>
    private async Task<List<EventListSummary>> LoadLiveEventsFromDbAsync()
    {
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

    /// <summary>
    /// Retrieves all live events and the most recent 100 non-live events.
    /// </summary>
    /// <returns>A combined list of live and recent event summaries.</returns>
    /// <response code="200">Returns the list of live and recent events.</response>
    /// <remarks>
    /// This endpoint is publicly accessible (no authentication required).
    /// Useful for displaying both current and past events in user interfaces.
    /// </remarks>
    [AllowAnonymous]
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<List<EventListSummary>>(StatusCodes.Status200OK)]
    public virtual async Task<List<EventListSummary>> LoadLiveAndRecentEvents()
    {
        Logger.LogTrace(nameof(LoadLiveAndRecentEvents));

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

    /// <summary>
    /// Loads detailed information for a specific event.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <returns>The event details including sessions, organization info, and configuration, or null if not found.</returns>
    /// <response code="200">Returns the event details.</response>
    /// <response code="404">Event not found.</response>
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<Event>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<Event>> LoadEvent(int eventId)
    {
        Logger.LogTrace("{m} for {id}", nameof(LoadEvent), eventId);

        using var context = await tsContext.CreateDbContextAsync();
        var dbEvent = await context.Events
            .Join(context.Organizations, e => e.OrganizationId, o => o.Id, (e, o) => new { e, o })
            .Where(x => x.e.Id == eventId && !x.e.IsDeleted).FirstOrDefaultAsync();

        if (dbEvent == null)
            return NotFound("event");

        byte[] defaultLogo = [];
        if (dbEvent.o.Logo == null)
        {
            defaultLogo = context.DefaultOrgImages.FirstOrDefault()?.ImageData ?? [];
        }

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
            OrganizationLogo = dbEvent.o.Logo ?? defaultLogo,
            TrackName = dbEvent.e.TrackName,
            CourseConfiguration = dbEvent.e.CourseConfiguration,
            Distance = dbEvent.e.Distance,
            Broadcast = dbEvent.e.Broadcast,
            Schedule = dbEvent.e.Schedule,
            HasControlLog = !string.IsNullOrEmpty(dbEvent.o.ControlLogType),
            IsLive = dbEvent.e.IsLive,
        };
    }

    /// <summary>
    /// Loads completed lap data for a specific car in a session.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="sessionId">The unique identifier of the session.</param>
    /// <param name="carNumber">The car number to retrieve lap data for.</param>
    /// <returns>A list of car positions representing each completed lap.</returns>
    /// <response code="200">Returns the list of lap positions for the car.</response>
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<List<CarPosition>>(StatusCodes.Status200OK)]
    public virtual async Task<List<CarPosition>> LoadCarLaps(int eventId, int sessionId, string carNumber)
    {
        Logger.LogTrace("{m} for event {eventId}", nameof(LoadCarLaps), eventId);
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

    /// <summary>
    /// Loads all sessions for a specific event.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <returns>A list of sessions associated with the event.</returns>
    /// <response code="200">Returns the list of sessions.</response>
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<List<Session>>(StatusCodes.Status200OK)]
    public virtual async Task<List<Session>> LoadSessions(int eventId)
    {
        Logger.LogTrace("{m} for event {eventId}", nameof(LoadSessions), eventId);
        using var context = await tsContext.CreateDbContextAsync();
        return await context.Sessions.Where(s => s.EventId == eventId).ToListAsync();
    }

    /// <summary>
    /// Gets the current real-time session state from the event processor service.
    /// Returns MessagePack-serialized SessionState data.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <returns>MessagePack binary stream of the current session state.</returns>
    /// <response code="200">Returns MessagePack binary data (application/x-msgpack).</response>
    /// <response code="404">Event processor endpoint not found.</response>
    /// <response code="408">Request timeout.</response>
    /// <response code="503">Service unavailable.</response>
    /// <remarks>
    /// This endpoint communicates with the dedicated event processor service to retrieve live session data.
    /// The response is in MessagePack format for efficient serialization.
    /// </remarks>
    [HttpGet]
    [Produces("application/x-msgpack")]
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
        catch (HttpRequestException)
        {
            Logger.LogError("HTTP request to {url} failed after {elapsed} ms", url, sw.ElapsedMilliseconds);
            return NotFound();
        }
    }

    /// <summary>
    /// Retrieves the event processor service endpoint for a specific event from cache.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <returns>The HTTP endpoint URL of the event processor, or null if not found.</returns>
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

    /// <summary>
    /// Loads competitor metadata (driver/car information) for a specific car in an event.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="car">The car number/identifier.</param>
    /// <returns>Competitor metadata including driver name, car details, transponder info, etc., or null if not found.</returns>
    /// <response code="200">Returns the competitor metadata.</response>
    /// <remarks>
    /// Metadata is sourced from Orbits timing systems when available and configured by the organizer.
    /// </remarks>
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<CompetitorMetadata>(StatusCodes.Status200OK)]
    public virtual async Task<CompetitorMetadata?> LoadCompetitorMetadata(int eventId, string car)
    {
        Logger.LogTrace("{m} for event {eventId}, car {car}", nameof(LoadCompetitorMetadata), eventId, car);
        return await GetCompetitorMetadata(eventId, car);
    }

    /// <summary>
    /// Gets competitor metadata from cache or database.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="car">The car number/identifier.</param>
    /// <returns>Competitor metadata or null if not found.</returns>
    protected async Task<CompetitorMetadata?> GetCompetitorMetadata(int eventId, string car)
    {
        var key = string.Format(Consts.COMPETITOR_METADATA, car, eventId);
        return await hcache.GetOrCreateAsync(key,
            async cancel => await LoadDbCompetitorMetadata(eventId, car));
    }

    /// <summary>
    /// Loads competitor metadata from the database.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="car">The car number/identifier.</param>
    /// <returns>Competitor metadata from database or null if not found.</returns>
    protected async Task<CompetitorMetadata?> LoadDbCompetitorMetadata(int eventId, string car)
    {
        using var db = await tsContext.CreateDbContextAsync();
        return await db.CompetitorMetadata.FirstOrDefaultAsync(x => x.EventId == eventId && x.CarNumber == car);
    }

    #endregion

    #region Control Logs

    /// <summary>
    /// Loads the complete control log for an event.
    /// Control logs contain race control decisions, penalties, and incident reports.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <returns>A list of control log entries, or an empty list if not available.</returns>
    /// <response code="200">Returns the list of control log entries.</response>
    /// <remarks>
    /// Control logs are only available if configured by the event organizer.
    /// </remarks>
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<List<ControlLogEntry>>(StatusCodes.Status200OK)]
    public virtual async Task<List<ControlLogEntry>> LoadControlLog(int eventId)
    {
        Logger.LogTrace("{m} for event {eventId}", nameof(LoadControlLog), eventId);
        var logCacheKey = string.Format(Consts.CONTROL_LOG, eventId);
        var cache = cacheMux.GetDatabase();
        var json = await cache.StringGetAsync(logCacheKey);
        if (json.IsNullOrEmpty)
        {
            Logger.LogWarning("Control log for event {eventId} not found in cache", eventId);
            return [];
        }
        var ccl = JsonSerializer.Deserialize<CarControlLogs>(json.ToString());
        return ccl?.ControlLogEntries ?? [];
    }

    /// <summary>
    /// Loads control log entries specific to a particular car.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="car">The car number to filter control log entries for.</param>
    /// <returns>Control log data for the specified car, or null if not available.</returns>
    /// <response code="200">Returns the car-specific control logs.</response>
    /// <remarks>
    /// Useful for drivers/teams to see only penalties and incidents affecting their car.
    /// </remarks>
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<CarControlLogs>(StatusCodes.Status200OK)]
    public virtual async Task<CarControlLogs?> LoadCarControlLogs(int eventId, string car)
    {
        Logger.LogTrace("{m} for event {eventId} car {c}", nameof(LoadCarControlLogs), eventId, car);
        var carLogEntryKey = string.Format(Consts.CONTROL_LOG_CAR, eventId, car);
        var cache = cacheMux.GetDatabase();
        var json = await cache.StringGetAsync(carLogEntryKey);
        if (json.IsNullOrEmpty)
        {
            Logger.LogWarning("Control log for event {eventId} car {c} not found in cache", eventId, car);
            return null;
        }
        return JsonSerializer.Deserialize<CarControlLogs>(json.ToString());
    }

    #endregion

    #region In-Car Data

    /// <summary>
    /// Loads the in-car driver mode payload for a specific car.
    /// Provides data optimized for in-car display to drivers during an event.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="car">The car number/identifier.</param>
    /// <returns>In-car payload containing position, gaps, lap times, and flags, or null if not available.</returns>
    /// <response code="200">Returns the in-car payload data.</response>
    /// <remarks>
    /// In-car data includes current position, gap to cars ahead/behind, best lap comparison, and flag status.
    /// </remarks>
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<InCarPayload>(StatusCodes.Status200OK)]
    public virtual async Task<InCarPayload?> LoadInCarPayload(int eventId, string car)
    {
        Logger.LogTrace("{m} for event {eventId}, car {car}", nameof(LoadInCarPayload), eventId, car);
        var cache = cacheMux.GetDatabase();
        var cacheKey = string.Format(Consts.IN_CAR_DATA, eventId, car);
        var json = await cache.StringGetAsync(cacheKey);
        if (json.IsNullOrEmpty)
        {
            Logger.LogWarning("In-car data for event {eventId} car {car} not found in cache", eventId, car);
            return null;
        }
        return JsonSerializer.Deserialize<InCarPayload>(json.ToString());
    }

    #endregion

    #region Flags

    /// <summary>
    /// Loads flag history for a specific session.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="sessionId">The unique identifier of the session.</param>
    /// <returns>A list of flag durations showing when each flag state was active.</returns>
    /// <response code="200">Returns the list of flag durations.</response>
    /// <remarks>
    /// Flag types include Green, Yellow (caution), Red (stopped), White (final lap), Checkered (finished), and Black.
    /// </remarks>
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<List<FlagDuration>>(StatusCodes.Status200OK)]
    public virtual async Task<List<FlagDuration>> LoadFlags(int eventId, int sessionId)
    {
        Logger.LogTrace("{m} for event {eventId}, session {sessionId}", nameof(LoadFlags), eventId, sessionId);
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
