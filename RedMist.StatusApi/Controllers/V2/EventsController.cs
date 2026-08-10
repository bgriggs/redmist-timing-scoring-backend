using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.StatusApi.Filters;
using RedMist.TimingCommon;
using RedMist.TimingCommon.Extensions;
using RedMist.TimingCommon.LapTiming;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.StatusApi.Controllers.V2;

/// <summary>
/// Version 2 Events API controller.
/// Provides enhanced endpoints with improved data models for retrieving event information and timing data.
/// </summary>
/// <remarks>
/// Route: v2/Events/[action]
/// </remarks>
[Route("v{version:apiVersion}/[controller]/[action]")]
[ApiVersion("2.0")]
public class EventsController : EventsControllerBase
{
    private const string TRACK_MAP_RENDER_KEY = "track-map-render:{0}"; // {0} = eventId

    private static readonly MemoryCacheEntryOptions trackMapCacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1),
    };

    /// <summary>
    /// How long "this event has no map" is remembered. An event acquires its map mid-session, and
    /// the window while clients wait for one is exactly when the most of them are polling, so a miss
    /// has to be cheap without hiding a map that has just arrived. Seconds does both; the minute a
    /// hit is held would not.
    /// </summary>
    private static readonly MemoryCacheEntryOptions missCacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5),
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="EventsController"/> V2 class.
    /// </summary>
    /// <param name="loggerFactory">Factory to create loggers.</param>
    /// <param name="tsContext">Database context factory for timing and scoring data.</param>
    /// <param name="hcache">Hybrid cache for distributed caching.</param>
    /// <param name="cacheMux">Redis connection multiplexer.</param>
    /// <param name="memoryCache">In-memory cache for frequently accessed data.</param>
    /// <param name="httpClientFactory">Factory to create HTTP clients for inter-service communication.</param>
    public EventsController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
        HybridCache hcache, IConnectionMultiplexer cacheMux, IMemoryCache memoryCache,
        IHttpClientFactory httpClientFactory)
        : base(loggerFactory, tsContext, hcache, cacheMux, memoryCache, httpClientFactory)
    {
    }

    /// <summary>
    /// Loads session results for a specific event and session.
    /// V2 returns the SessionState object format (breaking change from V1).
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="sessionId">The unique identifier of the session.</param>
    /// <returns>Session results as a SessionState object, or null if not found.</returns>
    /// <response code="200">Returns the SessionState object with session results.</response>
    /// <remarks>
    /// <para>Version: 2.0</para>
    /// <para>The SessionState object provides an improved data structure compared to V1's Payload format.</para>
    /// <para>Breaking change from V1: Returns SessionState instead of Payload.</para>
    /// </remarks>
    [AllowAnonymous]
    [RequireEventAccessCode]
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<SessionState>(StatusCodes.Status200OK)]
    public async Task<SessionState?> LoadSessionResults(int eventId, int sessionId)
    {
        Logger.LogTrace("GetSessionResults for event {eventId}, session {sessionId}", eventId, sessionId);
        using var context = await tsContext.CreateDbContextAsync();
        var result = await context.SessionResults.FirstOrDefaultAsync(r => r.EventId == eventId && r.SessionId == sessionId);

        // Use the session state to generate the payload if available
        SessionState? sessionState = null;
        if (result != null && result.Payload != null && !string.IsNullOrEmpty(result.Payload.EventStatus?.EventId))
        {
            sessionState = result.Payload.ToSessionState();
        }

        return sessionState ?? result?.SessionState;
    }

    /// <summary>
    /// Retrieves the current UI version information.
    /// </summary>
    /// <returns>The current UI version information.</returns>
    /// <response code="200">Returns the UI version information.</response>
    /// <remarks>
    /// <para>Version: 2.0</para>
    /// <para>This endpoint returns version information for the frontend UI applications.</para>
    /// <para>The data is cached with a 15-minute expiration to reduce database load.</para>
    /// </remarks>
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<UIVersionInfo>(StatusCodes.Status200OK)]
    public async Task<UIVersionInfo> GetUIVersionInfoAsync()
    {
        Logger.LogTrace("GetUIVersionInfo");
        
        var cacheKey = "ui-version-info";
        var options = new HybridCacheEntryOptions 
        { 
            Expiration = TimeSpan.FromMinutes(15),
            LocalCacheExpiration = TimeSpan.FromMinutes(15)
        };
        
        return await hcache.GetOrCreateAsync(cacheKey,
          async cancel =>
            {
                using var context = await tsContext.CreateDbContextAsync(cancel);
                var versionInfo = await context.UIVersions.FirstOrDefaultAsync(cancel);
      
                // Return default version if none exists in database
                return versionInfo ?? new UIVersionInfo();
            },
            options, cancellationToken: default);
    }

    /// <summary>
    /// Retrieves a paginated list of archived events, ordered by start date in descending order.
    /// </summary>
    /// <param name="offset">The zero-based index of the first archived event to retrieve. Must be greater than or equal to 0.</param>
    /// <param name="take">The maximum number of archived events to return. Must be between 1 and 100.</param>
    /// <returns>An <see cref="ActionResult{T}">ActionResult</see> containing a list of <see cref="EventListSummary"/> objects
    /// representing the archived events. Returns a 400 Bad Request response if <paramref name="take"/> exceeds 100.</returns>
    [HttpGet]
    [AllowAnonymous]
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
                EventName = e.HideName ? string.Empty : e.Name,
                EventDate = e.StartDate.ToString("yyyy-MM-dd"),
                IsLive = false,
                IsSimulation = e.IsSimulation,
                IsArchived = e.IsArchived,
                IsPrivate = e.IsPrivate,
                HideName = e.HideName,
                TrackName = e.TrackName,
            })
            .Skip(offset)
            .Take(take)
            .ToListAsync();
        return Ok(archivedEvents);
    }

    #region Track Map

    /// <summary>
    /// Retrieves the learned track map for an event, projected into a form a client can draw
    /// directly.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <returns>The projected track map, or 404 when the event has no usable map yet.</returns>
    /// <response code="200">Returns the track map ready for rendering.</response>
    /// <response code="404">No track map has been learned for this event yet.</response>
    /// <response code="503">Neither the cache nor the database could be read.</response>
    /// <remarks>
    /// <para>Version: 2.0</para>
    /// <para>
    /// The map is learned from car GPS positions over clean laps, so it does not exist at the start
    /// of an event and appears once enough laps have been driven. A 404 therefore means "not yet",
    /// not "never" - a client polling for it should keep trying rather than give up. A 503 means the
    /// question could not be answered at all, and is the only response worth reporting as a fault.
    /// </para>
    /// <para>
    /// Points carry both latitude/longitude and an x/y projection in meters, north-west origin with
    /// y increasing downward, uniformly scaled so a single scale factor fits the track to a
    /// viewport. See <see cref="TrackMapRender"/> for the full coordinate contract.
    /// </para>
    /// </remarks>
    [AllowAnonymous]
    [RequireEventAccessCode]
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<TrackMapRender>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public virtual async Task<ActionResult<TrackMapRender>> LoadTrackMap(int eventId)
    {
        Logger.LogTrace("{m} for event {eventId}", nameof(LoadTrackMap), eventId);

        var cacheKey = string.Format(TRACK_MAP_RENDER_KEY, eventId);
        if (memoryCache.TryGetValue(cacheKey, out TrackMapRender? cached))
        {
            // A cached null is a recent miss, held briefly so that polling for a map the event has
            // not learned yet does not hit both stores on every request.
            return cached != null ? Ok(cached) : NoMap();
        }

        var (map, readFailed) = await LoadTrackMapAsync(eventId, HttpContext.RequestAborted);
        if (map == null)
        {
            // "We could not look" and "there is nothing there" are different answers, and only the
            // first is a fault. Neither is cached: an outage should be retried, not remembered.
            if (readFailed)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "The track map could not be read");

            memoryCache.Set(cacheKey, (TrackMapRender?)null, missCacheOptions);
            return NoMap();
        }

        // A map is keyed by event in both stores, so one carrying a different event's id was
        // mis-written. Serving it would put another track on this event's screen.
        if (map.EventId != eventId)
        {
            Logger.LogWarning("Track map stored for event {eventId} claims to be event {mapEventId}; ignoring it",
                eventId, map.EventId);
            return NoMap();
        }

        var trackName = await LoadTrackNameAsync(eventId, HttpContext.RequestAborted);

        TrackMapRender? render;
        try
        {
            render = TrackMapProjector.Project(map, trackName);
        }
        catch (Exception ex)
        {
            // Project is written to reject an unusable map rather than throw, but it reads a
            // structure that arrives from storage. Misses are barely cached, so letting a throw
            // escape would 500 every poll for this event until someone repaired the stored value.
            Logger.LogError(ex, "Error projecting the track map for event {eventId}", eventId);
            return NoMap();
        }

        if (render == null)
        {
            // A stored map with too few points or no extent. Nothing a client can draw, and nothing
            // it can do about it, so it reads the same as not having one.
            Logger.LogWarning("Track map for event {eventId} could not be projected: {points} points, {len} m",
                eventId, map.Points?.Count ?? 0, map.TotalLengthMeters);
            return NoMap();
        }

        // Once learned a map changes only when start/finish is recalibrated, which is rare and never
        // urgent, so this is cached to keep the projection off the hot path when a field of clients
        // loads the same event at once.
        memoryCache.Set(cacheKey, render, trackMapCacheOptions);
        return Ok(render);
    }

    private NotFoundObjectResult NoMap() => NotFound("No track map has been learned for this event yet");

    /// <summary>
    /// Reads the event's learned map from the cache the event processor publishes to, falling back
    /// to the database. The cache is the live copy and the database is the durable one; an event
    /// whose processor has shut down has only the latter, so both are needed to serve finished
    /// events as well as running ones.
    ///
    /// Reports whether every source it tried failed, so the caller can tell an event with no map
    /// from an event whose map could not be looked up.
    /// </summary>
    private async Task<(TrackMap? Map, bool ReadFailed)> LoadTrackMapAsync(int eventId, CancellationToken cancellationToken)
    {
        try
        {
            var json = await cacheMux.GetDatabase().StringGetAsync(string.Format(Consts.TRACK_MAP_KEY, eventId));
            if (!json.IsNullOrEmpty)
            {
                var map = JsonSerializer.Deserialize<TrackMap>(json.ToString());
                if (map != null)
                    return (map, false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The database still has it, so a cache problem should not deny the client a map.
            Logger.LogError(ex, "Error reading the cached track map for event {eventId}", eventId);
        }

        try
        {
            using var db = await tsContext.CreateDbContextAsync(cancellationToken);
            var map = await db.TrackMaps.AsNoTracking()
                .Where(t => t.EventId == eventId)
                .Select(t => t.Map)
                .FirstOrDefaultAsync(cancellationToken);
            return (map, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client hung up. Routine on an endpoint meant to be polled, and not a fault.
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading the stored track map for event {eventId}", eventId);

            // The durable store is the authority on whether a map exists, so failing to read it
            // means the question went unanswered rather than answered "no".
            return (null, true);
        }
    }

    /// <summary>
    /// The event's track name, for labelling the map. Absent names are not worth failing over.
    /// </summary>
    private async Task<string?> LoadTrackNameAsync(int eventId, CancellationToken cancellationToken)
    {
        try
        {
            using var db = await tsContext.CreateDbContextAsync(cancellationToken);
            return await db.Events
                .Where(e => e.Id == eventId)
                .Select(e => e.TrackName)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading the track name for event {eventId}", eventId);
            return null;
        }
    }

    #endregion
}
