namespace RedMist.StatusApi.Services.Exports;

/// <summary>
/// What went wrong while reading the rows an export is built from, carried from the data source to
/// the writer so it can be said in the file.
/// </summary>
/// <remarks>
/// <para>
/// The reader and the writer are separated by an <c>IAsyncEnumerable</c>, which can only yield rows -
/// it has no way to report that it dropped one or gave up early. Without this the losses were
/// invisible: an export whose <c>Content-Disposition</c> promises a session's laps would hand back a
/// file that skips from lap 1 to lap 3, and say nothing.
/// </para>
/// <para>
/// This matters because a drop is not hypothetical. <c>CarLapLog.LapData</c> is capped at 5000
/// characters, so a large lap payload is stored truncated and no longer parses, and the stored JSON
/// spans every build of the event processor that ever ran during the event.
/// </para>
/// <para>
/// One export reads its rows on one flow of execution, so the counters need no synchronization; an
/// instance must not be shared between exports.
/// </para>
/// </remarks>
public sealed class ExportScanDiagnostics
{
    /// <summary>Rows that could not be read and were left out of the export.</summary>
    public int SkippedRows { get; private set; }

    /// <summary>
    /// Whether the scan stopped at its row limit rather than at the end of the session, which means
    /// the export does not cover the whole session however complete it looks.
    /// </summary>
    public bool ScanCapReached { get; private set; }

    /// <summary>Records a row that could not be read.</summary>
    public void RowSkipped() => SkippedRows++;

    /// <summary>Records that the scan gave up at its row limit.</summary>
    public void ScanCapHit() => ScanCapReached = true;

    /// <summary>Folds what happened into the writer's result.</summary>
    /// <param name="result">The result to fold into.</param>
    /// <returns>The same result, for chaining.</returns>
    public ExportWriteResult ApplyTo(ExportWriteResult result)
    {
        result.SkippedRows += SkippedRows;
        result.Truncated |= ScanCapReached;
        return result;
    }
}
