using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus;

public interface ICarStateChange : IStateChange<CarPosition, CarPositionPatch>
{
    string Number { get; }
}
