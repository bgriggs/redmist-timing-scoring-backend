using RedMist.Backend.Shared;
using RedMist.ControlLogs.Announcements;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;

namespace RedMist.ControlLogProcessor.Services;

/// <summary>
/// Periodically pulls the full <see cref="SessionState"/> from the event processor and refreshes the
/// announcement control log store from its complete <see cref="SessionState.Announcements"/> list.
///
/// The <see cref="AnnouncementStreamConsumerService"/> reads the event stream from its tail, so any
/// announcements published before it started (or any it missed) would otherwise only reappear when the
/// source next republishes the list. This sync closes that gap: it runs at startup and once per minute,
/// and the processor's consolidated list is a superset of any single stream patch, so replacing the
/// store from it never drops an entry. Endpoint discovery mirrors StatusApi: the processor registers
/// <c>http://host:port</c> under <see cref="Consts.EVENT_SERVICE_ENDPOINT"/> in Redis, and its
/// <c>/status/GetStatus</c> returns the state as MessagePack.
/// </summary>
public class SessionStateAnnouncementSyncService : BackgroundService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(1);
    // Until the first successful sync (e.g. the processor endpoint isn't registered yet), retry sooner so
    // the startup gap is closed quickly rather than after a full minute.
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    private readonly IConnectionMultiplexer cacheMux;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IAnnouncementControlLogStore store;
    private readonly int eventId;
    private ILogger Logger { get; }

    public SessionStateAnnouncementSyncService(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux,
        IHttpClientFactory httpClientFactory, IConfiguration configuration, IAnnouncementControlLogStore store)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.httpClientFactory = httpClientFactory;
        this.store = store;
        eventId = configuration.GetValue("event_id", 0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (eventId <= 0)
        {
            Logger.LogError("Event ID is not set or invalid. Cannot start SessionStateAnnouncementSyncService.");
            return;
        }

        Logger.LogInformation("SessionStateAnnouncementSyncService starting for event {eventId}...", eventId);

        while (!stoppingToken.IsCancellationRequested)
        {
            bool synced = false;
            try
            {
                synced = await SyncAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to sync announcements from full session state");
            }

            try
            {
                await Task.Delay(synced ? SyncInterval : RetryInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        Logger.LogInformation("SessionStateAnnouncementSyncService stopped.");
    }

    /// <summary>Fetches the full state and refreshes the store. Returns false when the processor is not reachable yet.</summary>
    private async Task<bool> SyncAsync(CancellationToken stoppingToken)
    {
        var endpoint = await GetProcessorEndpointAsync(stoppingToken);
        if (endpoint is null)
        {
            Logger.LogDebug("Event processor endpoint not registered yet for event {eventId}", eventId);
            return false;
        }

        var url = endpoint.TrimEnd('/') + "/status/GetStatus";
        var client = httpClientFactory.CreateClient("EventProcessor");

        SessionState? state;
        try
        {
            await using var stream = await client.GetStreamAsync(url, stoppingToken);
            state = await MessagePack.MessagePackSerializer.DeserializeAsync<SessionState>(stream, cancellationToken: stoppingToken);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogDebug(ex, "Event processor {url} not reachable yet", url);
            return false;
        }

        // No announcements (or no state) — leave the store as-is rather than clearing entries the stream
        // consumer may already hold.
        if (state?.Announcements is not { Count: > 0 } announcements)
            return true;

        var entries = AnnouncementControlLogParser.ParseAll(announcements);
        store.Set(entries);
        Logger.LogDebug("Synced announcement control log from full state: {a} announcements -> {e} entries", announcements.Count, entries.Count);
        return true;
    }

    private async Task<string?> GetProcessorEndpointAsync(CancellationToken stoppingToken)
    {
        var key = string.Format(Consts.EVENT_SERVICE_ENDPOINT, eventId);
        var value = await cacheMux.GetDatabase().StringGetAsync(key);
        if (!value.HasValue)
            return null;
        var endpoint = value.ToString();
        return endpoint.StartsWith("http") ? endpoint : "http://" + endpoint;
    }
}
