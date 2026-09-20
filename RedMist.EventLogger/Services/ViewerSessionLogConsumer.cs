using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.EventLogger.Services;

/// <summary>
/// Consumes the per-event viewership stream (<see cref="Consts.EVENT_VIEWERSHIP_STREAM_KEY"/>) and
/// turns the connect and disconnect transitions the status API replicas publish into
/// <see cref="EventViewerSession"/> rows.
/// </summary>
/// <remarks>
/// Paired with <see cref="ViewerSessionReconciler"/>, which closes the sessions this consumer never
/// sees an end for. Modeled on <see cref="ExternalMessageLogConsumer"/>, with three deliberate
/// departures from it documented at their call sites: the consumer group starts at the beginning of
/// the stream, entries are read in batches through one database context, and simulation events are
/// dropped.
/// </remarks>
public class ViewerSessionLogConsumer : BackgroundService
{
    private ILogger Logger { get; }
    private readonly string streamKey;
    private readonly int eventId;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly SimulationGate simulationGate;
    private readonly TimeProvider timeProvider;

    private const string CONSUMER_GROUP = "log";
    private const string CONSUMER_NAME = "logger";

    /// <summary>
    /// Entries read per poll. Larger than the sibling consumers' 1 because the burst this has to
    /// absorb - a few hundred viewers subscribing when the green flag drops - lands on the same pod
    /// as the event processor at its busiest, and a batch shares one database round trip.
    /// </summary>
    private const int BATCH_SIZE = 20;

    private readonly SemaphoreSlim streamCheckLock = new(1);
    private static readonly TimeSpan interval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan pendingReclaimIdle = TimeSpan.FromMinutes(5);


    public ViewerSessionLogConsumer(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, IConfiguration configuration,
        IDbContextFactory<TsContext> tsContext, SimulationGate simulationGate, TimeProvider? timeProvider = null)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.tsContext = tsContext;
        this.simulationGate = simulationGate;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        eventId = configuration.GetValue("event_id", 0);
        streamKey = string.Format(Consts.EVENT_VIEWERSHIP_STREAM_KEY, eventId);
        cacheMux.ConnectionRestored += CacheMux_ConnectionRestored;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation(nameof(ViewerSessionLogConsumer) + " starting...");
        await EnsureStreamAsync();

        Logger.LogInformation("Starting viewer session logger loop for event {e} on stream {s}", eventId, streamKey);
        var lastMetricUpdate = timeProvider.GetUtcNow().UtcDateTime;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cache = cacheMux.GetDatabase();
                var result = await cache.StreamReadGroupAsync(streamKey, CONSUMER_GROUP, CONSUMER_NAME, ">", BATCH_SIZE);
                if (result.Length == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);
                }
                else
                {
                    await ProcessBatchAsync(cache, result, stoppingToken);
                }

                if ((timeProvider.GetUtcNow().UtcDateTime - lastMetricUpdate) > interval)
                {
                    var pending = await cache.StreamPendingAsync(streamKey, CONSUMER_GROUP);
                    Logger.LogInformation("Viewer sessions stream pending: {s}", pending.PendingMessageCount);
                    lastMetricUpdate = timeProvider.GetUtcNow().UtcDateTime;

                    // On the metric tick rather than every poll: this recovers entries a crashed
                    // consumer left behind, which is a once-in-a-while event, and polling runs
                    // several times a second.
                    await ReclaimAbandonedAsync(cache, stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                Logger.LogError(ex, "Error reading viewer session stream");

                // The stream carries a sliding expiry and is deleted outright at teardown, so an
                // event that goes quiet for long enough loses the key and the consumer group with
                // it. Every subsequent read then fails with NOGROUP, and without recreating it here
                // the loop would throttle and retry forever against a group that no longer exists.
                await EnsureStreamAsync();

                Logger.LogInformation("Throttling service for {i:0.0}secs", interval.TotalSeconds);
                await Task.Delay(interval, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Applies one batch of stream entries and acknowledges each one that was handled.
    /// </summary>
    /// <remarks>
    /// One database context for the batch rather than one per entry, and one save at the end. An
    /// entry whose payload cannot be understood is acknowledged rather than retried: a single
    /// malformed blob must not wedge an event's whole viewership capture.
    /// </remarks>
    internal async Task ProcessBatchAsync(IDatabase cache, StreamEntry[] entries, CancellationToken stoppingToken)
    {
        if (await simulationGate.IsSimulationAsync(stoppingToken))
        {
            // Load tests open hundreds of synthetic connections against simulation events. Counting
            // them would put fictional numbers into a real organization's report.
            foreach (var entry in entries)
            {
                await cache.StreamAcknowledgeAsync(streamKey, CONSUMER_GROUP, entry.Id);
            }
            return;
        }

        await using var db = await tsContext.CreateDbContextAsync(stoppingToken);
        var handled = new List<RedisValue>(entries.Length);

        foreach (var entry in entries)
        {
            try
            {
                foreach (var field in entry.Values)
                {
                    if (field.Name != Consts.VIEWER_SESSION_TYPE)
                    {
                        Logger.LogWarning("Unexpected viewer session field: {f}", field.Name);
                        continue;
                    }

                    var viewerEvent = JsonSerializer.Deserialize<ViewerSessionEvent>(field.Value.ToString());
                    if (viewerEvent == null || string.IsNullOrEmpty(viewerEvent.ConnectionId))
                    {
                        Logger.LogWarning("Unusable viewer session payload on entry {id}", entry.Id);
                        continue;
                    }

                    await ApplyAsync(db, viewerEvent, stoppingToken);
                }

                handled.Add(entry.Id);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error applying viewer session entry {id} for event {e}", entry.Id, eventId);
                handled.Add(entry.Id);
            }
        }

        try
        {
            await db.SaveChangesAsync(stoppingToken);
        }
        catch (DbUpdateException ex)
        {
            // One rejected insert aborts the whole batch, including the closes, which are the part
            // that cannot be reconstructed later. Drop the additions and save the rest.
            Logger.LogWarning(ex, "Viewer session batch insert rejected for event {e}; retrying without the additions", eventId);

            foreach (var entry in db.ChangeTracker.Entries<EventViewerSession>()
                .Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }

            try
            {
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception retry)
            {
                // Acknowledged regardless. Leaving a batch the database will always reject in the
                // pending list would have it reclaimed and retried for the life of the event, and
                // the reconciler recovers the sessions this loses.
                Logger.LogError(retry, "Viewer session batch could not be saved for event {e}; {n} entr(ies) dropped",
                    eventId, handled.Count);
            }
        }

        foreach (var id in handled)
        {
            await cache.StreamAcknowledgeAsync(streamKey, CONSUMER_GROUP, id);
        }
    }

    internal async Task ApplyAsync(TsContext db, ViewerSessionEvent viewerEvent, CancellationToken stoppingToken)
    {
        var timestamp = UtcTimestamp.Normalize(viewerEvent.TimestampUtc);

        var open = await FindOpenSessionAsync(db, viewerEvent.ConnectionId, stoppingToken);

        if (viewerEvent.Kind == ViewerSessionEventKind.Start)
        {
            // Exactly the key the unique index uses. A start redelivered after its own end has
            // already been applied leaves no open session, so the open-session check below would let
            // it through and insert a row the database then rejects - taking the whole batch with
            // it. Matching the index here is what keeps duplicates from ever reaching it.
            if (await ExistsAsync(db, viewerEvent.ConnectionId, timestamp, stoppingToken))
            {
                return;
            }

            // An already-open session for this connection means the reconciler backfilled it before
            // the start arrived, or the client re-subscribed without an end in between. Either way
            // the connection is already being counted.
            if (open != null)
            {
                return;
            }

            db.EventViewerSessions.Add(new EventViewerSession
            {
                EventId = eventId,
                ConnectionId = viewerEvent.ConnectionId,
                ClientType = viewerEvent.ClientType,
                StartUtc = timestamp,
                IsInCar = viewerEvent.IsInCar,
                CarNumber = viewerEvent.CarNumber,
            });
            ViewerSessionMetrics.Started.WithLabels(viewerEvent.ClientType, "false").Inc();
            return;
        }

        // No open session: the Start was lost, or the reconciler or a previous delivery of this same
        // End already closed it. Closing something twice must not move the end time it already has.
        if (open == null)
        {
            return;
        }

        open.EndUtc = timestamp;
        open.EndReason = viewerEvent.Reason ?? ViewerSessionEndReason.Disconnected;
        ViewerSessionMetrics.Closed.WithLabels(open.EndReason.Value.ToString()).Inc();
    }

    /// <summary>
    /// Whether a session already exists with exactly this start, which is what makes an entry a
    /// redelivery rather than a new stretch of watching.
    /// </summary>
    private async Task<bool> ExistsAsync(TsContext db, string connectionId, DateTime startUtc, CancellationToken stoppingToken)
    {
        var pending = db.ChangeTracker.Entries<EventViewerSession>()
            .Select(e => e.Entity)
            .Any(s => s.EventId == eventId && s.ConnectionId == connectionId && s.StartUtc == startUtc);
        if (pending)
        {
            return true;
        }

        return await db.EventViewerSessions
            .AnyAsync(s => s.EventId == eventId && s.ConnectionId == connectionId && s.StartUtc == startUtc, stoppingToken);
    }

    /// <summary>
    /// The open session for a connection, looking through the batch's pending additions as well as
    /// the database so two entries in one batch cannot both open one.
    /// </summary>
    private async Task<EventViewerSession?> FindOpenSessionAsync(TsContext db, string connectionId, CancellationToken stoppingToken)
    {
        var pending = db.ChangeTracker.Entries<EventViewerSession>()
            .Select(e => e.Entity)
            .FirstOrDefault(s => s.EventId == eventId && s.ConnectionId == connectionId && s.EndUtc == null);
        if (pending != null)
        {
            return pending;
        }

        return await db.EventViewerSessions
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.ConnectionId == connectionId && s.EndUtc == null, stoppingToken);
    }

    /// <summary>
    /// Takes over entries a previous consumer read but never acknowledged.
    /// </summary>
    /// <remarks>
    /// Reading with "&gt;" only ever delivers entries nobody has seen, so an entry left pending by a
    /// pod that died mid-batch would otherwise never be delivered again - and a lost Start
    /// under-counts silently while a lost End over-counts for the rest of the event. This narrows
    /// that window; the reconciler, not this, is the actual safety net.
    /// </remarks>
    private async Task ReclaimAbandonedAsync(IDatabase cache, CancellationToken stoppingToken)
    {
        try
        {
            var claimed = await cache.StreamAutoClaimAsync(streamKey, CONSUMER_GROUP, CONSUMER_NAME,
                (long)pendingReclaimIdle.TotalMilliseconds, "0-0", BATCH_SIZE);
            if (claimed.ClaimedEntries is { Length: > 0 })
            {
                Logger.LogInformation("Reclaimed {n} abandoned viewer session entries for event {e}",
                    claimed.ClaimedEntries.Length, eventId);
                await ProcessBatchAsync(cache, claimed.ClaimedEntries, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not reclaim abandoned viewer session entries for event {e}", eventId);
        }
    }

    private async void CacheMux_ConnectionRestored(object? sender, ConnectionFailedEventArgs e)
    {
        await EnsureStreamAsync();
    }

    private async Task EnsureStreamAsync()
    {
        // Lock to avoid race condition between checking for the stream and creating it
        await streamCheckLock.WaitAsync();
        try
        {
            var cache = cacheMux.GetDatabase();
            if (!await cache.KeyExistsAsync(streamKey) || (await cache.StreamGroupInfoAsync(streamKey)).All(static x => x.Name != CONSUMER_GROUP))
            {
                Logger.LogInformation("Creating new stream and consumer group {cg}", CONSUMER_GROUP);

                // From the beginning of the stream, not the default "$" the sibling consumers use.
                // This pod is started by the orchestrator after the relay connects, which can be well
                // after the first viewers have already subscribed, and "$" would silently discard
                // every Start published in between - leaving those sessions to be inferred rather
                // than observed. Safe to replay because the stream's MAXLEN bounds how far back it
                // goes and applying an entry twice is a no-op.
                await cache.StreamCreateConsumerGroupAsync(streamKey, CONSUMER_GROUP, StreamPosition.Beginning, createStream: true);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error checking stream");
        }
        finally
        {
            streamCheckLock.Release();
        }
    }
}
