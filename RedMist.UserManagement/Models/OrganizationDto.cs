using System.ComponentModel.DataAnnotations;

namespace RedMist.UserManagement.Models;

public class OrganizationDto
{
    public int Id { get; set; }

    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [Length(3, 8)]
    public string ShortName { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? Website { get; set; }

    public byte[]? Logo { get; set; }
}
