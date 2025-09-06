using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus;

public interface ICarStateChange
{
    /// <summary>
    /// Gets changes relative to the specified car state.
    /// </summary>
    /// <param name="state">The car state to which the operation will base upon. Cannot be <see langword="null"/>.</param>
    /// <returns>Changes as represented as a non-null value.</returns>
    CarPositionPatch? GetChanges(CarPosition state);
}
