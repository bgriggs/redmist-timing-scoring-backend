using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventManagement.Models;
using RedMist.EventManagement.Viewership;
using RedMist.TimingCommon.Models;

namespace RedMist.EventManagement.Controllers;

/// <summary>
/// Reads the viewership an organization's events drew: finished events from their post-event report,
/// and a running event live.
/// </summary>
/// <remarks>
/// <para>
/// A finished event's numbers are produced once, by the post-event report job, and stored. Nothing
/// here recomputes them: the report an organizer reads on the web has to be the same one that was
/// emailed to them.
/// </para>
/// <para>
/// A running event has no report yet, so <see cref="Live"/> computes its numbers on request - but
/// through the rules the report job uses, shared rather than copied, because a second implementation
/// of the aggregation would eventually disagree with the first.
/// </para>
/// <para>
/// Every count is a count of CONNECTIONS, not of people. A viewer who loses signal and reconnects
/// opens a second session, so the session count is the least meaningful number reported and must
/// never be labelled "viewers". Concurrency is the robust measure, because a reconnect closes one
/// session as it opens another.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
public abstract class ViewershipControllerBase : ControllerBase
{
    protected readonly IDbContextFactory<TsContext> tsContext;
    protected readonly IConfiguration configuration;
    protected readonly HybridCache hcache;
    protected readonly TimeProvider clock;
    protected ILogger Logger { get; }

    /// <summary>The largest page this will serve, however large a page is asked for.</summary>
    private const int MaxTake = 100;

    /// <summary>
    /// How far back the report job will look for events to process.
    /// </summary>
    /// <remarks>
    /// Mirrors PostEventReport:LookbackDays, which the report job reads from the same key, so setting
    /// it centrally keeps the two in step. It is used only to say whether an event can still be
    /// picked up - being a day out makes one row read "pending" slightly longer, which is a far
    /// smaller error than reporting every event from last season as pending forever.
    /// </remarks>
    private int LookbackDays => configuration.GetValue("PostEventReport:LookbackDays", 14);

    /// <summary>
    /// The cache key for one event's live viewership, qualified by the state of its sessions.
    /// </summary>
    private const string LIVE_CACHE_KEY = "viewership-live-{0}-{1}";

    /// <summary>
    /// How long a computed live answer is served before it is computed again.
    /// </summary>
    /// <remarks>
    /// Every dashboard open on a running event polls once a minute, and each computation reads every
    /// viewer session the event has - a row per connection, and phones reconnect constantly. Half the
    /// poll interval means no dashboard is shown numbers more than thirty seconds old, while every
    /// dashboard on the event shares one computation.
    /// </remarks>
    private static readonly HybridCacheEntryOptions liveCacheOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(30),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };

    protected ViewershipControllerBase(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
        IConfiguration configuration, HybridCache hcache, TimeProvider clock)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.configuration = configuration;
        this.hcache = hcache;
        this.clock = clock;
    }

    /// <summary>
    /// Lists the reports an organization has, newest event first.
    /// </summary>
    /// <param name="organizationId">The organization whose reports to list.</param>
    /// <param name="skip">Reports to skip.</param>
    /// <param name="take">Reports to return, capped at 100.</param>
    /// <response code="200">The reports, which may be an empty list.</response>
    /// <response code="400">The organization id is unusable, or paging arguments are negative.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <remarks>
    /// Only reports that produced viewership appear. A report row exists for every event the job
    /// processed, and one that found nothing to say carries no summary - listing those would show an
    /// organizer rows with no numbers behind them. A SUPPRESSED report is not excluded: the numbers
    /// were computed before the opt-out was consulted, and declining the email is not a reason to
    /// withhold them from the organization's own page. Its state says so.
    ///
    /// Deleted events are excluded, as they are everywhere else in this service.
    /// </remarks>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType<List<ViewershipReportSummaryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public virtual async Task<ActionResult<List<ViewershipReportSummaryDto>>> Reports(int organizationId,
        int skip = 0, int take = 20)
    {
        Logger.LogTrace("{m} {org}", nameof(Reports), organizationId);
        if (organizationId < 1)
        {
            return BadRequest("organizationId is required.");
        }
        if (skip < 0 || take < 1)
        {
            return BadRequest("skip must not be negative and take must be at least 1.");
        }

        using var db = await tsContext.CreateDbContextAsync();
        if (!await CallerOrganizations.IsPermittedAsync(db, User, organizationId))
        {
            return new List<ViewershipReportSummaryDto>();
        }

        // The summary is selected explicitly rather than left to the navigation property. Project is
        // a method call, which no provider can translate, so composing it into the query would
        // evaluate it against a report whose Viewership had never been loaded - a null reference in
        // memory, and a translation failure against Postgres.
        var rows = await db.PostEventReports
            .AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.Viewership != null)
            .Join(db.Events.Where(e => !e.IsDeleted), r => r.EventId, e => e.Id, (r, e) => new
            {
                Report = r,
                Viewership = r.Viewership!,
                EventName = e.Name,
                EventStart = e.StartDate,
            })
            .OrderByDescending(x => x.EventStart)
            .ThenByDescending(x => x.Report.EventId)
            .Skip(skip)
            .Take(Math.Min(take, MaxTake))
            .ToListAsync();

        return rows.ConvertAll(x => Project(x.Report, x.Viewership, x.EventName, x.EventStart));
    }

    /// <summary>
    /// Where each of an organization's finished events stands with the report job.
    /// </summary>
    /// <param name="organizationId">The organization to report on.</param>
    /// <param name="skip">Events to skip.</param>
    /// <param name="take">Events to return, capped at 100.</param>
    /// <response code="200">The events, newest first. May be empty.</response>
    /// <response code="400">The organization id is unusable, or paging arguments are out of range.</response>
    /// <remarks>
    /// <para>
    /// Answers "why is there no report for this event" without anybody having to guess. The job
    /// writes a row for EVERY event it processes, including ones nobody watched, so the presence of
    /// a row is a fact about what has happened rather than an estimate of when it will.
    /// </para>
    /// <para>
    /// Deliberately not a predicted date. Computing one would mean duplicating the settle window and
    /// the job's schedule into this service, where they would go stale the day somebody edited the
    /// CronJob - and nothing would fail, the page would just start giving out dates that were never
    /// true.
    /// </para>
    /// <para>
    /// Carries the name and end date so a page can render from this and the reports list alone. The
    /// events needing "no report yet" are exactly the ones absent from that list, so without a name
    /// here a caller would have to go to another service purely to label rows it already has ids for.
    /// </para>
    /// </remarks>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType<List<EventReportStatusDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public virtual async Task<ActionResult<List<EventReportStatusDto>>> ReportStatus(int organizationId,
        int skip = 0, int take = 20)
    {
        Logger.LogTrace("{m} {org}", nameof(ReportStatus), organizationId);
        if (organizationId < 1)
        {
            return BadRequest("organizationId is required.");
        }
        if (skip < 0 || take < 1)
        {
            return BadRequest("skip must not be negative and take must be at least 1.");
        }

        using var db = await tsContext.CreateDbContextAsync();
        if (!await CallerOrganizations.IsPermittedAsync(db, User, organizationId))
        {
            return new List<EventReportStatusDto>();
        }

        // Paged like the reports list rather than answered whole: this is every finished event for
        // all time, and the largest organizations already hold dozens.
        //
        // Simulations and events still flagged live are excluded because the job excludes them, so
        // listing them could only ever say "pending" about something that will never be processed.
        var now = DateTime.UtcNow;
        var eligibleAfter = now.AddDays(-LookbackDays);
        return await db.Events
            .AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && !e.IsDeleted && e.EndDate <= now
                        && !e.IsSimulation && !e.IsLive)
            .OrderByDescending(e => e.StartDate)
            .ThenByDescending(e => e.Id)
            .Skip(skip)
            .Take(Math.Min(take, MaxTake))
            .Select(e => new EventReportStatusDto
            {
                EventId = e.Id,
                EventName = e.Name,
                EventEndDate = e.EndDate,
                Eligible = e.EndDate >= eligibleAfter,
                State = db.PostEventReports
                    .Where(r => r.EventId == e.Id)
                    .Select(r => r.State)
                    .FirstOrDefault(),
            })
            .ToListAsync();
    }

    /// <summary>
    /// How many reports an organization has, for paging.
    /// </summary>
    /// <param name="organizationId">The organization to count for.</param>
    /// <response code="200">The count, which may be zero.</response>
    /// <response code="400">The organization id is unusable.</response>
    /// <remarks>
    /// Counts exactly what <see cref="Reports"/> lists, so the two cannot disagree about how many
    /// pages there are. "Load more until nothing comes back" is not the same fact as "12 reports",
    /// which is one an organizer wants to read.
    /// </remarks>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType<int>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public virtual async Task<ActionResult<int>> ReportCount(int organizationId)
    {
        Logger.LogTrace("{m} {org}", nameof(ReportCount), organizationId);
        if (organizationId < 1)
        {
            return BadRequest("organizationId is required.");
        }

        using var db = await tsContext.CreateDbContextAsync();
        if (!await CallerOrganizations.IsPermittedAsync(db, User, organizationId))
        {
            return 0;
        }

        return await db.PostEventReports
            .AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.Viewership != null)
            .Join(db.Events.Where(e => !e.IsDeleted), r => r.EventId, e => e.Id, (r, e) => r.Id)
            .CountAsync();
    }

    /// <summary>
    /// One event's viewership in full: the roll-up, each racing session, and every bucket.
    /// </summary>
    /// <param name="eventId">The event to report on.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">The event id is unusable.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="404">No report with viewership exists for that event, or it is not the caller's.</response>
    /// <remarks>
    /// The event names its own organization, so no organization id is taken here; membership is
    /// checked against the organization the event belongs to. A report the caller may not read and
    /// an event with no report are the same answer on purpose - distinguishing them would confirm
    /// that somebody else's event exists.
    /// </remarks>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType<ViewershipReportDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<ViewershipReportDto>> Report(int eventId)
    {
        Logger.LogTrace("{m} {event}", nameof(Report), eventId);
        if (eventId < 1)
        {
            return BadRequest("eventId is required.");
        }

        using var db = await tsContext.CreateDbContextAsync();

        // Identify and authorize before loading anything. The detail below is large, and doing it
        // the other way round let any authenticated caller make the server materialize thousands of
        // rows for any event id by counting upwards - and made "not yours" take visibly longer than
        // "does not exist", which is the difference this endpoint is supposed to hide.
        var meta = await db.PostEventReports
            .AsNoTracking()
            .Where(r => r.EventId == eventId)
            .Join(db.Events.Where(e => !e.IsDeleted), r => r.EventId, e => e.Id,
                (r, e) => new { r.OrganizationId, EventName = e.Name, EventStart = e.StartDate })
            .FirstOrDefaultAsync();

        if (meta == null || !await CallerOrganizations.IsPermittedAsync(db, User, meta.OrganizationId))
        {
            return NotFound();
        }

        // Split, because two collection navigations at one level otherwise produce one result set of
        // |Sessions| x |Buckets| rows with the report - both jsonb columns included - repeated on
        // every one of them. Buckets run to a row per fifteen minutes per client type per session,
        // so the product is thousands of rows for an ordinary event and tens of thousands at the
        // window cap.
        var report = await db.PostEventReports
            .AsNoTracking()
            .AsSplitQuery()
            .Include(r => r.Viewership!).ThenInclude(v => v.Sessions)
            .Include(r => r.Viewership!).ThenInclude(v => v.Buckets)
            .FirstOrDefaultAsync(r => r.EventId == eventId);

        if (report?.Viewership == null)
        {
            return NotFound();
        }

        var viewership = report.Viewership;
        return new ViewershipReportDto
        {
            Summary = Project(report, viewership, meta.EventName, meta.EventStart),
            Sessions = [.. viewership.Sessions
                .OrderBy(s => s.StartUtc)
                .ThenBy(s => s.SessionId)
                .Select(s => new ViewershipSessionDto
                {
                    SessionId = s.SessionId,
                    SessionName = s.SessionName,
                    IsPracticeQualifying = s.IsPracticeQualifying,
                    StartUtc = s.StartUtc,
                    EndUtc = s.EndUtc,
                    TotalViewerMinutes = s.TotalViewerMinutes,
                    MaxConcurrent = s.MaxConcurrent,
                    PeakUtc = s.PeakUtc,
                    TopClientType = s.TopClientType,
                })],
            // Ordered so a caller can walk them straight into a series without sorting: the
            // event-level series first, then each session's, each in time order.
            Buckets = [.. viewership.Buckets
                // HasValue first, not SessionId ?? 0: the feed emits a genuine session id 0, and
                // coalescing onto it would interleave that session's series with the event-level one
                // and hand a caller two series spliced together.
                .OrderBy(b => b.SessionId.HasValue)
                .ThenBy(b => b.SessionId ?? 0)
                .ThenBy(b => b.BucketStartUtc)
                .ThenBy(b => b.ClientType, StringComparer.Ordinal)
                .Select(b => new ViewershipBucketDto
                {
                    SessionId = b.SessionId,
                    BucketStartUtc = b.BucketStartUtc,
                    ClientType = b.ClientType,
                    MinConcurrent = b.MinConcurrent,
                    MaxConcurrent = b.MaxConcurrent,
                    AvgConcurrent = b.AvgConcurrent,
                    ViewerSeconds = b.ViewerSeconds,
                })],
        };
    }

    /// <summary>
    /// A running event's viewership as it stands now: per-session one-minute series and the event's
    /// totals.
    /// </summary>
    /// <param name="eventId">The event to report on.</param>
    /// <response code="200">The viewership so far. An event nobody has watched yet reads as zeros.</response>
    /// <response code="400">The event id is unusable.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="404">No such event, or the caller does not administer its organization.</response>
    /// <remarks>
    /// <para>
    /// Authorized exactly as <see cref="Report"/> is: the event names its own organization, the caller
    /// has to administer it, and an event the caller may not read is the same 404 as one that does not
    /// exist. Deleted events are not found. Nothing beyond the event's own row is read until the
    /// check has passed, so counting upwards through other organizations' event ids costs the server
    /// that lookup and the check, and never a read of their viewers.
    /// </para>
    /// <para>
    /// Computed by <see cref="LiveViewershipCalculator"/>, whose remarks set out what it shares with
    /// the post-event report and where it deliberately differs - including why open rows are counted
    /// to now rather than checked against the live connection hash, and the worst case that leaves.
    /// </para>
    /// <para>
    /// Cached per event for thirty seconds, keyed on the state of the event's sessions and its live
    /// flag as well as the event. The dashboard polls once a minute and once more whenever the session
    /// changes, and that second poll is precisely the one that has to see the change: under a plain
    /// per-event key it would be answered from before the change, and the new session would not
    /// appear until the poll after. The sessions are a handful of rows read on every request anyway;
    /// the viewer sessions are what the cache saves reading.
    /// </para>
    /// </remarks>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType<LiveViewershipDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<LiveViewershipDto>> Live(int eventId)
    {
        Logger.LogTrace("{m} {event}", nameof(Live), eventId);
        if (eventId < 1)
        {
            return BadRequest("eventId is required.");
        }

        var cancellationToken = HttpContext?.RequestAborted ?? CancellationToken.None;
        using var db = await tsContext.CreateDbContextAsync(cancellationToken);

        var evt = await db.Events
            .AsNoTracking()
            .Where(e => e.Id == eventId && !e.IsDeleted)
            .Select(e => new { e.OrganizationId, e.StartDate, e.EndDate, e.IsLive })
            .FirstOrDefaultAsync(cancellationToken);

        if (evt == null || !await CallerOrganizations.IsPermittedAsync(db, User, evt.OrganizationId, cancellationToken))
        {
            return NotFound();
        }

        var sessions = await db.Sessions
            .AsNoTracking()
            .Where(s => s.EventId == eventId)
            .ToListAsync(cancellationToken);

        return await hcache.GetOrCreateAsync(
            string.Format(LIVE_CACHE_KEY, eventId, LiveFingerprint(evt.IsLive, sessions)),
            (EventId: eventId, evt.StartDate, evt.EndDate, evt.IsLive, Sessions: sessions),
            async (state, cancel) =>
            {
                // A context of its own rather than the request's. The cache runs one computation for
                // every caller waiting on the same key, and carries on for the others if the caller
                // that started it goes away - by which point that request's context is disposed.
                await using var context = await tsContext.CreateDbContextAsync(cancel);

                // Only the three columns the numbers are made of, because this is every row the event
                // has.
                var viewers = await context.EventViewerSessions
                    .AsNoTracking()
                    .Where(s => s.EventId == state.EventId)
                    .Select(s => new EventViewerSession { StartUtc = s.StartUtc, EndUtc = s.EndUtc, ClientType = s.ClientType })
                    .ToListAsync(cancel);

                return LiveViewershipCalculator.Compute(state.EventId, state.StartDate, state.EndDate,
                    state.IsLive, state.Sessions, viewers, clock.GetUtcNow().UtcDateTime);
            },
            liveCacheOptions,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// What changes about an event when its session does: a session starting, the latest one ending
    /// or being retired, or the event going live or being torn down.
    /// </summary>
    /// <remarks>
    /// Everything that decides whether a session is running is in here, so a cached answer can never
    /// go on calling a session running after the rows say it has stopped.
    /// </remarks>
    private static string LiveFingerprint(bool eventIsLive, List<Session> sessions)
    {
        var latest = sessions.OrderBy(s => s.StartTime).ThenBy(s => s.Id).LastOrDefault();
        return latest == null
            ? $"{eventIsLive}-none"
            : $"{eventIsLive}-{sessions.Count}-{latest.Id}-{latest.EndTime?.Ticks ?? 0}-{latest.IsLive}";
    }

    /// <summary>
    /// One projection so the list and the detail cannot describe the same report differently.
    /// </summary>
    private static ViewershipReportSummaryDto Project(PostEventReport report, EventViewershipSummary viewership,
        string eventName, DateTime eventStart)
        => new()
        {
            EventId = report.EventId,
            EventName = eventName,
            EventStartDate = eventStart,
            GeneratedUtc = report.GeneratedUtc,
            State = report.State,
            WindowStartUtc = viewership.WindowStartUtc,
            WindowEndUtc = viewership.WindowEndUtc,
            TotalViewerMinutes = viewership.TotalViewerMinutes,
            MaxConcurrent = viewership.MaxConcurrent,
            PeakUtc = viewership.PeakUtc,
            TopClientType = viewership.TopClientType,
            SessionCount = viewership.SessionCount,
            OpenSessions = viewership.OpenSessions,
            AnomalousSessions = viewership.AnomalousSessions,
            TrackOffsetMinutes = viewership.TrackOffsetMinutes,
        };
}
