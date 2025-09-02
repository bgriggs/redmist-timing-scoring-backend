using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;

public record PitSfCrossingStateUpdate(LineCrossing LineCrossing) : ISessionStateChange
{
    public List<string> Targets => [nameof(CarPosition.IsPitStartFinish)];

    public Task<bool> ApplyToState(SessionState state)
    {
        var c = state.CarPositions.FirstOrDefault(c => c.Number == LineCrossing.Number);
        if (c != null)
        {
            c.IsPitStartFinish = LineCrossing.CrossingStatus == LineCrossingStatus.Pit;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}
