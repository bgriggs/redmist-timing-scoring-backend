using RedMist.TimingCommon.Models;
using System.Globalization;

namespace RedMist.EventProcessor.EventStatus.Sectors;

/// <summary>
/// Reads sector times out of the car patches an external timing source sends. Such a source
/// computes everything else itself, but not the sector bests: those are left to
/// <see cref="SectorProcessor"/> so every source colors sectors the same way.
/// </summary>
public static class ExternalSectorSource
{
    /// <summary>
    /// Takes the sector times out of a patch, before it is applied. <see cref="CarPositionPatch.Sectors"/>
    /// is removed from the patch: the car's sectors are the processor's to publish, flags included,
    /// and a source's own list would overwrite them.
    ///
    /// A source that predates <see cref="CarPosition.Sectors"/> sends its sectors as
    /// <see cref="CarPosition.CompletedSections"/> named S1, S2 and so on. Those are read too and left
    /// in the patch for readers of that field. Their lap is the car's laps completed, the same for
    /// every sector, which cannot say whether the last sector closed the lap just counted or the one
    /// before; so it is not used, and the processor works the lap out.
    /// </summary>
    public static List<SectorCrossing> TakeCrossings(CarPositionPatch patch)
    {
        var crossings = new List<SectorCrossing>();
        var number = patch.Number;
        if (string.IsNullOrWhiteSpace(number))
            return crossings;

        if (patch.Sectors != null)
        {
            foreach (var sector in patch.Sectors)
            {
                int? lap = sector.Lap > 0 ? sector.Lap : null;
                crossings.Add(new SectorCrossing(number, sector.Index, sector.TimeMs, lap, sector.IsInvalid));
            }
            patch.Sectors = null;
            return crossings;
        }

        if (patch.CompletedSections != null)
        {
            foreach (var section in patch.CompletedSections)
            {
                if (TryParseSectorIndex(section.SectionId, out var index))
                    crossings.Add(new SectorCrossing(number, index, section.LastSectionTimeMs));
            }
        }

        return crossings;
    }

    /// <summary>Reads "S1" to "S8" as sectors 1 to 8; any other section name is not a sector.</summary>
    private static bool TryParseSectorIndex(string? sectionId, out int index)
    {
        index = 0;
        return sectionId is { Length: >= 2 } id
            && (id[0] == 'S' || id[0] == 's')
            && int.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out index)
            && index >= 1 && index <= SectorProcessor.MaxSectors;
    }
}
