using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;

public class PracticeQualifyingStateUpdate(RunInformation runInformation) : ISessionStateChange
{
    public List<string> Targets => [nameof(SessionState.IsPracticeQualifying)];

    public Task<bool> ApplyToState(SessionState state)
    {
        state.IsPracticeQualifying = runInformation.RunType == 
            RunType.Practice || runInformation.RunType == RunType.Qualifying || runInformation.RunType == RunType.SingleCarQualifying;
        return Task.FromResult(true);
    }
}
