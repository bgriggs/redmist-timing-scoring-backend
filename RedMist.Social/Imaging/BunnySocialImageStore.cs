using Microsoft.Extensions.Logging;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database.Models;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

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
public sealed partial class BunnySocialImageStore : ISocialImageStore
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
        SocialChannel channel, int eventId, int sessionId, CapturedImage image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);

        var path = BuildPath(channel, eventId, sessionId, image.Png);

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

    public async Task<bool> DeleteAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;

        if (TryGetPath(url, settings.PublicBaseUrl) is not { } path)
        {
            // Refused rather than guessed at. A URL from somewhere else is either a bug or a post
            // edited by hand, and turning an unrecognized address into a storage path is how a
            // delete ends up removing the wrong object.
            logger.LogWarning("Not deleting '{Url}': it is not an address in this image store", url);
            return false;
        }

        using var cdn = new BunnyCdn(
            settings.StorageZoneName, settings.StorageAccessKey, settings.MainReplicationRegion,
            settings.ApiAccessKey, loggerFactory, httpClientFactory);

        var deleted = await cdn.DeleteAsync($"/{settings.StorageZoneName}{path}");
        if (deleted)
            logger.LogInformation("Deleted results image {Url}", url);

        return deleted;
    }

    /// <summary>
    /// Builds the storage path. Public and static so the URL format is pinned by tests rather than
    /// discovered from a live upload.
    /// </summary>
    public static string BuildPath(SocialChannel channel, int eventId, int sessionId, byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);

        var hash = Convert.ToHexStringLower(SHA256.HashData(png))[..HashLength];
        return $"/social/{channel.ToString().ToLowerInvariant()}/event-{eventId}/session-{sessionId}-{hash}.png";
    }

    /// <summary>
    /// Recovers the storage path from a URL this store produced, or null if the URL is not one of
    /// ours.
    /// </summary>
    /// <remarks>
    /// The inverse of the concatenation in <see cref="StoreAsync"/>, kept beside it and pinned by
    /// tests: a delete that mis-parses a path either removes nothing or removes something else, and
    /// neither failure announces itself.
    /// </remarks>
    public static string? TryGetPath(string? url, string publicBaseUrl)
    {
        var prefix = (publicBaseUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrEmpty(url) || prefix.Length == 0 || !url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var path = url[prefix.Length..];

        // Matched against the exact shape this store writes rather than a prefix check. A prefix is
        // not enough: "/social/../logos/org-1.img" starts with "/social/" and would let a delete walk
        // out of this feature's directory and into another's. The zone holds organization logos and
        // archives too, and nothing here has any business reaching them.
        return StoredPath().IsMatch(path) ? path : null;
    }

    /// <summary>
    /// The one path shape <see cref="BuildPath"/> produces; the hash length must match <c>HashLength</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately literal about its character classes. <c>\d</c> would also match Arabic-Indic and
    /// full-width digits, and <c>$</c> would accept a trailing newline -- neither can escape the
    /// prefix, but both would let a path through that this store could never have written, which is
    /// exactly the guarantee a delete leans on.
    /// </remarks>
    [GeneratedRegex(@"\A/social/[a-z]+/event-[0-9]+/session-[0-9]+-[0-9a-f]{16}\.png\z", RegexOptions.CultureInvariant)]
    private static partial Regex StoredPath();
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
