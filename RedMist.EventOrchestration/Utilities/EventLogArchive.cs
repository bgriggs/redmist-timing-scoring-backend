using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;

namespace RedMist.EventOrchestration.Utilities;

public class EventLogArchive
{
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly IArchiveStorage archiveStorage;
    private ILogger Logger { get; }


    public EventLogArchive(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IArchiveStorage archiveStorage)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.archiveStorage = archiveStorage;
    }


    public async Task<bool> ArchiveEventLogsAsync(int eventId)
    {
        return await ArchiveEventLogsAsync(eventId, CancellationToken.None);
    }

    public async Task<bool> ArchiveEventLogsAsync(int eventId, CancellationToken cancellationToken)
    {
        string? tempEventFilePath = null;
        var sessionFiles = new Dictionary<int, SessionFileWriters>();
        try
        {
            tempEventFilePath = Path.Combine(Path.GetTempPath(), $"event-{eventId}-logs-{Guid.NewGuid()}.json");

            var totalLogs = await WriteLogsToFilesAsync(eventId, tempEventFilePath, sessionFiles, cancellationToken);

            if (totalLogs == 0)
            {
                Logger.LogInformation("No logs found to archive for event {eventId}", eventId);
                return true;
            }

            var uploadSuccess = await CompressAndUploadSessionFilesAsync(eventId, sessionFiles, cancellationToken);
            if (!uploadSuccess)
                return false;

            uploadSuccess = await CompressAndUploadEventFileAsync(eventId, tempEventFilePath, cancellationToken);
            if (!uploadSuccess)
                return false;

            await DeleteLogsFromDatabaseAsync(eventId, totalLogs, cancellationToken);

            Logger.LogInformation("Successfully archived and deleted {count} logs for event {eventId}", totalLogs, eventId);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error archiving logs for event {eventId}", eventId);
            return false;
        }
        finally
        {
            await CleanupSessionFilesAsync(sessionFiles);
            CleanupEventFiles(tempEventFilePath, eventId);
        }
    }

    private async Task<long> WriteLogsToFilesAsync(int eventId, string tempFilePath, Dictionary<int, SessionFileWriters> sessionFiles, CancellationToken cancellationToken)
    {
        long totalLogs = 0;
        const int batchSize = 500;
        bool isFirstBatch = true;

        await using var eventFileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        await using var eventWriter = new StreamWriter(eventFileStream);

        await eventWriter.WriteLineAsync("[");

        while (true)
        {
            await using var dbContext = await tsContext.CreateDbContextAsync(cancellationToken);

            var logBatch = await dbContext.EventStatusLogs
                .Where(e => e.EventId == eventId)
                .OrderBy(e => e.Id)
                .Skip((int)totalLogs)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (logBatch.Count == 0)
                break;

            foreach (var log in logBatch)
            {
                if (!isFirstBatch)
                    await eventWriter.WriteLineAsync(",");

                var logJson = System.Text.Json.JsonSerializer.Serialize(log);
                await eventWriter.WriteAsync(logJson);
                isFirstBatch = false;

                if (log.SessionId > 0)
                {
                    await WriteLogToSessionFileAsync(eventId, log, sessionFiles);
                }
            }

            totalLogs += logBatch.Count;
            Logger.LogDebug("Archived {count} logs for event {eventId} (total: {totalLogs})", logBatch.Count, eventId, totalLogs);

            if (logBatch.Count < batchSize)
                break;
        }

        await eventWriter.WriteLineAsync();
        await eventWriter.WriteLineAsync("]");

        await FinalizeSessionFilesAsync(sessionFiles);

        return totalLogs;
    }

    private static async Task WriteLogToSessionFileAsync(int eventId, Database.Models.EventStatusLog log, Dictionary<int, SessionFileWriters> sessionFiles)
    {
        if (!sessionFiles.TryGetValue(log.SessionId, out SessionFileWriters? writers))
        {
            var sessionTempFile = Path.Combine(Path.GetTempPath(), $"event-{eventId}-session-{log.SessionId}-logs-{Guid.NewGuid()}.json");
            var sessionFileStream = new FileStream(sessionTempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
            var sessionWriter = new StreamWriter(sessionFileStream);

            await sessionWriter.WriteLineAsync("[");
            writers = new SessionFileWriters(sessionTempFile, sessionFileStream, sessionWriter, true);
            sessionFiles[log.SessionId] = writers;
        }

        if (writers.IsFirstLog)
        {
            writers.IsFirstLog = false;
        }
        else
        {
            await writers.Writer.WriteLineAsync(",");
        }

        var logJson = System.Text.Json.JsonSerializer.Serialize(log);
        await writers.Writer.WriteAsync(logJson);
    }

    private static async Task FinalizeSessionFilesAsync(Dictionary<int, SessionFileWriters> sessionFiles)
    {
        foreach (var (_, writers) in sessionFiles)
        {
            await writers.Writer.WriteLineAsync();
            await writers.Writer.WriteLineAsync("]");
            await writers.Writer.DisposeAsync();
            await writers.Stream.DisposeAsync();
        }
    }

    private async Task<bool> CompressAndUploadSessionFilesAsync(int eventId, Dictionary<int, SessionFileWriters> sessionFiles, CancellationToken cancellationToken)
    {
        foreach (var (sessionId, writers) in sessionFiles)
        {
            Logger.LogInformation("Compressing and uploading logs for event {eventId}, session {sessionId}", eventId, sessionId);

            var gzipFile = writers.FilePath + ".gz";
            writers.GzipFilePath = gzipFile;

            await using (var originalStream = new FileStream(writers.FilePath, FileMode.Open, FileAccess.Read, FileShare.None, 8192, useAsync: true))
            await using (var compressedStream = new FileStream(gzipFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
            await using (var gzipStream = new System.IO.Compression.GZipStream(compressedStream, System.IO.Compression.CompressionLevel.Optimal))
            {
                await originalStream.CopyToAsync(gzipStream, cancellationToken);
            }

            await using (var uploadStream = new FileStream(gzipFile, FileMode.Open, FileAccess.Read, FileShare.None, 8192, useAsync: true))
            {
                var uploadSuccess = await archiveStorage.UploadSessionLogsAsync(uploadStream, eventId, sessionId);
                if (!uploadSuccess)
                {
                    Logger.LogError("Failed to upload session logs for event {eventId}, session {sessionId}", eventId, sessionId);
                    return false;
                }
            }

            Logger.LogInformation("Successfully uploaded session logs for event {eventId}, session {sessionId}", eventId, sessionId);
        }

        return true;
    }

    private async Task<bool> CompressAndUploadEventFileAsync(int eventId, string tempFilePath, CancellationToken cancellationToken)
    {
        string gzipFilePath = tempFilePath + ".gz";

        await using (var originalFileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.None, 8192, useAsync: true))
        await using (var compressedFileStream = new FileStream(gzipFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
        await using (var gzipStream = new System.IO.Compression.GZipStream(compressedFileStream, System.IO.Compression.CompressionLevel.Optimal))
        {
            await originalFileStream.CopyToAsync(gzipStream, cancellationToken);
        }

        await using var uploadStream = new FileStream(gzipFilePath, FileMode.Open, FileAccess.Read, FileShare.None, 8192, useAsync: true);
        var uploadSuccess = await archiveStorage.UploadEventLogsAsync(uploadStream, eventId);
        if (!uploadSuccess)
        {
            Logger.LogError("Failed to upload archived logs for event {eventId}", eventId);
            return false;
        }

        return true;
    }

    private async Task DeleteLogsFromDatabaseAsync(int eventId, long totalLogs, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Deleting {count} logs from database for event {eventId}", totalLogs, eventId);
        await using var dbContext = await tsContext.CreateDbContextAsync(cancellationToken);

        // ExecuteDeleteAsync is not supported by InMemory database, so we need to handle both approaches
        try
        {
            await dbContext.EventStatusLogs
                .Where(e => e.EventId == eventId)
                .ExecuteDeleteAsync(cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("ExecuteDelete"))
        {
            // Fallback for InMemory database
            var logsToDelete = await dbContext.EventStatusLogs
                .Where(e => e.EventId == eventId)
                .ToListAsync(cancellationToken);
            dbContext.EventStatusLogs.RemoveRange(logsToDelete);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task CleanupSessionFilesAsync(Dictionary<int, SessionFileWriters> sessionFiles)
    {
        foreach (var (sessionId, writers) in sessionFiles)
        {
            try
            {
                if (File.Exists(writers.FilePath))
                    File.Delete(writers.FilePath);

                if (writers.GzipFilePath != null && File.Exists(writers.GzipFilePath))
                    File.Delete(writers.GzipFilePath);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to delete session temp files for session {sessionId}", sessionId);
            }
        }
    }

    private void CleanupEventFiles(string? tempFilePath, int eventId)
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
            Logger.LogWarning(ex, "Failed to delete event temp files for event {eventId}", eventId);
        }
    }

    private class SessionFileWriters
    {
        public string FilePath { get; }
        public FileStream Stream { get; }
        public StreamWriter Writer { get; }
        public bool IsFirstLog { get; set; }
        public string? GzipFilePath { get; set; }

        public SessionFileWriters(string filePath, FileStream stream, StreamWriter writer, bool isFirstLog)
        {
            FilePath = filePath;
            Stream = stream;
            Writer = writer;
            IsFirstLog = isFirstLog;
        }
    }
}
