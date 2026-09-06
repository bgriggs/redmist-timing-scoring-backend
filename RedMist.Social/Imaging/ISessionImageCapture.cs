namespace RedMist.Social.Imaging;

/// <summary>
/// Captures a picture of a finished session's results as they appear on the timing page.
/// </summary>
/// <remarks>
/// A seam rather than a concrete class because the only implementation drives a real browser. Every
/// decision worth testing -- which sessions are captured, what happens when one fails, what a
/// reviewer is told -- belongs on this side of it.
/// </remarks>
public interface ISessionImageCapture
{
    /// <summary>
    /// Renders one session and returns the image.
    /// </summary>
    /// <exception cref="SessionImageCaptureException">
    /// The page could not be rendered. Callers are expected to carry on without the image rather
    /// than lose the post over it.
    /// </exception>
    Task<CapturedImage> CaptureAsync(SessionImageRequest request, CancellationToken cancellationToken);
}

/// <summary>Which session to photograph.</summary>
/// <param name="EventId">Event the session belongs to.</param>
/// <param name="SessionId">Session to render.</param>
/// <param name="SessionName">Only for logging and error messages; the page is addressed by id.</param>
public sealed record SessionImageRequest(int EventId, int SessionId, string SessionName);

/// <summary>A rendered image.</summary>
/// <param name="Png">PNG bytes.</param>
/// <param name="Width">Pixel width, which is the CSS width times the device scale factor.</param>
/// <param name="Height">Pixel height.</param>
public sealed record CapturedImage(byte[] Png, int Width, int Height);

/// <summary>Raised when a session page could not be rendered.</summary>
public class SessionImageCaptureException : Exception
{
    public SessionImageCaptureException(string message) : base(message) { }

    public SessionImageCaptureException(string message, Exception innerException) : base(message, innerException) { }

    public SessionImageCaptureException() { }
}
