using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus.FlagData.StateChanges;

public record FlagsStateChange(List<FlagDuration> FlagDurations) : ISessionStateChange
{
    /// <summary>
    /// Any change to the flag durations means we send the whole list again.
    /// </summary>
    /// <param name="state"></param>
    /// <returns></returns>
    public SessionStatePatch? GetChanges(SessionState state)
    {
        // If anything has changed, send the whole list.
        if (state.FlagDurations.Count != FlagDurations.Count)
            return new SessionStatePatch { FlagDurations = FlagDurations };

        for (int i = 0; i < FlagDurations.Count; i++)
        {
            if (!state.FlagDurations[i].Equals(FlagDurations[i]))
            {
                return new SessionStatePatch { FlagDurations = FlagDurations };
            }
        }

        return null;
    }
}