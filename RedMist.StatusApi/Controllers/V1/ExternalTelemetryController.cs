using Asp.Versioning;
using k8s.KubeConfigModels;
using Microsoft.AspNetCore.Mvc;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarVideo;
using StackExchange.Redis;
using System.Globalization;
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
    /// if the input is invalid, 401 Unauthorized if the user is not authorized, 423 if the record is set by a higher priority source,
    /// or 500 Internal Server Error if an unexpected error occurs.</returns>
    [HttpPost]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status423Locked)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UpdateDriversAsync([FromBody] List<DriverInfo> drivers)
    {
        var clientId = User.Claims.First(c => c.Type == "azp").Value;
        if (string.IsNullOrEmpty(clientId))
            return BadRequest("No client ID found");
        // All relay apps and external telemetry API apps are allowed
        bool isRelaySource = clientId.StartsWith("relay", true, CultureInfo.InvariantCulture);
        if (!isRelaySource && !User.IsInRole("ext-telem"))
            return Forbid();
        if (drivers == null || drivers.Count == 0)
            return BadRequest("No drivers found");

        Logger.LogTrace("{m} source: {s}", nameof(UpdateDriversAsync), clientId);

        var cache = cacheMux.GetDatabase();
        foreach (var driver in drivers)
        {
            try
            {
                var dis = new DriverInfoSource(driver, clientId, DateTime.UtcNow);
                var json = JsonSerializer.Serialize(dis);
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
                    try
                    {
                        var existingDis = JsonSerializer.Deserialize<DriverInfoSource>(existingDriverJson!);
                        if (existingDis == null)
                        {
                            changed = true;
                        }
                        // Prioritize the relay source for change detection
                        else if (isRelaySource)
                        {
                            // Check to see if there is a name set by a non-relay source and the relay source is trying to clear it
                            if (!existingDis.ClientId.StartsWith("relay", true, CultureInfo.InvariantCulture) 
                                && !string.IsNullOrWhiteSpace(existingDis.DriverInfo.DriverName) 
                                && string.IsNullOrWhiteSpace(dis.DriverInfo.DriverName))
                            {
                                // Reject the change to prioritizing using the name from another source when the relay does not have one
                                return StatusCode(StatusCodes.Status423Locked, "Record is set with data from another source that has a name");
                            }
                            changed = existingDis.EqualsDriverInfo(dis);
                        }
                        // Non-relay source, only mark as changed if the existing source is also non-relay
                        else if (!existingDis.ClientId.StartsWith("relay", true, CultureInfo.InvariantCulture))
                        {
                            changed = existingDis.EqualsDriverInfo(dis);
                        }
                        else
                        {
                            Logger.LogDebug("Rejecting change for non-relay source over existing relay source");
                            return StatusCode(StatusCodes.Status423Locked, "Record is locked by a higher priority user");
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e, "Error deserializing existing driver info");
                        changed = true;
                    }
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
