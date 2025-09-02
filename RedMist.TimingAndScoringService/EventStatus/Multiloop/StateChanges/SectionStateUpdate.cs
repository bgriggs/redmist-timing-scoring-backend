using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;

public record SectionStateUpdate(string CarNumber, List<CompletedSection> MultiloopCompletedSections) : ISessionStateChange
{
    private static readonly CompletedSectionMapper mapper = new();
    
    public List<string> Targets => [nameof(CarPosition.CompletedSections)];

    public Task<bool> ApplyToState(SessionState state)
    {
        var c = state.CarPositions.FirstOrDefault(c => c.Number == CarNumber);
        if (c != null)
        {
            c.CompletedSections.Clear();
            var timingCommonCompletedSections = MultiloopCompletedSections.Select(mapper.ToTimingCommonCompletedSection).ToList();
            c.CompletedSections.AddRange(timingCommonCompletedSections);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}
