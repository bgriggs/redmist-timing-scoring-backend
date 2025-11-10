using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus.Multiloop.StateChanges;

public record PositionInfoStateUpdate(CompletedLap CompletedLap) : ICarStateChange
{
    public string Number => CompletedLap.Number;

    public CarPositionPatch? GetChanges(CarPosition state)
    {
        var patch = new CarPositionPatch { Number = state.Number };
        if (state.OverallStartingPosition != CompletedLap.StartPosition)
            patch.OverallStartingPosition = CompletedLap.StartPosition;
        if (state.LapsLedOverall != CompletedLap.LapsLed)
            patch.LapsLedOverall = CompletedLap.LapsLed;

        // Make sure the status is at most 12 characters long
        var status = string.IsNullOrEmpty(CompletedLap.CurrentStatus)
            ? string.Empty
            : CompletedLap.CurrentStatus.Length > 12
                ? CompletedLap.CurrentStatus[..12]
                : CompletedLap.CurrentStatus;
        if (state.CurrentStatus != status)
            patch.CurrentStatus = status;
        return patch;
    }
}
