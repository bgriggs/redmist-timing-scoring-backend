using RedMist.TimingCommon.Models;
using System.Collections.Immutable;

namespace RedMist.EventProcessor.EventStatus;

public record PatchUpdates(ImmutableList<SessionStatePatch> SessionPatches, ImmutableList<CarPositionPatch> CarPatches)
{
}
