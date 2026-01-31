using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using RedMist.Database;
using RedMist.TimingCommon;
using RedMist.TimingCommon.Extensions;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;

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
                EventName = e.Name,
                EventDate = e.StartDate.ToString("yyyy-MM-dd"),
                IsLive = false,
                IsSimulation = e.IsSimulation,
                IsArchived = e.IsArchived,
                TrackName = e.TrackName,
            })
            .Skip(offset)
            .Take(take)
            .ToListAsync();
        return Ok(archivedEvents);
    }
}
