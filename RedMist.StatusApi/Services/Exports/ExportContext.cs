namespace RedMist.StatusApi.Services.Exports;

/// <summary>
/// Identifies what an export covers. Carried into the writers so the header of a PDF, the envelope
/// of a JSON file and the download's file name all describe the same thing.
/// </summary>
public sealed class ExportContext
{
    /// <summary>The event the export covers.</summary>
    public int EventId { get; set; }

    /// <summary>The event's name, or empty when the event hides its name.</summary>
    public string EventName { get; set; } = string.Empty;

    /// <summary>The session the export covers.</summary>
    public int SessionId { get; set; }

    /// <summary>The session's name.</summary>
    public string SessionName { get; set; } = string.Empty;

    /// <summary>The single car the export covers, or null for every car in the session.</summary>
    public string? CarNumber { get; set; }

    /// <summary>When the export was produced, in UTC.</summary>
    public DateTime GeneratedUtc { get; set; }
}

/// <summary>
/// What a writer actually produced, so the caller can log it and the file can say so.
/// </summary>
public sealed class ExportWriteResult
{
    /// <summary>Rows written to the file.</summary>
    public int RowsWritten { get; set; }

    /// <summary>Whether the row cap was hit and the export stops short of the full session.</summary>
    public bool Truncated { get; set; }

    /// <summary>
    /// Rows that could not be read and were left out. Stored lap JSON was written by whichever
    /// version of the event processor was running at the time, so a row that no longer parses is a
    /// possibility the export has to survive rather than fail on.
    /// </summary>
    public int SkippedRows { get; set; }
}
