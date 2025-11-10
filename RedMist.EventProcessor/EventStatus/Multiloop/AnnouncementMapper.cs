using Riok.Mapperly.Abstractions;

namespace RedMist.EventProcessor.EventStatus.Multiloop;

[Mapper]
public partial class AnnouncementMapper
{
    [MapProperty(nameof(Announcement.PriorityStr), nameof(TimingCommon.Models.Announcement.Priority))]
    [MapProperty(nameof(Announcement.Timestamp), nameof(TimingCommon.Models.Announcement.Timestamp))]
    [MapProperty(nameof(Announcement.Text), nameof(TimingCommon.Models.Announcement.Text))]
    public partial TimingCommon.Models.Announcement ToTimingCommonAnnouncement(Announcement source);
}
