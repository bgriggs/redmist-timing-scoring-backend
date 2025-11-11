using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.TimingCommon.Models.Configuration;
using StackExchange.Redis;
using System.Security.Claims;

namespace RedMist.EventManagement.Controllers;

/// <summary>
/// Base controller for Event management operations.
/// Provides endpoints for creating, updating, and managing racing events.
/// </summary>
/// <remarks>
/// This is an abstract base controller inherited by versioned controllers.
/// Requires authentication and validates that users can only manage events for their organization.
/// </remarks>
[ApiController]
[Authorize]
public abstract class EventControllerBase : ControllerBase
{
    protected readonly IDbContextFactory<TsContext> tsContext;
    protected readonly IConnectionMultiplexer cacheMux;
    protected ILogger Logger { get; }


    /// <summary>
    /// Initializes a new instance of the <see cref="EventControllerBase"/> class.
    /// </summary>
    /// <param name="loggerFactory">Factory to create loggers.</param>
    /// <param name="tsContext">Database context factory for timing and scoring data.</param>
    /// <param name="cacheMux">Redis connection multiplexer for cache and pub/sub operations.</param>
    protected EventControllerBase(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IConnectionMultiplexer cacheMux)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.cacheMux = cacheMux;
    }


    /// <summary>
    /// Loads summary information for all events belonging to the authenticated user's organization.
    /// </summary>
    /// <returns>A list of event summaries ordered by start date (newest first).</returns>
    /// <response code="200">Returns the list of event summaries.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <remarks>
    /// Events are filtered by the authenticated user's client_id to ensure users only see their organization's events.
    /// Deleted events are excluded from results.
    /// </remarks>
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<List<EventSummary>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public virtual async Task<List<EventSummary>> LoadEventSummaries()
    {
        Logger.LogTrace("LoadEventSummaries");
        var clientId = User.FindFirstValue("client_id");
        using var context = await tsContext.CreateDbContextAsync();
        var dbEvents = await context.Events
            .Join(context.Organizations, e => e.OrganizationId, o => o.Id, (e, o) => new { e, o })
            .Where(s => s.o.ClientId == clientId && !s.e.IsDeleted)
            .OrderByDescending(s => s.e.StartDate)
            .Select(s => new EventSummary { Id = s.e.Id, Name = s.e.Name, StartDate = s.e.StartDate, IsActive = s.e.IsActive })
            .ToListAsync();

        return dbEvents;
    }

    /// <summary>
    /// Loads detailed information for a specific event.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <returns>The event details, or null if not found or user is not authorized.</returns>
    /// <response code="200">Returns the event details.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <remarks>
    /// Users can only load events that belong to their organization.
    /// Returns null if the event is not found or the user does not have access to it.
    /// </remarks>
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<Event>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public virtual async Task<Event?> LoadEvent(int eventId)
    {
        Logger.LogTrace("LoadEvent {event}", eventId);
        var clientId = User.FindFirstValue("client_id");
        using var context = await tsContext.CreateDbContextAsync();
        return await context.Events
            .Join(context.Organizations, e => e.OrganizationId, o => o.Id, (e, o) => new { e, o })
            .Where(s => s.o.ClientId == clientId && s.e.Id == eventId && !s.e.IsDeleted)
            .Select(s => s.e)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Creates a new event for the authenticated user's organization.
    /// </summary>
    /// <param name="newEvent">The event details to create.</param>
    /// <returns>The ID of the newly created event.</returns>
    /// <response code="200">Returns the new event ID.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <response code="404">If the user's organization is not found.</response>
    /// <remarks>
    /// The event is automatically associated with the authenticated user's organization.
    /// After creation, a configuration change notification is published to update dependent services.
    /// </remarks>
    [HttpPost]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<int>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<int>> SaveNewEvent(Event newEvent)
    {
        Logger.LogTrace("SaveNewEvent {event}", newEvent.Name);
        var clientId = User.FindFirstValue("client_id");
        using var context = await tsContext.CreateDbContextAsync();
        var org = await context.Organizations.FirstOrDefaultAsync(x => x.ClientId == clientId);
        if (org == null)
            return NotFound("org");
        newEvent.OrganizationId = org.Id;
        context.Events.Add(newEvent);
        await context.SaveChangesAsync();

        // Publish event configuration change notification
        await PublishEventConfigurationChangedAsync(newEvent.Id);

        return newEvent.Id;
    }

    /// <summary>
    /// Updates an existing event's configuration.
    /// </summary>
    /// <param name="event">The event with updated details.</param>
    /// <returns>No content on success.</returns>
    /// <response code="200">Event updated successfully.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <response code="404">If the user's organization or the event is not found.</response>
    /// <remarks>
    /// Users can only update events belonging to their organization.
    /// After update, a configuration change notification is published to update dependent services.
    /// If the event is not found or does not belong to the user's organization, returns 200 OK without making changes.
    /// </remarks>
    [HttpPost]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<IActionResult> UpdateEvent(Event @event)
    {
        Logger.LogTrace("UpdateEvent {event}", @event.Name);
        var clientId = User.FindFirstValue("client_id");
        using var context = await tsContext.CreateDbContextAsync();
        var org = await context.Organizations.FirstOrDefaultAsync(x => x.ClientId == clientId);
        if (org == null)
            return NotFound("org");

        var dbEvent = await context.Events.FirstOrDefaultAsync(x => x.Id == @event.Id && x.OrganizationId == org.Id);
        if (dbEvent != null)
        {
            dbEvent.Name = @event.Name;
            dbEvent.StartDate = @event.StartDate;
            dbEvent.EndDate = @event.EndDate;
            dbEvent.IsActive = @event.IsActive;
            dbEvent.EventUrl = @event.EventUrl;
            dbEvent.Schedule = @event.Schedule;
            dbEvent.EnableSourceDataLogging = @event.EnableSourceDataLogging;
            dbEvent.TrackName = @event.TrackName;
            dbEvent.CourseConfiguration = @event.CourseConfiguration;
            dbEvent.Distance = @event.Distance;
            dbEvent.Broadcast = @event.Broadcast;
            dbEvent.LoopsMetadata = @event.LoopsMetadata;
            await context.SaveChangesAsync();

            // Publish event configuration change notification
            await PublishEventConfigurationChangedAsync(@event.Id);
        }

        return Ok();
    }

    /// <summary>
    /// Sets an event as the active event for the organization.
    /// Deactivates all other events for the same organization.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event to activate.</param>
    /// <returns>No content on success.</returns>
    /// <response code="200">Event status updated successfully.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <response code="404">If the user's organization or the event is not found.</response>
    /// <remarks>
    /// Only one event can be active per organization at a time.
    /// After activation, a configuration change notification is published.
    /// If the event is not found or does not belong to the user's organization, returns 200 OK without making changes.
    /// </remarks>
    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<IActionResult> UpdateEventStatusActive(int eventId)
    {
        Logger.LogTrace("UpdateEventStatusActive {event}", eventId);
        var clientId = User.FindFirstValue("client_id");
        using var context = await tsContext.CreateDbContextAsync();
        var org = await context.Organizations.FirstOrDefaultAsync(x => x.ClientId == clientId);
        if (org == null)
            return NotFound("org");

        var dbEvent = await context.Events.FirstOrDefaultAsync(x => x.Id == eventId && x.OrganizationId == org.Id);
        if (dbEvent != null)
        {
            await context.Database.ExecuteSqlRawAsync("UPDATE \"Events\" SET \"IsActive\" = false WHERE \"OrganizationId\" = @p0", org.Id);
            await context.Database.ExecuteSqlRawAsync("UPDATE \"Events\" SET \"IsActive\" = true WHERE \"Id\" = @p0", eventId);

            // Publish event configuration change notification
            await PublishEventConfigurationChangedAsync(eventId);
        }
        return Ok();
    }

    /// <summary>
    /// Deletes an event.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event to delete.</param>
    /// <returns>No content on success.</returns>
    /// <response code="200">Event deleted successfully.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <response code="404">If the user's organization or the event is not found.</response>
    /// <remarks>
    /// <para>If the deleted event was active, the most recent event is automatically set as active.</para>
    /// <para>A configuration change notification is published after deletion.</para>
    /// <para>If the event is not found or does not belong to the user's organization, completes without making changes.</para>
    /// </remarks>
    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<IActionResult> DeleteEvent(int eventId)
    {
        Logger.LogTrace("DeleteEvent {event}", eventId);
        var clientId = User.FindFirstValue("client_id");
        using var context = await tsContext.CreateDbContextAsync();
        var org = await context.Organizations.FirstOrDefaultAsync(x => x.ClientId == clientId);
        if (org == null)
            return NotFound("org");

        var dbEvent = await context.Events.FirstOrDefaultAsync(x => x.Id == eventId && x.OrganizationId == org.Id);
        if (dbEvent != null)
        {
            dbEvent.IsDeleted = true;
            await context.SaveChangesAsync();

            // Publish event configuration change notification
            await PublishEventConfigurationChangedAsync(eventId);

            // If the deleted event was active, set the newest event as active
            if (dbEvent.IsActive)
            {
                var newestEvent = await context.Events.OrderByDescending(e => e.StartDate).FirstOrDefaultAsync(e => e.OrganizationId == org.Id && !e.IsDeleted);
                if (newestEvent != null)
                {
                    Logger.LogDebug("Reassigning active event for organization {orgId} to event ID {newestEventId}", org.Id, newestEvent.Id);
                    await UpdateEventStatusActive(newestEvent.Id);
                }
            }
        }
        else
        {
            return NotFound("event");
        }

        return Ok();
    }

    /// <summary>
    /// Publishes an event configuration change notification via Redis pub/sub.
    /// Notifies dependent services (e.g., timing processors) to reload event configuration.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event that changed.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="RedisConnectionException">Thrown when Redis connection fails after retries.</exception>
    /// <remarks>
    /// Implements exponential backoff retry logic (up to 3 attempts) for Redis connection issues.
    /// </remarks>
    protected async Task PublishEventConfigurationChangedAsync(int eventId)
    {
        const int maxRetries = 3;
        var retryCount = 0;

        while (retryCount < maxRetries)
        {
            try
            {
                var cache = cacheMux.GetDatabase();
                var streamKey = string.Format(Consts.EVENT_STATUS_STREAM_KEY, eventId);
                var fieldName = $"{Consts.EVENT_CONFIGURATION_CHANGED}-{eventId}-999999";

                await cache.StreamAddAsync(streamKey, fieldName, eventId.ToString());
                Logger.LogDebug("Published event configuration change notification for event {EventId}", eventId);
                return;
            }
            catch (RedisConnectionException ex)
            {
                retryCount++;
                Logger.LogWarning("Redis connection issue publishing event configuration change for event {EventId}, attempt {AttemptNumber}/{MaxRetries}: {Exception}",
                    eventId, retryCount, maxRetries, ex.Message);

                if (retryCount >= maxRetries)
                {
                    Logger.LogError("Failed to publish event configuration change notification for event {EventId} after {MaxRetries} attempts", eventId, maxRetries);
                    throw;
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryCount))); // Exponential backoff
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error publishing event configuration change notification for event {EventId}", eventId);
                throw;
            }
        }
    }
}
