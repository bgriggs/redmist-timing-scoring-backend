using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.Database.Models;

namespace RedMist.SponsorDataRollup;

/// <summary>
/// Background job that aggregates the previous month's sponsor telemetry logs
/// into SponsorStatistics (total, per-event, and per-source breakdowns).
/// Stops the host after completion.
/// </summary>
public class SponsorStatisticsRollupJob(
    ILoggerFactory loggerFactory,
    IDbContextFactory<TsContext> contextFactory,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    private const string IMPRESSION = "Impression";
    private const string VIEWABLE_IMPRESSION = "ViewableImpression";
    private const string CLICK_THROUGH = "ClickThrough";
    private const string ENGAGEMENT_DURATION = "EngagementDuration";

    private readonly ILogger logger = loggerFactory.CreateLogger<SponsorStatisticsRollupJob>();

    private sealed class SponsorAggregate
    {
        public required int SponsorId { get; init; }
        public int Impressions { get; set; }
        public int ViewableImpressions { get; set; }
        public int ClickThroughs { get; set; }
        public long EngagementDurationMs { get; set; }
        public Dictionary<int, EventAggregate> EventStatistics { get; } = [];
        public Dictionary<string, SourceAggregate> SourceStatistics { get; } = new(StringComparer.Ordinal);
    }

    private sealed class EventAggregate
    {
        public required int SponsorId { get; init; }
        public required int EventId { get; init; }
        public int Impressions { get; set; }
        public int ViewableImpressions { get; set; }
        public int ClickThroughs { get; set; }
        public long EngagementDurationMs { get; set; }
    }

    private sealed class SourceAggregate
    {
        public required int SponsorId { get; init; }
        public required string Source { get; init; }
        public int Impressions { get; set; }
        public int ViewableImpressions { get; set; }
        public int ClickThroughs { get; set; }
        public long EngagementDurationMs { get; set; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for the host to fully start before executing (K8s networking/DNS readiness)
        await WaitForStartupAsync(stoppingToken);

        try
        {
            logger.LogInformation("Sponsor statistics rollup job starting");

            var now = DateTime.UtcNow;
            var previousMonth = now.AddMonths(-1);
            var monthStart = new DateTime(previousMonth.Year, previousMonth.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthEnd = monthStart.AddMonths(1);
            var month = DateOnly.FromDateTime(monthStart);

            logger.LogInformation("Processing telemetry for {Month:yyyy-MM} (range {Start} to {End})", month, monthStart, monthEnd);

            await using var context = await CreateDbContextWithRetryAsync(stoppingToken);

            // Build SponsorId lookup set from Sponsors table
            var sponsorIds = await context.Sponsors
                .AsNoTracking()
                .Select(s => s.Id)
                .ToHashSetAsync(stoppingToken);
            logger.LogInformation("Loaded {Count} sponsors for ID lookup", sponsorIds.Count);

            // Stream previous month's telemetry logs and aggregate on the fly to avoid
            // materializing the full result set and a second resolved copy in memory.
            var sponsorAggregates = new Dictionary<int, SponsorAggregate>();
            var unmatchedImageSamples = new HashSet<string>(StringComparer.Ordinal);
            var unmatchedRowCount = 0;
            var totalTelemetryRows = 0;

            await foreach (var log in context.SponsorTelemetryLogs
                .AsNoTracking()
                .Where(t => t.Timestamp >= monthStart && t.Timestamp < monthEnd)
                .Select(t => new
                {
                    t.ImageId,
                    t.EventId,
                    t.EventType,
                    t.DurationMs,
                    t.Source,
                })
                .AsAsyncEnumerable()
                .WithCancellation(stoppingToken))
            {
                totalTelemetryRows++;

                if (!int.TryParse(log.ImageId, out var sponsorId) || !sponsorIds.Contains(sponsorId))
                {
                    unmatchedRowCount++;
                    if (unmatchedImageSamples.Count < 20)
                    {
                        unmatchedImageSamples.Add(log.ImageId);
                    }

                    continue;
                }

                if (!sponsorAggregates.TryGetValue(sponsorId, out var aggregate))
                {
                    aggregate = new SponsorAggregate { SponsorId = sponsorId };
                    sponsorAggregates.Add(sponsorId, aggregate);
                }

                ApplyEventType(aggregate, log.EventType, log.DurationMs);

                var eventId = ParseEventId(log.EventId);
                if (!aggregate.EventStatistics.TryGetValue(eventId, out var eventAggregate))
                {
                    eventAggregate = new EventAggregate
                    {
                        SponsorId = sponsorId,
                        EventId = eventId,
                    };
                    aggregate.EventStatistics.Add(eventId, eventAggregate);
                }

                ApplyEventType(eventAggregate, log.EventType, log.DurationMs);

                if (!aggregate.SourceStatistics.TryGetValue(log.Source, out var sourceAggregate))
                {
                    sourceAggregate = new SourceAggregate
                    {
                        SponsorId = sponsorId,
                        Source = log.Source,
                    };
                    aggregate.SourceStatistics.Add(log.Source, sourceAggregate);
                }

                ApplyEventType(sourceAggregate, log.EventType, log.DurationMs);
            }

            logger.LogInformation("Found {Count} telemetry log entries for {Month:yyyy-MM}", totalTelemetryRows, month);

            if (totalTelemetryRows == 0)
            {
                logger.LogInformation("No telemetry data to process, exiting");
                lifetime.StopApplication();
                return;
            }

            if (unmatchedRowCount > 0)
            {
                logger.LogWarning("Could not resolve SponsorId for {Count} telemetry row(s). Sample ImageId values: {ImageIds}",
                    unmatchedRowCount, string.Join(", ", unmatchedImageSamples));
            }

            if (sponsorAggregates.Count == 0)
            {
                logger.LogInformation("No telemetry rows matched known sponsors, exiting");
                lifetime.StopApplication();
                return;
            }

            var existingStatistics = await context.SponsorStatistics
                .Include(s => s.EventStatistics)
                .Include(s => s.SourceStatistics)
                .Where(s => s.Month == month && sponsorAggregates.Keys.Contains(s.SponsorId))
                .ToDictionaryAsync(s => s.SponsorId, stoppingToken);

            foreach (var aggregate in sponsorAggregates.Values)
            {
                var sponsorId = aggregate.SponsorId;

                // Check for existing record for this sponsor/month to support re-runs
                existingStatistics.TryGetValue(sponsorId, out var existing);

                // Compute aggregated values
                SponsorStatistics stats;

                if (existing != null)
                {
                    logger.LogWarning("Statistics already exist for SponsorId {SponsorId} for {Month:yyyy-MM}, overwriting with new data", sponsorId, month);

                    // Remove old child records
                    context.EventSponsorStatistics.RemoveRange(existing.EventStatistics);
                    context.SourceSponsorStatistics.RemoveRange(existing.SourceStatistics);

                    // Overwrite totals on existing record
                    existing.Impressions = aggregate.Impressions;
                    existing.ViewableImpressions = aggregate.ViewableImpressions;
                    existing.ClickThroughs = aggregate.ClickThroughs;
                    existing.EngagementDurationMs = aggregate.EngagementDurationMs;
                    existing.ReportProcessed = false;
                    existing.ReportProcessingSuccessful = false;

                    stats = existing;
                }
                else
                {
                    // Level 1: Total statistics per sponsor
                    stats = new SponsorStatistics
                    {
                        Month = month,
                        SponsorId = sponsorId,
                        Impressions = aggregate.Impressions,
                        ViewableImpressions = aggregate.ViewableImpressions,
                        ClickThroughs = aggregate.ClickThroughs,
                        EngagementDurationMs = aggregate.EngagementDurationMs,
                    };
                    context.SponsorStatistics.Add(stats);
                }

                // Level 2: Per-event statistics
                foreach (var eventAggregate in aggregate.EventStatistics.Values)
                {
                    stats.EventStatistics.Add(new EventSponsorStatistics
                    {
                        SponsorId = sponsorId,
                        EventId = eventAggregate.EventId,
                        Impressions = eventAggregate.Impressions,
                        ViewableImpressions = eventAggregate.ViewableImpressions,
                        ClickThroughs = eventAggregate.ClickThroughs,
                        EngagementDurationMs = eventAggregate.EngagementDurationMs,
                    });
                }

                // Level 3: Per-source statistics
                foreach (var sourceAggregate in aggregate.SourceStatistics.Values)
                {
                    stats.SourceStatistics.Add(new SourceSponsorStatistics
                    {
                        SponsorId = sponsorId,
                        Source = sourceAggregate.Source,
                        Impressions = sourceAggregate.Impressions,
                        ViewableImpressions = sourceAggregate.ViewableImpressions,
                        ClickThroughs = sourceAggregate.ClickThroughs,
                        EngagementDurationMs = sourceAggregate.EngagementDurationMs,
                    });
                }

                logger.LogInformation("Sponsor {SponsorId}: {Impressions} impressions, {Viewable} viewable, {Clicks} clicks, {Duration}ms engagement, {Events} event groups, {Sources} source groups",
                    sponsorId, stats.Impressions, stats.ViewableImpressions, stats.ClickThroughs, stats.EngagementDurationMs,
                    stats.EventStatistics.Count, stats.SourceStatistics.Count);
            }

            await context.SaveChangesAsync(stoppingToken);
            logger.LogInformation("Sponsor statistics rollup completed for {Month:yyyy-MM}. Processed {SponsorCount} sponsor(s)", month, sponsorAggregates.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during sponsor statistics rollup");
            throw;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    /// <summary>
    /// Parses the EventId string to an int. Returns 0 for null, empty, or non-numeric values.
    /// </summary>
    private static int ParseEventId(string? eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
            return 0;
        return int.TryParse(eventId, out var id) && id > 0 ? id : 0;
    }

    private static void ApplyEventType(SponsorAggregate aggregate, string eventType, int? durationMs)
    {
        switch (eventType)
        {
            case IMPRESSION:
                aggregate.Impressions++;
                break;
            case VIEWABLE_IMPRESSION:
                aggregate.ViewableImpressions++;
                break;
            case CLICK_THROUGH:
                aggregate.ClickThroughs++;
                break;
            case ENGAGEMENT_DURATION:
                aggregate.EngagementDurationMs += durationMs ?? 0;
                break;
        }
    }

    private static void ApplyEventType(EventAggregate aggregate, string eventType, int? durationMs)
    {
        switch (eventType)
        {
            case IMPRESSION:
                aggregate.Impressions++;
                break;
            case VIEWABLE_IMPRESSION:
                aggregate.ViewableImpressions++;
                break;
            case CLICK_THROUGH:
                aggregate.ClickThroughs++;
                break;
            case ENGAGEMENT_DURATION:
                aggregate.EngagementDurationMs += durationMs ?? 0;
                break;
        }
    }

    private static void ApplyEventType(SourceAggregate aggregate, string eventType, int? durationMs)
    {
        switch (eventType)
        {
            case IMPRESSION:
                aggregate.Impressions++;
                break;
            case VIEWABLE_IMPRESSION:
                aggregate.ViewableImpressions++;
                break;
            case CLICK_THROUGH:
                aggregate.ClickThroughs++;
                break;
            case ENGAGEMENT_DURATION:
                aggregate.EngagementDurationMs += durationMs ?? 0;
                break;
        }
    }

    /// <summary>
    /// Waits for the host application to signal that it has fully started.
    /// </summary>
    private async Task WaitForStartupAsync(CancellationToken stoppingToken)
    {
        var tcs = new TaskCompletionSource();
        await using var reg = stoppingToken.Register(() => tcs.TrySetCanceled(stoppingToken));
        lifetime.ApplicationStarted.Register(() => tcs.TrySetResult());
        await tcs.Task;
        // Additional delay for K8s DNS/networking to fully stabilize
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        logger.LogInformation("Host started, beginning rollup job");
    }

    /// <summary>
    /// Creates a DbContext with retry logic for transient connection failures.
    /// Uses an independent timeout so host shutdown doesn't cancel DB operations mid-connect.
    /// </summary>
    private async Task<TsContext> CreateDbContextWithRetryAsync(CancellationToken stoppingToken)
    {
        const int maxRetries = 5;
        for (int attempt = 1; ; attempt++)
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var context = await contextFactory.CreateDbContextAsync(connectTimeout.Token);
                await context.Database.CanConnectAsync(connectTimeout.Token);
                return context;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                logger.LogWarning(ex, "Database connection attempt {Attempt}/{MaxRetries} failed, retrying in {Delay}s", attempt, maxRetries, delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
            }
        }
    }
}
