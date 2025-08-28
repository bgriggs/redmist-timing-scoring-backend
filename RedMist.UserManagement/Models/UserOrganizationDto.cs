using System.ComponentModel.DataAnnotations;

namespace RedMist.UserManagement.Models;

public class UserOrganizationDto
{
    public int OrganizationId { get; set; }

    [Required]
    [MaxLength(20)]
    public string Role { get; set; } = string.Empty;
}