using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;

namespace RedMist.EventOrchestration.Utilities;

public class X2LogArchive : BaseArchive
{
    public X2LogArchive(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IArchiveStorage archiveStorage, PurgeUtilities purgeUtilities)
        : base(loggerFactory, tsContext, archiveStorage, purgeUtilities)
    {
    }


    public async Task<bool> ArchiveX2DataAsync(int eventId)
    {
        return await ArchiveX2DataAsync(eventId, CancellationToken.None);
    }

    public async Task<bool> ArchiveX2DataAsync(int eventId, CancellationToken cancellationToken)
    {
        string? tempLoopsFilePath = null;
        string? tempPassingsFilePath = null;
        try
        {
            tempLoopsFilePath = Path.Combine(Path.GetTempPath(), $"event-{eventId}-x2loops-{Guid.NewGuid()}.json");
            tempPassingsFilePath = Path.Combine(Path.GetTempPath(), $"event-{eventId}-x2passings-{Guid.NewGuid()}.json");

            var totalLoops = await WriteLoopsToFileAsync(eventId, tempLoopsFilePath, cancellationToken);
            var totalPassings = await WritePassingsToFileAsync(eventId, tempPassingsFilePath, cancellationToken);

            if (totalLoops == 0 && totalPassings == 0)
            {
                Logger.LogInformation("No X2 data found to archive for event {eventId}", eventId);
                return true;
            }

            // Upload loops if any exist
            if (totalLoops > 0)
            {
                var uploadSuccess = await CompressAndUploadLoopsFileAsync(eventId, tempLoopsFilePath, cancellationToken);
                if (!uploadSuccess)
                    return false;
            }

            // Upload passings if any exist
            if (totalPassings > 0)
            {
                var uploadSuccess = await CompressAndUploadPassingsFileAsync(eventId, tempPassingsFilePath, cancellationToken);
                if (!uploadSuccess)
                    return false;
            }

            await DeleteX2DataFromDatabaseAsync(eventId, totalLoops, totalPassings, cancellationToken);

            Logger.LogInformation("Successfully archived and deleted {loopCount} loops and {passingCount} passings for event {eventId}", 
                totalLoops, totalPassings, eventId);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error archiving X2 data for event {eventId}", eventId);
            return false;
        }
        finally
        {
            CleanupFile(tempLoopsFilePath, eventId, "loops");
            CleanupFile(tempPassingsFilePath, eventId, "passings");
        }
    }

    private async Task<long> WriteLoopsToFileAsync(int eventId, string tempFilePath, CancellationToken cancellationToken)
    {
        await using var dbContext = await TsContext.CreateDbContextAsync(cancellationToken);
        var query = dbContext.X2Loops
            .Where(l => l.EventId == eventId)
            .OrderBy(l => l.Id);

        return await WriteToJsonFileAsync(
            query,
            tempFilePath,
            batchSize: 100,
            jsonSerializer: null,
            progressCallback: (count, total) => Logger.LogDebug("Archived {count} X2 loops for event {eventId} (total: {totalLoops})", count, eventId, total),
            cancellationToken);
    }

    private async Task<long> WritePassingsToFileAsync(int eventId, string tempFilePath, CancellationToken cancellationToken)
    {
        await using var dbContext = await TsContext.CreateDbContextAsync(cancellationToken);
        var query = dbContext.X2Passings
            .Where(p => p.EventId == eventId)
            .OrderBy(p => p.Id);

        return await WriteToJsonFileAsync(
            query,
            tempFilePath,
            batchSize: 500,
            jsonSerializer: null,
            progressCallback: (count, total) => Logger.LogDebug("Archived {count} X2 passings for event {eventId} (total: {totalPassings})", count, eventId, total),
            cancellationToken);
    }

    private async Task<bool> CompressAndUploadLoopsFileAsync(int eventId, string tempFilePath, CancellationToken cancellationToken)
    {
        return await CompressAndUploadFileAsync(
            tempFilePath,
            stream => ArchiveStorage.UploadEventX2LoopsAsync(stream, eventId),
            $"archived X2 loops for event {eventId}",
            cancellationToken);
    }

    private async Task<bool> CompressAndUploadPassingsFileAsync(int eventId, string tempFilePath, CancellationToken cancellationToken)
    {
        return await CompressAndUploadFileAsync(
            tempFilePath,
            stream => ArchiveStorage.UploadEventX2PassingsAsync(stream, eventId),
            $"archived X2 passings for event {eventId}",
            cancellationToken);
    }

    private async Task DeleteX2DataFromDatabaseAsync(int eventId, long totalLoops, long totalPassings, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Deleting {loopCount} loops and {passingCount} passings from database for event {eventId}", 
            totalLoops, totalPassings, eventId);
        await using var dbContext = await TsContext.CreateDbContextAsync(cancellationToken);

        if (totalLoops > 0)
        {
            await ExecuteDeleteWithFallbackAsync(
                dbContext.X2Loops,
                query => query.Where(l => l.EventId == eventId),
                cancellationToken);
        }

        if (totalPassings > 0)
        {
            await ExecuteDeleteWithFallbackAsync(
                dbContext.X2Passings,
                query => query.Where(p => p.EventId == eventId),
                cancellationToken);
        }
    }

    private void CleanupFile(string? tempFilePath, int eventId, string fileType)
    {
        CleanupFile(tempFilePath, $"{fileType} for event {eventId}");
    }
}
