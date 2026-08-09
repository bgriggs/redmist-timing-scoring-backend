using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Database;
using RedMist.StatusApi.Services;
using RedMist.TimingCommon.Models;

namespace RedMist.StatusApi.Controllers.V1;

[ApiController]
[AllowAnonymous]
[EnableRateLimiting("sponsor-telemetry")]
[Route("v{version:apiVersion}/[controller]/[action]")]
[Route("[controller]/[action]")]
[ApiVersion("1.0")]
public class SponsorTelemetryController : Controller
{
    private const string IMPRESSION = "Impression";
    private const string VIEWABLE_IMPRESSION = "ViewableImpression";
    private const string CLICK_THROUGH = "ClickThrough";
    private const string ENGAGEMENT_DURATION = "EngagementDuration";

    private static readonly HybridCacheEntryOptions SponsorCacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(15),
        LocalCacheExpiration = TimeSpan.FromMinutes(15)
    };

    private ILogger Logger { get; }
    private readonly SponsorTelemetryQueue queue;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly HybridCache hcache;


    public SponsorTelemetryController(ILoggerFactory loggerFactory, SponsorTelemetryQueue queue, IDbContextFactory<TsContext> tsContext, HybridCache hcache)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.queue = queue;
        this.tsContext = tsContext;
        this.hcache = hcache;
    }


    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult SaveImpression(string source, string imageId, string eventId = "")
    {
        Enqueue(source, eventId, imageId, IMPRESSION);
        return Ok();
    }

    /// <summary>
    /// 50% pixels, ≥1s visible
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult SaveViewableImpression(string source, string imageId, string eventId = "")
    {
        Enqueue(source, eventId, imageId, VIEWABLE_IMPRESSION);
        return Ok();
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult SaveClickThrough(string source, string imageId, string eventId = "")
    {
        Enqueue(source, eventId, imageId, CLICK_THROUGH);
        return Ok();
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult SaveEngagementDuration(string source, string imageId, int durationMs, string eventId = "")
    {
        Enqueue(source, eventId, imageId, ENGAGEMENT_DURATION, durationMs);
        return Ok();
    }

    private void Enqueue(string source, string eventId, string imageId, string eventType, int? durationMs = null)
    {
        //Logger.LogTrace("{m} source={s} eventId={e} imageId={i} type={t}", nameof(Enqueue), source, eventId, imageId, eventType);
        queue.TryEnqueue(new SponsorTelemetryEntry(source, eventId, imageId, eventType, durationMs));
    }

    /// <summary>
    /// Gets the sponsors with a currently active subscription.
    /// </summary>
    /// <param name="eventId">
    /// Optional event to scope the list to. When supplied, sponsors excluded by the organization running
    /// that event (typically competitors) are removed from the result. Omit for contexts with no event,
    /// such as the landing page, which shows every active sponsor.
    /// </param>
    [HttpGet]
    [ProducesResponseType<List<SponsorInfo>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<SponsorInfo>>> GetSponsorsAsync(int? eventId = null)
    {
        var sponsors = await hcache.GetOrCreateAsync(
            "sponsors-all",
            async ct =>
            {
                using var context = await tsContext.CreateDbContextAsync(ct);
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                return await context.Sponsors
                    .Where(s => s.SubscriptionStart <= today && (s.SubscriptionEnd == null || s.SubscriptionEnd >= today))
                    .Select(s => new SponsorInfo
                    {
                        Id = s.Id,
                        Name = s.Name,
                        ImageUrl = s.ImageUrl,
                        TargetUrl = s.TargetUrl,
                        AltText = s.AltText,
                        DisplayDurationMs = s.DisplayDurationMs,
                        DisplayPriority = s.DisplayPriority
                    })
                    .ToListAsync(ct);
            },
            SponsorCacheOptions);

        if (eventId is null)
        {
            return Ok(sponsors);
        }

        var excluded = await GetExcludedSponsorIdsAsync(eventId.Value);
        if (excluded.Length == 0)
        {
            return Ok(sponsors);
        }

        // New list rather than a mutation: the list above can be the local cache's own instance,
        // handed to every other request for the next 15 minutes.
        return Ok(sponsors.Where(s => !excluded.Contains(s.Id)).ToList());
    }

    /// <summary>
    /// Gets the sponsors the organization running <paramref name="eventId"/> has excluded from its events.
    /// Returns empty when the event is unknown or the organization has no exclusions, so an unresolvable
    /// event shows the full sponsor list rather than none.
    /// </summary>
    private async Task<int[]> GetExcludedSponsorIdsAsync(int eventId)
    {
        return await hcache.GetOrCreateAsync(
            $"sponsor-exclusions-event-{eventId}",
            eventId,
            async (id, ct) =>
            {
                using var context = await tsContext.CreateDbContextAsync(ct);
                return await (from e in context.Events
                              join x in context.SponsorExclusions on e.OrganizationId equals x.OrganizationId
                              where e.Id == id
                              select x.SponsorId)
                    .ToArrayAsync(ct);
            },
            SponsorCacheOptions);
    }
}
