namespace RedMist.SocialCompose;

/// <summary>
/// What one event's image capture produced, and how much of it can be trusted as complete.
/// </summary>
/// <remarks>
/// The two flags exist because "no pictures" has three meanings that must not be conflated, and the
/// difference decides whether existing images are deleted:
///
/// <list type="bullet">
/// <item>not attempted -- images are switched off, so the post's existing pictures stand;</item>
/// <item>attempted and complete -- the new set is authoritative, so anything it replaced can go;</item>
/// <item>attempted and incomplete -- a session failed to render, so its absence from the new set is
/// a gap rather than a decision, and deleting on that basis would destroy a good picture.</item>
/// </list>
/// </remarks>
/// <param name="WasAttempted">False when image capture is switched off or unconfigured.</param>
/// <param name="IsComplete">True when every race in scope produced a picture.</param>
/// <param name="Urls">Public addresses of the pictures taken, one per race, in race order.</param>
/// <param name="Warnings">What a reviewer should know about the pictures, or their absence.</param>
public sealed record CapturedImageSet(
    bool WasAttempted,
    bool IsComplete,
    List<string> Urls,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Images are switched off; the post keeps whatever pictures it already had.</summary>
    public static CapturedImageSet NotAttempted { get; } = new(false, false, [], []);
}
