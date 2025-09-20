using Microsoft.EntityFrameworkCore;
using Prometheus;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.TimingCommon.Models.X2;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.EventLogger.Services;

public class LogConsumerService : BackgroundService
{
    private readonly string streamKey;
    private readonly int eventId;
    private readonly static TimeSpan interval = TimeSpan.FromSeconds(10);
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IDbContextFactory<TsContext> tsContext;
    private const string CONSUMER_GROUP = "log";
    private readonly Queue<DateTime> logTimestamps = new();
    private readonly TimeSpan window = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim streamCheckLock = new(1);

    private ILogger Logger { get; }


    public LogConsumerService(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, IConfiguration configuration, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.tsContext = tsContext;
        eventId = configuration.GetValue("event_id", 0);
        streamKey = string.Format(Consts.EVENT_STATUS_STREAM_KEY, eventId);
        cacheMux.ConnectionRestored += CacheMux_ConnectionRestored;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("LogConsumerService starting...");
        await EnsureStream();

        var counter = Metrics.CreateCounter("logger_logs_total", "Total number of logs processed");
        var logsPending = Metrics.CreateCounter("logger_logs_pending", "Total logs in stream to be processed");
        var rateGauge = Metrics.CreateGauge("logger_logs_rate", "Log save rate");

        DateTime lastMetricUpdate = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cache = cacheMux.GetDatabase();
                var result = await cache.StreamReadGroupAsync(streamKey, CONSUMER_GROUP, "logger", ">", 1);
                foreach (var entry in result)
                {
                    foreach (var field in entry.Values)
                    {
                        var tags = field.Name.ToString().Split('-');
                        if (tags.Length < 3)
                        {
                            Logger.LogWarning("Invalid event status update: {f}", field.Name);
                            continue;
                        }

                        var type = tags[0];
                        var eventId = int.Parse(tags[1]);
                        var sessionId = int.Parse(tags[2]);
                        var data = field.Value.ToString();

                        await LogStatusData(type, eventId, sessionId, data, stoppingToken);

                        // Parse RMonitor data
                        if (type == Consts.RMONITOR_TYPE)
                        {
                        }
                        // Passings
                        else if (type == Consts.X2PASS_TYPE)
                        {
                            await SavePassings(data, stoppingToken);
                        }
                        // Loops
                        else if (type == "x2loop")
                        {
                            await SaveLoops(data, stoppingToken);
                        }
                        // Flags
                        else if (type == Consts.FLAGS_TYPE)
                        {
                        }
                        // Competitor Metadata
                        else if (type == "competitors")
                        {
                        }

                        counter.Inc();
                        RecordLogSave();
                    }
                    await cache.StreamAcknowledgeAsync(streamKey, CONSUMER_GROUP, entry.Id);
                }

                // Update metrics
                if ((DateTime.UtcNow - lastMetricUpdate) > interval)
                {
                    var rate = GetRate();
                    rateGauge.Set(rate);
                    var streamLength = await cache.StreamPendingAsync(streamKey, CONSUMER_GROUP);
                    logsPending.IncTo(streamLength.PendingMessageCount);
                    Logger.LogInformation("Total: {t} Rate: {r:0.0}/min Stream: {s}", counter.Value, rate, streamLength.PendingMessageCount);
                    lastMetricUpdate = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error reading event status stream");
                Logger.LogInformation("Throttling service for {i:0.0}secs", interval.TotalSeconds);
                await Task.Delay(interval, stoppingToken);
            }
        }
    }

    private async void CacheMux_ConnectionRestored(object? sender, ConnectionFailedEventArgs e)
    {
        await EnsureStream();
    }

    private async Task EnsureStream()
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

    private async Task LogStatusData(string type, int eventId, int sessionId, string data, CancellationToken stoppingToken)
    {
        try
        {
            using var db = await tsContext.CreateDbContextAsync(stoppingToken);
            var log = new EventStatusLog
            {
                Type = type,
                EventId = eventId,
                SessionId = sessionId,
                Timestamp = DateTime.UtcNow,
                Data = data,
            };
            db.EventStatusLogs.Add(log);
            await db.SaveChangesAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error logging status data for event {id}", eventId);
        }
    }

    private async Task SavePassings(string json, CancellationToken stoppingToken)
    {
        try
        {
            var passings = JsonSerializer.Deserialize<List<Passing>>(json);
            if (passings == null || passings.Count == 0)
            {
                Logger.LogWarning("No passings to process");
                return;
            }

            using var db = await tsContext.CreateDbContextAsync(stoppingToken);
            foreach (var passing in passings)
            {
                try
                {
                    await db.AddAsync(passing, stoppingToken);
                    await db.SaveChangesAsync(stoppingToken);
                }
                catch (DbUpdateException)
                {
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error processing X2 passings");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing X2 passings");
        }
    }

    private async Task SaveLoops(string json, CancellationToken stoppingToken)
    {
        try
        {
            var loops = JsonSerializer.Deserialize<List<Loop>>(json);
            if (loops == null || loops.Count == 0)
            {
                Logger.LogWarning("No loops to process");
                return;
            }

            // Insert or update loops in the database
            using var db = await tsContext.CreateDbContextAsync(stoppingToken);

            foreach (var loop in loops)
            {
                await db.X2Loops.Where(l => l.OrganizationId == loop.OrganizationId && l.EventId == loop.EventId && l.Id == loop.Id)
                    .ExecuteDeleteAsync(cancellationToken: stoppingToken);
            }

            await db.X2Loops.AddRangeAsync(loops, cancellationToken: stoppingToken);
            await db.SaveChangesAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing X2 loops");
        }
    }

    #region Metrics

    private void RecordLogSave()
    {
        var now = DateTime.UtcNow;
        logTimestamps.Enqueue(now);
        CleanupOld(now);
    }

    private double GetRate()
    {
        var now = DateTime.UtcNow;
        CleanupOld(now);
        return logTimestamps.Count;
    }

    private void CleanupOld(DateTime now)
    {
        while (logTimestamps.TryPeek(out var timestamp))
        {
            if (now - timestamp > window)
                logTimestamps.TryDequeue(out _);
            else
                break;
        }
    }

    #endregion
}
