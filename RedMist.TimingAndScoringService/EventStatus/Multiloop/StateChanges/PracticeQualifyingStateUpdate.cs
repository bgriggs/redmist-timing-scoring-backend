using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;

public record PracticeQualifyingStateUpdate(RunInformation RunInformation) : ISessionStateChange
{
    public List<string> Targets => [nameof(SessionState.IsPracticeQualifying)];

    public Task<bool> ApplyToState(SessionState state)
    {
        state.IsPracticeQualifying = RunInformation.RunType == 
            RunType.Practice || RunInformation.RunType == RunType.Qualifying || RunInformation.RunType == RunType.SingleCarQualifying;
        return Task.FromResult(true);
    }
}
