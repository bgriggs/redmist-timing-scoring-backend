using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;

public record CarLapTimeStateUpdate(PassingInformation PassingInformation) : ICarStateChange
{
    public string Number => PassingInformation.RegistrationNumber;

    public CarPositionPatch? GetChanges(CarPosition state)
    {
        var patch = new CarPositionPatch();

        if (state.LastLapTime != PassingInformation.LapTime)
            patch.LastLapTime = PassingInformation.LapTime;
        if (state.TotalTime != PassingInformation.RaceTime)
            patch.TotalTime = PassingInformation.RaceTime;

        return patch;
    }
}