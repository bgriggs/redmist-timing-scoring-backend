using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;

public record PitStateUpdate(CompletedLap CompletedLap) : ICarStateChange
{
    public string Number => CompletedLap.Number;

    public CarPositionPatch? GetChanges(CarPosition state)
    {
        var patch = new CarPositionPatch { Number = state.Number };

        if (state.LastLapPitted != CompletedLap.LastLapPitted)
            patch.LastLapPitted = CompletedLap.LastLapPitted;

        if (state.PitStopCount != CompletedLap.PitStopCount)
            patch.PitStopCount = CompletedLap.PitStopCount;

        return patch;
    }
}
