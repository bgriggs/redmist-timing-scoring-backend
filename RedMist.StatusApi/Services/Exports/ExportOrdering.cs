namespace RedMist.StatusApi.Services.Exports;

/// <summary>
/// Puts collected export rows into the order a person reads them, and records where a truncated
/// scan stopped.
/// </summary>
/// <remarks>
/// <para>
/// Rows arrive in the database index order, which sorts car numbers as text because that is what
/// they are: "100" lands before "18", and "18x" before "2". Every report a person looks at wants
/// them the way a grid sheet lists them instead.
/// </para>
/// <para>
/// The two steps are bundled into one call on purpose. The cap that truncates a scan cuts while the
/// rows are still in scan order, so the boundary can only be identified before the sort - and once
/// sorted, the rows that were dropped are scattered through the numbering rather than being the
/// tail. Capturing and sorting in separate places is exactly how that gets done in the wrong order.
/// </para>
/// <para>
/// This is only for the reports that already hold their rows. The streamed lap exports are not
/// ordered here and must not be: see <see cref="LapExportWriter.WriteCsvAsync"/>.
/// </para>
/// </remarks>
public static class ExportOrdering
{
    /// <summary>
    /// Orders collected lap rows by car number, then by lap within each car.
    /// </summary>
    /// <param name="scanned">The rows as the scan read them; not modified.</param>
    /// <param name="truncated">Whether the scan stopped at its cap.</param>
    /// <returns>The ordered rows, and the last car the scan reached when it was truncated.</returns>
    public static (List<LapExportRow> Rows, string? TruncatedAfterCarNumber) ForDisplay(
        IReadOnlyList<LapExportRow> scanned, bool truncated)
    {
        var boundary = Boundary(truncated, scanned.Count > 0 ? scanned[^1].CarNumber : null);
        var ordered = scanned
            .OrderBy(x => x.CarNumber, CarNumberComparer.Instance)
            .ThenBy(x => x.LapNumber)
            .ToList();
        return (ordered, boundary);
    }

    /// <summary>
    /// Orders collected pit stops by car number, then by the order the stops happened.
    /// </summary>
    /// <param name="scanned">The stops as the scan read them; not modified.</param>
    /// <param name="truncated">Whether the scan stopped at its cap.</param>
    /// <returns>The ordered stops, and the last car the scan reached when it was truncated.</returns>
    public static (List<PitStopRecord> Stops, string? TruncatedAfterCarNumber) ForDisplay(
        IReadOnlyList<PitStopRecord> scanned, bool truncated)
    {
        var boundary = Boundary(truncated, scanned.Count > 0 ? scanned[^1].CarNumber : null);
        var ordered = scanned
            .OrderBy(x => x.CarNumber, CarNumberComparer.Instance)
            .ThenBy(x => x.StopNumber)
            .ToList();
        return (ordered, boundary);
    }

    private static string? Boundary(bool truncated, string? lastScannedCarNumber) =>
        truncated ? lastScannedCarNumber : null;
}
