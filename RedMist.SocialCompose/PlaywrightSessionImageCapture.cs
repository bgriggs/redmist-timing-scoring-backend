using Microsoft.Playwright;
using System.Buffers.Binary;
using RedMist.Social.Imaging;

namespace RedMist.SocialCompose;

/// <summary>
/// Photographs a finished session's results by loading the timing page in a headless browser.
/// </summary>
/// <remarks>
/// Kept in the job rather than in RedMist.Social so the browser dependency stops here. The admin API
/// and anything else that reads a digest should not inherit Chromium.
///
/// The browser is launched once and reused across the run's sessions. Launching per capture would
/// add a couple of seconds and about 100MB of churn each time for no benefit, since captures are
/// sequential and there is nothing to isolate between them -- every page is the same first-party
/// site. Each capture still gets its own context, so cookies and storage from one session's page
/// cannot affect the next.
/// </remarks>
public sealed class PlaywrightSessionImageCapture(
    SessionImageCaptureOptions options, ILoggerFactory loggerFactory) : ISessionImageCapture, IAsyncDisposable
{
    private readonly ILogger logger = loggerFactory.CreateLogger<PlaywrightSessionImageCapture>();
    private readonly SemaphoreSlim launchLock = new(1, 1);

    private IPlaywright? playwright;
    private IBrowser? browser;

    public async Task<CapturedImage> CaptureAsync(SessionImageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = BuildUrl(options.BaseUrl, request.EventId, request.SessionId, options.QueryString);

        try
        {
            // Inside the try, so a failure to start the browser reaches a caller as the documented
            // capture exception rather than as whatever Playwright happened to throw.
            var instance = await GetBrowserAsync(cancellationToken);
            return await CaptureWithAsync(instance, request, url, cancellationToken);
        }
        catch (SessionImageCaptureException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            // Playwright maps every driver deadline to System.TimeoutException, so this covers
            // navigation, the selector wait and the screenshot alike. The message stays general
            // rather than asserting which one it was, because guessing wrong writes a confidently
            // incorrect diagnosis into a warning a person will read.
            throw new SessionImageCaptureException(
                $"Timed out rendering {url} within {options.ReadyTimeout.TotalSeconds:0}s. The session may have no " +
                $"stored results, or the page may no longer match '{options.ReadySelector}'.", ex);
        }
        catch (Exception ex)
        {
            throw new SessionImageCaptureException($"Failed to capture {url}: {ex.Message}", ex);
        }
    }

    private async Task<CapturedImage> CaptureWithAsync(
        IBrowser instance, SessionImageRequest request, string url, CancellationToken cancellationToken)
    {
        await using var context = await instance.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = options.ViewportWidth, Height = options.ViewportHeight },

            // Renders at native resolution and downsamples in the feed, which is the difference
            // between legible timing rows and mush on a phone.
            DeviceScaleFactor = options.DeviceScaleFactor,
        });

        await BlockRequestsAsync(context);

        var page = await context.NewPageAsync();
        page.SetDefaultTimeout((float)options.ReadyTimeout.TotalMilliseconds);

        try
        {
            var response = await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = (float)options.ReadyTimeout.TotalMilliseconds,
            });

            // A 404 or a 500 still renders a page, and screenshotting an error page would produce a
            // picture that looks deliberate. Checked before waiting, so the failure is the real one.
            if (response is not null && !response.Ok)
            {
                throw new SessionImageCaptureException(
                    $"{url} returned HTTP {response.Status} for session '{request.SessionName}'.");
            }

            // The rows only render once results have loaded, so waiting for one is what separates a
            // results picture from a screenshot of a spinner -- the failure that would otherwise
            // reach a public page.
            await page.WaitForSelectorAsync(options.ReadySelector, new PageWaitForSelectorOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = (float)options.ReadyTimeout.TotalMilliseconds,
            });

            // Buys the asynchronous pieces that are not covered by the row appearing: the
            // organization logo image and web fonts. Without it the picture can show fallback type or
            // an empty logo box.
            if (options.SettleDelay > TimeSpan.Zero)
                await Task.Delay(options.SettleDelay, cancellationToken);

            await HideChromeAsync(page);

            var png = await CaptureElementOrPageAsync(page, request, url);

            logger.LogInformation(
                "Event {EventId} session {SessionId} ('{SessionName}'): captured {Bytes} bytes from {Url}",
                request.EventId, request.SessionId, request.SessionName, png.Length, url);

            // Measured from the PNG, not from the viewport. With a clip selector the two differ --
            // the picture is the element's size, which is the whole point of clipping -- and a
            // publisher that declares dimensions to a platform would be declaring the wrong ones.
            var (width, height) = ReadPngSize(png);
            return new CapturedImage(png, width, height);
        }
        finally
        {
            // Closing the context closes its pages, so this is only tidiness -- and it throws if the
            // browser has already died, which would replace the real failure with a misleading one.
            try
            {
                await page.CloseAsync();
            }
            catch (PlaywrightException)
            {
                // The context disposal below handles it.
            }
        }
    }

    /// <summary>
    /// Reads the pixel dimensions out of a PNG header.
    /// </summary>
    /// <remarks>
    /// Every PNG starts with an 8-byte signature and then an IHDR chunk whose first two fields are
    /// the width and height as big-endian 32-bit integers, so the size is always the same 8 bytes in.
    /// Returns zeroes rather than throwing for anything shorter: the dimensions are for logging and a
    /// reviewer's context, and losing a picture over them would be a poor trade.
    /// </remarks>
    public static (int Width, int Height) ReadPngSize(byte[] png)
    {
        const int widthOffset = 16;
        if (png is null || png.Length < widthOffset + 8)
            return (0, 0);

        return (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(widthOffset, 4)),
                BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(widthOffset + 4, 4)));
    }

    /// <summary>
    /// Builds the timing page address, e.g. https://redmist.racing/timing/384/26?embed=1.
    /// </summary>
    public static string BuildUrl(string baseUrl, int eventId, int sessionId, string? queryString = null)
    {
        var url = $"{baseUrl.TrimEnd('/')}/timing/{eventId}/{sessionId}";
        var query = queryString?.TrimStart('?').Trim();
        return string.IsNullOrEmpty(query) ? url : $"{url}?{query}";
    }

    /// <summary>
    /// Refuses the requests a screenshot has no business making.
    /// </summary>
    /// <remarks>
    /// The timing page reports sponsor impressions as it renders, and those writes are real: they land
    /// in SponsorTelemetryLogs and are what the sponsor rollup and the monthly sponsor reports are
    /// computed from. A nightly screenshot run would otherwise manufacture impressions -- for events
    /// that finished days earlier, seen by nobody -- and quietly inflate the numbers a sponsor is
    /// shown. Blocking the calls is the only honest option; hiding the panel afterwards is too late,
    /// because the impression fires as soon as it renders.
    /// </remarks>
    private async Task BlockRequestsAsync(IBrowserContext context)
    {
        if (options.BlockedUrlSubstrings.Count == 0)
            return;

        // Matched by substring through the predicate overload rather than by Playwright's URL globs.
        // Glob semantics have moved between Playwright versions -- particularly around whether the
        // query string participates -- and a pattern that silently stops matching would resume
        // writing sponsor impressions with nothing to show for it.
        await context.RouteAsync(
            url => options.BlockedUrlSubstrings.Any(s => url.Contains(s, StringComparison.OrdinalIgnoreCase)),
            route => route.AbortAsync());
    }

    /// <summary>
    /// Removes page furniture that has no business in a results picture.
    /// </summary>
    /// <remarks>
    /// Sponsor panels rotate, so leaving one in would put whichever sponsor happened to be on screen
    /// at the shutter into a post -- an implied endorsement nobody agreed to. The selectors are
    /// best-effort: one that matches nothing is a layout change, not a failure, and is not worth
    /// losing the image over.
    /// </remarks>
    private async Task HideChromeAsync(IPage page)
    {
        try
        {
            await page.EvaluateAsync(
                @"selectors => {
                    // A scrollbar down the edge of the picture is the giveaway that it is a screen
                    // capture rather than a graphic, and it carries no information.
                    const style = document.createElement('style');
                    style.textContent = '::-webkit-scrollbar { display: none !important; } ' +
                        'html, body { scrollbar-width: none !important; }';
                    document.head.appendChild(style);

                    for (const selector of selectors) {
                        for (const el of document.querySelectorAll(selector)) {
                            el.style.display = 'none';
                        }
                    }

                    // Hiding the header shifts the rows up, so the capture starts from the leaders
                    // rather than wherever the page happened to be sitting.
                    window.scrollTo(0, 0);
                }",
                options.HideSelectors);
        }
        catch (PlaywrightException ex)
        {
            logger.LogWarning("Could not hide page chrome before capture: {Message}", ex.Message);
        }
    }

    private async Task<byte[]> CaptureElementOrPageAsync(IPage page, SessionImageRequest request, string url)
    {
        if (!string.IsNullOrWhiteSpace(options.ClipSelector))
        {
            var element = await page.QuerySelectorAsync(options.ClipSelector);
            if (element is not null)
                return await element.ScreenshotAsync(new ElementHandleScreenshotOptions { Type = ScreenshotType.Png });

            // Falling back rather than failing: a wider picture is worse than an intended one, but far
            // better than no post about the event at all.
            logger.LogWarning(
                "Event {EventId} session {SessionId}: '{ClipSelector}' matched nothing on {Url}; capturing the viewport instead",
                request.EventId, request.SessionId, options.ClipSelector, url);
        }

        return await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png, FullPage = false });
    }

    /// <summary>
    /// Returns the shared browser, launching or relaunching it as needed.
    /// </summary>
    /// <remarks>
    /// Liveness is checked rather than assumed. A renderer that dies -- which a 55-car page against a
    /// memory ceiling can cause -- leaves a non-null but unusable browser, and without this check
    /// every remaining capture in the run would fail with "target has been closed" and nothing would
    /// point at the real cause.
    /// </remarks>
    private async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken)
    {
        if (browser is { IsConnected: true })
            return browser;

        await launchLock.WaitAsync(cancellationToken);
        try
        {
            if (browser is { IsConnected: true })
                return browser;

            if (browser is not null)
                logger.LogWarning("The headless browser is no longer connected; relaunching it");

            await DisposeBrowserAsync();

            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args =
                [
                    // Chromium's shared memory default is 64MB in a container and it crashes on
                    // pages larger than that. Writing to /tmp instead is the standard fix.
                    "--disable-dev-shm-usage",

                    // The renderer sandbox needs privileges this pod deliberately does not have.
                    // Accepted because the browser only ever navigates to our own timing page, but it
                    // is a real reduction: the page decodes third-party images (organization logos and
                    // sponsor artwork), which is the classic renderer-exploit surface, and this pod's
                    // environment carries the database, Anthropic and CDN credentials. The pod's
                    // securityContext in the Helm chart carries the compensating controls.
                    "--no-sandbox",
                ],
            });

            logger.LogInformation("Launched headless Chromium {Version}", browser.Version);
            return browser;
        }
        catch (Exception ex)
        {
            // Whatever failed, do not leave a half-started driver behind: the next capture would
            // overwrite the field and orphan its node process, and twenty of those inside a 1.5GB
            // container turn a graceful degradation to text-only posts into an OOM kill.
            await DisposeBrowserAsync();

            throw new SessionImageCaptureException(
                "Could not start the headless browser. The image is likely missing from the container: " +
                $"the Dockerfile installs it with 'playwright install chromium'. ({ex.Message})", ex);
        }
        finally
        {
            launchLock.Release();
        }
    }

    private async Task DisposeBrowserAsync()
    {
        if (browser is not null)
        {
            try
            {
                await browser.CloseAsync();
            }
            catch (PlaywrightException)
            {
                // Already gone, which is the outcome wanted.
            }

            browser = null;
        }

        playwright?.Dispose();
        playwright = null;
    }

    public async ValueTask DisposeAsync() => await DisposeBrowserAsync();
}

/// <summary>
/// How a session page is rendered and what part of it is kept.
/// </summary>
/// <param name="BaseUrl">Site the timing page is served from.</param>
/// <param name="QueryString">
/// Query appended to the page address. Defaults to the landing UI's embed flag, which drops the site
/// toolbar and footer properly rather than by hiding them after they have rendered.
/// </param>
/// <param name="ViewportWidth">
/// CSS width the page lays out at. Narrower than a desktop on purpose: a feed shows an image around
/// 500px across, so a full-width timing table would arrive unreadable.
/// </param>
/// <param name="ViewportHeight">CSS height, which bounds how many rows appear.</param>
/// <param name="DeviceScaleFactor">Pixels per CSS pixel. Two keeps small type legible after the feed downsamples it.</param>
/// <param name="ReadySelector">Element whose appearance means results have rendered.</param>
/// <param name="ClipSelector">Element to crop to, or empty to keep the viewport.</param>
/// <param name="HideSelectors">Elements to remove before the shutter, such as the tab strip.</param>
/// <param name="BlockedUrlSubstrings">
/// Requests the page must not be allowed to make while being photographed, matched as case-insensitive
/// substrings of the full URL.
/// </param>
/// <param name="ReadyTimeout">How long to wait for navigation and for <paramref name="ReadySelector"/>.</param>
/// <param name="SettleDelay">Pause after the rows appear, for images and fonts.</param>
public sealed record SessionImageCaptureOptions(
    string BaseUrl,
    string QueryString,
    int ViewportWidth,
    int ViewportHeight,
    float DeviceScaleFactor,
    string ReadySelector,
    string ClipSelector,
    IReadOnlyList<string> HideSelectors,
    IReadOnlyList<string> BlockedUrlSubstrings,
    TimeSpan ReadyTimeout,
    TimeSpan SettleDelay);
