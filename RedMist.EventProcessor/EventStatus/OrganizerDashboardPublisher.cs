using Microsoft.AspNetCore.SignalR;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Utilities;
using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Models;
using RedMist.Database;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.EventProcessor.EventStatus;

/// <summary>
/// Pushes this event's live viewer counts and status to any organizer dashboard watching it.
/// </summary>
/// <remarks>
/// <para>
/// Its own timer rather than the status fan-out. That fan-out is change-driven - it sends patches
/// when timing data moves - so riding it would starve the count through a caution and fire it many
/// times a second through a busy lap, neither of which describes how many people are watching.
/// </para>
/// <para>
/// The read is per EVENT, not per watcher: this runs once for the event whether nobody or fifty
/// dashboards are subscribed, and SignalR fans the result out. A dashboard costs a group membership
/// and nothing more, which is why the counts are pushed rather than polled.
/// </para>
/// <para>
/// Exactly one of these exists per live event, because the processor is one pod per event, so no
/// coordination is needed to avoid publishing twice.
/// </para>
/// </remarks>
public class OrganizerDashboardPublisher : BackgroundService
{
    /// <summary>
    /// How often counts are published.
    /// </summary>
    /// <remarks>
    /// Viewer counts do not move fast enough to want more, and a dashboard treats three missed ticks
    /// as stale - so this also sets how quickly a page can tell that the processor has gone.
    /// </remarks>
    internal static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(5);

    private readonly int eventId;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IHubContext<StatusHub> hubContext;
    private readonly TimeProvider timeProvider;

    private ILogger Logger { get; }

    private readonly SessionContext sessionContext;
    private readonly IDbContextFactory<TsContext> tsContext;

    public OrganizerDashboardPublisher(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux,
        IConfiguration configuration, IHubContext<StatusHub> hubContext, SessionContext sessionContext,
        IDbContextFactory<TsContext> tsContext, TimeProvider? timeProvider = null)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.hubContext = hubContext;
        this.sessionContext = sessionContext;
        this.tsContext = tsContext;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        eventId = configuration.GetValue("event_id", 0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (eventId <= 0)
        {
            Logger.LogWarning("No event id configured; dashboard updates will not be published.");
            return;
        }

        Logger.LogInformation("Publishing dashboard updates for event {eventId} every {interval}s",
            eventId, PublishInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One failed tick is not worth ending the loop over. A dashboard sees the gap as an
                // aging timestamp, which is the truth, and the next tick recovers on its own.
                Logger.LogError(ex, "Error publishing dashboard updates for event {eventId}", eventId);
            }

            try
            {
                await Task.Delay(PublishInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task PublishAsync(CancellationToken stoppingToken)
    {
        var cache = cacheMux.GetDatabase();
        var asOf = timeProvider.GetUtcNow().UtcDateTime;
        var group = string.Format(Consts.EVENT_VIEWER_COUNTS_SUB, eventId);

        var counts = await ViewerCounts.ReadAsync(cache, eventId, asOf);
        await hubContext.Clients.Group(group).SendAsync("ReceiveViewerCounts", counts, stoppingToken);

        var status = await ReadStatusAsync(cache, asOf, stoppingToken);
        await hubContext.Clients.Group(group).SendAsync("ReceiveEventStatusSummary", status, stoppingToken);
    }

    /// <summary>
    /// Takes the handful of scalars a dashboard card shows from the state this pod already holds.
    /// </summary>
    /// <remarks>
    /// Under the read lock, and copying values out rather than holding a reference: the fan-out
    /// below must not be inside the lock, and the pipeline is free to replace the state the instant
    /// the lock is released.
    /// </remarks>
    private async Task<EventStatusSummary> ReadStatusAsync(IDatabase cache, DateTime asOf,
        CancellationToken stoppingToken)
    {
        int sessionId, carCount;
        string sessionName;
        bool isPracticeQualifying;
        string flag;

        using (await sessionContext.SessionStateLock.AcquireReadLockAsync(stoppingToken))
        {
            var state = sessionContext.SessionState;
            sessionId = state.SessionId;
            sessionName = state.SessionName;
            isPracticeQualifying = state.IsPracticeQualifying;
            carCount = state.CarPositions.Count;
            flag = sessionContext.GetEffectiveTrackFlag().ToString();
        }

        // Both of the remaining reads are outside the lock on purpose - neither touches session
        // state, and holding it across a database or Redis round trip would put the pipeline behind
        // the network.
        var lastData = await ReadLastDataAsync(sessionId, stoppingToken);
        return new EventStatusSummary(eventId, asOf, sessionId, sessionName, isPracticeQualifying,
            flag, carCount, lastData, await ReadRelayHeartbeatAsync(cache));
    }

    /// <summary>
    /// When timing data last arrived for the running session, or null if none has.
    /// </summary>
    /// <remarks>
    /// Read from the Sessions row rather than from session state, because that is the only place it
    /// is recorded: SessionMonitor writes it with ExecuteUpdateAsync, which never touches the
    /// in-memory copy. SessionState.LastUpdated exists but nothing in the solution ever assigns it,
    /// so reading it would have shipped a field that was null for every event forever.
    ///
    /// The write is debounced, so this lags real arrivals slightly. That is the right trade for a
    /// staleness indicator: it answers "is data still coming" rather than timing each message.
    /// </remarks>
    private async Task<DateTime?> ReadLastDataAsync(int sessionId, CancellationToken stoppingToken)
    {
        if (sessionId <= 0)
        {
            return null;
        }

        try
        {
            await using var db = await tsContext.CreateDbContextAsync(stoppingToken);
            return await db.Sessions
                .AsNoTracking()
                .Where(s => s.EventId == eventId && s.Id == sessionId)
                .Select(s => s.LastUpdated)
                .FirstOrDefaultAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Could not read last data timestamp for event {eventId}", eventId);
            return null;
        }
    }

    /// <summary>
    /// When the relay last checked in for this event, or null if it never has.
    /// </summary>
    /// <remarks>
    /// Read from the hash RelayHub writes on every heartbeat rather than from anything this pod
    /// tracks, so it reflects the relay's own connection rather than whether data happens to be
    /// flowing through the pipeline. A relay connected but sending nothing is a different problem
    /// from a relay that has gone, and a dashboard has to be able to tell them apart.
    /// </remarks>
    private async Task<DateTime?> ReadRelayHeartbeatAsync(IDatabase cache)
    {
        var json = await cache.HashGetAsync(Consts.RELAY_EVENT_CONNECTIONS,
            string.Format(Consts.RELAY_HEARTBEAT, eventId));
        if (json.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RelayConnectionEventEntry>(json.ToString())?.Timestamp;
        }
        catch (JsonException ex)
        {
            // A heartbeat we cannot read is not worth failing the tick over; the card shows it as
            // absent, which is the honest answer when we do not know.
            Logger.LogWarning(ex, "Unreadable relay heartbeat for event {eventId}", eventId);
            return null;
        }
    }
}
