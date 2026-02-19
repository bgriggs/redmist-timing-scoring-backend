using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RedMist.StatusApi.Services;

namespace RedMist.StatusApi.Controllers.V1;

[ApiController]
[AllowAnonymous]
[EnableRateLimiting("sponsor-telemetry")]
[Route("v{version:apiVersion}/[controller]/[action]")]
[Route("[controller]/[action]")]
[ApiVersion("1.0")]
public class SponsorTelemetryControler : Controller
{
    private const string IMPRESSION = "Impression";
    private const string VIEWABLE_IMPRESSION = "ViewableImpression";
    private const string CLICK_THROUGH = "ClickThrough";
    private const string ENGAGEMENT_DURATION = "EngagementDuration";

    private ILogger Logger { get; }
    private readonly SponsorTelemetryQueue queue;

    public SponsorTelemetryControler(ILoggerFactory loggerFactory, SponsorTelemetryQueue queue)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.queue = queue;
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult SaveImpression(string source, string eventId, string imageId)
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
    public IActionResult SaveViewableImpression(string source, string eventId, string imageId)
    {
        Enqueue(source, eventId, imageId, VIEWABLE_IMPRESSION);
        return Ok();
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult SaveClickThrough(string source, string eventId, string imageId)
    {
        Enqueue(source, eventId, imageId, CLICK_THROUGH);
        return Ok();
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult SaveEngagementDuration(string source, string eventId, string imageId, int durationMs)
    {
        Enqueue(source, eventId, imageId, ENGAGEMENT_DURATION, durationMs);
        return Ok();
    }

    private void Enqueue(string source, string eventId, string imageId, string eventType, int? durationMs = null)
    {
        Logger.LogTrace("{m} source={s} eventId={e} imageId={i} type={t}", nameof(Enqueue), source, eventId, imageId, eventType);
        queue.TryEnqueue(new SponsorTelemetryEntry(source, eventId, imageId, eventType, durationMs));
    }
}
