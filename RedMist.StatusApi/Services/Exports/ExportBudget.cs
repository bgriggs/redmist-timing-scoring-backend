namespace RedMist.StatusApi.Services.Exports;

/// <summary>
/// The size ceiling an export file is written under.
/// </summary>
/// <remarks>
/// A row cap alone does not bound a file: a single stored lap payload can be 5KB, so the size of an
/// export is decided by the data rather than by the row count, and 200,000 of them is several
/// hundred megabytes. Exports are staged on the pod's ephemeral disk, which is shared with every
/// other container on the node and which this deployment sets no request or limit for - so an
/// export that grows without bound is a node eviction, not a slow download. The concurrency limiter
/// does not help here either: it is released once a file is generated, so the number of finished
/// exports sitting on disk waiting for slow clients is bounded only by the rate limiter.
/// </remarks>
public static class ExportBudget
{
    /// <summary>
    /// Largest export file that will be produced. Beyond this the export stops and marks itself
    /// truncated, which is a legible outcome; running the node out of disk is not.
    /// </summary>
    public const long MaxExportBytes = 100L * 1024 * 1024;

    /// <summary>
    /// How many rows a CSV writer puts down between size checks. A check needs a flush to see the
    /// true file size, and flushing every row would defeat the write buffer.
    /// </summary>
    public const int CheckInterval = 2_000;

    /// <summary>
    /// Whether the file behind <paramref name="output"/> has reached its ceiling.
    /// </summary>
    /// <param name="output">The stream being written; must have been flushed for the answer to be current.</param>
    /// <param name="maxBytes">The ceiling.</param>
    /// <returns>
    /// True when the budget is spent. A stream that cannot report its position - which the file
    /// exports never use, but a caller could - is treated as within budget rather than refused.
    /// </returns>
    public static bool Exceeded(Stream output, long maxBytes) => output.CanSeek && output.Position >= maxBytes;
}
