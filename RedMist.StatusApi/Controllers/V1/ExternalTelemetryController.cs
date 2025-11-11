using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using RedMist.Backend.Shared;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarVideo;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.StatusApi.Controllers.V1;

[Route("v{version:apiVersion}/[controller]/[action]")]
[Route("[controller]/[action]")] // Also handle legacy unversioned routes
[ApiVersion("1.0")]
public class ExternalTelemetryController : Controller
{
    private ILogger Logger { get; }
    private readonly IConnectionMultiplexer cacheMux;
    private readonly V2.EventsController eventsController;


    public ExternalTelemetryController(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, V2.EventsController eventsController)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.eventsController = eventsController;
    }


    /// <summary>
    /// Updates the information for one or more drivers. Each instance requires either the eventId and CarNumber or the car's
    /// transponder. To clear the name, send an empty string for DriverName.
    /// </summary>
    /// <param name="drivers">A list of driver information objects to update. Each object must contain sufficient identifying information,
    /// such as a car number and event ID or a transponder ID. The list cannot be null or empty.</param>
    /// <returns>An IActionResult indicating the result of the operation. Returns 200 OK if the update succeeds, 400 Bad Request
    /// if the input is invalid, 401 Unauthorized if the user is not authorized, or 500 Internal Server Error if an
    /// unexpected error occurs.</returns>
    [HttpPost]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UpdateDriversAsync([FromBody]List<DriverInfo> drivers)
    {
        if (!User.IsInRole("ext-telem"))
            return Forbid();
        if (drivers == null || drivers.Count == 0)
            return BadRequest("No drivers found");

        Logger.LogTrace("{m}", nameof(UpdateDriversAsync));
        var cache = cacheMux.GetDatabase();
        foreach (var driver in drivers)
        {
            try
            {
                var json = JsonSerializer.Serialize(driver);
                string key = string.Empty;
                if (!string.IsNullOrEmpty(driver.CarNumber) && driver.EventId > 0)
                {
                    key = string.Format(Consts.EVENT_DRIVER_KEY, driver.EventId, driver.CarNumber);
                }
                else if (driver.TransponderId > 0)
                {
                    key = string.Format(Consts.DRIVER_TRANSPONDER_KEY, driver.TransponderId);
                }
                else
                {
                    Logger.LogWarning("Skipping driver update with insufficient info");
                    continue;
                }

                // Check cache to see if driver exists
                var existingDriverJson = await cache.StringGetAsync(key);
                bool changed = false;
                if (existingDriverJson.HasValue)
                {
                    changed = existingDriverJson != json;
                }
                else
                {
                    changed = true;
                }

                // Set new driver info to update the expiration
                await cache.StringSetAsync(key, json, expiry: TimeSpan.FromMinutes(10));

                if (changed)
                {
                    if (!string.IsNullOrEmpty(driver.CarNumber) && driver.EventId > 0)
                    {
                        await SendEventStreamsAsync(json, string.Format(Consts.EVENT_DRIVER_CHANGE_STREAM_FIELD, driver.EventId), driver.EventId);
                    }
                    else
                    {
                        var eventIds = await GetLiveEventIdsAsync();
                        await SendEventStreamsAsync(json, Consts.DRIVER_CHANGE_TRANSPONDER_FIELD, eventIds);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error setting event driver info");
            }
        }
        return Ok();
    }


    /// <summary>
    /// Updates car videos for one or more vehicles. Each instance requires either the eventId and CarNumber or the car's
    /// transponder.
    /// </summary>
    /// <param name="videos">A list of video metadata objects to be stored and processed. Cannot be null or empty.</param>
    /// <returns>An IActionResult indicating the result of the operation. Returns 200 OK if successful, 400 Bad Request if the
    /// input is invalid, 401 Unauthorized if the user is not authorized, or 500 Internal Server Error if an error
    /// occurs during processing.</returns>
    [HttpPost]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateCarVideosAsync([FromBody] List<VideoMetadata> videos)
    {
        if (!User.IsInRole("ext-telem"))
            return Forbid();

        Logger.LogTrace("{m}", nameof(UpdateCarVideosAsync));
        if (videos == null || videos.Count == 0)
            return BadRequest("No video data found");

        var cache = cacheMux.GetDatabase();
        foreach (var video in videos)
        {
            try
            {
                var json = JsonSerializer.Serialize(video);
                var key = string.Format(Consts.EVENT_VIDEO_KEY, video.EventId, video.CarNumber, video.TransponderId);

                // Check cache to see if driver exists
                var existingDriverJson = await cache.StringGetAsync(key);
                bool changed = false;
                if (existingDriverJson.HasValue)
                {
                    changed = existingDriverJson != json;
                }
                else
                {
                    changed = true;
                }

                // Set new video info to update the expiration
                await cache.StringSetAsync(key, json, expiry: TimeSpan.FromMinutes(10));

                if (changed)
                {
                    if (video.EventId > 0)
                    {
                        await SendEventStreamsAsync(json, string.Format(Consts.EVENT_VIDEO_CHANGE_STREAM_FIELD, video.EventId), video.EventId);
                    }
                    else // Broadcast to all live events
                    {
                        var eventIds = await GetLiveEventIdsAsync();
                        await SendEventStreamsAsync(json, string.Format(Consts.EVENT_VIDEO_CHANGE_STREAM_FIELD, 999999), eventIds);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error setting video data");
                return StatusCode(StatusCodes.Status500InternalServerError, "An error occurred while processing the request");
            }
        }
        return Ok();
    }

    private async Task SendEventStreamsAsync(string json, string field, params List<int> eventIds)
    {
        var cache = cacheMux.GetDatabase();
        foreach (var eventId in eventIds)
        {
            var streamId = string.Format(Consts.EVENT_STATUS_STREAM_KEY, eventId);
            await cache.StreamAddAsync(streamId, field, json);
        }
    }

    private async Task<List<int>> GetLiveEventIdsAsync()
    {
        var liveEvents = await eventsController.LoadLiveEvents();
        if (liveEvents != null)
        {
            return [.. liveEvents.Select(static e => e.Id).Distinct()];
        }
        return [];
    }
}
