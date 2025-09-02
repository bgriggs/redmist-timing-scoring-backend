using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;

public record CompetitorStateUpdate(List<Competitor> Competitors, Dictionary<int, string> Classes) : ISessionStateChange
{
    public List<string> Targets => [nameof(SessionState.EventEntries)];

    public Task<bool> ApplyToState(SessionState state)
    {
        state.EventEntries.Clear();
        var entries = Competitors.Select(c => c.ToEventEntry(GetClassName));
        state.EventEntries.AddRange(entries);
        return Task.FromResult(true);
    }

    private string? GetClassName(int classId)
    {
        if (Classes.TryGetValue(classId, out var className))
        {
            return className;
        }
        return null;
    }
}