using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;

public record CarLapStateUpdate(RaceInformation RaceInformation) : ICarStateChange
{
    public List<string> Targets => [nameof(CarPosition.LastLapCompleted)];

    public CarPositionPatch? GetChanges(CarPosition state)
    {
        var patch = new CarPositionPatch();
        if (state.LastLapCompleted != RaceInformation.Laps)
            patch.LastLapCompleted = RaceInformation.Laps;
        if (state.TotalTime != RaceInformation.RaceTime)
            patch.TotalTime = RaceInformation.RaceTime;
        return patch;
    }
}