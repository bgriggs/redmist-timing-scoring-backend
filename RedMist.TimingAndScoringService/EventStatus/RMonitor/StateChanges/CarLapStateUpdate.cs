using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;

public record CarLapStateUpdate(RaceInformation RaceInformation) : ICarStateChange
{
    public string Number => RaceInformation.RegistrationNumber;

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