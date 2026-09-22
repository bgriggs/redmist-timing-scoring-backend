using RedMist.Backend.Shared.Utilities;

namespace RedMist.PostEventReports.Email;

/// <summary>
/// The colors the report uses, chosen to survive being inverted.
/// </summary>
/// <remarks>
/// <para>
/// Every series color is a mid-tone that reads against both a white and a near-black background. That,
/// rather than the dark-mode CSS, is the real defence: Gmail's mobile apps force-invert a message and
/// ignore <c>prefers-color-scheme</c> entirely, so a palette that only works in one mode will be
/// wrong for a large share of readers no matter what the stylesheet says.
/// </para>
/// <para>
/// For the same reason no number is ever printed on top of a colored bar. An inverting client flips
/// the text and leaves the bar alone, and white-on-yellow is the result.
/// </para>
/// </remarks>
public static class EmailPalette
{
    public const string Accent = "#C0392B";

    public const string TextLight = "#2B3240";
    public const string MutedLight = "#5A6472";
    public const string PageLight = "#F4F5F7";
    public const string CardLight = "#FFFFFF";
    public const string RuleLight = "#E0E3E8";
    public const string TrackLight = "#E6E8EC";

    public const string TextDark = "#E7EAF0";
    public const string MutedDark = "#9AA3B2";
    public const string PageDark = "#141820";
    public const string CardDark = "#1F242E";
    public const string RuleDark = "#2C3340";
    public const string TrackDark = "#2C3340";

    /// <summary>The bar color for a client type.</summary>
    public static string ForClientType(string clientType) => clientType switch
    {
        ViewerClientTypes.IOS => "#4F8FE0",
        ViewerClientTypes.Android => "#3DA35D",
        ViewerClientTypes.Web => "#E0A33D",
        ViewerClientTypes.Api => "#8E6BD1",
        _ => "#8A93A0",
    };
}
