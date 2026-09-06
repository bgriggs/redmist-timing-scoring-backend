namespace RedMist.Social.Imaging;

/// <summary>
/// Puts a captured image somewhere a social platform can fetch it, and returns that public address.
/// </summary>
/// <remarks>
/// Publishing a photo means handing the platform a URL it can reach itself, so an image that only
/// exists in the database is of no use. Storing it also means a reviewer sees exactly the picture
/// that will be posted rather than a re-render of it.
///
/// KNOWN LIMIT: this is the one part of composition that reaches outside the database before a person
/// has approved anything. The job's rule is that it writes nothing publishable, and that holds for the
/// copy -- but an image is publicly fetchable from the moment it is stored, and there is no delete
/// path anywhere yet. Rejecting a post therefore leaves its picture reachable: unlinked, and behind a
/// content hash that is impractical to guess, but reachable. Closing this properly means either
/// storing images somewhere private and copying them out at approval time, or giving the review step
/// a delete. Worth doing before this points at anything more sensitive than public race results.
/// </remarks>
public interface ISocialImageStore
{
    /// <summary>Stores one image and returns the URL to reference it by.</summary>
    Task<string> StoreAsync(int eventId, int sessionId, CapturedImage image, CancellationToken cancellationToken);
}
