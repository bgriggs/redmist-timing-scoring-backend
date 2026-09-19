namespace RedMist.Database.Models;

/// <summary>
/// An organization's preferences for the reports Red Mist sends it. Absent row means the defaults.
/// </summary>
/// <remarks>
/// Deliberately a table of its own rather than fields on <c>Organization</c>. That type lives in the
/// separate redmist-timing-common repository, is consumed here as a NuGet package, and is a
/// MessagePack contract that the iOS, Android and web clients all decode. A flag there would cost a
/// cross-repository release, and an older client round-tripping the object would deserialize the
/// missing member back to false and silently opt the organizer out of their own reports.
/// </remarks>
public class OrganizationReportSettings
{
    public int OrganizationId { get; set; }

    /// <summary>
    /// Whether the organization receives the post-event report. Opt-out rather than opt-in: an
    /// organization that has never heard of the feature should still get its first report.
    /// </summary>
    public bool SendPostEventReport { get; set; } = true;
}
