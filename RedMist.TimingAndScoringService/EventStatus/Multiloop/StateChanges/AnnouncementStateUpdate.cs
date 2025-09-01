using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;

public class AnnouncementStateUpdate(Dictionary<ushort, Announcement> announcements) : ISessionStateChange
{
    private static readonly AnnouncementMapper mapper = new();

    public List<string> Targets => [nameof(SessionState.Announcements)];

    public Task<bool> ApplyToState(SessionState state)
    {
        state.Announcements.Clear();
        state.Announcements.AddRange(announcements.Values.Select(mapper.ToTimingCommonAnnouncement));
        return Task.FromResult(true);
    }
}
