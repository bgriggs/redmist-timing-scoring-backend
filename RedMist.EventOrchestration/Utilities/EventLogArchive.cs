using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;

namespace RedMist.EventOrchestration.Utilities;

public class EventLogArchive : BaseArchive
{
    public EventLogArchive(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IArchiveStorage archiveStorage, PurgeUtilities purgeUtilities)
        : base(loggerFactory, tsContext, archiveStorage, purgeUtilities)
    {
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

            await PurgeUtilities.DeleteEventStatusLogsFromDatabaseAsync(eventId, totalLogs, cancellationToken);

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

        var eventFileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        var eventWriter = new StreamWriter(eventFileStream);

        try
        {
            await eventWriter.WriteLineAsync("[");

            while (true)
            {
                await using var dbContext = await TsContext.CreateDbContextAsync(cancellationToken);

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
            await eventWriter.FlushAsync(cancellationToken);
            await eventFileStream.FlushAsync(cancellationToken);

            await FinalizeSessionFilesAsync(sessionFiles);
        }
        finally
        {
            await eventWriter.DisposeAsync();
            await eventFileStream.DisposeAsync();
        }

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
            await writers.Writer.FlushAsync();
            await writers.Stream.FlushAsync();
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

            var uploadSuccess = await CompressAndUploadFileAsync(
                writers.FilePath,
                stream => ArchiveStorage.UploadSessionLogsAsync(stream, eventId, sessionId),
                $"session logs for event {eventId}, session {sessionId}",
                cancellationToken);

            if (!uploadSuccess)
                return false;
        }

        return true;
    }

    private async Task<bool> CompressAndUploadEventFileAsync(int eventId, string tempFilePath, CancellationToken cancellationToken)
    {
        return await CompressAndUploadFileAsync(
            tempFilePath,
            stream => ArchiveStorage.UploadEventLogsAsync(stream, eventId),
            $"archived logs for event {eventId}",
            cancellationToken);
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
        CleanupFile(tempFilePath, $"event {eventId}");
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
