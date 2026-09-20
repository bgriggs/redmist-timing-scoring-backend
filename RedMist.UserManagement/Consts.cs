namespace RedMist.UserManagement;

public static class Consts
{
    public const string RELAY_CLIENT_ID = "relay-{0}";
    public const string API_CLIENT_ID = "api-{0}";
    /// <summary>
    /// The role an organization's creator is given. Deliberately a different spelling from the one
    /// the administrator screen writes; whether a stored role confers administration is decided by
    /// RedMist.Backend.Shared.Utilities.OrganizationRoles, which accepts both.
    /// </summary>
    public const string DEFAULT_ORGANIZATION_ROLE = "Admin";
}
