using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.TimingCommon.Models.Configuration;
using StackExchange.Redis;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace RedMist.EventManagement.Controllers;

/// <summary>
/// Base controller for Event management operations.
/// Provides endpoints for creating, updating, and managing racing events.
/// </summary>
/// <remarks>
/// This is an abstract base controller inherited by versioned controllers.
/// Requires authentication and validates that users can only manage events for their organization. The
/// two event status log endpoints are the exception, and are scoped by event id alone so the relay's
/// simulation tab can replay another organization's event data.
/// </remarks>
[ApiController]
[Authorize]
public abstract class EventControllerBase : ControllerBase
{
    private static readonly Regex AccessCodePattern = new("^[0-9]{1,7}$", RegexOptions.Compiled);

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
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public virtual async Task<ActionResult<List<EventSummary>>> LoadEventSummaries(int organizationId)
    {
        Logger.LogTrace("LoadEventSummaries {org}", organizationId);

        // An id below 1 is not an organization anybody could hold, so it is a malformed request
        // rather than an empty one. Answering with an empty list instead let a caller that had not
        // resolved its organization yet - or had simply forgotten the parameter - show an empty
        // page at every startup with nothing logged anywhere. Refusing it names the mistake.
        if (organizationId < 1)
        {
            return BadRequest("organizationId is required.");
        }

        using var context = await tsContext.CreateDbContextAsync();
        if (!await CallerOrganizations.IsPermittedAsync(context, User, organizationId))
        {
            // An organization the caller does not hold is not an error: nothing to show is a valid
            // answer, and saying more would confirm the organization exists.
            return new List<EventSummary>();
        }

        return await context.Events
            .AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && !e.IsDeleted)
            .OrderByDescending(e => e.StartDate)
            .Select(e => new EventSummary { Id = e.Id, Name = e.Name, StartDate = e.StartDate, EndDate = e.EndDate, IsActive = e.IsActive, IsSimulation = e.IsSimulation, IsArchived = e.IsArchived })
            .ToListAsync();
    }

    /// <summary>
    /// Whether an organization's own Keycloak client is an API client rather than a relay.
    /// </summary>
    /// <remarks>
    /// API organizations exist to feed synthetic data in, so their events are simulations. This is a
    /// property of the organization, not of whoever is calling.
    /// </remarks>
    private static bool IsApiOrganization(TimingCommon.Models.Organization org)
        => org.ClientId.StartsWith("api", StringComparison.OrdinalIgnoreCase);

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
        using var context = await tsContext.CreateDbContextAsync();
        var permitted = await CallerOrganizations.ResolveAsync(context, User);
        return await context.Events
            .AsNoTracking()
            .Where(e => permitted.Contains(e.OrganizationId) && e.Id == eventId && !e.IsDeleted)
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
    public virtual async Task<ActionResult<int>> SaveNewEvent(Event newEvent, int organizationId)
    {
        Logger.LogTrace("SaveNewEvent {event}", newEvent.Name);
        if (newEvent.IsPrivate && !IsAccessCodeValid(newEvent.AccessCode))
            return BadRequest("AccessCode must be 1-7 digits when IsPrivate is true.");

        using var context = await tsContext.CreateDbContextAsync();
        if (!await CallerOrganizations.IsPermittedAsync(context, User, organizationId))
            return NotFound("org");
        // FirstOrDefault, not First: nothing links UserOrganizationMappings to Organizations, so a
        // mapping can outlive the organization it names and be permitted against a row that is gone.
        var org = await context.Organizations.FirstOrDefaultAsync(x => x.Id == organizationId);
        if (org == null)
            return NotFound("org");
        newEvent.OrganizationId = org.Id;
        newEvent.IsSimulation = IsApiOrganization(org);
        newEvent.EnableSourceDataLogging = !newEvent.IsSimulation;
        if (!newEvent.IsPrivate)
            newEvent.AccessCode = null;
        // TimingSource + ExternalConfig persist as-is; external config only applies to external sources.
        if (newEvent.TimingSource != TimingCommon.Models.TimingSource.External)
            newEvent.ExternalConfig = null;
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
        if (@event.IsPrivate && !IsAccessCodeValid(@event.AccessCode))
            return BadRequest("AccessCode must be 1-7 digits when IsPrivate is true.");

        using var context = await tsContext.CreateDbContextAsync();
        var permitted = await CallerOrganizations.ResolveAsync(context, User);

        var dbEvent = await context.Events.FirstOrDefaultAsync(x => x.Id == @event.Id && permitted.Contains(x.OrganizationId));
        if (dbEvent != null)
        {
            var org = await context.Organizations.FirstAsync(x => x.Id == dbEvent.OrganizationId);
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
            dbEvent.IsSimulation = @event.IsSimulation;
            dbEvent.IsArchived = @event.IsArchived;
            dbEvent.IsPrivate = @event.IsPrivate;
            dbEvent.HideName = @event.HideName;
            dbEvent.AccessCode = @event.IsPrivate ? @event.AccessCode : null;
            dbEvent.TimingSource = @event.TimingSource;
            dbEvent.ExternalConfig = @event.TimingSource == TimingCommon.Models.TimingSource.External ? @event.ExternalConfig : null;
            // An API organization exists to produce synthetic data, so its events are simulations
            // whatever the request says. Read from the organization rather than from the caller's
            // token: a person signing in holds the web client's id, which says nothing about the
            // organization the event belongs to, and the previous null-defaults-to-true reading
            // would have turned every event they touched into a permanent simulation.
            dbEvent.IsSimulation = @event.IsSimulation || IsApiOrganization(org);

            await context.SaveChangesAsync();

            // Publish event configuration change notification
            await PublishEventConfigurationChangedAsync(@event.Id);
            return Ok();
        }

        // An event the caller may not act on is refused rather than silently accepted. Scoping used
        // to answer 404 "org" for a caller with no organization and 200 for a caller asking about
        // somebody else's event; one rule now covers both, and neither tells the caller which of the
        // two it was.
        return NotFound("event");
    }

    /// <summary>
    /// Makes one event the active one for its organization, deactivating that organization's others.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="organizationId">The event's own organization, and the only one to deactivate within.</param>
    /// <param name="eventId">The event to activate.</param>
    /// <remarks>
    /// Its own method so the scope can be tested. The deactivation is the most destructive statement
    /// in this controller and it is raw SQL, which the in-memory provider cannot execute - so no test
    /// reached it, and removing its WHERE clause left the whole suite green. Overriding this lets a
    /// test assert which organization was deactivated without needing a database that runs SQL.
    ///
    /// The organization is the event's own, never the set the caller holds: activating an event must
    /// not deactivate the events of another organization the same person administers.
    /// </remarks>
    protected virtual async Task SetActiveEventAsync(TsContext context, int organizationId, int eventId)
    {
        await context.Database.ExecuteSqlRawAsync("UPDATE \"Events\" SET \"IsActive\" = false WHERE \"OrganizationId\" = @p0", organizationId);
        await context.Database.ExecuteSqlRawAsync("UPDATE \"Events\" SET \"IsActive\" = true WHERE \"Id\" = @p0", eventId);
    }

    private static bool IsAccessCodeValid(string? accessCode)
        => !string.IsNullOrEmpty(accessCode) && AccessCodePattern.IsMatch(accessCode);

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
        using var context = await tsContext.CreateDbContextAsync();
        var permitted = await CallerOrganizations.ResolveAsync(context, User);

        var dbEvent = await context.Events.FirstOrDefaultAsync(x => x.Id == eventId && permitted.Contains(x.OrganizationId));
        if (dbEvent != null)
        {
            await SetActiveEventAsync(context, dbEvent.OrganizationId, eventId);

            // Publish event configuration change notification
            await PublishEventConfigurationChangedAsync(eventId);
            return Ok();
        }
        return NotFound("event");
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
        using var context = await tsContext.CreateDbContextAsync();
        var permitted = await CallerOrganizations.ResolveAsync(context, User);

        var dbEvent = await context.Events.FirstOrDefaultAsync(x => x.Id == eventId && permitted.Contains(x.OrganizationId));
        if (dbEvent != null)
        {
            dbEvent.IsDeleted = true;
            await context.SaveChangesAsync();

            // Publish event configuration change notification
            await PublishEventConfigurationChangedAsync(eventId);

            // If the deleted event was active, set the newest event as active
            if (dbEvent.IsActive)
            {
                var newestEvent = await context.Events.OrderByDescending(e => e.StartDate).FirstOrDefaultAsync(e => e.OrganizationId == dbEvent.OrganizationId && !e.IsDeleted);
                if (newestEvent != null)
                {
                    Logger.LogDebug("Reassigning active event for organization {orgId} to event ID {newestEventId}", dbEvent.OrganizationId, newestEvent.Id);
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

                await cache.StreamAddAsync(streamKey, fieldName, eventId.ToString(),
                    maxLength: Consts.EVENT_STATUS_STREAM_MAX_LENGTH, useApproximateMaxLength: true);

                // Drop the cached access record so StatusApi instances re-read the current IsPrivate/AccessCode.
                await cache.KeyDeleteAsync(string.Format(Consts.EVENT_ACCESS, eventId), CommandFlags.FireAndForget);

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

    /// <summary>
    /// Streams the status log entries for an event, optionally scoped to a single session.
    /// </summary>
    /// <remarks>
    /// Deliberately scoped by event id alone, unlike every other endpoint on this controller: the relay's
    /// simulation tab replays another organization's real event data on a dev or test relay, so it reads
    /// logs for events its own client does not own. Any authenticated caller can therefore page any
    /// event's status logs by id. Do not add a <c>client_id</c> filter here without giving the relay
    /// another way to reach that data.
    /// </remarks>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType<IEnumerable<EventStatusLog>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public virtual async IAsyncEnumerable<EventStatusLog> LoadEventLogsAsync(int eventId, int? sessionId, int skip = 0, int take = 1000, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Logger.LogMethodEntry();
        var cappedTake = Math.Min(take, 10000);
        await using var context = await tsContext.CreateDbContextAsync(cancellationToken);
        await foreach (var log in context.EventStatusLogs
            .AsNoTracking()
            .Where(x => x.EventId == eventId && (!sessionId.HasValue || x.SessionId == sessionId.Value))
            .OrderByDescending(x => x.Timestamp)
            .ThenByDescending(x => x.Id)
            .Skip(skip)
            .Take(cappedTake)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken))
        {
            yield return log;
        }
    }

    /// <summary>
    /// Gets the total number of status log entries for an event, optionally scoped to a single session.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="sessionId">Optional session identifier to restrict the count to a single session.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The number of matching log entries.</returns>
    /// <response code="200">Returns the log entry count.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <remarks>
    /// Intended to be called before <see cref="LoadEventLogsAsync"/> so callers can size a progress indicator
    /// for the paged log download. Uses the same filter as <see cref="LoadEventLogsAsync"/>, including its
    /// deliberate lack of organization scoping - see the remarks there before adding a <c>client_id</c>
    /// filter to either one, since the relay calls both and scoping only this one silently zeroes the
    /// progress bar.
    /// </remarks>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType<int>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public virtual async Task<int> LoadEventLogsCountAsync(int eventId, int? sessionId, CancellationToken cancellationToken = default)
    {
        Logger.LogMethodEntry();
        await using var context = await tsContext.CreateDbContextAsync(cancellationToken);
        return await context.EventStatusLogs
            .AsNoTracking()
            .Where(x => x.EventId == eventId && (!sessionId.HasValue || x.SessionId == sessionId.Value))
            .CountAsync(cancellationToken);
    }
}
