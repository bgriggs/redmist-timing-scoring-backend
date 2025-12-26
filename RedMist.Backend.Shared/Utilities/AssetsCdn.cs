using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace RedMist.Backend.Shared.Utilities;

public class AssetsCdn
{
    private readonly string storageZoneName;
    private readonly string storageAccessKey;
    private readonly string mainReplicationRegion;
    private readonly string apiAccessKey;
    private readonly string cdnId;
    private readonly ILoggerFactory loggerFactory;
    private ILogger Logger { get; }


    public AssetsCdn(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        storageZoneName = configuration["Assets:StorageZoneName"] ?? throw new ArgumentNullException(nameof(configuration));
        storageAccessKey = configuration["Assets:StorageAccessKey"] ?? throw new ArgumentNullException(nameof(configuration));
        mainReplicationRegion = configuration["Assets:MainReplicationRegion"] ?? throw new ArgumentNullException(nameof(configuration));
        apiAccessKey = configuration["Assets:ApiAccessKey"] ?? throw new ArgumentNullException(nameof(configuration));
        cdnId = configuration["Assets:CdnId"] ?? throw new ArgumentNullException(nameof(configuration));
        this.loggerFactory = loggerFactory;
    }


    public async Task<bool> SaveLogoAsync(int organizationId, byte[] data)
    {
        using var cdnClient = new BunnyCdn(storageZoneName, storageAccessKey, mainReplicationRegion, apiAccessKey, loggerFactory);
        using var stream = new MemoryStream(data);
        Logger.LogInformation("Uploading logo for organization {OrganizationId} to CDN...", organizationId);
        var result = await cdnClient.UploadAsync(stream, $"/{storageZoneName}/logos/org-{organizationId}.img");
        if (!result)
        {
            Logger.LogError("Failed to upload logo for organization {OrganizationId} to CDN", organizationId);
            return result;
        }
        Logger.LogInformation("Purging CDN...");
        result = await cdnClient.PurgeCacheAsync(cdnId);
        Logger.LogInformation("CDN purge completed with result: {Result}", result);
        return result;
    }
}
