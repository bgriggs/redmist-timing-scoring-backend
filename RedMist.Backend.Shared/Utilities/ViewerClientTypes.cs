namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// The client types a viewer session can be attributed to, and the pseudo-type covering all of them.
/// </summary>
/// <remarks>
/// String constants rather than an enum because these values come from
/// <c>ClientTypeHelper.ResolveClientType</c>, which produces "iOS" - not something a C# enum member
/// can idiomatically be called - and because they are stored and read as text.
/// </remarks>
public static class ViewerClientTypes
{
    public const string IOS = "iOS";
    public const string Android = "Android";
    public const string Web = "Web";
    public const string Api = "API";

    /// <summary>
    /// The all-types series. Swept in its own right rather than summed from the others, because
    /// maxima do not add.
    /// </summary>
    public const string All = "All";

    /// <summary>The types reported individually, in the order they are presented.</summary>
    public static readonly string[] Reported = [IOS, Android, Web, Api];

    /// <summary>Every series stored, including <see cref="All"/>.</summary>
    public static readonly string[] Stored = [IOS, Android, Web, Api, All];

    /// <summary>
    /// Maps a recorded client type onto one of the reported types, folding anything unrecognized
    /// into Web.
    /// </summary>
    /// <remarks>
    /// Unrecognized values should not occur - the same helper produced them - but a report is not the
    /// place to discover that a new client id shipped. Folding keeps the series summing to the total.
    /// </remarks>
    public static string Normalize(string? clientType) =>
        Reported.FirstOrDefault(t => string.Equals(t, clientType, StringComparison.OrdinalIgnoreCase)) ?? Web;
}
