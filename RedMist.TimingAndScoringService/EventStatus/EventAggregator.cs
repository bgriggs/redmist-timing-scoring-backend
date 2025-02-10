using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus
{
    public class EventAggregator
    {
        public Action<EventStatusUpdateEventArgs<List<TimingCommon.Models.EventStatus>>>? EventStatusUpdated;
        public Action<EventStatusUpdateEventArgs<List<EventEntries>>>? EventEntriesUpdated;
        public Action<EventStatusUpdateEventArgs<List<CarPosition>>>? CarPositionsUpdated;
    }
}
