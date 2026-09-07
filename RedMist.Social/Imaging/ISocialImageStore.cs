using RedMist.Database.Models;

namespace RedMist.Social.Imaging;

/// <summary>
/// Puts a captured image somewhere a social platform can fetch it, and returns that public address.
/// </summary>
/// <remarks>
/// Publishing a photo means handing the platform a URL it can reach itself, so an image that only
/// exists in the database is of no use. Storing it also means a reviewer sees exactly the picture
/// that will be posted rather than a re-render of it.
///
/// An image is publicly fetchable from the moment it is stored, which is before anybody has reviewed
/// the post it belongs to. That is why <see cref="DeleteAsync"/> exists and why it has to be wired
/// into every path that discards a post or replaces its pictures: a picture whose post was rejected,
/// or that a redraft superseded, has no reason to remain reachable.
/// </remarks>
public interface ISocialImageStore
{
    /// <summary>Stores one image and returns the URL to reference it by.</summary>
    /// <remarks>
    /// The channel is part of the address, not decoration. Pictures are content-addressed, so two
    /// channels photographing the same session produce byte-identical files -- and without the
    /// channel in the path those would land on one object referenced by two posts, where rejecting
    /// one post deletes the picture the other is still showing.
    /// </remarks>
    Task<string> StoreAsync(
        SocialChannel channel, int eventId, int sessionId, CapturedImage image, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a stored image by the URL <see cref="StoreAsync"/> returned.
    /// </summary>
    /// <returns>
    /// True when the image is gone, including when it was already absent. Deleting something twice is
    /// the normal case here -- a retry, a re-reviewed post -- and is not a failure.
    /// </returns>
    /// <remarks>
    /// Takes the URL rather than the ids because that is what a post actually stores. Reconstructing
    /// the path from ids would not work anyway: the filename carries a content hash, so only the URL
    /// identifies which of an event's pictures this is.
    /// </remarks>
    Task<bool> DeleteAsync(string url, CancellationToken cancellationToken);
}
