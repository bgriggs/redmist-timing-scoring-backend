namespace RedMist.EventProcessor.EventStatus.Sectors;

/// <summary>
/// One car completing one sector, as reported by any sector source. Sources only translate their
/// own data into these; everything else - which lap the time belongs to when the source cannot say,
/// the bests and the flags - is decided by <see cref="SectorProcessor"/>, the same way for every
/// source.
/// </summary>
/// <param name="CarNumber">The car that completed the sector.</param>
/// <param name="Index">The sector completed, from 1.</param>
/// <param name="TimeMs">Time through the sector in milliseconds.</param>
/// <param name="Lap">
/// The lap the sector belongs to, when the source knows it. Null leaves it to the processor, which
/// follows the car round the lap from the order its sectors arrive in. A source that withdraws or
/// corrects a time after the car has moved on must give the lap: without it the correction is taken
/// for the car's current lap and the original time keeps counting.
/// </param>
/// <param name="IsInvalid">
/// The time is shown but must not count toward any best: through the pit lane, or marked invalid
/// by the source.
/// </param>
public record SectorCrossing(string CarNumber, int Index, int TimeMs, int? Lap = null, bool IsInvalid = false);
