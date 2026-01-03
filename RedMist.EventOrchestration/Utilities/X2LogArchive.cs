using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using System.IO.Compression;

namespace RedMist.EventOrchestration.Utilities;

public class X2LogArchive
{
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly IArchiveStorage archiveStorage;
    private readonly PurgeUtilities purgeUtilities;
    private ILogger Logger { get; }


    public X2LogArchive(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IArchiveStorage archiveStorage, PurgeUtilities purgeUtilities)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.archiveStorage = archiveStorage;
        this.purgeUtilities = purgeUtilities;
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
        long totalLoops = 0;
        const int batchSize = 100;
        bool isFirstBatch = true;

        var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        var writer = new StreamWriter(fileStream);

        try
        {
            await writer.WriteLineAsync("[");

            while (true)
            {
                await using var dbContext = await tsContext.CreateDbContextAsync(cancellationToken);

                var loopBatch = await dbContext.X2Loops
                    .Where(l => l.EventId == eventId)
                    .OrderBy(l => l.Id)
                    .Skip((int)totalLoops)
                    .Take(batchSize)
                    .ToListAsync(cancellationToken);

                if (loopBatch.Count == 0)
                    break;

                foreach (var loop in loopBatch)
                {
                    if (!isFirstBatch)
                        await writer.WriteLineAsync(",");

                    var loopJson = System.Text.Json.JsonSerializer.Serialize(loop);
                    await writer.WriteAsync(loopJson);
                    isFirstBatch = false;
                }

                totalLoops += loopBatch.Count;
                Logger.LogDebug("Archived {count} X2 loops for event {eventId} (total: {totalLoops})", loopBatch.Count, eventId, totalLoops);

                if (loopBatch.Count < batchSize)
                    break;
            }

            await writer.WriteLineAsync();
            await writer.WriteLineAsync("]");
            await writer.FlushAsync(cancellationToken);
            await fileStream.FlushAsync(cancellationToken);
        }
        finally
        {
            await writer.DisposeAsync();
            await fileStream.DisposeAsync();
        }

        return totalLoops;
    }

    private async Task<long> WritePassingsToFileAsync(int eventId, string tempFilePath, CancellationToken cancellationToken)
    {
        long totalPassings = 0;
        const int batchSize = 500;
        bool isFirstBatch = true;

        var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        var writer = new StreamWriter(fileStream);

        try
        {
            await writer.WriteLineAsync("[");

            while (true)
            {
                await using var dbContext = await tsContext.CreateDbContextAsync(cancellationToken);

                var passingBatch = await dbContext.X2Passings
                    .Where(p => p.EventId == eventId)
                    .OrderBy(p => p.Id)
                    .Skip((int)totalPassings)
                    .Take(batchSize)
                    .ToListAsync(cancellationToken);

                if (passingBatch.Count == 0)
                    break;

                foreach (var passing in passingBatch)
                {
                    if (!isFirstBatch)
                        await writer.WriteLineAsync(",");

                    var passingJson = System.Text.Json.JsonSerializer.Serialize(passing);
                    await writer.WriteAsync(passingJson);
                    isFirstBatch = false;
                }

                totalPassings += passingBatch.Count;
                Logger.LogDebug("Archived {count} X2 passings for event {eventId} (total: {totalPassings})", passingBatch.Count, eventId, totalPassings);

                if (passingBatch.Count < batchSize)
                    break;
            }

            await writer.WriteLineAsync();
            await writer.WriteLineAsync("]");
            await writer.FlushAsync(cancellationToken);
            await fileStream.FlushAsync(cancellationToken);
        }
        finally
        {
            await writer.DisposeAsync();
            await fileStream.DisposeAsync();
        }

        return totalPassings;
    }

    private async Task<bool> CompressAndUploadLoopsFileAsync(int eventId, string tempFilePath, CancellationToken cancellationToken)
    {
        string gzipFilePath = tempFilePath + ".gz";

        await using (var originalFileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.None, 8192, useAsync: true))
        await using (var compressedFileStream = new FileStream(gzipFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
        await using (var gzipStream = new GZipStream(compressedFileStream, CompressionLevel.Optimal))
        {
            await originalFileStream.CopyToAsync(gzipStream, cancellationToken);
        }

        await using var uploadStream = new FileStream(gzipFilePath, FileMode.Open, FileAccess.Read, FileShare.None, 8192, useAsync: true);
        var uploadSuccess = await archiveStorage.UploadEventX2LoopsAsync(uploadStream, eventId);
        if (!uploadSuccess)
        {
            Logger.LogError("Failed to upload archived X2 loops for event {eventId}", eventId);
            return false;
        }

        Logger.LogInformation("Successfully uploaded X2 loops for event {eventId}", eventId);
        return true;
    }

    private async Task<bool> CompressAndUploadPassingsFileAsync(int eventId, string tempFilePath, CancellationToken cancellationToken)
    {
        string gzipFilePath = tempFilePath + ".gz";

        await using (var originalFileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.None, 8192, useAsync: true))
        await using (var compressedFileStream = new FileStream(gzipFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
        await using (var gzipStream = new GZipStream(compressedFileStream, CompressionLevel.Optimal))
        {
            await originalFileStream.CopyToAsync(gzipStream, cancellationToken);
        }

        await using var uploadStream = new FileStream(gzipFilePath, FileMode.Open, FileAccess.Read, FileShare.None, 8192, useAsync: true);
        var uploadSuccess = await archiveStorage.UploadEventX2PassingsAsync(uploadStream, eventId);
        if (!uploadSuccess)
        {
            Logger.LogError("Failed to upload archived X2 passings for event {eventId}", eventId);
            return false;
        }

        Logger.LogInformation("Successfully uploaded X2 passings for event {eventId}", eventId);
        return true;
    }

    private async Task DeleteX2DataFromDatabaseAsync(int eventId, long totalLoops, long totalPassings, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Deleting {loopCount} loops and {passingCount} passings from database for event {eventId}", 
            totalLoops, totalPassings, eventId);
        await using var dbContext = await tsContext.CreateDbContextAsync(cancellationToken);

        // ExecuteDeleteAsync is not supported by InMemory database, so we need to handle both approaches
        try
        {
            if (totalLoops > 0)
            {
                await dbContext.X2Loops
                    .Where(l => l.EventId == eventId)
                    .ExecuteDeleteAsync(cancellationToken);
            }

            if (totalPassings > 0)
            {
                await dbContext.X2Passings
                    .Where(p => p.EventId == eventId)
                    .ExecuteDeleteAsync(cancellationToken);
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("ExecuteDelete"))
        {
            // Fallback for InMemory database
            if (totalLoops > 0)
            {
                var loopsToDelete = await dbContext.X2Loops
                    .Where(l => l.EventId == eventId)
                    .ToListAsync(cancellationToken);
                dbContext.X2Loops.RemoveRange(loopsToDelete);
            }

            if (totalPassings > 0)
            {
                var passingsToDelete = await dbContext.X2Passings
                    .Where(p => p.EventId == eventId)
                    .ToListAsync(cancellationToken);
                dbContext.X2Passings.RemoveRange(passingsToDelete);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private void CleanupFile(string? tempFilePath, int eventId, string fileType)
    {
        if (tempFilePath == null)
            return;

        try
        {
            if (File.Exists(tempFilePath))
                File.Delete(tempFilePath);

            string gzipFilePath = tempFilePath + ".gz";
            if (File.Exists(gzipFilePath))
                File.Delete(gzipFilePath);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to delete {fileType} temp files for event {eventId}", fileType, eventId);
        }
    }
}
