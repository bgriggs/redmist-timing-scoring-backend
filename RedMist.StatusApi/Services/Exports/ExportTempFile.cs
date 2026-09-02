namespace RedMist.StatusApi.Services.Exports;

/// <summary>
/// Builds an export on disk and hands back a stream that deletes the file when it is closed.
/// </summary>
/// <remarks>
/// <para>
/// Exports are written to a temp file rather than to the response or to a buffer because neither of
/// the alternatives is safe here. Building the file in memory would put a whole session of laps on
/// the heap of a pod that also carries the SignalR hub; writing straight to the response body means
/// a failure halfway through arrives at the client as a truncated, valid-looking download with a
/// 200 already on it.
/// </para>
/// <para>
/// The file is opened for reading with <see cref="FileOptions.DeleteOnClose"/>, so the operating
/// system removes it when the response stream is disposed - including when the client disconnects
/// mid-download and MVC tears the result down early. If anything fails before that stream exists,
/// the partial file is deleted here instead.
/// </para>
/// </remarks>
public static class ExportTempFile
{
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Where exports are staged. Shared with <see cref="ExportStartupService"/>, which sweeps it for
    /// files a killed process left behind.
    /// </summary>
    public static string Directory => Path.Combine(Path.GetTempPath(), "redmist-exports");

    /// <summary>
    /// Runs <paramref name="write"/> against a fresh temp file and reopens it for reading.
    /// </summary>
    /// <param name="extension">File extension including the leading dot, e.g. ".csv".</param>
    /// <param name="asyncWrites">
    /// True when the writer uses async I/O. Passed as false for writers that only write
    /// synchronously (QuestPDF), which an overlapped handle would only slow down.
    /// </param>
    /// <param name="write">Callback that produces the file's contents.</param>
    /// <param name="cancellationToken">Canceled when the client disconnects.</param>
    /// <returns>A readable stream over the finished file that deletes it on dispose.</returns>
    public static async Task<FileStream> BuildAsync(string extension, bool asyncWrites,
        Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        var directory = Directory;
        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + extension);

        try
        {
            var writeOptions = FileOptions.SequentialScan | (asyncWrites ? FileOptions.Asynchronous : FileOptions.None);
            var output = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = BufferSize,
                Options = writeOptions,
            });

            try
            {
                await write(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                await output.DisposeAsync();
            }
            catch
            {
                // Not an `await using`: disposing a FileStream flushes it, and a flush that fails
                // while an exception is already on its way up would replace the real fault - the
                // database error or the writer bug - with an IO error from the cleanup.
                try
                {
                    await output.DisposeAsync();
                }
                catch (IOException)
                {
                }
                throw;
            }

            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                // Share must include Delete for DeleteOnClose to be honored while the handle is open.
                Share = FileShare.Read | FileShare.Delete,
                BufferSize = BufferSize,
                Options = FileOptions.SequentialScan | FileOptions.Asynchronous | FileOptions.DeleteOnClose,
            });
        }
        catch
        {
            await TryDeleteAsync(path);
            throw;
        }
    }

    /// <summary>
    /// Deletes a partial export, swallowing anything that goes wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retried, because closing a handle and the file becoming deletable are not the same instant on
    /// Windows: an overlapped handle closed while its I/O is still completing leaves the file briefly
    /// undeletable, and <c>File.Delete</c> answers with a sharing violation. One attempt loses the
    /// race under load often enough to leave real files behind, which is how this was found.
    /// </para>
    /// <para>
    /// There is no existence check: deleting a file that is already gone is not an error, and testing
    /// first only adds a way to conclude wrongly that there is nothing to do.
    /// </para>
    /// <para>
    /// Failure is still swallowed at the end. This runs while an exception is already on its way up,
    /// and losing the real fault - the database error, the writer bug - to an IO error from cleanup
    /// would hide the thing worth fixing. <see cref="ExportStartupService"/> sweeps whatever survives.
    /// </para>
    /// </remarks>
    private static async Task TryDeleteAsync(string path)
    {
        const int attempts = 5;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == attempts)
                    return;
                await Task.Delay(20 * attempt);
            }
        }
    }
}
