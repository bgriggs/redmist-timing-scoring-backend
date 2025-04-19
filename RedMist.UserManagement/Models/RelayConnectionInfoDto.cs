namespace RedMist.UserManagement.Models;

public class RelayConnectionInfoDto
{
    public int OrgId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}
