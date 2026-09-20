using System.ComponentModel.DataAnnotations;

namespace RedMist.UserManagement.Models;

public class UserOrganizationDto
{
    public int OrganizationId { get; set; }

    /// <summary>
    /// The organization's display name.
    /// </summary>
    /// <remarks>
    /// Carried here so a client holding more than one membership can name them without a second
    /// call per organization.
    /// </remarks>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The organization's Keycloak client id.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// The organization's website, if it has one.
    /// </summary>
    public string? Website { get; set; }

    [Required]
    [MaxLength(20)]
    public string Role { get; set; } = string.Empty;
}