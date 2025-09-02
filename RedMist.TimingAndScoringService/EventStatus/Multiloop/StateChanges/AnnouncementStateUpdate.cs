using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;

public record AnnouncementStateUpdate(Dictionary<ushort, Announcement> Announcements) : ISessionStateChange
{
    private static readonly AnnouncementMapper mapper = new();

    public List<string> Targets => [nameof(SessionState.Announcements)];

    public Task<bool> ApplyToState(SessionState state)
    {
        state.Announcements.Clear();
        state.Announcements.AddRange(Announcements.Values.Select(mapper.ToTimingCommonAnnouncement));
        return Task.FromResult(true);
    }
}
