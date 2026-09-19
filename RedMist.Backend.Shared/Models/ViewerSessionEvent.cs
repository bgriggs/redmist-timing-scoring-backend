using RedMist.Database.Models;
using System.Text.Json.Serialization;

namespace RedMist.Backend.Shared.Models;

/// <summary>
/// A viewer session lifecycle transition, written to the per-event viewership stream by whichever
/// status API replica saw it and consumed by that event's logger pod.
/// </summary>
/// <remarks>
/// The enums serialize as strings. A stream entry is read by a person investigating why an event's
/// numbers look wrong, and "ReconciledAbsent" tells them something "3" does not; it also means
/// inserting an enum member cannot silently renumber entries already in the stream.
/// </remarks>
public class ViewerSessionEvent
{
    public ViewerSessionEventKind Kind { get; set; }

    public int EventId { get; set; }

    public string ConnectionId { get; set; } = string.Empty;

    public string ClientType { get; set; } = string.Empty;

    /// <summary>
    /// When the transition happened, always UTC.
    /// </summary>
    /// <remarks>
    /// The solution runs with Npgsql's legacy timestamp behavior, so a value that loses its UTC Kind
    /// on the way through is stored as if it were local. Producers stamp this with
    /// <see cref="DateTimeKind.Utc"/> and the consumer re-specifies the Kind before persisting.
    /// </remarks>
    public DateTime TimestampUtc { get; set; }

    /// <summary>Null on <see cref="ViewerSessionEventKind.Start"/>.</summary>
    public ViewerSessionEndReason? Reason { get; set; }

    public bool IsInCar { get; set; }

    public string? CarNumber { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ViewerSessionEventKind
{
    Start,
    End,
}
