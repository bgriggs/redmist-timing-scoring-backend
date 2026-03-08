using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using System.Text;

namespace RedMist.SponsorReports;

/// <summary>
/// Background job that finds unprocessed SponsorStatistics records and sends
/// HTML report emails to the corresponding sponsor contacts.
/// Stops the host after completion.
/// </summary>
public class SponsorReportJob(
    ILoggerFactory loggerFactory,
    IDbContextFactory<TsContext> contextFactory,
    EmailHelper emailHelper,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    private const string FROM_EMAIL = "support@redmist.racing";
    private const string BCC_EMAIL = "brian@bigmissionmotorsports.com";

    private readonly ILogger logger = loggerFactory.CreateLogger<SponsorReportJob>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Sponsor report job starting");

            await using var context = await contextFactory.CreateDbContextAsync(stoppingToken);

            // Find unprocessed statistics
            var unprocessed = await context.SponsorStatistics
                .Include(s => s.EventStatistics)
                .Include(s => s.SourceStatistics)
                .Where(s => !s.ReportProcessed)
                .ToListAsync(stoppingToken);

            logger.LogInformation("Found {Count} unprocessed sponsor statistics record(s)", unprocessed.Count);

            if (unprocessed.Count == 0)
            {
                logger.LogInformation("No reports to process, exiting");
                lifetime.StopApplication();
                return;
            }

            // Load sponsor info for all relevant sponsors
            var sponsorIds = unprocessed.Select(s => s.SponsorId).Distinct().ToList();
            var sponsors = await context.Sponsors
                .AsNoTracking()
                .Where(s => sponsorIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, stoppingToken);

            // Load event names for all referenced event IDs
            var eventIds = unprocessed
                .SelectMany(s => s.EventStatistics)
                .Select(e => e.EventId)
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            var eventNames = await context.Events
                .AsNoTracking()
                .Where(e => eventIds.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => e.Name, stoppingToken);

            foreach (var stats in unprocessed)
            {
                try
                {
                    if (!sponsors.TryGetValue(stats.SponsorId, out var sponsor))
                    {
                        logger.LogWarning("Sponsor {SponsorId} not found, skipping report", stats.SponsorId);
                        stats.ReportProcessed = true;
                        stats.ReportProcessingSuccessful = false;
                        await SendFailureNotificationAsync($"Sponsor report skipped: Sponsor ID {stats.SponsorId} not found in database (Month: {stats.Month:yyyy-MM}).");
                        continue;
                    }

                    var monthName = stats.Month.ToString("MMMM yyyy");
                    var subject = $"Red Mist Sponsor Report - {monthName}";
                    var html = BuildReportHtml(stats, sponsor, monthName, eventNames);

                    if (sponsor.SendMonthlyReport && !string.IsNullOrWhiteSpace(sponsor.ContactEmail))
                    {
                        await emailHelper.SendEmailAsync(subject, html, sponsor.ContactEmail, FROM_EMAIL, BCC_EMAIL);
                        logger.LogInformation("Report sent to {Email} for SponsorId {SponsorId} ({Name}) - {Month}",
                            sponsor.ContactEmail, sponsor.Id, sponsor.Name, monthName);
                    }
                    else
                    {
                        await emailHelper.SendEmailAsync(subject, html, BCC_EMAIL, FROM_EMAIL);
                        logger.LogInformation("Report sent to admin for SponsorId {SponsorId} ({Name}) - {Month} (SendMonthlyReport={Flag}, ContactEmail={Email})",
                            sponsor.Id, sponsor.Name, monthName, sponsor.SendMonthlyReport, sponsor.ContactEmail);
                    }

                    stats.ReportProcessed = true;
                    stats.ReportProcessingSuccessful = true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to send report for SponsorId {SponsorId}", stats.SponsorId);
                    stats.ReportProcessed = true;
                    stats.ReportProcessingSuccessful = false;

                    var sponsorName = sponsors.TryGetValue(stats.SponsorId, out var s) ? s.Name : "Unknown";
                    await SendFailureNotificationAsync(
                        $"Sponsor report failed for '{sponsorName}' (ID {stats.SponsorId}, Month: {stats.Month:yyyy-MM}).\n\nException: {ex}");
                }
            }

            await context.SaveChangesAsync(stoppingToken);
            logger.LogInformation("Sponsor report job completed. Processed {Count} report(s)", unprocessed.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during sponsor report processing");
            throw;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    private static string BuildReportHtml(SponsorStatistics stats, Sponsor sponsor, string monthName, Dictionary<int, string> eventNames)
    {
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head><style>");
        sb.AppendLine("body { font-family: Arial, Helvetica, sans-serif; color: #333; margin: 0; padding: 20px; }");
        sb.AppendLine("h2 { color: #c0392b; border-bottom: 2px solid #c0392b; padding-bottom: 6px; }");
        sb.AppendLine("table { border-collapse: collapse; width: 100%; margin-bottom: 24px; }");
        sb.AppendLine("th, td { border: 1px solid #ddd; padding: 10px 14px; text-align: left; }");
        sb.AppendLine("th { background-color: #c0392b; color: #fff; }");
        sb.AppendLine("tr:nth-child(even) { background-color: #f9f9f9; }");
        sb.AppendLine("</style></head><body>");

        // Greeting
        var contactName = string.IsNullOrWhiteSpace(sponsor.ContactName) ? sponsor.Name : sponsor.ContactName;
        sb.AppendLine($"<p>Hello {contactName},</p>");
        sb.AppendLine($"<p>Here is your {monthName} sponsorship report from Red Mist.</p>");

        // Overall Summary
        sb.AppendLine("<h2>Overall Summary</h2>");
        sb.AppendLine("<table>");
        sb.AppendLine($"<tr><td><strong>Impressions</strong></td><td>{stats.Impressions:N0}</td></tr>");
        sb.AppendLine($"<tr><td><strong>Viewable Impressions</strong></td><td>{stats.ViewableImpressions:N0}</td></tr>");
        sb.AppendLine($"<tr><td><strong>Click-Throughs</strong></td><td>{stats.ClickThroughs:N0}</td></tr>");
        sb.AppendLine($"<tr><td><strong>Engagement Duration</strong></td><td>{FormatDuration(stats.EngagementDurationMs)}</td></tr>");
        sb.AppendLine("</table>");

        // Event Breakdown
        if (stats.EventStatistics.Count > 0)
        {
            sb.AppendLine("<h2>Event Breakdown</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Event</th><th>Impressions</th><th>Viewable Impressions</th><th>Click-Throughs</th><th>Engagement Duration</th></tr>");

            foreach (var e in stats.EventStatistics.OrderByDescending(e => e.Impressions))
            {
                var eventName = ResolveEventName(e.EventId, eventNames);
                sb.AppendLine($"<tr><td>{eventName}</td><td>{e.Impressions:N0}</td><td>{e.ViewableImpressions:N0}</td><td>{e.ClickThroughs:N0}</td><td>{FormatDuration(e.EngagementDurationMs)}</td></tr>");
            }

            sb.AppendLine("</table>");
        }

        // Source Breakdown
        if (stats.SourceStatistics.Count > 0)
        {
            sb.AppendLine("<h2>Source Breakdown</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Source</th><th>Impressions</th><th>Viewable Impressions</th><th>Click-Throughs</th><th>Engagement Duration</th></tr>");

            foreach (var s in stats.SourceStatistics.OrderByDescending(s => s.Impressions))
            {
                sb.AppendLine($"<tr><td>{s.Source}</td><td>{s.Impressions:N0}</td><td>{s.ViewableImpressions:N0}</td><td>{s.ClickThroughs:N0}</td><td>{FormatDuration(s.EngagementDurationMs)}</td></tr>");
            }

            sb.AppendLine("</table>");
        }

        // Sponsorship details
        if (sponsor.SubscriptionStart > DateOnly.MinValue && sponsor.SubscriptionEnd.HasValue)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var monthsRemaining = ((sponsor.SubscriptionEnd.Value.Year - today.Year) * 12) + sponsor.SubscriptionEnd.Value.Month - today.Month;
            if (monthsRemaining < 0)
                monthsRemaining = 0;
            sb.AppendLine($"<p>Sponsorship {sponsor.SubscriptionStart:MMMM d, yyyy} to {sponsor.SubscriptionEnd.Value:MMMM d, yyyy}. {monthsRemaining} {(monthsRemaining == 1 ? "month" : "months")} remaining.</p>");
        }

        sb.AppendLine("<p>Thank you for your sponsorship!</p>");
        sb.AppendLine("<p>— Red Mist Timing</p>");
        sb.AppendLine("</body></html>");

        return sb.ToString();
    }

    private static string ResolveEventName(int eventId, Dictionary<int, string> eventNames)
    {
        if (eventId == 0)
            return "No Event";
        return eventNames.TryGetValue(eventId, out var name) ? name : "Unknown";
    }

    private async Task SendFailureNotificationAsync(string errorDetails)
    {
        try
        {
            var body = $"<html><body><p>A sponsor report processing error occurred:</p><pre>{System.Net.WebUtility.HtmlEncode(errorDetails)}</pre></body></html>";
            await emailHelper.SendEmailAsync("Red Mist Sponsor Report - Processing Error", body, BCC_EMAIL, FROM_EMAIL);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send failure notification email");
        }
    }

    /// <summary>
    /// Converts milliseconds to a human-readable duration string.
    /// </summary>
    internal static string FormatDuration(long totalMs)
    {
        if (totalMs <= 0)
            return "0 seconds";

        var ts = TimeSpan.FromMilliseconds(totalMs);
        var parts = new List<string>();

        if (ts.Days > 0)
            parts.Add($"{ts.Days} {(ts.Days == 1 ? "day" : "days")}");
        if (ts.Hours > 0)
            parts.Add($"{ts.Hours} {(ts.Hours == 1 ? "hour" : "hours")}");
        if (ts.Minutes > 0)
            parts.Add($"{ts.Minutes} {(ts.Minutes == 1 ? "minute" : "minutes")}");
        if (ts.Seconds > 0 || parts.Count == 0)
            parts.Add($"{ts.Seconds} {(ts.Seconds == 1 ? "second" : "seconds")}");

        return string.Join(", ", parts);
    }
}
