using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Database;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Controllers;

[ApiController]
[Route("[controller]/[action]")]
[Authorize]
public class TimingAndScoringController : ControllerBase
{
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly HybridCache hcache;

    private ILogger Logger { get; }


    public TimingAndScoringController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, HybridCache hcache)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.hcache = hcache;
    }


    [HttpGet]
    [ProducesResponseType<Event[]>(StatusCodes.Status200OK)]
    public async Task<Event[]> LoadEvents(DateTime startDateUtc)
    {
        Logger.LogTrace("LoadEvents");

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
                EventUrl = dbEvent.e.EventUrl,
                Sessions = sessions,
                OrganizationName = dbEvent.o.Name,
                OrganizationWebsite = dbEvent.o.Website,
                OrganizationLogo = dbEvent.o.Logo,
                TrackName = dbEvent.e.TrackName,
                CourseConfiguration = dbEvent.e.CourseConfiguration,
                Distance = dbEvent.e.Distance,
                Broadcast = dbEvent.e.Broadcast,
                Schedule = dbEvent.e.Schedule,
                HasControlLog = !string.IsNullOrEmpty(dbEvent.o.ControlLogType)
            };
            eventDtos.Add(eventDto);
        }

        return [.. eventDtos];
    }

    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType<List<EventListSummary>>(StatusCodes.Status200OK)]
    public async Task<List<EventListSummary>> LoadLiveEvents()
    {
        Logger.LogTrace("LoadLiveEvents");

        using var db2 = await tsContext.CreateDbContextAsync();
        var dbLiveEvents = await db2.Events
            .Join(db2.Organizations, e => e.OrganizationId, o => o.Id, (e, o) => new { e, o })
            .Join(db2.Sessions, eo => eo.e.Id, s => s.EventId, (eo, s) => new { eo, s })
            .Where(x => !x.eo.e.IsDeleted && x.eo.e.IsActive && x.s.IsLive)
            .OrderByDescending(x => x.eo.e.StartDate)
            .ToListAsync();

        var liveEvents = dbLiveEvents
            .Select(x => new EventListSummary
            {
                Id = x.eo.e.Id,
                OrganizationId = x.eo.o.Id,
                OrganizationName = x.eo.o.Name,
                EventName = x.eo.e.Name,
                EventDate = x.eo.e.StartDate.ToString(),
                IsLive = true,
                TrackName = x.eo.e.TrackName,
                Schedule = x.eo.e.Schedule
            })
            .ToList();

        return liveEvents;
    }

    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType<List<EventListSummary>>(StatusCodes.Status200OK)]
    public async Task<List<EventListSummary>> LoadLiveAndRecentEvents()
    {
        Logger.LogTrace("LoadLiveAndRecentEvents");

        using var db1 = await tsContext.CreateDbContextAsync();
        var dbRecentEventsTask = db1.Events
            .Join(db1.Organizations, e => e.OrganizationId, o => o.Id, (e, o) => new { e, o })
            .Join(db1.Sessions, eo => eo.e.Id, s => s.EventId, (eo, s) => new { eo, s })
            .Where(x => !x.eo.e.IsDeleted && !x.s.IsLive)
            .OrderByDescending(x => x.eo.e.StartDate)
            .Take(100)
            .ToListAsync();

        var liveEvents = await LoadLiveEvents();
        var recentEvents = await dbRecentEventsTask;
        
        var recentEventList = recentEvents
            .Select(x => new EventListSummary
            {
                Id = x.eo.e.Id,
                OrganizationId = x.eo.o.Id,
                OrganizationName = x.eo.o.Name,
                EventName = x.eo.e.Name,
                EventDate = x.eo.e.StartDate.ToString(),
                TrackName = x.eo.e.TrackName,
            })
            .ToList();

        // Merge recent and live events
        return [.. liveEvents, .. recentEventList];
    }


    [HttpGet]
    [ProducesResponseType<Event>(StatusCodes.Status200OK)]
    public async Task<Event?> LoadEvent(int eventId)
    {
        Logger.LogTrace("LoadEvent");

        using var context = await tsContext.CreateDbContextAsync();
        var dbEvent = await context.Events
            .Join(context.Organizations, e => e.OrganizationId, o => o.Id, (e, o) => new { e, o })
            .Where(x => x.e.Id == eventId && !x.e.IsDeleted).FirstOrDefaultAsync();

        if (dbEvent == null)
            return null;

        var sessions = await context.Sessions.Where(x => x.EventId == dbEvent.e.Id).ToArrayAsync();
        return new Event
        {
            EventId = dbEvent.e.Id,
            EventName = dbEvent.e.Name,
            EventDate = dbEvent.e.StartDate.ToString(),
            EventUrl = dbEvent.e.EventUrl,
            Sessions = sessions,
            OrganizationName = dbEvent.o.Name,
            OrganizationWebsite = dbEvent.o.Website,
            OrganizationLogo = dbEvent.o.Logo,
            TrackName = dbEvent.e.TrackName,
            CourseConfiguration = dbEvent.e.CourseConfiguration,
            Distance = dbEvent.e.Distance,
            Broadcast = dbEvent.e.Broadcast,
            Schedule = dbEvent.e.Schedule,
            HasControlLog = !string.IsNullOrEmpty(dbEvent.o.ControlLogType)
        };
    }

    [HttpGet]
    [ProducesResponseType<List<CarPosition>>(StatusCodes.Status200OK)]
    public async Task<List<CarPosition>> LoadCarLaps(int eventId, string carNumber)
    {
        Logger.LogTrace("GetCarPositions for event {eventId}", eventId);
        using var context = tsContext.CreateDbContext();
        var sessions = await context.Sessions.Where(s => s.EventId == eventId).ToListAsync();
        var activeSession = sessions.FirstOrDefault(s => s.IsLive);
        if (activeSession == null)
        {
            activeSession = sessions.OrderByDescending(s => s.StartTime)?.First();
        }
        if (activeSession == null)
            return [];

        var laps = await context.CarLapLogs
            .Where(c => c.EventId == eventId && c.SessionId == activeSession.Id && c.CarNumber == carNumber && c.Timestamp == context.CarLapLogs
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
                Logger.LogError(ex, "Error deserializing car position data for event {eventId}, car {carNumber}", eventId, carNumber);
            }
        }

        return carPositions;
    }

    [HttpGet]
    [ProducesResponseType<List<Session>>(StatusCodes.Status200OK)]
    public async Task<List<Session>> LoadSessions(int eventId)
    {
        Logger.LogTrace("GetSessions for event {eventId}", eventId);
        using var context = await tsContext.CreateDbContextAsync();
        return await context.Sessions.Where(s => s.EventId == eventId).ToListAsync();
    }

    [HttpGet]
    [ProducesResponseType<Payload>(StatusCodes.Status200OK)]
    public async Task<Payload?> LoadSessionResults(int eventId, int sessionId)
    {
        Logger.LogTrace("GetSessionResults for event {eventId}, session {sessionId}", eventId, sessionId);
        using var context = await tsContext.CreateDbContextAsync();
        var result = await context.SessionResults.FirstOrDefaultAsync(r => r.EventId == eventId && r.SessionId == sessionId);
        return result?.Payload;
    }

    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrganizationIcon(int organizationId)
    {
        Logger.LogTrace("GetOrganizationIcon for organization {organizationId}", organizationId);

        var cacheKey = $"org-icon-{organizationId}";
        var data = await hcache.GetOrCreateAsync(cacheKey, 
            async entry => await LoadOrganizationIcon(organizationId), 
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(30) });

        using var context = await tsContext.CreateDbContextAsync();
        var organization = await context.Organizations.FirstOrDefaultAsync(o => o.Id == organizationId);
        if (organization == null || organization.Logo == null)
            return NotFound();
        var mimeType = GetImageMimeType(organization.Logo);
        return File(organization.Logo, mimeType);
    }

    private async Task<byte[]> LoadOrganizationIcon(int organizationId)
    {
        using var context = await tsContext.CreateDbContextAsync();
        var organization = await context.Organizations.FirstOrDefaultAsync(o => o.Id == organizationId);
        return organization?.Logo ?? [];
    }

    private static string GetImageMimeType(byte[] imageBytes)
    {
        // Quick magic number detection (PNG, JPEG, GIF, BMP)
        if (imageBytes.Length >= 4)
        {
            if (imageBytes[0] == 0x89 && imageBytes[1] == 0x50 &&
                imageBytes[2] == 0x4E && imageBytes[3] == 0x47)
                return "image/png";

            if (imageBytes[0] == 0xFF && imageBytes[1] == 0xD8)
                return "image/jpeg";

            if (imageBytes[0] == 0x47 && imageBytes[1] == 0x49 &&
                imageBytes[2] == 0x46)
                return "image/gif";

            if (imageBytes[0] == 0x42 && imageBytes[1] == 0x4D)
                return "image/bmp";
        }

        return "application/octet-stream"; // fallback
    }
}
