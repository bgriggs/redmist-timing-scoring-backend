namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// Resolves client application type from an OAuth client ID.
/// </summary>
public static class ClientTypeHelper
{
    /// <summary>
    /// The live-count bucket for a phone in In Vehicle Driver mode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bucket of its own, not an overlap: a phone in driver mode is counted here and NOT also under
    /// iOS or Android, so the relay can add it to its total without counting the phone twice. The
    /// relay matches this with an exact, case-sensitive comparison, so the spelling is a contract.
    /// </para>
    /// <para>
    /// It describes what the connection is doing, not what it is running on - which is why it is
    /// never returned by <see cref="ResolveClientType"/> and is written only to the live per-event
    /// hash. Anything that needs the device, like the post-event report, has to recover it from the
    /// connection record rather than from this value.
    /// </para>
    /// </remarks>
    public const string InCar = "InCar";

    /// <summary>
    /// Determines the client application type from the OAuth client ID (azp claim).
    /// </summary>
    /// <remarks>
    /// Known client IDs: redmist-ios-ui, redmist-android-ui, redmist-browser-ui, api-*.
    /// </remarks>
    public static string ResolveClientType(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId))
            return "Web";

        if (clientId.StartsWith("api-", StringComparison.OrdinalIgnoreCase))
            return "API";

        return clientId switch
        {
            "redmist-ios-ui" => "iOS",
            "redmist-android-ui" => "Android",
            "redmist-browser-ui" => "Web",
            _ => "Web"
        };
    }
}
