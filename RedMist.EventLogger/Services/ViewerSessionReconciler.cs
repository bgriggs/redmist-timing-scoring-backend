using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Utilities;
using RedMist.Backend.Shared.Models;
using RedMist.Database;
using RedMist.Database.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.EventLogger.Services;

/// <summary>
/// Closes the viewer sessions that <see cref="ViewerSessionLogConsumer"/> never sees an end for, and
/// opens the ones it never saw a start for.
/// </summary>
/// <remarks>
/// <para>
/// A status API pod killed outright - OOM, node failure - never runs its disconnect handler, so
/// without this the sessions it was serving stay open forever and the event's viewer-minutes grow
/// without bound. The ordinary pod losses (rolling deploys, scale-down) are SIGTERM, where the
/// disconnect handler does run, so what reaches this reconciler is the violent minority.
/// </para>
/// <para>
/// <b>The live connection hash is evidence of presence, not of absence's opposite.</b> Live pods add
/// to <see cref="Consts.STATUS_EVENT_CONNECTIONS"/> continuously, so a connection listed there was
/// genuinely subscribed by someone. But entries are removed by the dying pod's disconnect handler,
/// which is exactly what does not run in the case this exists for - so an entry being present proves
/// nothing about the connection still being alive. Absence drives the close; presence is bounded
/// only by <see cref="MaxSessionDuration"/>.
/// </para>
/// <para>
/// One logger pod runs per live event, so this needs no lock or leader election.
/// </para>
/// </remarks>
public class ViewerSessionReconciler : BackgroundService
{
    private ILogger Logger { get; }
    private readonly int eventId;
    private readonly string connectionsKey;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly TimeProvider timeProvider;

    /// <summary>How often the open sessions are checked against the live connection hash.</summary>
    internal static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The longest a single session is allowed to run before it is closed at the cap.
    /// </summary>
    /// <remarks>
    /// Longer than any single racing session, shorter than a race weekend. This is the only bound on
    /// a session whose hash entry was orphaned by a hard pod kill, because such an entry is never
    /// removed and so the absence check below can never fire for it.
    /// </remarks>
    internal static readonly TimeSpan MaxSessionDuration = TimeSpan.FromHours(6);

    /// <summary>
    /// When each currently-missing connection was first noticed missing.
    /// </summary>
    /// <remarks>
    /// A timestamp rather than a counter because the value is used as the session's end time: the
    /// connection was demonstrably gone by the first pass that missed it, so ending it there bounds
    /// the over-count at one interval instead of two.
    /// </remarks>
    private readonly Dictionary<string, DateTime> absenceFirstSeen = [];

    /// <summary>
    /// Connections this pod has closed without their hash entry going away.
    /// </summary>
    /// <remarks>
    /// The cap and the teardown both close a session whose connection is still listed in the live
    /// hash - the cap fires precisely because it is still listed. Without this the very next pass
    /// would see a listed connection with no open row, conclude its start had been lost, and open a
    /// second session overlapping the one just closed: viewer-minutes doubled, and on PostgreSQL a
    /// duplicate key on (EventId, ConnectionId, StartUtc) that fails the whole pass and leaves the
    /// reconciler unable to close anything for the rest of the event.
    /// <para>
    /// Safe to hold forever: a SignalR connection id is unique to one connection, so a viewer who
    /// genuinely comes back arrives with a new one.
    /// </para>
    /// </remarks>
    private readonly HashSet<string> closedWhileListed = new(StringComparer.Ordinal);

    /// <summary>
    /// Set once the event is shutting down, after which there is nothing left to reconcile.
    /// </summary>
    private bool shuttingDown;

    /// <summary>
    /// Whether a pass has already run.
    /// </summary>
    /// <remarks>
    /// The first pass after startup only records what it sees: it closes nothing and infers nothing.
    /// Closing is held back because a pod bounce mid-event is routine and the viewers it was serving
    /// are still there; ending and reopening their sessions would triple the session count and punch
    /// a hole in peak concurrency at the moment the chart matters most. Inferring is held back
    /// because the stream consumer starts from the beginning of the stream and needs a moment to
    /// catch up - backfilling immediately would replace observed start times with guessed ones.
    /// </remarks>
    private bool hasObserved;


    public ViewerSessionReconciler(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, IConfiguration configuration,
        IDbContextFactory<TsContext> tsContext, TimeProvider? timeProvider = null)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.tsContext = tsContext;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        eventId = configuration.GetValue("event_id", 0);
        connectionsKey = string.Format(Consts.STATUS_EVENT_CONNECTIONS, eventId);
        cacheMux.ConnectionRestored += CacheMux_ConnectionRestored;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation(nameof(ViewerSessionReconciler) + " starting for event {e}", eventId);
        await EnsureEventShutdownSubscriptionAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ReconcileInterval, timeProvider, stoppingToken);
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error reconciling viewer sessions for event {e}", eventId);
            }
        }
    }

    /// <summary>
    /// Runs one reconcile pass.
    /// </summary>
    internal async Task ReconcileAsync(CancellationToken stoppingToken)
    {
        if (shuttingDown)
        {
            // Everything was closed when the signal arrived, and the pod is about to go. Another
            // pass could only re-open what that close just settled.
            return;
        }

        HashEntry[] live;
        try
        {
            var cache = cacheMux.GetDatabase();
            live = await cache.HashGetAllAsync(connectionsKey);
        }
        catch (Exception ex)
        {
            // Abandon the pass rather than treat an unreachable Redis as an empty hash. Reading a
            // blip as "every viewer left at once" would close every open session for the event and
            // there is no way to reconstruct them afterwards.
            Logger.LogWarning(ex, "Could not read live connections for event {e}; skipping this reconcile pass", eventId);
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var liveTypes = live.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString(), StringComparer.Ordinal);

        await using var db = await tsContext.CreateDbContextAsync(stoppingToken);
        var open = await db.EventViewerSessions
            .Where(s => s.EventId == eventId && s.EndUtc == null)
            .ToListAsync(stoppingToken);

        var openByConnection = new HashSet<string>(open.Select(s => s.ConnectionId), StringComparer.Ordinal);

        if (!hasObserved)
        {
            // Observe-only: note who is already missing so the next pass has something to compare
            // against, and change nothing.
            foreach (var session in open.Where(s => !liveTypes.ContainsKey(s.ConnectionId)))
            {
                absenceFirstSeen[session.ConnectionId] = now;
            }
            hasObserved = true;
            ViewerSessionMetrics.Open.Set(open.Count);
            return;
        }

        foreach (var session in open)
        {
            if (liveTypes.ContainsKey(session.ConnectionId))
            {
                absenceFirstSeen.Remove(session.ConnectionId);

                // A connection the hash still lists but which has run past the cap. The entry is
                // almost certainly orphaned, and this is the only thing that will ever close it.
                if (now - session.StartUtc > MaxSessionDuration)
                {
                    Close(session, session.StartUtc + MaxSessionDuration, ViewerSessionEndReason.CappedDuration);
                    closedWhileListed.Add(session.ConnectionId);
                }
                continue;
            }

            if (!absenceFirstSeen.TryGetValue(session.ConnectionId, out var firstMissing))
            {
                // First time it has been missed. The hub writes the hash entry fire-and-forget, so a
                // session opened moments ago can legitimately not be in the hash yet; closing on a
                // single miss would end sessions milliseconds after they began.
                absenceFirstSeen[session.ConnectionId] = now;
                continue;
            }

            Close(session, firstMissing, ViewerSessionEndReason.ReconciledAbsent);
            absenceFirstSeen.Remove(session.ConnectionId);
        }

        // Anything the hash lists that has no open row: the Start was published while this pod was
        // down, or lost. Prefer the connection's real connect time over the current instant.
        foreach (var (connectionId, clientType) in liveTypes)
        {
            if (openByConnection.Contains(connectionId) || closedWhileListed.Contains(connectionId))
            {
                continue;
            }

            var resolved = await ResolveConnectionAsync(connectionId, clientType, now);
            db.EventViewerSessions.Add(new EventViewerSession
            {
                EventId = eventId,
                ConnectionId = connectionId,
                ClientType = resolved.ClientType,
                StartUtc = resolved.Start,
                StartInferred = true,
                IsInCar = resolved.IsInCar,
                CarNumber = EventViewerSession.FitCarNumber(resolved.CarNumber),
            });
            ViewerSessionMetrics.Started.WithLabels(resolved.ClientType, "true").Inc();
        }

        // Entries for connections that are neither still missing nor still open are spent: the
        // consumer applied their End between passes. Without this the map is only ever added to, and
        // it lives as long as the event does.
        foreach (var spent in absenceFirstSeen.Keys
            .Where(c => !liveTypes.ContainsKey(c) && !openByConnection.Contains(c))
            .ToList())
        {
            absenceFirstSeen.Remove(spent);
        }

        // Tracked-entity saves rather than ExecuteUpdateAsync: the in-memory provider the tests run
        // on does not implement the latter, and this is the behavior least affordable to leave
        // untested. Open sessions per event number in the hundreds, so the round trip is cheap.
        try
        {
            await db.SaveChangesAsync(stoppingToken);
        }
        catch (DbUpdateException ex)
        {
            // Closing sessions is what this exists to do, and an insert the database rejects must
            // not take the closes down with it. Drop the additions and save what is left.
            Logger.LogWarning(ex, "Viewer session reconcile insert rejected for event {e}; retrying without the additions", eventId);

            foreach (var entry in db.ChangeTracker.Entries<EventViewerSession>().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }

            try
            {
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception retry)
            {
                Logger.LogError(retry, "Viewer session reconcile save failed for event {e}", eventId);
                return;
            }
        }

        ViewerSessionMetrics.Open.Set(await db.EventViewerSessions.CountAsync(s => s.EventId == eventId && s.EndUtc == null, stoppingToken));
    }

    private static void Close(EventViewerSession session, DateTime endUtc, ViewerSessionEndReason reason)
    {
        // Never move an end time that is already set - every caller pre-filters on it, but a close
        // that silently moved one would be indistinguishable from a correct one afterwards.
        if (session.EndUtc != null)
        {
            return;
        }

        // Never end before the session began.
        session.EndUtc = endUtc < session.StartUtc ? session.StartUtc : endUtc;
        session.EndReason = reason;
        ViewerSessionMetrics.Closed.WithLabels(reason.ToString()).Inc();
    }

    /// <summary>What a backfilled session needs to know about the connection behind it.</summary>
    private readonly record struct ResolvedConnection(DateTime Start, string ClientType, bool IsInCar, string? CarNumber);

    /// <summary>
    /// The connection's connect time, device and driver-mode state, from its connection record.
    /// </summary>
    /// <param name="connectionId">The connection being backfilled.</param>
    /// <param name="liveBucket">The value the live hash holds for it.</param>
    /// <param name="fallback">The time to use when no connect time is recorded.</param>
    /// <remarks>
    /// <para>
    /// The live hash holds a count bucket, not necessarily a device. For a phone in driver mode it
    /// holds InCar, which says what the connection is doing rather than what it runs on. The report
    /// breaks viewership down by device, and it would fold an unrecognized "InCar" into Web - so an
    /// in-car Android phone would have been reported as a web viewer. The device is recovered from the
    /// client id the connection record keeps, which is where the hub derived it in the first place.
    /// </para>
    /// <para>
    /// Driver mode is read from the record for every backfill, not only when the bucket says InCar.
    /// Before, a reconstructed session never set IsInCar at all, even when the phone was plainly in
    /// driver mode.
    /// </para>
    /// </remarks>
    private async Task<ResolvedConnection> ResolveConnectionAsync(string connectionId, string liveBucket,
        DateTime fallback)
    {
        var start = fallback;
        var clientType = liveBucket;
        var isInCar = false;
        string? carNumber = null;

        try
        {
            var cache = cacheMux.GetDatabase();
            var json = await cache.HashGetAsync(Consts.STATUS_CONNECTIONS, connectionId);
            if (!json.IsNullOrEmpty)
            {
                var conn = JsonSerializer.Deserialize<StatusConnection>(json.ToString());
                if (conn != null)
                {
                    if (conn.ConnectedTimestamp != default)
                    {
                        var connected = UtcTimestamp.Normalize(conn.ConnectedTimestamp);

                        // Clamp rather than trust: a connect time older than the cap would create a
                        // session that is already over-length the moment it is written.
                        var earliest = fallback - MaxSessionDuration;
                        start = connected < earliest ? earliest : connected;
                    }

                    isInCar = conn.InCarDriverConnection != null;
                    carNumber = conn.InCarDriverConnection?.CarNumber;

                    if (liveBucket == ClientTypeHelper.InCar)
                    {
                        clientType = ClientTypeHelper.ResolveClientType(conn.ClientId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not read connection record for {connectionId}", connectionId);
        }

        // The bucket alone still says driver mode even when the record is missing, and a device of
        // "InCar" must never reach the report.
        if (clientType == ClientTypeHelper.InCar)
        {
            isInCar = true;
            clientType = ClientTypeHelper.ResolveClientType(null);
        }

        return new ResolvedConnection(start, clientType, isInCar, carNumber);
    }

    #region Event shutdown

    private async void CacheMux_ConnectionRestored(object? sender, ConnectionFailedEventArgs e)
    {
        await EnsureEventShutdownSubscriptionAsync();
    }

    private async Task EnsureEventShutdownSubscriptionAsync()
    {
        try
        {
            var sub = cacheMux.GetSubscriber();
            await sub.UnsubscribeAsync(new RedisChannel(Consts.EVENT_SHUTDOWN_SIGNAL, RedisChannel.PatternMode.Literal));
            var ch = await sub.SubscribeAsync(new RedisChannel(Consts.EVENT_SHUTDOWN_SIGNAL, RedisChannel.PatternMode.Literal));
            ch.OnMessage(HandleEventShutdown);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error ensuring event shutdown subscription");
        }
    }

    private void HandleEventShutdown(ChannelMessage msg) => HandleEventShutdown(msg.Message.ToString());

    /// <summary>
    /// <see cref="HandleEventShutdown(ChannelMessage)"/> over the raw payload, which is all the
    /// handler uses. Separated so it can be driven without a Redis channel message.
    /// </summary>
    /// <param name="payload">JSON list of the event IDs being shut down</param>
    internal void HandleEventShutdown(string payload)
    {
        try
        {
            var eventIds = JsonSerializer.Deserialize<List<int>>(payload);
            if (eventIds?.Contains(eventId) ?? false)
            {
                Logger.LogInformation("Received shutdown signal for event {eventId}; closing open viewer sessions", eventId);
                shuttingDown = true;

                // The orchestrator waits fifteen seconds after this signal before deleting the pod,
                // which is ample. If it is not - if the process is killed first - the orchestrator
                // closes whatever is left as it disposes the event.
                CloseAllOpenAsync().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing shutdown signal");
        }
    }

    private async Task CloseAllOpenAsync()
    {
        await using var db = await tsContext.CreateDbContextAsync();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var open = await db.EventViewerSessions
            .Where(s => s.EventId == eventId && s.EndUtc == null)
            .ToListAsync();

        foreach (var session in open)
        {
            Close(session, now, ViewerSessionEndReason.EventTeardown);

            // The hash is untouched by this, so anything still listed would look to the next pass
            // like a connection whose start was lost.
            closedWhileListed.Add(session.ConnectionId);
        }

        await db.SaveChangesAsync();
        Logger.LogInformation("Closed {n} open viewer sessions for event {e} at teardown", open.Count, eventId);
    }

    #endregion
}
