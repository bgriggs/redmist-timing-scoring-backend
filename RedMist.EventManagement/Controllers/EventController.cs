using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingCommon.Models.Configuration;
using System.Security.Claims;

namespace RedMist.EventManagement.Controllers;

[ApiController]
[Route("[controller]/[action]")]
[Authorize]
public class EventController : ControllerBase
{
    private readonly IDbContextFactory<TsContext> tsContext;

    private ILogger Logger { get; }

    public EventController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
    }

    [HttpGet]
    [ProducesResponseType<List<EventSummary>>(StatusCodes.Status200OK)]
    public async Task<List<EventSummary>> LoadEventSummaries()
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
    public async Task<Event?> LoadEvent(int eventId)
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
    public async Task<int> SaveNewEvent(Event newEvent)
    {
        Logger.LogTrace("SaveNewEvent {event}", newEvent.Name);
        var clientId = User.FindFirstValue("client_id");
        using var context = await tsContext.CreateDbContextAsync();
        var org = await context.Organizations.FirstOrDefaultAsync(x => x.ClientId == clientId) ?? throw new Exception("Organization not found");
        newEvent.OrganizationId = org.Id;
        context.Events.Add(newEvent);
        await context.SaveChangesAsync();
        return newEvent.Id;
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task UpdateEvent(Event @event)
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
        }
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task UpdateEventStatusActive(int eventId)
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
        }
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task DeleteEvent(int eventId)
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
}
