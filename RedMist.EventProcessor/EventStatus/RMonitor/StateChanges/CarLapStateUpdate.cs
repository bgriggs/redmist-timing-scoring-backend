using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus.RMonitor.StateChanges;

public record CarLapStateUpdate(RaceInformation RaceInformation) : ICarStateChange
{
    public string Number => RaceInformation.RegistrationNumber;
    public SessionContext? SessionContext { get; set; }

    public CarPositionPatch? GetChanges(CarPosition state)
    {
        var patch = new CarPositionPatch();
        bool positionChanged = state.OverallPosition != RaceInformation.Position;
        if (state.LastLapCompleted != RaceInformation.Laps)
            patch.LastLapCompleted = RaceInformation.Laps;
        else if (RaceInformation.Laps == 0 && positionChanged)
            patch.LastLapCompleted = 0; // Include lap 0 on initial position so LapProcessor can log it
        if (state.TotalTime != RaceInformation.RaceTime)
            patch.TotalTime = RaceInformation.RaceTime;
        if (positionChanged)
            patch.OverallPosition = RaceInformation.Position;

        var f = SessionContext?.SessionState.CurrentFlag;
        if (state.TrackFlag != f)
            patch.TrackFlag = f;

        var eventId = SessionContext?.SessionState.EventId.ToString();
        if (state.EventId != eventId)
            patch.EventId = eventId;

        var sessionId = SessionContext?.SessionState.SessionId.ToString();
        if (state.SessionId != sessionId)
            patch.SessionId = sessionId;

        return patch;
    }
}