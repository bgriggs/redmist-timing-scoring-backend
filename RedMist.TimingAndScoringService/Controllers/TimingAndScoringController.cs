using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Controllers;

[ApiController]
[Route("[controller]/[action]")]
[Authorize]
public class TimingAndScoringController : ControllerBase
{
    private ILogger Logger { get; }

    public TimingAndScoringController(ILoggerFactory loggerFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }

    [HttpGet]
    [ProducesResponseType<Event[]>(StatusCodes.Status200OK)]
    public Task<Event[]> GetEvents()
    {
        Logger.LogTrace("GetEvents");
        return Task.FromResult(new Event[] { new() { EventId = 1, EventName = "Test", EventDate = "2025-01-01" } });
    }
}
