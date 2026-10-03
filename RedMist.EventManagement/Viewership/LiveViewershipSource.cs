using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventManagement.Models;
using RedMist.TimingCommon.Models;

namespace RedMist.EventManagement.Viewership;

/// <summary>
/// Reads one running event's live viewership: the event and its sessions from the database, then the
/// computed answer from the cache or from <see cref="LiveViewershipCalculator"/>.
/// </summary>
/// <remarks>
/// <para>
/// The one code path behind both the organizer dashboard (<c>ViewershipControllerBase.Live</c>) and
/// the site operations page (<c>SiteOperationsControllerBase.Viewership</c>). The two differ only in
/// who may read which event, so that is the one thing a caller supplies; everything about how the
/// answer is computed and cached is here, once.
/// </para>
/// <para>
/// Both share one cache entry per event. The permission check runs before the cache is consulted, so
/// sharing it hands nobody an answer they could not have computed for themselves.
/// </para>
/// <para>
/// Cached per event for thirty seconds, keyed on the state of the event's sessions and its live flag
/// as well as the event. The dashboard polls once a minute and once more whenever the session
/// changes, and that second poll is precisely the one that has to see the change: under a plain
/// per-event key it would be answered from before the change, and the new session would not appear
/// until the poll after. The sessions are a handful of rows read on every request anyway; the viewer
/// sessions are what the cache saves reading.
/// </para>
/// </remarks>
public sealed class LiveViewershipSource
{
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

    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly HybridCache hcache;
    private readonly TimeProvider clock;

    public LiveViewershipSource(IDbContextFactory<TsContext> tsContext, HybridCache hcache, TimeProvider clock)
    {
        this.tsContext = tsContext;
        this.hcache = hcache;
        this.clock = clock;
    }

    /// <summary>
    /// Reads an event's live viewership, or returns null when there is no such event or the caller
    /// may not read it.
    /// </summary>
    /// <param name="eventId">The event.</param>
    /// <param name="isPermitted">
    /// Whether the caller may read an event of the given organization, asked with the request's own
    /// database context. Asked before anything beyond the event's own row is read, so counting upwards
    /// through event ids the caller may not read costs the server that lookup and the check, and never
    /// a read of their viewers.
    /// </param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <remarks>
    /// Deleted events are not found. No such event and an event the caller may not read are the same
    /// null, so a caller cannot use the difference to learn that an event exists.
    /// </remarks>
    public async Task<LiveViewershipDto?> ReadAsync(int eventId,
        Func<TsContext, int, CancellationToken, Task<bool>> isPermitted, CancellationToken cancellationToken)
    {
        using var db = await tsContext.CreateDbContextAsync(cancellationToken);

        var evt = await db.Events
            .AsNoTracking()
            .Where(e => e.Id == eventId && !e.IsDeleted)
            .Select(e => new { e.OrganizationId, e.StartDate, e.EndDate, e.IsLive })
            .FirstOrDefaultAsync(cancellationToken);

        if (evt == null || !await isPermitted(db, evt.OrganizationId, cancellationToken))
        {
            return null;
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
}
