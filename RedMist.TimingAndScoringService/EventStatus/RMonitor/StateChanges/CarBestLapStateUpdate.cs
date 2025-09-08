using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;

public record CarBestLapStateUpdate(PracticeQualifying PracticeQualifying) : ICarStateChange
{
    public string Number => PracticeQualifying.RegistrationNumber;

    public CarPositionPatch? GetChanges(CarPosition state)
    {
        var patch = new CarPositionPatch();

        if (state.BestLap != PracticeQualifying.BestLap)
            patch.BestLap = PracticeQualifying.BestLap;
        if (state.BestTime != PracticeQualifying.BestLapTime)
            patch.BestTime = PracticeQualifying.BestLapTime;

        return patch;
    }
}
