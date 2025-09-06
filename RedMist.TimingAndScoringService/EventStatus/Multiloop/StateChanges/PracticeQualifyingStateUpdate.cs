using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;

public record PracticeQualifyingStateUpdate(RunInformation RunInformation) : ISessionStateChange
{
    public SessionStatePatch? GetChanges(SessionState state)
    {
        var patch = new SessionStatePatch { SessionId = state.SessionId };

        var isPracticeQualifying = 
            RunInformation.RunType == RunType.Practice || 
            RunInformation.RunType == RunType.Qualifying || 
            RunInformation.RunType == RunType.SingleCarQualifying;

        if (state.IsPracticeQualifying != isPracticeQualifying)
        {
            patch.IsPracticeQualifying = isPracticeQualifying;
        }

        return patch;
    }
}
