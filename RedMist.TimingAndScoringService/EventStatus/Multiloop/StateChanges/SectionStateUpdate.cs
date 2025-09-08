using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;

public record SectionStateUpdate(string CarNumber, List<CompletedSection> MultiloopCompletedSections) : ICarStateChange
{
    private static readonly CompletedSectionMapper mapper = new();

    public string Number => CarNumber;

    public CarPositionPatch? GetChanges(CarPosition state)
    {
        var patch = new CarPositionPatch { Number = state.Number };
        var timingCommonCompletedSections = MultiloopCompletedSections.Select(mapper.ToTimingCommonCompletedSection).ToList();
        if (!state.CompletedSections.SequenceEqual(timingCommonCompletedSections))
            patch.CompletedSections = timingCommonCompletedSections;
        return patch;
    }
}
