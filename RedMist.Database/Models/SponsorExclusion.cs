namespace RedMist.Database.Models;

/// <summary>
/// Blocks a sponsor from appearing on events run by a given organization, typically because the sponsor
/// competes with the organization running the event (e.g. ChampCar events excluding Lucky Dog and WRL).
/// One row per excluded sponsor; an organization with no rows excludes nothing.
/// </summary>
public class SponsorExclusion
{
    /// <summary>Organization running the event whose sponsor list is being filtered.</summary>
    public int OrganizationId { get; set; }

    /// <summary>Sponsor to hide from that organization's events.</summary>
    public int SponsorId { get; set; }
}
