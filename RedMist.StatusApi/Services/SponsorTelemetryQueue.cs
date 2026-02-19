using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.Database.Models;
using System.Threading.Channels;

namespace RedMist.StatusApi.Services;

public record SponsorTelemetryEntry(string Source, string EventId, string ImageId, string EventType, int? DurationMs);

/// <summary>
/// Background service that buffers sponsor telemetry events in a bounded channel
/// and flushes them to the database in batches.
/// </summary>
public class SponsorTelemetryQueue : BackgroundService
{
    private const int MAX_CHANNEL_SIZE = 5000;
    private const int BATCH_SIZE = 50;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly Channel<SponsorTelemetryEntry> channel = Channel.CreateBounded<SponsorTelemetryEntry>(
        new BoundedChannelOptions(MAX_CHANNEL_SIZE)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly IDbContextFactory<TsContext> tsContext;
    private ILogger Logger { get; }

    public SponsorTelemetryQueue(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
    }

    /// <summary>
    /// Enqueues a telemetry entry. Returns false if the channel is full (oldest entry will be dropped).
    /// </summary>
    public bool TryEnqueue(SponsorTelemetryEntry entry) => channel.Writer.TryWrite(entry);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("SponsorTelemetryQueue starting...");
        var batch = new List<SponsorTelemetryLog>(BATCH_SIZE);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for at least one entry or timeout for periodic flush
                if (await WaitForDataAsync(stoppingToken))
                {
                    // Drain available entries up to batch size
                    while (batch.Count < BATCH_SIZE && channel.Reader.TryRead(out var entry))
                    {
                        batch.Add(new SponsorTelemetryLog
                        {
                            Timestamp = DateTime.UtcNow,
                            Source = entry.Source,
                            EventId = entry.EventId,
                            ImageId = entry.ImageId,
                            EventType = entry.EventType,
                            DurationMs = entry.DurationMs
                        });
                    }
                }

                if (batch.Count > 0)
                {
                    await FlushBatchAsync(batch, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error processing sponsor telemetry batch");
                batch.Clear();
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        // Drain remaining entries on shutdown
        while (channel.Reader.TryRead(out var entry))
        {
            batch.Add(new SponsorTelemetryLog
            {
                Timestamp = DateTime.UtcNow,
                Source = entry.Source,
                EventId = entry.EventId,
                ImageId = entry.ImageId,
                EventType = entry.EventType,
                DurationMs = entry.DurationMs
            });
        }

        if (batch.Count > 0)
        {
            await FlushBatchAsync(batch, CancellationToken.None);
        }

        Logger.LogInformation("SponsorTelemetryQueue stopped. Flushed remaining entries.");
    }

    private async Task<bool> WaitForDataAsync(CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(FlushInterval);
        try
        {
            return await channel.Reader.WaitToReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Flush interval elapsed — check if there's anything to flush
            return channel.Reader.TryPeek(out _);
        }
    }

    private async Task FlushBatchAsync(List<SponsorTelemetryLog> batch, CancellationToken stoppingToken)
    {
        try
        {
            await using var db = await tsContext.CreateDbContextAsync(stoppingToken);
            db.SponsorTelemetryLogs.AddRange(batch);
            await db.SaveChangesAsync(stoppingToken);
            Logger.LogTrace("Flushed {count} sponsor telemetry entries", batch.Count);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error flushing {count} sponsor telemetry entries to database", batch.Count);
        }
        finally
        {
            batch.Clear();
        }
    }
}
