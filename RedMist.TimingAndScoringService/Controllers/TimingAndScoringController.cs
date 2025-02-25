using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RedMist.TimingAndScoringService.Database;
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

        using var context = tsContext.CreateDbContext();
        var dbEvents = await context.Events.Where(x => x.StartDate >= startDateUtc).ToArrayAsync();

        // Map to Event model
        List<Event> eventDtos = [];
        foreach (var dbEvent in dbEvents)
        {
            var eventDto = new Event
            {
                EventId = dbEvent.Id,
                EventName = dbEvent.Name,
                EventDate = dbEvent.StartDate.ToString()
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
