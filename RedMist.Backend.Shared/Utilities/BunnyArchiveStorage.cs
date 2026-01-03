using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace RedMist.Backend.Shared.Utilities;

public class BunnyArchiveStorage : IArchiveStorage
{
    private readonly string storageZoneName;
    private readonly string storageAccessKey;
    private readonly string mainReplicationRegion;
    private readonly string apiAccessKey;
    private readonly string cdnId;
    private readonly ILoggerFactory loggerFactory;
    private ILogger Logger { get; }


    public BunnyArchiveStorage(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        storageZoneName = configuration["Archive:StorageZoneName"] ?? throw new ArgumentNullException(nameof(configuration));
        storageAccessKey = configuration["Archive:StorageAccessKey"] ?? throw new ArgumentNullException(nameof(configuration));
        mainReplicationRegion = configuration["Archive:MainReplicationRegion"] ?? throw new ArgumentNullException(nameof(configuration));
        apiAccessKey = configuration["Archive:ApiAccessKey"] ?? throw new ArgumentNullException(nameof(configuration));
        cdnId = configuration["Archive:CdnId"] ?? throw new ArgumentNullException(nameof(configuration));
        this.loggerFactory = loggerFactory;
    }


    public async Task<bool> UploadEventLogsAsync(Stream stream, int eventId)
    {

        using var cdnClient = new BunnyCdn(storageZoneName, storageAccessKey, mainReplicationRegion, apiAccessKey, loggerFactory);
        Logger.LogInformation("Uploading event logs for event {EventId} to CDN...", eventId);
        var result = await cdnClient.UploadAsync(stream, $"/{storageZoneName}/event-logs/event-{eventId}.gz");
        if (!result)
        {
            Logger.LogError("Failed to upload event logs to CDN for event {EventId}", eventId);
        }
        return result;
    }

    public async Task<bool> UploadSessionLogsAsync(Stream stream, int eventId, int sessionId)
    {
        using var cdnClient = new BunnyCdn(storageZoneName, storageAccessKey, mainReplicationRegion, apiAccessKey, loggerFactory);
        Logger.LogInformation("Uploading session logs for event {EventId}, session {SessionId} to CDN...", eventId, sessionId);
        var result = await cdnClient.UploadAsync(stream, $"/{storageZoneName}/event-logs/sessions-{eventId}/session-{sessionId}.gz");
        if (!result)
        {
            Logger.LogError("Failed to upload session logs to CDN for event {EventId}, session {SessionId}", eventId, sessionId);
        }
        return result;
    }

    public async Task<bool> UploadSessionLapsAsync(Stream stream, int eventId, int sessionId)
    {
        using var cdnClient = new BunnyCdn(storageZoneName, storageAccessKey, mainReplicationRegion, apiAccessKey, loggerFactory);
        Logger.LogInformation("Uploading session laps for event {EventId}, session {SessionId} to CDN...", eventId, sessionId);
        var result = await cdnClient.UploadAsync(stream, $"/{storageZoneName}/event-laps/event-{eventId}-session-{sessionId}-laps.gz");
        if (!result)
        {
            Logger.LogError("Failed to upload session laps to CDN for event {EventId}, session {SessionId}", eventId, sessionId);
        }
        return result;
    }

    public async Task<bool> UploadSessionCarLapsAsync(Stream stream, int eventId, int sessionId, string carNum)
    {
        using var cdnClient = new BunnyCdn(storageZoneName, storageAccessKey, mainReplicationRegion, apiAccessKey, loggerFactory);
        Logger.LogInformation("Uploading session car laps for event {EventId}, session {SessionId}, car {CarNum} to CDN...", eventId, sessionId, carNum);
        var result = await cdnClient.UploadAsync(stream, $"/{storageZoneName}/event-laps/event-{eventId}-session-{sessionId}-car-laps/car-{carNum}-laps.gz");
        if (!result)
        {
            Logger.LogError("Failed to upload session car laps to CDN for event {EventId}, session {SessionId}, car {CarNum}", eventId, sessionId, carNum);
        }
        return result;
    }

    public async Task<bool> UploadEventX2PassingsAsync(Stream stream, int eventId)
    {
        using var cdnClient = new BunnyCdn(storageZoneName, storageAccessKey, mainReplicationRegion, apiAccessKey, loggerFactory);
        Logger.LogInformation("Uploading event X2 passings for event {EventId} to CDN...", eventId);
        var result = await cdnClient.UploadAsync(stream, $"/{storageZoneName}/event-passings/event-{eventId}-passings.gz");
        if (!result)
        {
            Logger.LogError("Failed to upload event X2 passings to CDN for event {EventId}", eventId);
        }
        return result;
    }

    public async Task<bool> UploadEventX2LoopsAsync(Stream stream, int eventId)
    {
        using var cdnClient = new BunnyCdn(storageZoneName, storageAccessKey, mainReplicationRegion, apiAccessKey, loggerFactory);
        Logger.LogInformation("Uploading event X2 loops for event {EventId} to CDN...", eventId);
        var result = await cdnClient.UploadAsync(stream, $"/{storageZoneName}/event-loops/event-{eventId}-loops.gz");
        if (!result)
        {
            Logger.LogError("Failed to upload event X2 loops to CDN for event {EventId}", eventId);
        }
        return result;
    }

    public async Task<bool> UploadSessionFlagsAsync(Stream stream, int eventId, int sessionId)
    {
        using var cdnClient = new BunnyCdn(storageZoneName, storageAccessKey, mainReplicationRegion, apiAccessKey, loggerFactory);
        Logger.LogInformation("Uploading session flags for event {EventId}, session {SessionId} to CDN...", eventId, sessionId);
        var result = await cdnClient.UploadAsync(stream, $"/{storageZoneName}/event-flags/event-{eventId}-session-{sessionId}-flags.gz");
        if (!result)
        {
            Logger.LogError("Failed to upload session flags to CDN for event {EventId}, session {SessionId}", eventId, sessionId);
        }
        return result;
    }

    public async Task<bool> UploadEventCompetitorMetadataAsync(Stream stream, int eventId)
    {
        using var cdnClient = new BunnyCdn(storageZoneName, storageAccessKey, mainReplicationRegion, apiAccessKey, loggerFactory);
        Logger.LogInformation("Uploading event competitor metadata for event {EventId} to CDN...", eventId);
        var result = await cdnClient.UploadAsync(stream, $"/{storageZoneName}/event-competitor-metadata/event-{eventId}-competitor-metadata.gz");
        if (!result)
        {
            Logger.LogError("Failed to upload event competitor metadata to CDN for event {EventId}", eventId);
        }
        return result;
    }
}
