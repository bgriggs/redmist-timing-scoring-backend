using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventManagement.Models;
using RedMist.EventManagement.Operations;
using RedMist.EventManagement.Viewership;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.EventManagement.Controllers;

/// <summary>
/// The site operations page: every running event on the site, its relay, its pods, its viewers and
/// what the relay has sent, across every organization.
/// </summary>
/// <remarks>
/// <para>
/// Read-only. Nothing here starts, stops or changes anything; it reads what the relay hub, the
/// status hub and the orchestrator already write to Redis, and the live flags in the database.
/// </para>
/// <para>
/// Gated on the site-admin realm role, and deliberately not through <c>CallerOrganizations</c>: the
/// page spans organizations no single administrator belongs to, and widening that check instead would
/// widen every organizer endpoint in the service along with it. Event names are shown unredacted for
/// the same reason the gate exists - the people who can read this are the people who run the site.
/// </para>
/// <para>
/// No Kubernetes access. This service has no permissions in the cluster, and the page does not give
/// it any: pod health comes from the orchestrator, which already lists pods, by way of
/// <see cref="Consts.SITE_POD_HEALTH"/>.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = Consts.SITE_ADMIN_ROLE)]
public abstract class SiteOperationsControllerBase : ControllerBase
{
    private const string OVERVIEW_CACHE_KEY = "site-operations-overview";
    private const string OVERALL_VIEWERSHIP_CACHE_KEY = "site-operations-overall-viewership";

    /// <summary>
    /// How long an overview is served before it is assembled again.
    /// </summary>
    /// <remarks>
    /// The page polls every fifteen seconds and takes its fastest-moving figures - viewer counts and
    /// data health - from the status hub every five, so this only has to stop several open copies of
    /// the page each reading every event's counters on every poll.
    /// </remarks>
    private static readonly HybridCacheEntryOptions overviewCacheOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(5),
        LocalCacheExpiration = TimeSpan.FromSeconds(5),
    };

    /// <summary>
    /// How long the site-wide viewership series is served before it is computed again: half the page's
    /// one-minute poll, as for one event's live viewership.
    /// </summary>
    private static readonly HybridCacheEntryOptions overallCacheOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(30),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };

    protected readonly IDbContextFactory<TsContext> tsContext;
    protected readonly IConnectionMultiplexer cacheMux;
    protected readonly HybridCache hcache;
    protected readonly TimeProvider clock;
    private readonly LiveViewershipSource liveSource;

    protected ILogger Logger { get; }

    protected SiteOperationsControllerBase(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
        IConnectionMultiplexer cacheMux, HybridCache hcache, TimeProvider clock)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.cacheMux = cacheMux;
        this.hcache = hcache;
        this.clock = clock;
        liveSource = new LiveViewershipSource(tsContext, hcache, clock);
    }

    /// <summary>
    /// Every active event on the site, with its relay, pods, current viewers and relay message
    /// counts, plus the shared services and every relay connection.
    /// </summary>
    /// <response code="200">The overview. With nothing running it has no events, not an error.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not hold the site-admin role.</response>
    /// <remarks>
    /// <para>
    /// An event is active when it is flagged live in the database, when a relay heartbeat for it is
    /// still in Redis - which includes a relay gone quiet but not yet past the orchestrator's
    /// ten-minute teardown, the case the live flag alone would hide - or when pods are running for it
    /// with neither, which is an orphan the orchestrator has not yet reaped. Simulations are included.
    /// An archived or deleted event can only appear through a heartbeat or pods, and a deleted one
    /// appears with no name because its row is not read.
    /// </para>
    /// <para>
    /// Cached for five seconds.
    /// </para>
    /// </remarks>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType<SiteOperationsOverviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public virtual async Task<ActionResult<SiteOperationsOverviewDto>> Overview()
    {
        Logger.LogTrace("{m}", nameof(Overview));
        var cancellationToken = HttpContext?.RequestAborted ?? CancellationToken.None;
        return await hcache.GetOrCreateAsync(OVERVIEW_CACHE_KEY, this,
            static async (self, cancel) => await self.BuildOverviewAsync(cancel),
            overviewCacheOptions,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// One event's live viewership, exactly as the organizer dashboard reads it, for any event on the
    /// site.
    /// </summary>
    /// <param name="eventId">The event to report on.</param>
    /// <response code="200">The viewership so far. An event nobody has watched yet reads as zeros.</response>
    /// <response code="400">The event id is unusable.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not hold the site-admin role.</response>
    /// <response code="404">No such event, or it has been deleted.</response>
    /// <remarks>
    /// The same code path, shape and cache as <c>ViewershipControllerBase.Live</c>, through
    /// <see cref="LiveViewershipSource"/>; the only difference is that no organization is checked,
    /// because the role already permits every one.
    /// </remarks>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType<LiveViewershipDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<LiveViewershipDto>> Viewership(int eventId)
    {
        Logger.LogTrace("{m} {event}", nameof(Viewership), eventId);
        if (eventId < 1)
        {
            return BadRequest("eventId is required.");
        }

        var cancellationToken = HttpContext?.RequestAborted ?? CancellationToken.None;
        var live = await liveSource.ReadAsync(eventId, static (_, _, _) => Task.FromResult(true), cancellationToken);
        return live == null ? NotFound() : live;
    }

    /// <summary>
    /// Concurrent connections summed across every active event, in one-minute buckets.
    /// </summary>
    /// <response code="200">The series. With nothing running it has no buckets.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not hold the site-admin role.</response>
    /// <remarks>
    /// The same active events as <see cref="Overview"/> except orphans, which have no relay feeding
    /// them and so no live audience worth adding. Computed by
    /// <see cref="LiveViewershipCalculator.ComputeOverall"/>, which explains the window and why the
    /// events are swept together rather than summed. Cached for thirty seconds.
    /// </remarks>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType<OverallViewershipDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public virtual async Task<ActionResult<OverallViewershipDto>> OverallViewership()
    {
        Logger.LogTrace("{m}", nameof(OverallViewership));
        var cancellationToken = HttpContext?.RequestAborted ?? CancellationToken.None;
        return await hcache.GetOrCreateAsync(OVERALL_VIEWERSHIP_CACHE_KEY, this,
            static async (self, cancel) => await self.BuildOverallViewershipAsync(cancel),
            overallCacheOptions,
            cancellationToken: cancellationToken);
    }

    #region Assembly

    /// <summary>The columns of an event the page shows, plus the dates the viewership window needs.</summary>
    private sealed record ActiveEventRow(int Id, int OrganizationId, string Name, string TrackName, bool IsPrivate,
        bool HideName, bool IsSimulation, bool IsLive, bool IsArchived, TimingSource TimingSource,
        DateTime StartDate, DateTime EndDate);

    /// <summary>The active events, and which of them are only active because pods are running.</summary>
    private sealed record ActiveEvents(Dictionary<int, ActiveEventRow> Rows, List<int> ActiveIds, List<int> OrphanIds);

    private async Task<SiteOperationsOverviewDto> BuildOverviewAsync(CancellationToken cancellationToken)
    {
        var asOf = clock.GetUtcNow().UtcDateTime;
        var cache = cacheMux.GetDatabase();

        var relayRead = ReadRelayStateAsync(cache);
        var podsRead = ReadPodHealthAsync(cache);
        var relay = await relayRead;
        var podHealth = await podsRead;
        var pods = podHealth?.Pods ?? [];

        // A context of its own, as for live viewership: the cache carries on computing for other
        // callers if the request that started it goes away.
        await using var db = await tsContext.CreateDbContextAsync(cancellationToken);

        var podEventIds = pods.Where(p => p.EventId > 0).Select(p => p.EventId!.Value);
        var active = await LoadActiveEventsAsync(db, relay, podEventIds, cancellationToken);
        var allIds = active.ActiveIds.Concat(active.OrphanIds).ToList();

        // Organizations named by the events, by the heartbeats and pods of ids with no event row, and
        // by the relays' client ids.
        var organizationIds = active.Rows.Values.Select(e => e.OrganizationId)
            .Concat(relay.Heartbeats.Values.Select(h => h.OrganizationId))
            .Concat(pods.Where(p => p.OrganizationId > 0).Select(p => p.OrganizationId!.Value))
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        var clientIds = relay.Connections.Select(c => c.ClientId).Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
        var organizations = await db.Organizations
            .AsNoTracking()
            .Where(o => organizationIds.Contains(o.Id) || clientIds.Contains(o.ClientId))
            .Select(o => new { o.Id, o.Name, o.ShortName, o.ClientId })
            .ToListAsync(cancellationToken);
        var organizationsById = organizations.ToDictionary(o => o.Id);
        var organizationsByClient = organizations
            .Where(o => !string.IsNullOrEmpty(o.ClientId))
            .GroupBy(o => o.ClientId)
            .ToDictionary(g => g.Key, g => g.First());

        // Every read for every event issued before any is awaited, so the lot costs about one round
        // trip: the viewer hash, the message totals and the sixty-one minute hashes per event.
        var currentMinute = RelayMessageCounter.UnixMinute(asOf);
        var firstMinute = currentMinute - SiteOperationsReadings.MessageHistoryMinutes;
        var viewerReads = allIds.ToDictionary(id => id, id => ViewerCounts.ReadAsync(cache, id, asOf));
        var messageReads = allIds.ToDictionary(id => id, id => ReadMessagesAsync(cache, id, firstMinute));
        await Task.WhenAll(viewerReads.Values.Cast<Task>().Concat(messageReads.Values));

        var connectionsById = relay.Connections
            .GroupBy(c => c.ConnectionId)
            .ToDictionary(g => g.Key, g => g.First());

        var events = new List<SiteOperationsEventDto>();
        foreach (var id in allIds)
        {
            active.Rows.TryGetValue(id, out var row);
            relay.Heartbeats.TryGetValue(id, out var heartbeat);
            var eventPods = pods.Where(p => p.EventId == id).ToList();

            var organizationId = row?.OrganizationId
                ?? (heartbeat?.OrganizationId > 0 ? heartbeat.OrganizationId : (int?)null)
                ?? eventPods.Select(p => p.OrganizationId).FirstOrDefault(o => o > 0)
                ?? 0;
            organizationsById.TryGetValue(organizationId, out var organization);

            var connection = heartbeat != null && !string.IsNullOrEmpty(heartbeat.ConnectionId)
                ? connectionsById.GetValueOrDefault(heartbeat.ConnectionId)
                : null;

            var snapshot = await viewerReads[id];
            events.Add(new SiteOperationsEventDto
            {
                EventId = id,
                OrganizationId = organizationId,
                OrganizationName = organization?.Name ?? string.Empty,
                OrganizationShortName = organization?.ShortName ?? string.Empty,
                EventName = row?.Name ?? string.Empty,
                TrackName = string.IsNullOrEmpty(row?.TrackName) ? null : row.TrackName,
                IsPrivate = row?.IsPrivate ?? false,
                HideName = row?.HideName ?? false,
                IsSimulation = row?.IsSimulation ?? false,
                IsLiveInDb = row?.IsLive ?? false,
                IsArchived = row?.IsArchived ?? false,
                TimingSource = row?.TimingSource.ToString() ?? string.Empty,
                EventStartDate = row?.StartDate,
                Relay = new SiteOperationsRelayDto
                {
                    HasHeartbeat = heartbeat != null,
                    LastHeartbeatUtc = heartbeat?.Timestamp,
                    RelayVersion = string.IsNullOrEmpty(heartbeat?.RelayVersion) ? null : heartbeat.RelayVersion,
                    ConnectionId = string.IsNullOrEmpty(heartbeat?.ConnectionId) ? null : heartbeat.ConnectionId,
                    ConnectionPresent = connection != null,
                    ConnectedUtc = connection?.ConnectedTimestamp,
                },
                IsOrphan = active.OrphanIds.Contains(id),
                Pods = eventPods,
                Viewers = snapshot == null ? null : new SiteViewerCountsDto
                {
                    AsOfUtc = snapshot.AsOfUtc,
                    Total = snapshot.Total,
                    ByClientType = new Dictionary<string, int>(snapshot.ByClientType),
                },
                Messages = await messageReads[id],
            });
        }

        var relays = relay.Connections
            .Select(c =>
            {
                var organization = organizationsByClient.GetValueOrDefault(c.ClientId);
                return new SiteOperationsRelayConnectionDto
                {
                    ConnectionId = c.ConnectionId,
                    ClientId = c.ClientId,
                    OrganizationId = organization?.Id,
                    OrganizationName = organization?.Name,
                    ConnectedUtc = c.ConnectedTimestamp,
                    HeartbeatEventIds = [.. relay.Heartbeats.Values
                        .Where(h => h.ConnectionId == c.ConnectionId)
                        .Select(h => h.EventId)
                        .Order()],
                };
            })
            .OrderBy(r => r.OrganizationName == null)
            .ThenBy(r => r.OrganizationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ConnectedUtc)
            .ToList();

        return new SiteOperationsOverviewDto
        {
            AsOfUtc = asOf,
            Events = [.. events
                .OrderBy(e => e.OrganizationName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(e => e.EventId)],
            Totals = new SiteOperationsTotalsDto
            {
                Events = events.Count,
                Viewers = SiteOperationsReadings.SumViewers(events.Select(e => e.Viewers), asOf),
                // Only connections a current heartbeat points at. A connection record is removed only on
                // a clean disconnect, so one left by a relay hub pod that was killed stays in the hash
                // forever; counting every record would make the headline grow with dead connections.
                // Heartbeats are torn down ten minutes after they stop, so a dead connection drops out
                // of this within that. The relay list still shows every record, heartbeat or not.
                RelaysConnected = relays.Count(r => r.HeartbeatEventIds.Count > 0),
                UnhealthyPods = pods.Count(SiteOperationsReadings.IsUnhealthy),
            },
            SharedPods = [.. pods.Where(p => p.EventId == null)],
            MissingJobs = podHealth?.MissingJobs ?? [],
            PodsAsOfUtc = podHealth?.AsOfUtc,
            Relays = relays,
        };
    }

    private async Task<OverallViewershipDto> BuildOverallViewershipAsync(CancellationToken cancellationToken)
    {
        var asOf = clock.GetUtcNow().UtcDateTime;
        var relay = await ReadRelayStateAsync(cacheMux.GetDatabase());

        await using var db = await tsContext.CreateDbContextAsync(cancellationToken);
        var active = await LoadActiveEventsAsync(db, relay, [], cancellationToken);

        // Only events with a row: an id heartbeating with no event behind it has no dates to bound
        // its viewers by.
        var ids = active.ActiveIds.Where(active.Rows.ContainsKey).ToList();
        var sessions = await db.Sessions
            .AsNoTracking()
            .Where(s => ids.Contains(s.EventId))
            .ToListAsync(cancellationToken);
        // Only the rows that can reach the series, and only the columns the numbers are made of. A row
        // that ended before the floor adds nothing to any bucket, and reading them all would make this
        // grow without limit with a multi-day event or a relay left heartbeating an old one - every
        // phone reconnect is a row.
        var floor = LiveViewershipCalculator.OverallFloor(asOf);
        var viewers = await db.EventViewerSessions
            .AsNoTracking()
            .Where(s => ids.Contains(s.EventId) && (s.EndUtc == null || s.EndUtc >= floor))
            .Select(s => new EventViewerSession { EventId = s.EventId, StartUtc = s.StartUtc, EndUtc = s.EndUtc, ClientType = s.ClientType })
            .ToListAsync(cancellationToken);
        // What was left out, one row per event: enough to tell whether an event's window began before
        // the floor, which is all those rows could change.
        var before = await db.EventViewerSessions
            .AsNoTracking()
            .Where(s => ids.Contains(s.EventId) && s.EndUtc != null && s.EndUtc < floor && s.EndUtc > s.StartUtc)
            .GroupBy(s => s.EventId)
            .Select(g => new { EventId = g.Key, MinStart = g.Min(s => s.StartUtc), MaxEnd = g.Max(s => s.EndUtc!.Value) })
            .ToListAsync(cancellationToken);

        var sessionsByEvent = sessions.ToLookup(s => s.EventId);
        var viewersByEvent = viewers.ToLookup(v => v.EventId);
        var beforeByEvent = before.ToDictionary(b => b.EventId, b => new LiveViewershipCalculator.RowSpan(b.MinStart, b.MaxEnd));
        var inputs = ids
            .Select(id => new LiveViewershipCalculator.OverallEventInput(id, active.Rows[id].StartDate, active.Rows[id].EndDate,
                [.. sessionsByEvent[id]], [.. viewersByEvent[id]],
                beforeByEvent.TryGetValue(id, out var span) ? span : null))
            .ToList();

        return LiveViewershipCalculator.ComputeOverall(inputs, asOf);
    }

    /// <summary>
    /// The events flagged live, every event a relay is heartbeating, and every event pods are running
    /// for, with the last of those that are neither split out as orphans.
    /// </summary>
    /// <remarks>
    /// Live-flagged events are read only when not deleted. Heartbeat and pod ids are read by id
    /// whether or not they are archived, because an archived event with a relay still pointed at it
    /// is exactly what this page is for. Deleted rows are not read; such an id still appears, but with
    /// nothing from its row.
    /// </remarks>
    private static async Task<ActiveEvents> LoadActiveEventsAsync(TsContext db, RelayState relay,
        IEnumerable<int> podEventIds, CancellationToken cancellationToken)
    {
        var heartbeatIds = relay.Heartbeats.Keys.ToList();
        var podIds = podEventIds.Distinct().ToList();
        var byId = heartbeatIds.Concat(podIds).Distinct().ToList();

        var rows = await db.Events
            .AsNoTracking()
            .Where(e => !e.IsDeleted && (e.IsLive || byId.Contains(e.Id)))
            .Select(e => new ActiveEventRow(e.Id, e.OrganizationId, e.Name, e.TrackName, e.IsPrivate, e.HideName,
                e.IsSimulation, e.IsLive, e.IsArchived, e.TimingSource, e.StartDate, e.EndDate))
            .ToListAsync(cancellationToken);
        var rowsById = rows.ToDictionary(r => r.Id);

        var activeIds = rows.Where(r => r.IsLive).Select(r => r.Id).Concat(heartbeatIds).Distinct().ToList();
        var orphanIds = podIds.Except(activeIds).ToList();
        return new ActiveEvents(rowsById, activeIds, orphanIds);
    }

    /// <summary>
    /// One event's relay message totals and its last sixty-one minutes.
    /// </summary>
    /// <remarks>
    /// Every command is issued before any is awaited, so the caller can start every event's reads
    /// together and pay about one round trip for the lot. A Redis failure blanks only this event's
    /// messages, as a failed viewer count blanks only its viewers: with dozens of hashes per event, one
    /// timeout among them must not take the whole page down.
    /// </remarks>
    private async Task<SiteOperationsMessagesDto> ReadMessagesAsync(IDatabase cache, int id, long firstMinute)
    {
        var totals = cache.HashGetAllAsync(string.Format(Consts.RELAY_MESSAGE_COUNTS, id));
        var minutes = Enumerable.Range(0, SiteOperationsReadings.MessageHistoryMinutes + 1)
            .Select(i => cache.HashGetAllAsync(string.Format(Consts.RELAY_MESSAGE_MINUTE, id, firstMinute + i)))
            .ToArray();
        try
        {
            await Task.WhenAll(minutes.Cast<Task>().Append(totals));
            return SiteOperationsReadings.BuildMessages(await totals, [.. minutes.Select(m => m.Result)], firstMinute);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            Logger.LogWarning(ex, "Relay message counts unreadable for event {eventId}", id);
            return new SiteOperationsMessagesDto();
        }
    }

    /// <remarks>
    /// Not guarded against Redis failures, unlike the per-event reads: the event list itself depends on
    /// it, so without it there is no page to show.
    /// </remarks>
    private static async Task<RelayState> ReadRelayStateAsync(IDatabase cache)
    {
        var entries = await cache.HashGetAllAsync(Consts.RELAY_EVENT_CONNECTIONS);
        return SiteOperationsReadings.ParseRelayState(entries);
    }

    /// <summary>
    /// The orchestrator's latest pod report, or null when there is none to read.
    /// </summary>
    /// <remarks>
    /// Null for a missing key, for one that does not parse, and for a Redis failure alike: either way
    /// the page can say no more than that the pod figures are unavailable, and an unreadable report
    /// must not take the rest of the overview down with it.
    /// </remarks>
    private async Task<SitePodHealth?> ReadPodHealthAsync(IDatabase cache)
    {
        RedisValue json;
        try
        {
            json = await cache.StringGetAsync(Consts.SITE_POD_HEALTH);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            Logger.LogWarning(ex, "Site pod health report unreadable");
            return null;
        }
        if (json.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            var health = JsonSerializer.Deserialize<SitePodHealth>(json.ToString(), SitePodHealth.JsonOptions);
            if (health != null)
            {
                health.AsOfUtc = UtcTimestamp.Normalize(health.AsOfUtc);
            }
            return health;
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Unreadable site pod health report");
            return null;
        }
    }

    #endregion
}
