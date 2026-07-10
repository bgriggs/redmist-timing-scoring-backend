using Microsoft.EntityFrameworkCore;
using Prometheus;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.Database.Models;
using StackExchange.Redis;

namespace RedMist.EventLogger.Services;

/// <summary>
/// Consumes the external-source raw message stream (<see cref="Consts.EVENT_EXTERNAL_LOG_STREAM_KEY"/>)
/// and persists each batch verbatim to <see cref="ExternalMessageLog"/>. The payload is opaque: this
/// service never parses or interprets it — its format is private to the external source's ingestor.
/// Mirrors <see cref="EventProcessLogger"/>'s stream/consumer-group loop.
/// </summary>
public class ExternalMessageLogConsumer : BackgroundService
{
    private ILogger Logger { get; }
    private readonly string streamKey;
    private readonly int eventId;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IDbContextFactory<TsContext> tsContext;
    private const string CONSUMER_GROUP = "log";
    private readonly SemaphoreSlim streamCheckLock = new(1);
    private readonly static TimeSpan interval = TimeSpan.FromSeconds(10);


    public ExternalMessageLogConsumer(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, IConfiguration configuration, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.tsContext = tsContext;
        eventId = configuration.GetValue("event_id", 0);
        streamKey = string.Format(Consts.EVENT_EXTERNAL_LOG_STREAM_KEY, eventId);
        cacheMux.ConnectionRestored += CacheMux_ConnectionRestored;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation(nameof(ExternalMessageLogConsumer) + " starting...");
        await EnsureStreamAsync();

        var counter = Metrics.CreateCounter("external_msg_logs_total", "Total number of external messages processed");

        Logger.LogInformation("Starting external message logger loop for event {e} on stream {s}", eventId, streamKey);
        var lastMetricUpdate = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cache = cacheMux.GetDatabase();
                var result = await cache.StreamReadGroupAsync(streamKey, CONSUMER_GROUP, "logger", ">", 1);
                if (result.Length == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);
                }
                else
                {
                    foreach (var entry in result)
                    {
                        foreach (var field in entry.Values)
                        {
                            var tags = field.Name.ToString().Split('-');
                            if (tags.Length < 3)
                            {
                                Logger.LogWarning("Invalid external message field: {f}", field.Name);
                                continue;
                            }

                            var type = tags[0];
                            var sessionId = int.Parse(tags[2]);
                            await SaveMessageAsync(type, sessionId, field.Value.ToString(), stoppingToken);
                            counter.Inc();
                        }
                        await cache.StreamAcknowledgeAsync(streamKey, CONSUMER_GROUP, entry.Id);
                    }
                }

                if ((DateTime.UtcNow - lastMetricUpdate) > interval)
                {
                    var streamLength = await cache.StreamPendingAsync(streamKey, CONSUMER_GROUP);
                    Logger.LogInformation("Total: {t} Stream pending: {s}", counter.Value, streamLength.PendingMessageCount);
                    lastMetricUpdate = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error reading external message stream");
                Logger.LogInformation("Throttling service for {i:0.0}secs", interval.TotalSeconds);
                await Task.Delay(interval, stoppingToken);
            }
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
                await cache.StreamCreateConsumerGroupAsync(streamKey, CONSUMER_GROUP, createStream: true);
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

    private async Task SaveMessageAsync(string type, int sessionId, string data, CancellationToken stoppingToken)
    {
        try
        {
            using var db = await tsContext.CreateDbContextAsync(stoppingToken);
            db.ExternalMessageLogs.Add(new ExternalMessageLog
            {
                Type = type.Length > 20 ? type[..20] : type,
                EventId = eventId,
                SessionId = sessionId,
                Timestamp = DateTime.UtcNow,
                Data = data,
            });
            await db.SaveChangesAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error saving external message for event {id}", eventId);
        }
    }
}
