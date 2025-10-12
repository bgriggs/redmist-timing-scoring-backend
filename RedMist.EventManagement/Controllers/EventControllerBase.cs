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
/// Base controller for Event management across API versions
/// </summary>
[ApiController]
[Authorize]
public abstract class EventControllerBase : ControllerBase
{
    protected readonly IDbContextFactory<TsContext> tsContext;
    protected readonly IConnectionMultiplexer cacheMux;
    protected ILogger Logger { get; }


    protected EventControllerBase(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IConnectionMultiplexer cacheMux)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.cacheMux = cacheMux;
    }


    [HttpGet]
    [ProducesResponseType<List<EventSummary>>(StatusCodes.Status200OK)]
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

    [HttpGet]
    [ProducesResponseType<Event>(StatusCodes.Status200OK)]
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

    [HttpPost]
    [ProducesResponseType<int>(StatusCodes.Status200OK)]
    public virtual async Task<int> SaveNewEvent(Event newEvent)
    {
        Logger.LogTrace("SaveNewEvent {event}", newEvent.Name);
        var clientId = User.FindFirstValue("client_id");
        using var context = await tsContext.CreateDbContextAsync();
        var org = await context.Organizations.FirstOrDefaultAsync(x => x.ClientId == clientId) ?? throw new Exception("Organization not found");
        newEvent.OrganizationId = org.Id;
        context.Events.Add(newEvent);
        await context.SaveChangesAsync();
        
        // Publish event configuration change notification
        await PublishEventConfigurationChangedAsync(newEvent.Id);
        
        return newEvent.Id;
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public virtual async Task UpdateEvent(Event @event)
    {
        Logger.LogTrace("UpdateEvent {event}", @event.Name);
        var clientId = User.FindFirstValue("client_id");
        using var context = await tsContext.CreateDbContextAsync();
        var org = await context.Organizations.FirstOrDefaultAsync(x => x.ClientId == clientId) ?? throw new Exception("Organization not found");
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
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public virtual async Task UpdateEventStatusActive(int eventId)
    {
        Logger.LogTrace("UpdateEventStatusActive {event}", eventId);
        var clientId = User.FindFirstValue("client_id");
        using var context = await tsContext.CreateDbContextAsync();
        var org = await context.Organizations.FirstOrDefaultAsync(x => x.ClientId == clientId) ?? throw new Exception("Organization not found");
        var dbEvent = await context.Events.FirstOrDefaultAsync(x => x.Id == eventId && x.OrganizationId == org.Id);
        if (dbEvent != null)
        {
            await context.Database.ExecuteSqlRawAsync("UPDATE Events SET IsActive=0 WHERE OrganizationId=@p0", org.Id);
            await context.Database.ExecuteSqlRawAsync("UPDATE Events SET IsActive=1 WHERE ID=@p0", eventId);
            
            // Publish event configuration change notification
            await PublishEventConfigurationChangedAsync(eventId);
        }
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public virtual async Task DeleteEvent(int eventId)
    {
        Logger.LogTrace("DeleteEvent {event}", eventId);
        var clientId = User.FindFirstValue("client_id");
        using var context = await tsContext.CreateDbContextAsync();
        var org = await context.Organizations.FirstOrDefaultAsync(x => x.ClientId == clientId) ?? throw new Exception("Organization not found");
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
    }

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
