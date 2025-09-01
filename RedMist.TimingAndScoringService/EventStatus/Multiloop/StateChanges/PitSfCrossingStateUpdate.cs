using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;

public class PitSfCrossingStateUpdate(LineCrossing lineCrossing) : ISessionStateChange
{
    public List<string> Targets => [nameof(CarPosition.IsPitStartFinish)];

    public Task<bool> ApplyToState(SessionState state)
    {
        var c = state.CarPositions.FirstOrDefault(c => c.Number == lineCrossing.Number);
        if (c != null)
        {
            c.IsPitStartFinish = lineCrossing.CrossingStatus == LineCrossingStatus.Pit;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}
