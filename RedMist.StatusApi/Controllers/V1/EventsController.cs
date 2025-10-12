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

[Route("v{version:apiVersion}/[controller]/[action]")]
[Route("[controller]/[action]")] // Also handle legacy unversioned routes
[ApiVersion("1.0")]
public class EventsController : EventsControllerBase
{
    public EventsController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
        HybridCache hcache, IConnectionMultiplexer cacheMux, IMemoryCache memoryCache,
        IHttpClientFactory httpClientFactory)
        : base(loggerFactory, tsContext, hcache, cacheMux, memoryCache, httpClientFactory)
    {
    }

    /// <summary>
    /// V1: Returns Payload object
    /// </summary>
    [HttpGet]
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
