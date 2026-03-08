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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Sponsor statistics rollup job starting");

            var now = DateTime.UtcNow;
            var previousMonth = now.AddMonths(-1);
            var monthStart = new DateTime(previousMonth.Year, previousMonth.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthEnd = monthStart.AddMonths(1);
            var month = DateOnly.FromDateTime(monthStart);

            logger.LogInformation("Processing telemetry for {Month:yyyy-MM} (range {Start} to {End})", month, monthStart, monthEnd);

            await using var context = await contextFactory.CreateDbContextAsync(stoppingToken);

            // Build SponsorId lookup set from Sponsors table
            var sponsorIds = await context.Sponsors
                .AsNoTracking()
                .Select(s => s.Id)
                .ToHashSetAsync(stoppingToken);
            logger.LogInformation("Loaded {Count} sponsors for ID lookup", sponsorIds.Count);

            // Load previous month's telemetry logs
            var telemetryLogs = await context.SponsorTelemetryLogs
                .AsNoTracking()
                .Where(t => t.Timestamp >= monthStart && t.Timestamp < monthEnd)
                .ToListAsync(stoppingToken);

            logger.LogInformation("Found {Count} telemetry log entries for {Month:yyyy-MM}", telemetryLogs.Count, month);

            if (telemetryLogs.Count == 0)
            {
                logger.LogInformation("No telemetry data to process, exiting");
                lifetime.StopApplication();
                return;
            }

            // Resolve SponsorId for each log entry by parsing ImageId as int
            var resolvedLogs = new List<(SponsorTelemetryLog Log, int SponsorId)>();
            var unmatchedImages = new HashSet<string>();

            foreach (var log in telemetryLogs)
            {
                if (int.TryParse(log.ImageId, out var sponsorId) && sponsorIds.Contains(sponsorId))
                {
                    resolvedLogs.Add((log, sponsorId));
                }
                else
                {
                    unmatchedImages.Add(log.ImageId);
                }
            }

            if (unmatchedImages.Count > 0)
            {
                logger.LogWarning("Could not resolve SponsorId for {Count} distinct ImageId value(s): {ImageIds}",
                    unmatchedImages.Count, string.Join(", ", unmatchedImages.Take(20)));
            }

            // Group by SponsorId and build statistics
            var sponsorGroups = resolvedLogs.GroupBy(r => r.SponsorId);

            foreach (var sponsorGroup in sponsorGroups)
            {
                var sponsorId = sponsorGroup.Key;
                var logs = sponsorGroup.Select(g => g.Log).ToList();

                // Check for existing record for this sponsor/month to support re-runs
                var existing = await context.SponsorStatistics
                    .Include(s => s.EventStatistics)
                    .Include(s => s.SourceStatistics)
                    .FirstOrDefaultAsync(s => s.SponsorId == sponsorId && s.Month == month, stoppingToken);

                // Compute aggregated values
                var impressions = logs.Count(l => l.EventType == IMPRESSION);
                var viewableImpressions = logs.Count(l => l.EventType == VIEWABLE_IMPRESSION);
                var clickThroughs = logs.Count(l => l.EventType == CLICK_THROUGH);
                var engagementDurationMs = logs.Where(l => l.EventType == ENGAGEMENT_DURATION).Sum(l => (long)(l.DurationMs ?? 0));

                SponsorStatistics stats;

                if (existing != null)
                {
                    logger.LogWarning("Statistics already exist for SponsorId {SponsorId} for {Month:yyyy-MM}, overwriting with new data", sponsorId, month);

                    // Remove old child records
                    context.EventSponsorStatistics.RemoveRange(existing.EventStatistics);
                    context.SourceSponsorStatistics.RemoveRange(existing.SourceStatistics);

                    // Overwrite totals on existing record
                    existing.Impressions = impressions;
                    existing.ViewableImpressions = viewableImpressions;
                    existing.ClickThroughs = clickThroughs;
                    existing.EngagementDurationMs = engagementDurationMs;
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
                        Impressions = impressions,
                        ViewableImpressions = viewableImpressions,
                        ClickThroughs = clickThroughs,
                        EngagementDurationMs = engagementDurationMs,
                    };
                    context.SponsorStatistics.Add(stats);
                }

                // Level 2: Per-event statistics
                var eventGroups = logs.GroupBy(l => ParseEventId(l.EventId));
                foreach (var eventGroup in eventGroups)
                {
                    stats.EventStatistics.Add(new EventSponsorStatistics
                    {
                        SponsorId = sponsorId,
                        EventId = eventGroup.Key,
                        Impressions = eventGroup.Count(l => l.EventType == IMPRESSION),
                        ViewableImpressions = eventGroup.Count(l => l.EventType == VIEWABLE_IMPRESSION),
                        ClickThroughs = eventGroup.Count(l => l.EventType == CLICK_THROUGH),
                        EngagementDurationMs = eventGroup.Where(l => l.EventType == ENGAGEMENT_DURATION).Sum(l => (long)(l.DurationMs ?? 0)),
                    });
                }

                // Level 3: Per-source statistics
                var sourceGroups = logs.GroupBy(l => l.Source);
                foreach (var sourceGroup in sourceGroups)
                {
                    stats.SourceStatistics.Add(new SourceSponsorStatistics
                    {
                        SponsorId = sponsorId,
                        Source = sourceGroup.Key,
                        Impressions = sourceGroup.Count(l => l.EventType == IMPRESSION),
                        ViewableImpressions = sourceGroup.Count(l => l.EventType == VIEWABLE_IMPRESSION),
                        ClickThroughs = sourceGroup.Count(l => l.EventType == CLICK_THROUGH),
                        EngagementDurationMs = sourceGroup.Where(l => l.EventType == ENGAGEMENT_DURATION).Sum(l => (long)(l.DurationMs ?? 0)),
                    });
                }

                logger.LogInformation("Sponsor {SponsorId}: {Impressions} impressions, {Viewable} viewable, {Clicks} clicks, {Duration}ms engagement, {Events} event groups, {Sources} source groups",
                    sponsorId, stats.Impressions, stats.ViewableImpressions, stats.ClickThroughs, stats.EngagementDurationMs,
                    stats.EventStatistics.Count, stats.SourceStatistics.Count);
            }

            await context.SaveChangesAsync(stoppingToken);
            logger.LogInformation("Sponsor statistics rollup completed for {Month:yyyy-MM}. Processed {SponsorCount} sponsor(s)", month, sponsorGroups.Count());
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
}
