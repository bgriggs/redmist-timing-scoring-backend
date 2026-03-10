namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// Resolves client application type from an OAuth client ID.
/// </summary>
public static class ClientTypeHelper
{
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
