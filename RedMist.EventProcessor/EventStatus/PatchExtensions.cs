using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus;

public static class PatchExtensions
{
    /// <summary>
    /// Applies a session state change immediately to the session state.
    /// </summary>
    public static SessionStatePatch? ApplySessionChange(this ISessionStateChange change, SessionState sessionState)
    {
        if (change == null) return null;

        var patch = change.GetChanges(sessionState);
        if (patch != null)
        {
            TimingCommon.Models.Mappers.SessionStateMapper.ApplyPatch(patch, sessionState);
        }
        return patch;
    }

    /// <summary>
    /// Applies a car state change immediately to the session context.
    /// </summary>
    public static CarPositionPatch? ApplyCarChange(this ICarStateChange? change, SessionContext sessionContext)
    {
        if (change == null) return null;

        var car = sessionContext.GetCarByNumber(change.Number);
        if (car != null)
        {
            var patch = change.GetChanges(car);
            if (patch != null)
            {
                TimingCommon.Models.Mappers.CarPositionMapper.ApplyPatch(patch, car);
                patch.Number = car.Number; // Ensure number is set
                return patch;
            }
        }
        return null;
    }
}
