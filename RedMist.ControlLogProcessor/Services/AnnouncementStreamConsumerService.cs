using RedMist.Backend.Shared;
using RedMist.ControlLogs;
using RedMist.ControlLogs.Announcements;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.ControlLogProcessor.Services;

/// <summary>
/// Reads the event status stream (<c>evt-st-{eventId}</c>) with its own consumer group and turns the
/// external source's race-control announcements into control log entries. The announcements ride along
/// in <see cref="ExternalPatchBatch"/> (field type <see cref="Consts.EXTERNAL_PATCH_TYPE"/>) as
/// <see cref="SessionStatePatch.Announcements"/>; the source republishes the full announcement list
/// whenever any message changes, so each carried list is a complete replacement.
///
/// Parsed entries are pushed into the shared <see cref="IAnnouncementControlLogStore"/>. The existing
/// <see cref="StatusAggregatorService"/>/<c>ControlLogCache</c> poll then reads them through
/// <see cref="AnnouncementControlLog"/> and publishes them like any other control log source, so the
/// caches, penalty counts, SignalR contract, and UIs are unchanged. This consumer only runs when the
/// org's control log type is <see cref="ControlLogType.ANNOUNCEMENT"/>.
/// </summary>
public class AnnouncementStreamConsumerService : BackgroundService
{
    private const string CONSUMER_GROUP = "controllog-ann";
    private const string CONSUMER_NAME = "cl-ann";

    private readonly IConnectionMultiplexer cacheMux;
    private readonly IAnnouncementControlLogStore store;
    private readonly int eventId;
    private readonly string streamKey;
    private ILogger Logger { get; }

    public AnnouncementStreamConsumerService(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux,
        IConfiguration configuration, IAnnouncementControlLogStore store)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.store = store;
        eventId = configuration.GetValue("event_id", 0);
        streamKey = string.Format(Consts.EVENT_STATUS_STREAM_KEY, eventId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (eventId <= 0)
        {
            Logger.LogError("Event ID is not set or invalid. Cannot start AnnouncementStreamConsumerService.");
            return;
        }

        Logger.LogInformation("AnnouncementStreamConsumerService starting for event {eventId}...", eventId);
        await EnsureStreamAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cache = cacheMux.GetDatabase();
                var result = await cache.StreamReadGroupAsync(streamKey, CONSUMER_GROUP, CONSUMER_NAME, ">", count: 10);
                if (result.Length == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
                    continue;
                }

                foreach (var entry in result)
                {
                    foreach (var field in entry.Values)
                    {
                        // Field name format is "{type}-{eventId}-{sessionId}"; only external patch batches
                        // carry announcements.
                        var tags = field.Name.ToString().Split('-');
                        if (tags.Length < 3 || tags[0] != Consts.EXTERNAL_PATCH_TYPE)
                            continue;

                        ProcessExternalBatch(field.Value.ToString());
                    }

                    await cache.StreamAcknowledgeAsync(streamKey, CONSUMER_GROUP, entry.Id);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error reading event status stream for announcements");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                await EnsureStreamAsync();
            }
        }

        Logger.LogInformation("AnnouncementStreamConsumerService stopped.");
    }

    private void ProcessExternalBatch(string data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return;

        ExternalPatchBatch? batch;
        try
        {
            batch = JsonSerializer.Deserialize<ExternalPatchBatch>(data);
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Unable to deserialize external patch batch");
            return;
        }

        if (batch?.SessionPatches is not { Count: > 0 } patches)
            return;

        // The source republishes the full announcement list on any change, so the most recent non-null
        // list in the batch is the complete current set.
        List<Announcement>? latest = null;
        foreach (var patch in patches)
        {
            if (patch.Announcements is { Count: >= 0 } a)
                latest = a;
        }
        if (latest is null)
            return;

        var entries = AnnouncementControlLogParser.ParseAll(latest);
        store.Set(entries);
        Logger.LogDebug("Updated announcement control log store: {a} announcements -> {e} entries", latest.Count, entries.Count);
    }

    private async Task EnsureStreamAsync()
    {
        try
        {
            var cache = cacheMux.GetDatabase();
            // Create at the stream tail ("$"): we only want new announcements. The source republishes the
            // full list when any message changes, so the store self-heals after a late start.
            if (!await cache.KeyExistsAsync(streamKey) || (await cache.StreamGroupInfoAsync(streamKey)).All(x => x.Name != CONSUMER_GROUP))
            {
                Logger.LogInformation("Creating consumer group {cg} on {sk}", CONSUMER_GROUP, streamKey);
                await cache.StreamCreateConsumerGroupAsync(streamKey, CONSUMER_GROUP, StreamPosition.NewMessages, createStream: true);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error ensuring announcement consumer group on {sk}", streamKey);
        }
    }
}
