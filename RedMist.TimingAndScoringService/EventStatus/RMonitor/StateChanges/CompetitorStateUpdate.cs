using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;

public record CompetitorStateUpdate(List<Competitor> Competitors, Dictionary<int, string> Classes) : ISessionStateChange
{
    public SessionStatePatch? GetChanges(SessionState state)
    {
        return new SessionStatePatch
        {
            EventEntries = [.. Competitors.Select(c => c.ToEventEntry(GetClassName))]
        };
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