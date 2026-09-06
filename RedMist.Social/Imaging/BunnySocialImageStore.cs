using Microsoft.Extensions.Logging;
using RedMist.Backend.Shared.Utilities;
using System.Security.Cryptography;

namespace RedMist.Social.Imaging;

/// <summary>
/// Stores post images on the existing Bunny assets zone.
/// </summary>
/// <remarks>
/// The filename carries a hash of the image content, which buys two things and costs nothing.
/// Re-capturing after results change writes to a new path, so a platform that already fetched the
/// old URL cannot serve a stale picture and no CDN purge is needed; and re-capturing something
/// unchanged writes the same path, so a redraft does not litter the zone with duplicates.
/// </remarks>
public sealed class BunnySocialImageStore : ISocialImageStore
{
    /// <summary>
    /// Characters of the content hash kept in the filename. Short enough to stay readable in a URL,
    /// long enough that a collision between two images of the same session is not a real concern.
    /// </summary>
    private const int HashLength = 16;

    private readonly BunnyCdnSettings settings;
    private readonly ILoggerFactory loggerFactory;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger logger;

    public BunnySocialImageStore(
        BunnyCdnSettings settings, ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(settings);

        this.settings = settings;
        this.loggerFactory = loggerFactory;
        this.httpClientFactory = httpClientFactory;
        logger = loggerFactory.CreateLogger<BunnySocialImageStore>();
    }

    public async Task<string> StoreAsync(
        int eventId, int sessionId, CapturedImage image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);

        var path = BuildPath(eventId, sessionId, image.Png);

        using var cdn = new BunnyCdn(
            settings.StorageZoneName, settings.StorageAccessKey, settings.MainReplicationRegion,
            settings.ApiAccessKey, loggerFactory, httpClientFactory);

        using var stream = new MemoryStream(image.Png);
        var uploaded = await cdn.UploadAsync(stream, $"/{settings.StorageZoneName}{path}");

        if (!uploaded)
        {
            throw new SessionImageCaptureException(
                $"Failed to upload the results image for event {eventId} session {sessionId} to the CDN.");
        }

        var url = settings.PublicBaseUrl.TrimEnd('/') + path;
        logger.LogInformation(
            "Event {EventId} session {SessionId}: stored a {Width}x{Height} results image at {Url}",
            eventId, sessionId, image.Width, image.Height, url);

        return url;
    }

    /// <summary>
    /// Builds the storage path. Public and static so the URL format is pinned by tests rather than
    /// discovered from a live upload.
    /// </summary>
    public static string BuildPath(int eventId, int sessionId, byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);

        var hash = Convert.ToHexStringLower(SHA256.HashData(png))[..HashLength];
        return $"/social/event-{eventId}/session-{sessionId}-{hash}.png";
    }
}

/// <summary>
/// Credentials and addresses for the Bunny assets zone.
/// </summary>
/// <param name="StorageZoneName">Storage zone the image is written into.</param>
/// <param name="StorageAccessKey">Write key for that zone.</param>
/// <param name="MainReplicationRegion">Primary replication region.</param>
/// <param name="ApiAccessKey">Account API key.</param>
/// <param name="PublicBaseUrl">
/// The address the zone is readable at, which is not derivable from the zone name in every setup and
/// is what a social platform will actually fetch.
/// </param>
public sealed record BunnyCdnSettings(
    string StorageZoneName,
    string StorageAccessKey,
    string MainReplicationRegion,
    string ApiAccessKey,
    string PublicBaseUrl);
