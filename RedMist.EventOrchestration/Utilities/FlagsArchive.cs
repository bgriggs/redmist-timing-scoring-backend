using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;

namespace RedMist.EventOrchestration.Utilities;

public class FlagsArchive : BaseArchive
{
    public FlagsArchive(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IArchiveStorage archiveStorage, PurgeUtilities purgeUtilities)
        : base(loggerFactory, tsContext, archiveStorage, purgeUtilities)
    {
    }


    public async Task<bool> ArchiveFlagsAsync(int eventId, int sessionId)
    {
        return await ArchiveFlagsAsync(eventId, sessionId, CancellationToken.None);
    }

    public async Task<bool> ArchiveFlagsAsync(int eventId, int sessionId, CancellationToken cancellationToken)
    {
        string? tempFilePath = null;
        try
        {
            tempFilePath = Path.Combine(Path.GetTempPath(), $"event-{eventId}-session-{sessionId}-flags-{Guid.NewGuid()}.json");

            var totalFlags = await WriteFlagsToFileAsync(eventId, sessionId, tempFilePath, cancellationToken);

            if (totalFlags == 0)
            {
                Logger.LogInformation("No flags found to archive for event {eventId}, session {sessionId}", eventId, sessionId);
                return true;
            }

            var uploadSuccess = await CompressAndUploadFileAsync(eventId, sessionId, tempFilePath, cancellationToken);
            if (!uploadSuccess)
                return false;

            await DeleteFlagsFromDatabaseAsync(eventId, sessionId, totalFlags, cancellationToken);

            Logger.LogInformation("Successfully archived and deleted {count} flags for event {eventId}, session {sessionId}", totalFlags, eventId, sessionId);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error archiving flags for event {eventId}, session {sessionId}", eventId, sessionId);
            return false;
        }
        finally
        {
            CleanupFiles(tempFilePath, eventId, sessionId);
        }
    }

    private async Task<long> WriteFlagsToFileAsync(int eventId, int sessionId, string tempFilePath, CancellationToken cancellationToken)
    {
        await using var dbContext = await TsContext.CreateDbContextAsync(cancellationToken);
        var query = dbContext.FlagLog
            .Where(f => f.EventId == eventId && f.SessionId == sessionId)
            .OrderBy(f => f.Flag)
            .ThenBy(f => f.StartTime);

        return await WriteToJsonFileAsync(
            query,
            tempFilePath,
            batchSize: 100,
            jsonSerializer: null,
            progressCallback: (count, total) => Logger.LogDebug("Archived {count} flags for event {eventId}, session {sessionId} (total: {totalFlags})", count, eventId, sessionId, total),
            cancellationToken);
    }

    private async Task<bool> CompressAndUploadFileAsync(int eventId, int sessionId, string tempFilePath, CancellationToken cancellationToken)
    {
        return await CompressAndUploadFileAsync(
            tempFilePath,
            stream => ArchiveStorage.UploadSessionFlagsAsync(stream, eventId, sessionId),
            $"archived flags for event {eventId}, session {sessionId}",
            cancellationToken);
    }

    private async Task DeleteFlagsFromDatabaseAsync(int eventId, int sessionId, long totalFlags, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Deleting {count} flags from database for event {eventId}, session {sessionId}", totalFlags, eventId, sessionId);
        await using var dbContext = await TsContext.CreateDbContextAsync(cancellationToken);

        await ExecuteDeleteWithFallbackAsync(
            dbContext.FlagLog,
            query => query.Where(f => f.EventId == eventId && f.SessionId == sessionId),
            cancellationToken);
    }

    private void CleanupFiles(string? tempFilePath, int eventId, int sessionId)
    {
        CleanupFile(tempFilePath, $"event {eventId}, session {sessionId}");
    }
}
