using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace RedMist.Database.Models;

[PrimaryKey(nameof(Username), nameof(OrganizationId))]
public class UserOrganizationMapping
{
    [Required]
    [MaxLength(200)]
    public string Username { get; set; } = string.Empty;
    [Required]
    public int OrganizationId { get; set; }
    [Required]
    [MaxLength(20)]
    public string Role { get; set; } = string.Empty;
}
