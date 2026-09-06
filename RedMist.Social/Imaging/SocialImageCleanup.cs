using Microsoft.Extensions.Logging;

namespace RedMist.Social.Imaging;

/// <summary>
/// Removes stored images that a post no longer has any claim to.
/// </summary>
/// <remarks>
/// One place for it because more than one thing has to do it and they must agree. A picture becomes
/// unwanted for three reasons -- the post was rejected, the post was deleted, or a redraft replaced it
/// -- and only the first is obvious. Leaving any of them behind means a publicly fetchable image of
/// results that were never approved, or were superseded, sitting in the zone with nothing referencing
/// it and nothing that will ever notice.
///
/// Deletion is deliberately best-effort. An image that cannot be removed is worth a loud log and no
/// more: failing the caller would mean a post that cannot be rejected because its picture would not
/// delete, which is a worse outcome than an orphaned file.
/// </remarks>
public sealed class SocialImageCleanup(ISocialImageStore store, ILoggerFactory loggerFactory)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<SocialImageCleanup>();

    /// <summary>
    /// Deletes the given images, carrying on past any that fail.
    /// </summary>
    /// <returns>How many were removed.</returns>
    public async Task<int> ReleaseAsync(IEnumerable<string> urls, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(urls);

        var released = 0;
        foreach (var url in urls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await store.DeleteAsync(url, cancellationToken))
                    released++;
                else
                    logger.LogWarning("Results image {Url} could not be deleted and is now orphaned", url);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Failed to delete results image {Url}; it is now orphaned", url);
            }
        }

        return released;
    }

    /// <summary>
    /// The images in <paramref name="previous"/> that <paramref name="current"/> no longer references.
    /// </summary>
    /// <remarks>
    /// A redraft of unchanged results produces byte-identical pictures, and because the filename
    /// carries a content hash those keep the same URLs. Comparing rather than deleting everything the
    /// post used to hold is what stops a redraft removing the image it is about to reference again.
    /// </remarks>
    public static IReadOnlyList<string> Superseded(IEnumerable<string> previous, IEnumerable<string> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        return [.. previous.Except(current, StringComparer.OrdinalIgnoreCase).Where(u => !string.IsNullOrWhiteSpace(u))];
    }
}
