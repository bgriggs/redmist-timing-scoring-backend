using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingCommon.Models;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Controllers;

[ApiController]
[Route("[controller]/[action]")]
[Authorize]
public class TimingAndScoringController : ControllerBase
{
    private readonly IDbContextFactory<TsContext> tsContext;

    private ILogger Logger { get; }


    public TimingAndScoringController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
    }


    [HttpGet]
    [ProducesResponseType<Event[]>(StatusCodes.Status200OK)]
    public async Task<Event[]> LoadEvents(DateTime startDateUtc)
    {
        Logger.LogTrace("GetEvents");

        using var context = await tsContext.CreateDbContextAsync();
        var dbEvents = await context.Events
            .Join(context.Organizations, e => e.OrganizationId, o => o.Id, (e, o) => new { e, o })
            .Where(x => x.e.StartDate >= startDateUtc && !x.e.IsDeleted).ToArrayAsync();

        // Map to Event model
        List<Event> eventDtos = [];
        foreach (var dbEvent in dbEvents)
        {
            var sessions = await context.Sessions.Where(x => x.EventId == dbEvent.e.Id).ToArrayAsync();
            var eventDto = new Event
            {
                EventId = dbEvent.e.Id,
                EventName = dbEvent.e.Name,
                EventDate = dbEvent.e.StartDate.ToString(),
                Sessions = sessions,
                OrganizationName = dbEvent.o.Name,
                OrganizationWebsite = dbEvent.o.Website,
                OrganizationLogo = dbEvent.o.Logo
            };
            eventDtos.Add(eventDto);
        }

        return [.. eventDtos];
    }

    [HttpGet]
    [ProducesResponseType<Event[]>(StatusCodes.Status200OK)]
    public async Task<List<CarPosition>> LoadCarLaps(int eventId, string carNumber)
    {
        Logger.LogTrace("GetCarPositions for event {0}", eventId);
        using var context = tsContext.CreateDbContext();
        var laps = await context.CarLapLogs
            .Where(c => c.EventId == eventId && c.CarNumber == carNumber && c.Timestamp == context.CarLapLogs
                .Where(r => r.Id == c.Id)
                .Max(r => r.Timestamp)) // When there are multiple of the same lap for the same car, such as from a simulated replay, load the newest one
            .ToListAsync();

        var carPositions = new List<CarPosition>();
        foreach (var lap in laps)
        {
            try
            {
                var cp = JsonSerializer.Deserialize<CarPosition>(lap.LapData);
                if (cp != null)
                {
                    carPositions.Add(cp);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error deserializing car position data for event {0}, car {1}", eventId, carNumber);
            }
        }

        return carPositions;
    }

}
