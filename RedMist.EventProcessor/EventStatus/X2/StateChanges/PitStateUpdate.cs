using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using RedMist.TimingCommon.Models.X2;

namespace RedMist.EventProcessor.EventStatus.X2.StateChanges;

public record PitStateUpdate(
    string CarNumber,
    Dictionary<string, HashSet<int>> CarLapsWithPitStops,
    Dictionary<uint, Passing> InPit,
    Dictionary<uint, Passing> PitEntrance,
    Dictionary<uint, Passing> PitExit,
    Dictionary<uint, Passing> PitSf,
    Dictionary<uint, Passing> PitOther,
    Dictionary<uint, Passing> Other,
    Dictionary<uint, LoopMetadata> LoopMetadata,
    Func<uint, bool>? IsExitRecent = null) : ICarStateChange
{
    public string Number => CarNumber;

    public CarPositionPatch? GetChanges(CarPosition state)
    {
        var patch = new CarPositionPatch { Number = CarNumber };

        // Set loop-specific flags based on loop types. Exited holds only briefly past the pit-exit
        // loop, as the pit phase does, rather than until the car's next passing a lap later.
        bool exitedLoop = PitExit.TryGetValue(state.TransponderId, out _);
        patch.IsEnteredPit = PitEntrance.TryGetValue(state.TransponderId, out _);
        patch.IsExitedPit = exitedLoop && (IsExitRecent?.Invoke(state.TransponderId) ?? true);
        patch.IsPitStartFinish = PitSf.TryGetValue(state.TransponderId, out _);

        // IsInPit follows the passing's own in-pit flag, and the pit-in and pit S/F loops put the
        // car in the pit whatever that flag says. Worked out as a level and then compared, so a
        // typed loop lacking the flag puts a car in the pit that was not already there.
        bool newIsInPit = InPit.ContainsKey(state.TransponderId)
            || patch.IsEnteredPit == true || patch.IsPitStartFinish == true;
        if (state.IsInPit != newIsInPit)
            patch.IsInPit = newIsInPit;

        // Loops only say which one the car crossed last, so the visit is entered at the pit-in
        // loop, in the pit (with no finer detail) past the pit start/finish or any in-pit loop, and
        // exited at the pit-exit loop. An event with no pit-in or pit-exit loop simply never shows
        // those. The phase never reads None while the car is shown in the pit, so a client falling
        // back to the in-pit flag on None agrees with it.
        bool inPitAfter = patch.IsInPit ?? state.IsInPit;
        PitPhase phase;
        if (patch.IsEnteredPit == true)
            phase = PitPhase.EnteredPit;
        else if (patch.IsExitedPit == true)
            phase = PitPhase.ExitedPit;
        else if (inPitAfter)
            phase = PitPhase.InPit;
        else
            phase = PitPhase.None;
        if (state.PitPhase != phase)
            patch.PitPhase = phase;

        if (Other.TryGetValue(state.TransponderId, out var otherPass) &&
            LoopMetadata.TryGetValue(otherPass.Id, out var lm) && state.LastLoopName != lm.Name)
        {
            patch.LastLoopName = lm.Name;
        }
        else
        {
            patch.LastLoopName = string.Empty;
        }

        // Keep track of laps where cars have pitted
        if (patch.IsInPit ?? false && !string.IsNullOrEmpty(patch.Number))
        {
            if (!CarLapsWithPitStops.TryGetValue(patch.Number!, out var laps))
            {
                laps = [];
                CarLapsWithPitStops[patch.Number!] = laps;
            }
            laps.Add(state.LastLapCompleted + 1);
        }

        // Check if the lap was included in a pit stop
        patch.LapIncludedPit = patch.IsInPit;
        if (!patch.IsInPit ?? false && !string.IsNullOrEmpty(patch.Number))
        {
            patch.LapIncludedPit = CarLapsWithPitStops.TryGetValue(patch.Number!, out var laps) && laps.Contains(state.LastLapCompleted);
        }

        // If previously included in a pit stop, clear it out
        if (state.LapIncludedPit && patch.LapIncludedPit == null)
        {
            patch.LapIncludedPit = false;
        }

        // Several clients only move a pit badge off "entered" or "exited" when the in-pit flag
        // arrives, so it is resent unchanged when either drops. Last, so the lap bookkeeping above
        // only ever sees a real change in the flag.
        if ((state.IsEnteredPit && patch.IsEnteredPit == false) || (state.IsExitedPit && patch.IsExitedPit == false))
            patch.IsInPit ??= inPitAfter;

        return patch;
    }
}
