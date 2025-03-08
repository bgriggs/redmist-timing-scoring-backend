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
            .Where(s => s.o.ClientId == clientId)
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
            .Where(s => s.o.ClientId == clientId)
            .Select(s => s.e)
            .FirstOrDefaultAsync();
    }
}
