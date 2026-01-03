using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using StackExchange.Redis;
using System.Text;

namespace RedMist.RelayApi.Services;

public class RelayLogBatchEmailService : BackgroundService
{
    private readonly ILogger<RelayLogBatchEmailService> logger;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly EmailHelper emailHelper;
    private const int InactivityThresholdSeconds = 60;
    private const int CheckIntervalSeconds = 15;


    public RelayLogBatchEmailService(
        ILogger<RelayLogBatchEmailService> logger,
        IConnectionMultiplexer cacheMux,
        IDbContextFactory<TsContext> tsContext,
        EmailHelper emailHelper)
    {
        this.logger = logger;
        this.cacheMux = cacheMux;
        this.tsContext = tsContext;
        this.emailHelper = emailHelper;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("RelayLogBatchEmailService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchesAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(CheckIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in RelayLogBatchEmailService");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        logger.LogInformation("RelayLogBatchEmailService stopped");
    }

    private async Task ProcessBatchesAsync(CancellationToken stoppingToken)
    {
        var cache = cacheMux.GetDatabase();
        
        // Get all tracked client IDs
        var trackedClients = await cache.SetMembersAsync(Consts.RELAY_LOG_BATCH_TRACKING);
        
        foreach (var clientIdValue in trackedClients)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            var clientId = clientIdValue.ToString();
            await ProcessClientBatchAsync(clientId, cache, stoppingToken);
        }
    }

    private async Task ProcessClientBatchAsync(string clientId, IDatabase cache, CancellationToken stoppingToken)
    {
        try
        {
            var batchKey = string.Format(Consts.RELAY_LOG_BATCH, clientId);
            
            // Check if batch exists
            if (!await cache.KeyExistsAsync(batchKey))
            {
                await cache.SetRemoveAsync(Consts.RELAY_LOG_BATCH_TRACKING, clientId);
                return;
            }

            // Get last log timestamp
            var lastLogTicksValue = await cache.HashGetAsync(batchKey, "lastLogTimestamp");
            if (!lastLogTicksValue.HasValue)
                return;

            var lastLogTimestamp = new DateTime(long.Parse(lastLogTicksValue!));
            var timeSinceLastLog = DateTime.UtcNow - lastLogTimestamp;

            // Check if batch is ready to send (no logs for 1 minute)
            if (timeSinceLastLog.TotalSeconds < InactivityThresholdSeconds)
                return;

            // Get batch data
            var batchData = await cache.HashGetAllAsync(batchKey);
            var batchDict = batchData.ToDictionary(
                x => x.Name.ToString(),
                x => x.Value.ToString()
            );

            if (!batchDict.ContainsKey("count") || !batchDict.ContainsKey("clientId"))
                return;

            var count = int.Parse(batchDict["count"]);
            if (count == 0)
                return;

            // Get organization name
            var orgId = int.Parse(batchDict.GetValueOrDefault("orgId", "0"));
            var orgName = await GetOrganizationNameAsync(orgId, stoppingToken);

            // Send email
            await SendBatchEmailAsync(clientId, orgName, count, batchDict, stoppingToken);

            // Clean up
            await cache.KeyDeleteAsync(batchKey);
            await cache.SetRemoveAsync(Consts.RELAY_LOG_BATCH_TRACKING, clientId);

            logger.LogInformation("Sent relay log batch email for client {ClientId} with {Count} logs", clientId, count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing batch for client {ClientId}", clientId);
        }
    }

    private async Task<string> GetOrganizationNameAsync(int orgId, CancellationToken stoppingToken)
    {
        try
        {
            if (orgId == 0)
                return "Unknown Organization";

            await using var db = await tsContext.CreateDbContextAsync(stoppingToken);
            var org = await db.Organizations.FirstOrDefaultAsync(o => o.Id == orgId, stoppingToken);
            return org?.Name ?? $"Relay Logs Received from Organization {orgId}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting organization name for ID {OrgId}", orgId);
            return $"Organization {orgId}";
        }
    }

    private async Task SendBatchEmailAsync(string clientId, string orgName, int count, Dictionary<string, string> batchData, CancellationToken stoppingToken)
    {
        try
        {
            var subject = $"Red Mist Relay Log Report - {clientId}";

            var bodyBuilder = new StringBuilder();
            bodyBuilder.AppendLine("<html><body>");
            bodyBuilder.AppendLine("<h2>Relay Log Batch Report</h2>");
            bodyBuilder.AppendLine($"<p><strong>Client ID:</strong> {clientId}</p>");
            bodyBuilder.AppendLine($"<p><strong>Organization:</strong> {orgName}</p>");
            bodyBuilder.AppendLine($"<p><strong>Total Logs:</strong> {count}</p>");
            bodyBuilder.AppendLine($"<p><strong>Report Time:</strong> {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</p>");

            // Add log level breakdown
            var levelCounts = batchData
                .Where(kvp => kvp.Key.StartsWith("level_"))
                .OrderByDescending(kvp => int.Parse(kvp.Value))
                .ToList();

            if (levelCounts.Any())
            {
                bodyBuilder.AppendLine("<h3>Log Levels:</h3>");
                bodyBuilder.AppendLine("<ul>");
                foreach (var levelCount in levelCounts)
                {
                    var level = levelCount.Key.Replace("level_", "");
                    var levelCountValue = levelCount.Value;
                    bodyBuilder.AppendLine($"<li><strong>{level}:</strong> {levelCountValue}</li>");
                }
                bodyBuilder.AppendLine("</ul>");
            }

            bodyBuilder.AppendLine("<p>This is an automated notification that relay logs have been received and saved to the database.</p>");
            bodyBuilder.AppendLine("</body></html>");

            var body = bodyBuilder.ToString();

            await emailHelper.SendEmailAsync(subject, body, "support@redmist.racing", "noreply@redmist.racing");
            
            logger.LogInformation("Successfully sent relay log batch email for client {ClientId}", clientId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send relay log batch email for client {ClientId}", clientId);
        }
    }
}
