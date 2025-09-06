using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// Captures changes to be applied to the session state.
/// </summary>
public interface ISessionStateChange
{
    /// <summary>
    /// Gets changes based on the specified session state.
    /// </summary>
    /// <param name="state">The session state to which the operation will be applied. Cannot be <see langword="null"/>.</param>
    /// <returns>Changes as represented as a non-null value.</returns>
    SessionStatePatch? GetChanges(SessionState state);
}
