using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;

namespace RedMist.EventOrchestration.Utilities;

public class CompetitorMetadataArchive : BaseArchive
{
    public CompetitorMetadataArchive(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IArchiveStorage archiveStorage, PurgeUtilities purgeUtilities)
        : base(loggerFactory, tsContext, archiveStorage, purgeUtilities)
    {
    }


    public async Task<bool> ArchiveCompetitorMetadataAsync(int eventId)
    {
        return await ArchiveCompetitorMetadataAsync(eventId, CancellationToken.None);
    }

    public async Task<bool> ArchiveCompetitorMetadataAsync(int eventId, CancellationToken cancellationToken)
    {
        string? tempFilePath = null;
        try
        {
            tempFilePath = Path.Combine(Path.GetTempPath(), $"event-{eventId}-competitor-metadata-{Guid.NewGuid()}.json");

            var totalRecords = await WriteCompetitorMetadataToFileAsync(eventId, tempFilePath, cancellationToken);

            if (totalRecords == 0)
            {
                Logger.LogInformation("No competitor metadata found to archive for event {eventId}", eventId);
                return true;
            }

            var uploadSuccess = await CompressAndUploadFileAsync(eventId, tempFilePath, cancellationToken);
            if (!uploadSuccess)
                return false;

            await DeleteCompetitorMetadataFromDatabaseAsync(eventId, totalRecords, cancellationToken);

            Logger.LogInformation("Successfully archived and deleted {count} competitor metadata records for event {eventId}", totalRecords, eventId);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error archiving competitor metadata for event {eventId}", eventId);
            return false;
        }
        finally
        {
            CleanupFile(tempFilePath, eventId);
        }
    }

    private async Task<long> WriteCompetitorMetadataToFileAsync(int eventId, string tempFilePath, CancellationToken cancellationToken)
    {
        await using var dbContext = await TsContext.CreateDbContextAsync(cancellationToken);
        var query = dbContext.CompetitorMetadata
            .Where(c => c.EventId == eventId)
            .OrderBy(c => c.CarNumber);

        return await WriteToJsonFileAsync(
            query,
            tempFilePath,
            batchSize: 100,
            jsonSerializer: null,
            progressCallback: (count, total) => Logger.LogDebug("Archived {count} competitor metadata records for event {eventId} (total: {totalRecords})", count, eventId, total),
            cancellationToken);
    }

    private async Task<bool> CompressAndUploadFileAsync(int eventId, string tempFilePath, CancellationToken cancellationToken)
    {
        return await CompressAndUploadFileAsync(
            tempFilePath,
            stream => ArchiveStorage.UploadEventCompetitorMetadataAsync(stream, eventId),
            $"archived competitor metadata for event {eventId}",
            cancellationToken);
    }

    private async Task DeleteCompetitorMetadataFromDatabaseAsync(int eventId, long totalRecords, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Deleting {count} competitor metadata records from database for event {eventId}", totalRecords, eventId);
        await using var dbContext = await TsContext.CreateDbContextAsync(cancellationToken);

        await ExecuteDeleteWithFallbackAsync(
            dbContext.CompetitorMetadata,
            query => query.Where(c => c.EventId == eventId),
            cancellationToken);
    }

    private void CleanupFile(string? tempFilePath, int eventId)
    {
        CleanupFile(tempFilePath, $"event {eventId}");
    }
}
