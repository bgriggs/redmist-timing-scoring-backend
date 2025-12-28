using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.TimingCommon.Models;
using System.Text.Json;

namespace RedMist.EventOrchestration.Utilities;

public class LapsLogArchive
{
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly IArchiveStorage archiveStorage;
    private ILogger Logger { get; }


    public LapsLogArchive(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IArchiveStorage archiveStorage)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.archiveStorage = archiveStorage;
    }


    public async Task<bool> ArchiveLapsAsync(int eventId, int sessionId)
    {
        return await ArchiveLapsAsync(eventId, sessionId, CancellationToken.None);
    }

    public async Task<bool> ArchiveLapsAsync(int eventId, int sessionId, CancellationToken cancellationToken)
    {
        string? tempSessionFilePath = null;
        var carFiles = new Dictionary<string, CarFileWriters>();
        try
        {
            tempSessionFilePath = Path.Combine(Path.GetTempPath(), $"event-{eventId}-session-{sessionId}-laps-{Guid.NewGuid()}.json");

            var totalLaps = await WriteLapsToFilesAsync(eventId, sessionId, tempSessionFilePath, carFiles, cancellationToken);

            if (totalLaps == 0)
            {
                Logger.LogInformation("No laps found to archive for event {eventId}, session {sessionId}", eventId, sessionId);
                return true;
            }

            var uploadSuccess = await CompressAndUploadCarFilesAsync(eventId, sessionId, carFiles, cancellationToken);
            if (!uploadSuccess)
                return false;

            uploadSuccess = await CompressAndUploadSessionFileAsync(eventId, sessionId, tempSessionFilePath, cancellationToken);
            if (!uploadSuccess)
                return false;

            await DeleteLapsFromDatabaseAsync(eventId, sessionId, totalLaps, cancellationToken);

            Logger.LogInformation("Successfully archived and deleted {count} laps for event {eventId}, session {sessionId}", totalLaps, eventId, sessionId);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error archiving laps for event {eventId}, session {sessionId}", eventId, sessionId);
            return false;
        }
        finally
        {
            await CleanupCarFilesAsync(carFiles);
            CleanupSessionFiles(tempSessionFilePath, eventId, sessionId);
        }
    }

    private async Task<long> WriteLapsToFilesAsync(int eventId, int sessionId, string tempFilePath, Dictionary<string, CarFileWriters> carFiles, CancellationToken cancellationToken)
    {
        long totalLaps = 0;
        const int batchSize = 100;
        bool isFirstBatch = true;

        var sessionFileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        var sessionWriter = new StreamWriter(sessionFileStream);

        try
        {
            await sessionWriter.WriteLineAsync("[");

            while (true)
            {
                await using var dbContext = await tsContext.CreateDbContextAsync(cancellationToken);

                var lapBatch = await dbContext.CarLapLogs
                    .Where(l => l.EventId == eventId && l.SessionId == sessionId)
                    .OrderBy(l => l.Id)
                    .Skip((int)totalLaps)
                    .Take(batchSize)
                    .ToListAsync(cancellationToken);

                if (lapBatch.Count == 0)
                    break;

                foreach (var lap in lapBatch)
                {
                    if (!isFirstBatch)
                        await sessionWriter.WriteLineAsync(",");

                    // Deserialize LapData as CarPosition and write it to the file
                    try
                    {
                        var carPosition = JsonSerializer.Deserialize<CarPosition>(lap.LapData);
                        if (carPosition != null)
                        {
                            var carPositionJson = JsonSerializer.Serialize(carPosition);
                            await sessionWriter.WriteAsync(carPositionJson);
                            isFirstBatch = false;

                            // Write to car-specific file
                            await WriteLapToCarFileAsync(eventId, sessionId, carPosition, carFiles);
                        }
                        else
                        {
                            Logger.LogWarning("Failed to deserialize LapData for lap {lapId} in event {eventId}, session {sessionId}", lap.Id, eventId, sessionId);
                        }
                    }
                    catch (JsonException ex)
                    {
                        Logger.LogError(ex, "Error deserializing LapData for lap {lapId} in event {eventId}, session {sessionId}", lap.Id, eventId, sessionId);
                    }
                }

                totalLaps += lapBatch.Count;
                Logger.LogDebug("Archived {count} laps for event {eventId}, session {sessionId} (total: {totalLaps})", lapBatch.Count, eventId, sessionId, totalLaps);

                if (lapBatch.Count < batchSize)
                    break;
            }

            await sessionWriter.WriteLineAsync();
            await sessionWriter.WriteLineAsync("]");
            await sessionWriter.FlushAsync(cancellationToken);
            await sessionFileStream.FlushAsync(cancellationToken);

            await FinalizeCarFilesAsync(carFiles);
        }
        finally
        {
            await sessionWriter.DisposeAsync();
            await sessionFileStream.DisposeAsync();
        }

        return totalLaps;
    }

    private static async Task WriteLapToCarFileAsync(int eventId, int sessionId, CarPosition carPosition, Dictionary<string, CarFileWriters> carFiles)
    {
        var carNumber = carPosition.Number;

        if (!carFiles.TryGetValue(carNumber, out CarFileWriters? writers))
        {
            var carTempFile = Path.Combine(Path.GetTempPath(), $"event-{eventId}-session-{sessionId}-car-{carNumber}-laps-{Guid.NewGuid()}.json");
            var carFileStream = new FileStream(carTempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
            var carWriter = new StreamWriter(carFileStream);

            await carWriter.WriteLineAsync("[");
            writers = new CarFileWriters(carTempFile, carFileStream, carWriter, true);
            carFiles[carNumber] = writers;
        }

        if (writers.IsFirstLap)
        {
            writers.IsFirstLap = false;
        }
        else
        {
            await writers.Writer.WriteLineAsync(",");
        }

        var carPositionJson = JsonSerializer.Serialize(carPosition);
        await writers.Writer.WriteAsync(carPositionJson);
    }

    private static async Task FinalizeCarFilesAsync(Dictionary<string, CarFileWriters> carFiles)
    {
        foreach (var (_, writers) in carFiles)
        {
            await writers.Writer.WriteLineAsync();
            await writers.Writer.WriteLineAsync("]");
            await writers.Writer.FlushAsync();
            await writers.Stream.FlushAsync();
            await writers.Writer.DisposeAsync();
            await writers.Stream.DisposeAsync();
        }
    }

    private async Task<bool> CompressAndUploadCarFilesAsync(int eventId, int sessionId, Dictionary<string, CarFileWriters> carFiles, CancellationToken cancellationToken)
    {
        foreach (var (carNumber, writers) in carFiles)
        {
            Logger.LogInformation("Compressing and uploading laps for event {eventId}, session {sessionId}, car {carNumber}", eventId, sessionId, carNumber);

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
                var uploadSuccess = await archiveStorage.UploadSessionCarLapsAsync(uploadStream, eventId, sessionId, carNumber);
                if (!uploadSuccess)
                {
                    Logger.LogError("Failed to upload car laps for event {eventId}, session {sessionId}, car {carNumber}", eventId, sessionId, carNumber);
                    return false;
                }
            }

            Logger.LogInformation("Successfully uploaded car laps for event {eventId}, session {sessionId}, car {carNumber}", eventId, sessionId, carNumber);
        }

        return true;
    }

    private async Task<bool> CompressAndUploadSessionFileAsync(int eventId, int sessionId, string tempFilePath, CancellationToken cancellationToken)
    {
        string gzipFilePath = tempFilePath + ".gz";

        await using (var originalFileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.None, 8192, useAsync: true))
        await using (var compressedFileStream = new FileStream(gzipFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
        await using (var gzipStream = new System.IO.Compression.GZipStream(compressedFileStream, System.IO.Compression.CompressionLevel.Optimal))
        {
            await originalFileStream.CopyToAsync(gzipStream, cancellationToken);
        }

        await using var uploadStream = new FileStream(gzipFilePath, FileMode.Open, FileAccess.Read, FileShare.None, 8192, useAsync: true);
        var uploadSuccess = await archiveStorage.UploadSessionLapsAsync(uploadStream, eventId, sessionId);
        if (!uploadSuccess)
        {
            Logger.LogError("Failed to upload archived laps for event {eventId}, session {sessionId}", eventId, sessionId);
            return false;
        }

        return true;
    }

    private async Task DeleteLapsFromDatabaseAsync(int eventId, int sessionId, long totalLaps, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Deleting {count} laps from database for event {eventId}, session {sessionId}", totalLaps, eventId, sessionId);
        await using var dbContext = await tsContext.CreateDbContextAsync(cancellationToken);

        // ExecuteDeleteAsync is not supported by InMemory database, so we need to handle both approaches
        try
        {
            await dbContext.CarLapLogs
                .Where(l => l.EventId == eventId && l.SessionId == sessionId)
                .ExecuteDeleteAsync(cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("ExecuteDelete"))
        {
            // Fallback for InMemory database
            var lapsToDelete = await dbContext.CarLapLogs
                .Where(l => l.EventId == eventId && l.SessionId == sessionId)
                .ToListAsync(cancellationToken);
            dbContext.CarLapLogs.RemoveRange(lapsToDelete);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task CleanupCarFilesAsync(Dictionary<string, CarFileWriters> carFiles)
    {
        foreach (var (carNumber, writers) in carFiles)
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
                Logger.LogWarning(ex, "Failed to delete car temp files for car {carNumber}", carNumber);
            }
        }
    }

    private void CleanupSessionFiles(string? tempFilePath, int eventId, int sessionId)
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
            Logger.LogWarning(ex, "Failed to delete session temp files for event {eventId}, session {sessionId}", eventId, sessionId);
        }
    }

    private class CarFileWriters
    {
        public string FilePath { get; }
        public FileStream Stream { get; }
        public StreamWriter Writer { get; }
        public bool IsFirstLap { get; set; }
        public string? GzipFilePath { get; set; }

        public CarFileWriters(string filePath, FileStream stream, StreamWriter writer, bool isFirstLap)
        {
            FilePath = filePath;
            Stream = stream;
            Writer = writer;
            IsFirstLap = isFirstLap;
        }
    }
}

