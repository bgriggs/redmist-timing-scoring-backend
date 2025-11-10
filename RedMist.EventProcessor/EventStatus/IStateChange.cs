namespace RedMist.EventProcessor.EventStatus;

/// <summary>
/// Non-generic marker interface for all state changes, allowing heterogeneous collections.
/// </summary>
public interface IStateChange
{
}

/// <summary>
/// Base interface for state change operations that can generate patches based on current state.
/// </summary>
/// <typeparam name="TState">The type of state that this change can be applied to.</typeparam>
/// <typeparam name="TPatch">The type of patch that represents the changes.</typeparam>
public interface IStateChange<in TState, out TPatch> : IStateChange
{
    /// <summary>
    /// Gets changes relative to the specified state.
    /// </summary>
    /// <param name="state">The current state to which the operation will base upon. Cannot be <see langword="null"/>.</param>
    /// <returns>Changes represented as a patch, or null if no changes are applicable.</returns>
    TPatch? GetChanges(TState state);
}