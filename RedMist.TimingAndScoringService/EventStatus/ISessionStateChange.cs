using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// Captures a change to be applied to the session state.
/// </summary>
public interface ISessionStateChange
{
    /// <summary>
    /// What data will be updated by this change.
    /// </summary>
    List<string> Targets { get; }
    /// <summary>
    /// Applies the current operation to the specified session state.
    /// </summary>
    /// <param name="state">The session state to which the operation will be applied. Cannot be <see langword="null"/>.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is <see langword="true"/> if the operation
    /// was successfully applied; otherwise, <see langword="false"/>.</returns>
    Task<bool> ApplyToState(SessionState state);
}
