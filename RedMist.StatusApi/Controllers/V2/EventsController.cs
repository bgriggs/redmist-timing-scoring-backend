using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using RedMist.Database;
using RedMist.TimingCommon.Extensions;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;

namespace RedMist.StatusApi.Controllers.V2;

[Route("v{version:apiVersion}/[controller]/[action]")]
[ApiVersion("2.0")]
public class EventsController : EventsControllerBase
{
    public EventsController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
        HybridCache hcache, IConnectionMultiplexer cacheMux, IMemoryCache memoryCache,
        IHttpClientFactory httpClientFactory)
        : base(loggerFactory, tsContext, hcache, cacheMux, memoryCache, httpClientFactory)
    {
    }

    /// <summary>
    /// V2: Returns SessionState object (breaking change from V1)
    /// </summary>
    [HttpGet]
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
}
