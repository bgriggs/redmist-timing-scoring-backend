using Microsoft.EntityFrameworkCore;
using Prometheus;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.Database;
using RedMist.Database.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.EventLogger.Services;

public class EventProcessLogger : BackgroundService
{
    private ILogger Logger { get; }
    private readonly string streamKey;
    private readonly int eventId;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IDbContextFactory<TsContext> tsContext;
    private const string CONSUMER_GROUP = "log";
    private readonly SemaphoreSlim streamCheckLock = new(1);
    private readonly static TimeSpan interval = TimeSpan.FromSeconds(10);
    private readonly Queue<DateTime> logTimestamps = new();
    private readonly TimeSpan window = TimeSpan.FromMinutes(1);


    public EventProcessLogger(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, IConfiguration configuration, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.cacheMux = cacheMux;
        this.tsContext = tsContext;
        eventId = configuration.GetValue("event_id", 0);
        streamKey = string.Format(Consts.EVENT_PROCESSOR_LOGGING_STREAM_KEY, eventId);
        cacheMux.ConnectionRestored += CacheMux_ConnectionRestored;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation(nameof(EventProcessLogger) + " starting...");
        await EnsureStreamAsync();

        var counter = Metrics.CreateCounter("event_proc_logs_total", "Total number of logs processed");
        var logsPending = Metrics.CreateCounter("event_proc_logs_pending", "Total logs in stream to be processed");
        var rateGauge = Metrics.CreateGauge("event_proc_logs_rate", "Log save rate");

        Logger.LogInformation("Starting event processor loop for event {e} on stream {s}", eventId, streamKey);
        var lastMetricUpdate = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cache = cacheMux.GetDatabase();
                var result = await cache.StreamReadGroupAsync(streamKey, CONSUMER_GROUP, "logger", ">", 1);
                if (result.Length == 0)
                {
                    // No messages available, wait before next poll
                    await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken);
                }
                else
                {
                    foreach (var entry in result)
                    {
                        foreach (var field in entry.Values)
                        {
                            var tag = field.Name.ToString();

                            if (tag == "laps")
                            {
                                var lapData = JsonSerializer.Deserialize<List<CarLapData>>(field.Value.ToString());
                                if (lapData != null)
                                {
                                    await SaveLogsAsync(lapData, stoppingToken);
                                }
                            }

                            counter.Inc();
                            RecordLogSave();
                        }
                        await cache.StreamAcknowledgeAsync(streamKey, CONSUMER_GROUP, entry.Id);
                    }
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

    private async Task SaveLogsAsync(List<CarLapData> lapLogs, CancellationToken stoppingToken)
    {
        using var context = tsContext.CreateDbContext();
        try
        {
            foreach (var log in lapLogs)
            {
                try
                {
                    context.CarLapLogs.Add(log.Log);

                    // Save the last lap reference
                    var lastLapRef = await context.CarLastLaps.FirstOrDefaultAsync(x => x.EventId == eventId && x.SessionId == log.SessionId && x.CarNumber == log.Log.CarNumber, cancellationToken: stoppingToken);
                    if (lastLapRef == null)
                    {
                        lastLapRef = new CarLastLap { EventId = eventId, SessionId = log.SessionId, CarNumber = log.Log.CarNumber, LastLapNumber = log.LastLapNum, LastLapTimestamp = DateTime.UtcNow };
                        context.CarLastLaps.Add(lastLapRef);
                    }
                    else
                    {
                        lastLapRef.LastLapNumber = log.LastLapNum;
                        lastLapRef.LastLapTimestamp = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error logging new lap for car {c} in event {e}", log.LastLapNum, eventId);
                }
            }

            // Save the changes
            await context.SaveChangesAsync(stoppingToken);
        }
        catch (DbUpdateException ex)
        {
            var log = lapLogs.FirstOrDefault();
            Logger.LogWarning(ex, "Error saving lap for event:{e},session:{s},car:{c},lap:{l}", eventId, log?.SessionId, log?.Log.CarNumber, log?.LastLapNum);
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
