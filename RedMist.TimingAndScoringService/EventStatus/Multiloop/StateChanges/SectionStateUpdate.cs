using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;

public class SectionStateUpdate(string carNumber, List<CompletedSection> multiloopCompletedSections) : ISessionStateChange
{
    private static readonly CompletedSectionMapper mapper = new();
    
    public List<string> Targets => [nameof(CarPosition.CompletedSections)];

    public Task<bool> ApplyToState(SessionState state)
    {
        var c = state.CarPositions.FirstOrDefault(c => c.Number == carNumber);
        if (c != null)
        {
            c.CompletedSections.Clear();
            var timingCommonCompletedSections = multiloopCompletedSections.Select(mapper.ToTimingCommonCompletedSection).ToList();
            c.CompletedSections.AddRange(timingCommonCompletedSections);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}
