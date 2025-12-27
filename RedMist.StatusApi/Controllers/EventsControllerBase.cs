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
using RedMist.TimingCommon.Extensions;

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
    [ProducesResponseType<List<Event>>(StatusCodes.Status200OK)]
    public virtual async Task<ActionResult<List<Event>>> LoadEvents(DateTime startDateUtc)
    {
        Logger.LogTrace("{m} for {d}", nameof(LoadEvents), startDateUtc);

        var duration = DateTime.UtcNow - startDateUtc;
        if (duration > TimeSpan.FromDays(30))
        {
            Logger.LogWarning("LoadEvents called with startDateUtc {d} exceeding 30 days", startDateUtc);
            return BadRequest("startDateUtc exceeds maximum range of 30 days");
        }

        using var context = await tsContext.CreateDbContextAsync();
        
        // Load events with sessions in a single query
        var results = await (
            from e in context.Events
            join o in context.Organizations on e.OrganizationId equals o.Id
            where e.StartDate >= startDateUtc && !e.IsDeleted
            select new
            {
                e.Id,
                e.Name,
                e.StartDate,
                e.EventUrl,
                e.TrackName,
                e.CourseConfiguration,
                e.Distance,
                e.Broadcast,
                e.Schedule,
                e.IsLive,
                e.IsArchived,
                e.IsSimulation,
                OrganizationName = o.Name,
                OrganizationWebsite = o.Website,
                o.ControlLogType,
                Sessions = context.Sessions.Where(s => s.EventId == e.Id).ToArray()
            }).ToListAsync();

        // Map to Event DTOs
        var eventDtos = results.Select(result => new Event
        {
            EventId = result.Id,
            EventName = result.Name,
            EventDate = result.StartDate.ToString(),
            EventUrl = result.EventUrl,
            Sessions = result.Sessions,
            OrganizationName = result.OrganizationName,
            OrganizationWebsite = result.OrganizationWebsite,
            TrackName = result.TrackName,
            CourseConfiguration = result.CourseConfiguration,
            Distance = result.Distance,
            Broadcast = result.Broadcast,
            Schedule = result.Schedule,
            HasControlLog = !string.IsNullOrEmpty(result.ControlLogType),
            IsLive = result.IsLive,
            IsArchived = result.IsArchived,
            IsSimulation = result.IsSimulation,
        }).ToList();

        return Ok(eventDtos);
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
        Logger.LogTrace(nameof(LoadLiveEvents));

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
                   e.IsArchived,
                   e.IsSimulation,
                   OrganizationName = o.Name,
                   EventName = e.Name,
                   EventDate = e.StartDate,
                   TrackName = e.TrackName,
                   Schedule = e.Schedule,
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
                   Schedule = g.Key.Schedule,
                   IsArchived = g.Key.IsArchived,
                   IsSimulation = g.Key.IsSimulation,
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
            where !e.IsDeleted && !e.IsLive && !e.IsArchived
            orderby e.StartDate descending
            select new EventListSummary
            {
                Id = e.Id,
                OrganizationId = e.OrganizationId,
                OrganizationName = o.Name,
                EventName = e.Name,
                EventDate = e.StartDate.ToString("yyyy-MM-dd"),
                IsLive = false,
                IsSimulation = e.IsSimulation,
                TrackName = e.TrackName,
            })
            .Take(25)
            .ToListAsync();
        var liveEvents = await LoadLiveEvents();

        return [.. liveEvents, .. recentEvents];
    }

    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<List<EventListSummary>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public virtual async Task<ActionResult<List<EventListSummary>>> LoadArchivedEventsAsync(int offset, int take)
    {
        Logger.LogTrace(nameof(LoadArchivedEventsAsync));
        if (take > 100)
            return BadRequest("Take parameter exceeds maximum of 100");

        using var db = await tsContext.CreateDbContextAsync();
        var archivedEvents = await (
            from e in db.Events
            join o in db.Organizations on e.OrganizationId equals o.Id
            where !e.IsDeleted && e.IsArchived
            orderby e.StartDate descending
            select new EventListSummary
            {
                Id = e.Id,
                OrganizationId = e.OrganizationId,
                OrganizationName = o.Name,
                EventName = e.Name,
                EventDate = e.StartDate.ToString("yyyy-MM-dd"),
                IsLive = e.IsLive,
                IsSimulation = e.IsSimulation,
                IsArchived = e.IsArchived,
                TrackName = e.TrackName,
            })
            .Skip(offset)
            .Take(take)
            .ToListAsync();
        return Ok(archivedEvents);
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
        var result = await (
            from e in context.Events
            join o in context.Organizations on e.OrganizationId equals o.Id
            where e.Id == eventId && !e.IsDeleted
            select new
            {
                e.Id,
                e.Name,
                e.StartDate,
                e.EventUrl,
                e.TrackName,
                e.CourseConfiguration,
                e.Distance,
                e.Broadcast,
                e.Schedule,
                e.IsLive,
                e.IsArchived,
                e.IsSimulation,
                OrganizationName = o.Name,
                OrganizationWebsite = o.Website,
                o.ControlLogType,
                Sessions = context.Sessions.Where(s => s.EventId == e.Id).ToArray()
            }).FirstOrDefaultAsync();

        if (result == null)
            return NotFound("event");

        return new Event
        {
            EventId = result.Id,
            EventName = result.Name,
            EventDate = result.StartDate.ToString(),
            EventUrl = result.EventUrl,
            Sessions = result.Sessions,
            OrganizationName = result.OrganizationName,
            OrganizationWebsite = result.OrganizationWebsite,
            TrackName = result.TrackName,
            CourseConfiguration = result.CourseConfiguration,
            Distance = result.Distance,
            Broadcast = result.Broadcast,
            Schedule = result.Schedule,
            HasControlLog = !string.IsNullOrEmpty(result.ControlLogType),
            IsLive = result.IsLive,
            IsArchived = result.IsArchived,
            IsSimulation = result.IsSimulation,
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

        var lapsData = await (
            from lap in context.CarLapLogs
            where lap.EventId == eventId
                && lap.SessionId == sessionId
                && lap.CarNumber == carNumber
                && lap.LapNumber > 0
            group lap by lap.Id into g
            let maxTimestamp = g.Max(x => x.Timestamp)
            select g.FirstOrDefault(x => x.Timestamp == maxTimestamp) != null
                ? g.FirstOrDefault(x => x.Timestamp == maxTimestamp)!.LapData
                : null
        ).ToListAsync();

        var carPositions = new List<CarPosition>(lapsData.Count);
        foreach (var lapData in lapsData)
        {
            if (string.IsNullOrEmpty(lapData))
                continue;

            try
            {
                var cp = JsonSerializer.Deserialize<CarPosition>(lapData);
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

    /// <summary>
    /// Gets the current real-time session state as JSON from the event processor service.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <returns>The current session state as a SessionState JSON object.</returns>
    /// <response code="200">Returns the SessionState object as JSON.</response>
    /// <response code="404">Event processor endpoint not found or session state unavailable.</response>
    /// <response code="408">Request timeout.</response>
    /// <response code="500">Internal server error.</response>
    /// <remarks>
    /// This endpoint retrieves the current SessionState from the event processor service
    /// and returns it as JSON for easy consumption by web clients.
    /// </remarks>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType(typeof(SessionState), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status408RequestTimeout)]
    public virtual async Task<IActionResult> GetCurrentSessionStateJson(int eventId)
    {
        var result = await GetCurrentSessionState(eventId);

        if (result is FileStreamResult fileResult)
        {
            try
            {
                var sessionState = await MessagePack.MessagePackSerializer.DeserializeAsync<SessionState>(fileResult.FileStream);

                if (sessionState == null)
                {
                    Logger.LogWarning("Failed to deserialize SessionState for event {eventId}", eventId);
                    return NotFound();
                }

                return Ok(sessionState);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error processing session state for event {eventId}", eventId);
                return StatusCode(500, "Internal server error");
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the current session state in legacy Payload format from the event processor service.
    /// This method will eventually be removed.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <returns>The current session state as a legacy Payload object.</returns>
    /// <response code="200">Returns the legacy Payload object.</response>
    /// <response code="404">Event processor endpoint not found or session state unavailable.</response>
    /// <response code="500">Internal server error.</response>
    /// <remarks>
    /// This endpoint retrieves the current SessionState from the event processor service
    /// and converts it to the legacy Payload format for backward compatibility.
    /// </remarks>
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType(typeof(Payload), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<IActionResult> GetCurrentLegacySessionPayload(int eventId)
    {
        var result = await GetCurrentSessionState(eventId);

        if (result is FileStreamResult fileResult)
        {
            try
            {
                var sessionState = await MessagePack.MessagePackSerializer.DeserializeAsync<SessionState>(fileResult.FileStream);

                if (sessionState == null)
                {
                    Logger.LogWarning("Failed to deserialize SessionState for event {eventId}", eventId);
                    return NotFound();
                }

                var payload = sessionState.ToPayload();
                return Ok(payload);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error processing session state for event {eventId}", eventId);
                return StatusCode(500, "Internal server error");
            }
        }

        return result;
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

        return await context.FlagLog
            .Where(f => f.EventId == eventId && f.SessionId == sessionId)
            .Select(f => new FlagDuration
            {
                Flag = f.Flag,
                StartTime = f.StartTime,
                EndTime = f.EndTime,
            })
            .ToListAsync();
    }

    #endregion
}
