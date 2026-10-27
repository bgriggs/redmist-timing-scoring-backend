using RedMist.TimingCommon.Models.Configuration;

namespace RedMist.Database.Models;

/// <summary>
/// An event's sector setup: which timing loops divide the lap into sectors. One row per event.
///
/// Kept apart from the event's loop metadata on purpose. The event is saved whole - by the relay
/// among others, from whatever version of the shared models it was built with - so anything carried
/// on the event can be dropped by a client that does not know about it. A row of its own is only
/// ever written by the clients that edit it.
/// </summary>
public class EventSectorConfiguration
{
    /// <summary>Event the setup belongs to (primary key: one per event).</summary>
    public int EventId { get; set; }

    /// <summary>The setup itself, stored as JSONB.</summary>
    public SectorConfiguration Configuration { get; set; } = new();

    /// <summary>When the setup was last saved, in UTC.</summary>
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// How the lap is divided into sectors using the event's X2 timing loops.
/// </summary>
public class SectorConfiguration
{
    /// <summary>The most sectors a lap may be divided into.</summary>
    public const int MaxSectors = 8;

    /// <summary>The longest a sector label may be.</summary>
    public const int MaxLabelLength = 8;

    /// <summary>
    /// The loop on the start/finish line, where the first sector starts and the last one ends. Null
    /// until the organizer picks it.
    /// </summary>
    public uint? StartFinishLoopId { get; set; }

    /// <summary>
    /// The sectors in lap order, from 1 to <see cref="MaxSectors"/>. Every sector but the last ends
    /// at a loop of its own; the last ends back at the start/finish loop. Empty when the event has
    /// no sectors.
    /// </summary>
    public List<SectorDefinition> Sectors { get; set; } = [];

    /// <summary>
    /// Checks the setup against the event's loops. Returns why it cannot be used, or null when it
    /// can. An empty setup - no sectors - is valid: it is how sectors are turned off.
    /// </summary>
    /// <param name="loops">The event's loops, as recorded by the relay.</param>
    public string? Validate(IReadOnlyCollection<LoopMetadata> loops)
    {
        if (Sectors.Count == 0)
            return null;
        if (Sectors.Count > MaxSectors)
            return $"A lap can have at most {MaxSectors} sectors.";

        var known = loops.GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.First());
        if (StartFinishLoopId is not uint startFinish)
            return "Choose the start/finish loop.";
        if (!known.TryGetValue(startFinish, out var startFinishLoop) || IsPitLoop(startFinishLoop.Type))
            return "The start/finish loop must be one of the event's track loops.";

        var used = new HashSet<uint> { startFinish };
        for (int i = 0; i < Sectors.Count; i++)
        {
            var sector = Sectors[i];
            var name = $"Sector {i + 1}";
            if (sector == null)
                return $"{name} is missing.";
            if (sector.Label.Trim().Length > MaxLabelLength)
                return $"{name}: the label can be at most {MaxLabelLength} characters.";
            if (sector.LengthMeters is double length && (!double.IsFinite(length) || length <= 0))
                return $"{name}: the length must be a positive number of meters.";

            var isLast = i == Sectors.Count - 1;
            if (isLast)
            {
                if (sector.EndLoopId != null)
                    return $"{name} is the last and ends at the start/finish loop.";
                continue;
            }

            if (sector.EndLoopId is not uint end)
                return $"{name}: choose the loop where it ends.";
            if (!known.TryGetValue(end, out var endLoop) || IsPitLoop(endLoop.Type))
                return $"{name}: its loop must be one of the event's track loops.";
            if (!used.Add(end))
                return $"{name}: its loop is already used.";
        }

        return null;
    }

    /// <summary>Pit loops are off the racing line, so they cannot bound a sector of the lap.</summary>
    public static bool IsPitLoop(LoopType type) =>
        type is LoopType.PitIn or LoopType.PitExit or LoopType.PitStartFinish or LoopType.PitOther;
}

/// <summary>
/// One sector of the lap.
/// </summary>
public class SectorDefinition
{
    /// <summary>Column heading for the sector. Empty means the default, "S1", "S2" and so on.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// The loop where the sector ends. Null for the last sector, which ends at the start/finish loop.
    /// </summary>
    public uint? EndLoopId { get; set; }

    /// <summary>The sector's length in meters, when the organizer knows it.</summary>
    public double? LengthMeters { get; set; }
}
