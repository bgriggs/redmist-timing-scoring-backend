using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus;

public interface ICarStateChange : IStateChange<CarPosition, CarPositionPatch>
{
    string Number { get; }
}
