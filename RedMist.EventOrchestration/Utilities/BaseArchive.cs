using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using System.IO.Compression;

namespace RedMist.EventOrchestration.Utilities;

public abstract class BaseArchive
{
    protected readonly IDbContextFactory<TsContext> TsContext;
    protected readonly IArchiveStorage ArchiveStorage;
    protected readonly PurgeUtilities PurgeUtilities;
    protected ILogger Logger { get; }

    protected BaseArchive(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IArchiveStorage archiveStorage, PurgeUtilities purgeUtilities)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        TsContext = tsContext;
        ArchiveStorage = archiveStorage;
        PurgeUtilities = purgeUtilities;
    }

    protected async Task<bool> CompressAndUploadFileAsync(string tempFilePath, Func<Stream, Task<bool>> uploadFunc, string errorContext, CancellationToken cancellationToken)
    {
        string gzipFilePath = tempFilePath + ".gz";

        await using (var originalFileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.None, 8192, useAsync: true))
        await using (var compressedFileStream = new FileStream(gzipFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
        await using (var gzipStream = new GZipStream(compressedFileStream, CompressionLevel.Optimal))
        {
            await originalFileStream.CopyToAsync(gzipStream, cancellationToken);
        }

        await using var uploadStream = new FileStream(gzipFilePath, FileMode.Open, FileAccess.Read, FileShare.None, 8192, useAsync: true);
        var uploadSuccess = await uploadFunc(uploadStream);
        if (!uploadSuccess)
        {
            Logger.LogError("Failed to upload {context}", errorContext);
            return false;
        }

        Logger.LogInformation("Successfully uploaded {context}", errorContext);
        return true;
    }

    protected void CleanupFile(string? tempFilePath, string context)
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
            Logger.LogWarning(ex, "Failed to delete temp files for {context}", context);
        }
    }

    protected async Task<long> WriteToJsonFileAsync<T>(
        IQueryable<T> query,
        string tempFilePath,
        int batchSize,
        Func<T, string>? jsonSerializer,
        Action<int, long>? progressCallback,
        CancellationToken cancellationToken) where T : class
    {
        long totalRecords = 0;
        bool isFirstBatch = true;

        var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        var writer = new StreamWriter(fileStream);

        try
        {
            await writer.WriteLineAsync("[");

            while (true)
            {
                var batch = await query
                    .Skip((int)totalRecords)
                    .Take(batchSize)
                    .ToListAsync(cancellationToken);

                if (batch.Count == 0)
                    break;

                foreach (var record in batch)
                {
                    if (!isFirstBatch)
                        await writer.WriteLineAsync(",");

                    var recordJson = jsonSerializer != null 
                        ? jsonSerializer(record) 
                        : System.Text.Json.JsonSerializer.Serialize(record);
                    await writer.WriteAsync(recordJson);
                    isFirstBatch = false;
                }

                totalRecords += batch.Count;
                progressCallback?.Invoke(batch.Count, totalRecords);

                if (batch.Count < batchSize)
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

        return totalRecords;
    }

    protected async Task ExecuteDeleteWithFallbackAsync<T>(
        DbSet<T> dbSet,
        Func<IQueryable<T>, IQueryable<T>> whereClause,
        CancellationToken cancellationToken) where T : class
    {
        var dbContext = dbSet.GetService<ICurrentDbContext>().Context;

        try
        {
            await whereClause(dbSet).ExecuteDeleteAsync(cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("ExecuteDelete"))
        {
            // Fallback for InMemory database
            var recordsToDelete = await whereClause(dbSet).ToListAsync(cancellationToken);
            dbSet.RemoveRange(recordsToDelete);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
