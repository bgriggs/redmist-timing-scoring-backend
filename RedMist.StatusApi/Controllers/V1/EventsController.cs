using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using RedMist.Database;
using RedMist.TimingCommon.Extensions;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;

namespace RedMist.StatusApi.Controllers.V1;

/// <summary>
/// Version 1 Events API controller.
/// Provides endpoints for retrieving event information, sessions, and timing data.
/// </summary>
/// <remarks>
/// Routes:
/// <list type="bullet">
/// <item>v1/Events/[action] - Versioned route</item>
/// <item>Events/[action] - Legacy unversioned route (for backward compatibility)</item>
/// </list>
/// </remarks>
[Route("v{version:apiVersion}/[controller]/[action]")]
[Route("[controller]/[action]")] // Also handle legacy unversioned routes
[ApiVersion("1.0")]
public class EventsController : EventsControllerBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EventsController"/> V1 class.
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
    /// V1 returns the legacy Payload object format.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="sessionId">The unique identifier of the session.</param>
    /// <returns>Session results as a Payload object, or null if not found.</returns>
    /// <response code="200">Returns the Payload object with session results.</response>
    /// <remarks>
    /// <para>Version: 1.0</para>
    /// <para>The Payload object contains event status, car positions, entries, and flag information.</para>
    /// <para>For V2 API, use the V2 endpoint which returns SessionState format.</para>
    /// </remarks>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType<Payload>(StatusCodes.Status200OK)]
    public async Task<Payload?> LoadSessionResults(int eventId, int sessionId)
    {
        Logger.LogTrace("GetSessionResults for event {eventId}, session {sessionId}", eventId, sessionId);
        using var context = await tsContext.CreateDbContextAsync();
        var result = await context.SessionResults.FirstOrDefaultAsync(r => r.EventId == eventId && r.SessionId == sessionId);

        // Use the session state to generate the payload if available
        Payload? payload = null;
        if (result != null && result.SessionState != null && result.SessionState.SessionId > 0)
        {
            payload = result.SessionState.ToPayload();
        }

        return payload ?? result?.Payload;
    }
}
