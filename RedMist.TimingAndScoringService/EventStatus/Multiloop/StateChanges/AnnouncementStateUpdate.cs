using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;

public record AnnouncementStateUpdate(Dictionary<ushort, Announcement> Announcements) : ISessionStateChange
{
    private static readonly AnnouncementMapper mapper = new();

    /// <summary>
    /// If anything has changed, send the whole list.
    /// </summary>
    /// <param name="state"></param>
    /// <returns></returns>
    public SessionStatePatch? GetChanges(SessionState state)
    {
        var commonAnnouncements = Announcements.Values.Select(mapper.ToTimingCommonAnnouncement).ToList();
        if (!state.Announcements.SequenceEqual(commonAnnouncements))
        {
            return new SessionStatePatch
            {
                SessionId = state.SessionId,
                Announcements = commonAnnouncements
            };
        }
        return null;
    }
}
