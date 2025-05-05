using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.StatusApi.Models;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Formats.Tar;
using System.Text.Json;

namespace RedMist.StatusApi.Controllers;

[ApiController]
[Route("[controller]/[action]")]
[Authorize]
public class EventsController : ControllerBase
{
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly HybridCache hcache;
    private readonly IConnectionMultiplexer cacheMux;

    private ILogger Logger { get; }


    public EventsController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, HybridCache hcache, IConnectionMultiplexer cacheMux)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.hcache = hcache;
        this.cacheMux = cacheMux;
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
        using var db = await tsContext.CreateDbContextAsync();
        var summaries = await (
        from e in db.Events
        join o in db.Organizations on e.OrganizationId equals o.Id
        join s in db.Sessions on e.Id equals s.EventId
        where e.IsActive && !e.IsDeleted && s.IsLive
        group new { e, o, s } by new
        {
            e.Id,
            e.OrganizationId,
            OrganizationName = o.Name,
            EventName = e.Name,
            EventDate = e.StartDate,
            TrackName = e.TrackName,
            Schedule = e.Schedule
        } into g
        select new EventListSummary
        {
            Id = g.Key.Id,
            OrganizationId = g.Key.OrganizationId,
            OrganizationName = g.Key.OrganizationName,
            EventName = g.Key.EventName,
            EventDate = g.Key.EventDate.ToString("yyyy-MM-dd"),
            IsLive = true, // At least one session in the group is live
            TrackName = g.Key.TrackName,
            Schedule = g.Key.Schedule
        }).ToListAsync();

        return summaries;
    }

    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType<List<EventListSummary>>(StatusCodes.Status200OK)]
    public async Task<List<EventListSummary>> LoadLiveAndRecentEvents()
    {
        Logger.LogTrace("LoadLiveAndRecentEvents");

        using var db1 = await tsContext.CreateDbContextAsync();
        var recentEvents = await (
            from e in db1.Events
            join o in db1.Organizations on e.OrganizationId equals o.Id
            where !e.IsDeleted
            where !db1.Sessions.Any(s => s.EventId == e.Id && s.IsLive)
            orderby e.StartDate descending
            select new EventListSummary
            {
                Id = e.Id,
                OrganizationId = e.OrganizationId,
                OrganizationName = o.Name,
                EventName = e.Name,
                EventDate = e.StartDate.ToString("yyyy-MM-dd"),
                IsLive = false,
                TrackName = e.TrackName,
            })
            .Take(100)
            .ToListAsync();
        var liveEvents = await LoadLiveEvents();

        // Merge recent and live events
        return [.. liveEvents, .. recentEvents];
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
    public async Task<List<CarPosition>> LoadCarLaps(int eventId, int sessionId, string carNumber)
    {
        Logger.LogTrace("GetCarPositions for event {eventId}", eventId);
        using var context = tsContext.CreateDbContext();
        var laps = await context.CarLapLogs
            .Where(c => c.EventId == eventId && c.SessionId == sessionId && c.CarNumber == carNumber && c.Timestamp == context.CarLapLogs
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

    #region Sessions

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

    #endregion

    #region Competitor Metadata

    [HttpGet]
    [ProducesResponseType<CompetitorMetadata>(StatusCodes.Status200OK)]
    public async Task<CompetitorMetadata?> LoadCompetitorMetadata(int eventId, string car)
    {
        Logger.LogTrace("LoadCompetitorMetadata for event {eventId}, car {car}", eventId, car);
        return await GetCompetitorMetadata(eventId, car);
    }

    private async Task<CompetitorMetadata?> GetCompetitorMetadata(int eventId, string car)
    {
        var key = string.Format(Consts.COMPETITOR_METADATA, car, eventId);
        return await hcache.GetOrCreateAsync(key,
            async cancel => await LoadDbCompetitorMetadata(eventId, car));
    }

    private async Task<CompetitorMetadata?> LoadDbCompetitorMetadata(int eventId, string car)
    {
        using var db = await tsContext.CreateDbContextAsync();
        return await db.CompetitorMetadata.FirstOrDefaultAsync(x => x.EventId == eventId && x.CarNumber == car);
    }

    #endregion

    #region Control Logs

    [HttpGet]
    [ProducesResponseType<List<ControlLogEntry>>(StatusCodes.Status200OK)]
    public async Task<List<ControlLogEntry>> LoadControlLog(int eventId)
    {
        Logger.LogTrace("LoadControlLog for event {eventId}", eventId);
        var logCacheKey = string.Format(Consts.CONTROL_LOG, eventId);
        var cache = cacheMux.GetDatabase();
        var json = await cache.StringGetAsync(logCacheKey);
        if (json.IsNullOrEmpty)
        {
            Logger.LogWarning("Control log for event {eventId} not found in cache", eventId);
            return [];
        }
        var ccl = JsonSerializer.Deserialize<CarControlLogs>(json!);
        return ccl?.ControlLogEntries ?? [];
    }

    [HttpGet]
    [ProducesResponseType<CarControlLogs>(StatusCodes.Status200OK)]
    public async Task<CarControlLogs?> LoadCarControlLogs(int eventId, string car)
    {
        Logger.LogTrace("LoadCarControlLog for event {eventId} car {c}", eventId, car);
        var carLogEntryKey = string.Format(Consts.CONTROL_LOG_CAR, eventId, car);
        var cache = cacheMux.GetDatabase();
        var json = await cache.StringGetAsync(carLogEntryKey);
        if (json.IsNullOrEmpty)
        {
            Logger.LogWarning("Control log for event {eventId} car {c} not found in cache", eventId, car);
            return null;
        }
        return JsonSerializer.Deserialize<CarControlLogs>(json!);
    }

    #endregion
}
