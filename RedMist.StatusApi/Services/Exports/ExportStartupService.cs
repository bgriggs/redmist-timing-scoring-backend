using QuestPDF.Fluent;
using QuestPDF.Helpers;

namespace RedMist.StatusApi.Services.Exports;

/// <summary>
/// Two things the export feature needs done once, at startup: clear out files a previous run left
/// behind, and prove the PDF renderer actually works in this image.
/// </summary>
/// <remarks>
/// Both exist so that a problem shows up at deploy time rather than during a race weekend, which is
/// the only time anybody asks for an export.
/// </remarks>
public sealed class ExportStartupService : IHostedService
{
    /// <summary>
    /// How old a leftover file must be before the sweep removes it. Nothing this process staged can
    /// still be in use at startup, but a developer machine can have a second instance running from
    /// the same temp directory, and deleting a file out from under it would be worse than leaving it.
    /// </summary>
    public static readonly TimeSpan OrphanAge = TimeSpan.FromHours(1);

    private readonly ILogger logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExportStartupService"/> class.
    /// </summary>
    /// <param name="loggerFactory">Factory to create loggers.</param>
    public ExportStartupService(ILoggerFactory loggerFactory) =>
        logger = loggerFactory.CreateLogger(nameof(ExportStartupService));

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        SweepOrphanedExports();
        SmokeRenderPdf();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Deletes exports left over from a previous run.
    /// </summary>
    /// <remarks>
    /// Every normal path removes its own file: the staged handle is opened delete-on-close, and a
    /// failed generation deletes the partial. What survives is a process killed outright - an OOM
    /// kill, a SIGKILL past the termination grace period - which leaves its staged file for the life
    /// of the container.
    /// </remarks>
    public void SweepOrphanedExports()
    {
        var directory = ExportTempFile.Directory;
        if (!Directory.Exists(directory))
            return;

        var cutoff = DateTime.UtcNow - OrphanAge;
        var removed = 0;
        long bytes = 0;

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.LastWriteTimeUtc > cutoff)
                        continue;

                    var length = info.Length;
                    info.Delete();
                    removed++;
                    bytes += length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still held by something, or not ours to delete. Leaving it is the safe answer.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not sweep the export staging directory {directory}", directory);
            return;
        }

        if (removed > 0)
            logger.LogInformation("Swept {count} orphaned export file(s) ({bytes} bytes) from {directory}", removed, bytes, directory);
    }

    /// <summary>
    /// Renders a one page document to a discarded stream so that a broken PDF renderer fails here.
    /// </summary>
    /// <remarks>
    /// The renderer is native code loaded on first use. Without this, a missing shared library or a
    /// runtime image change surfaces as a 500 on whichever export request happens to be the first
    /// one in production - which will be during an event, from a user, not from a health check. It
    /// is not fatal on purpose: every other endpoint this service owns works fine without PDF, and
    /// refusing to start would turn a degraded feature into an outage.
    /// </remarks>
    public void SmokeRenderPdf()
    {
        try
        {
            ExportPdf.EnsureConfigured();

            using var sink = Stream.Null;
            Document.Create(document =>
            {
                document.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(20);
                    page.Content().Text("RedMist export renderer startup check");
                });
            }).GeneratePdf(sink);

            logger.LogInformation("PDF export renderer startup check passed");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PDF export renderer startup check FAILED - PDF exports will return errors. " +
                                "JSON and CSV exports are unaffected.");
        }
    }
}
