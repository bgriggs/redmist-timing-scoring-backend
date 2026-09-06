using System.ComponentModel.DataAnnotations;

namespace RedMist.UserManagement.Models;

public class OrganizationDto
{
    public int Id { get; set; }

    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Bounded loosely here on purpose: the real rule is applied to the sanitized value by the
    /// controller, so that an availability check and creation cannot disagree about the same name.
    /// A tight bound on the raw value would reject "SCCA - SF" before the controller ever sees it,
    /// after the availability check had already reported on "scca-sf".
    /// </summary>
    [MaxLength(64)]
    public string ShortName { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? Website { get; set; }

    public byte[]? Logo { get; set; }

    [MaxLength(255)]
    public string ClientId { get; set; } = string.Empty;
}
