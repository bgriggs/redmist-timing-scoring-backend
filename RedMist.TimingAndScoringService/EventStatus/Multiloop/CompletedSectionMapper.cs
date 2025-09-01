using Riok.Mapperly.Abstractions;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop;

/// <summary>
/// Mapper for converting between Multiloop CompletedSection and TimingCommon CompletedSection
/// </summary>
[Mapper]
public partial class CompletedSectionMapper
{
    /// <summary>
    /// Maps from Multiloop CompletedSection to TimingCommon CompletedSection
    /// </summary>
    [MapProperty(nameof(CompletedSection.SectionIdentifier), nameof(TimingCommon.Models.CompletedSection.SectionId))]
    [MapProperty(nameof(CompletedSection.ElapsedTimeMs), nameof(TimingCommon.Models.CompletedSection.ElapsedTimeMs))]
    [MapProperty(nameof(CompletedSection.LastSectionTimeMs), nameof(TimingCommon.Models.CompletedSection.LastSectionTimeMs))]
    [MapProperty(nameof(CompletedSection.LastLap), nameof(TimingCommon.Models.CompletedSection.LastLap))]
    [MapProperty(nameof(CompletedSection.Number), nameof(TimingCommon.Models.CompletedSection.Number))]
    public partial TimingCommon.Models.CompletedSection ToTimingCommonCompletedSection(CompletedSection source);
}