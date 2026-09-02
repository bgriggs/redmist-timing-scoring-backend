using RedMist.StatusApi.Services.Exports;
using System.Text;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

/// <summary>
/// Exports are staged on disk, so the guarantee worth testing is that the file always goes away -
/// on success when the response stream closes, and on failure before the exception escapes. A pod
/// that leaks one temp file per failed export fills its ephemeral storage and gets evicted.
/// </summary>
/// <remarks>
/// Every export in the process shares one temp directory, and these tests run alongside the ones
/// that generate real exports, so the leak checks look for a marker unique to the test rather than
/// comparing directory listings - a listing would race with whatever else is exporting.
/// </remarks>
[TestClass]
public class ExportTempFileTests
{
    private static string ExportDirectory => Path.Combine(Path.GetTempPath(), "redmist-exports");

    [TestMethod]
    public async Task BuildAsync_ReturnsAReadableStreamOverWhatWasWritten()
    {
        await using var stream = await ExportTempFile.BuildAsync(".txt", asyncWrites: true,
            async (output, token) => await output.WriteAsync(Encoding.UTF8.GetBytes("hello"), token),
            CancellationToken.None);

        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        Assert.AreEqual("hello", await reader.ReadToEndAsync());
    }

    [TestMethod]
    public async Task BuildAsync_FileIsDeletedWhenTheStreamIsDisposed()
    {
        var stream = await ExportTempFile.BuildAsync(".txt", asyncWrites: true,
            async (output, token) => await output.WriteAsync(Encoding.UTF8.GetBytes("hello"), token),
            CancellationToken.None);

        var path = stream.Name;
        Assert.IsTrue(File.Exists(path), "the export should exist while its stream is open");

        await stream.DisposeAsync();

        Assert.IsFalse(File.Exists(path), "the export should be removed when its stream closes");
    }

    /// <summary>
    /// A writer that throws part way through - a dropped database connection, a lap row that breaks
    /// the PDF layout - must not leave its half-written file behind.
    /// </summary>
    [TestMethod]
    public async Task BuildAsync_WriterThrows_LeavesNoFileBehind()
    {
        var marker = $"marker-{Guid.NewGuid():N}";

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await ExportTempFile.BuildAsync(".txt", asyncWrites: true,
                async (output, token) =>
                {
                    await output.WriteAsync(Encoding.UTF8.GetBytes(marker), token);
                    throw new InvalidOperationException("writer failed");
                },
                CancellationToken.None));

        Assert.IsFalse(AnyExportContains(marker), "the partial export should have been removed");
    }

    /// <summary>
    /// A client that disconnects mid-export cancels the work; the staged file has to go with it.
    /// </summary>
    [TestMethod]
    public async Task BuildAsync_Canceled_LeavesNoFileBehind()
    {
        var marker = $"marker-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await ExportTempFile.BuildAsync(".txt", asyncWrites: true,
                async (output, token) =>
                {
                    await output.WriteAsync(Encoding.UTF8.GetBytes(marker), CancellationToken.None);
                    await cts.CancelAsync();
                    token.ThrowIfCancellationRequested();
                },
                cts.Token));

        Assert.IsFalse(AnyExportContains(marker), "the canceled export should have been removed");
    }

    /// <summary>
    /// Looks for the marker in any staged export.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every export in the process shares this directory, and these tests run beside ones generating
    /// real exports, so the share mode matters: <c>File.ReadAllText</c> opens denying DELETE, and an
    /// export reopening its finished file asks for delete-on-close, so merely looking at the
    /// directory was enough to fail somebody else's download with a sharing violation. Opening with
    /// everything shared means this can never deny another export anything.
    /// </para>
    /// <para>
    /// A file still being written is opened exclusively and simply cannot be read; that is safe to
    /// skip, because a file this test leaked would already be closed by the time the exception
    /// escaped.
    /// </para>
    /// </remarks>
    private static bool AnyExportContains(string marker)
    {
        if (!Directory.Exists(ExportDirectory))
            return false;

        foreach (var path in Directory.GetFiles(ExportDirectory))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                if (reader.ReadToEnd().Contains(marker, StringComparison.Ordinal))
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held exclusively by a concurrent export, so it is not the file this test wrote.
            }
        }

        return false;
    }
}
