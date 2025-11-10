using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus.Multiloop.StateChanges;

public record PitSfCrossingStateUpdate(LineCrossing LineCrossing) : ICarStateChange
{
    public string Number => LineCrossing.Number;

    public CarPositionPatch? GetChanges(CarPosition state)
    {
        var patch = new CarPositionPatch { Number = state.Number };

        if (state.IsPitStartFinish != (LineCrossing.CrossingStatus == LineCrossingStatus.Pit))
        {
            patch.IsPitStartFinish = LineCrossing.CrossingStatus == LineCrossingStatus.Pit;
            patch.IsInPit = patch.IsPitStartFinish;
        }

        return patch;
    }
}
