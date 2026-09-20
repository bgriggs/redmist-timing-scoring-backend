using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using System.Security.Claims;

namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// The organizations a caller is allowed to administer, whoever the caller is.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of caller reach the same endpoints. A relay or API client authenticates as itself: a
/// Keycloak client whose id is the organization's own <c>ClientId</c>, <c>relay-x</c> or
/// <c>api-x</c>, with no person behind it. A signed-in person authenticates against the web client,
/// whose <c>client_id</c> is that of the site rather than of any organization, and is tied to
/// organizations only through <c>UserOrganizationMappings</c>.
/// </para>
/// <para>
/// Endpoints used to resolve the caller by matching <c>client_id</c> against
/// <c>Organization.ClientId</c> directly, which silently answers "no organizations" for every human
/// being. Resolving both here keeps one rule rather than a machine convention and a person
/// convention that drift apart.
/// </para>
/// <para>
/// The client id is tried first, so a machine token costs one indexed lookup and behaves exactly as
/// it did before. Usernames are matched without regard to case for the reason given in
/// <see cref="OrganizationMembership"/>.
/// </para>
/// <para>
/// Administration, not mere membership. A mapping row is not by itself permission to reconfigure an
/// organization: these endpoints hand back Flagtronics and X2 credentials, rewrite timing sources,
/// and edit the administrator list itself - so a non-admin member reaching them could promote
/// themselves and remove the incumbents in one request. Roles are compared without regard to case
/// because both spellings are in the data.
/// </para>
/// </remarks>
public static class CallerOrganizations
{
    /// <summary>
    /// Every organization this caller may administer, ascending. Empty when the caller has none.
    /// </summary>
    public static async Task<List<int>> ResolveAsync(TsContext context, ClaimsPrincipal? user,
        CancellationToken cancellationToken = default)
    {
        var clientId = user?.FindFirstValue("client_id");
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            var owned = await context.Organizations
                .AsNoTracking()
                .Where(o => o.ClientId == clientId)
                .Select(o => o.Id)
                .ToListAsync(cancellationToken);
            if (owned.Count > 0)
            {
                return owned;
            }
        }

        var username = user?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username))
        {
            return [];
        }

        var normalized = username.ToLowerInvariant();
        return await context.UserOrganizationMappings
            .AsNoTracking()
            .Where(u => u.Username.ToLower() == normalized)
            .Where(OrganizationRoles.AdministratorMappings)
            .Select(u => u.OrganizationId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Whether this caller may administer <paramref name="organizationId"/>.
    /// </summary>
    /// <remarks>
    /// The organization is always named by the caller rather than inferred. A relay holds exactly
    /// one and could have had it guessed for it, but then the same endpoint would mean "my
    /// organization" to one caller and "the one I asked for" to another, and the guess would have to
    /// pick arbitrarily for a person holding several - which is the defect that made the old
    /// LoadUserOrganization unusable. Callers discover their ids once and pass them.
    /// </remarks>
    public static async Task<bool> IsPermittedAsync(TsContext context, ClaimsPrincipal? user, int organizationId,
        CancellationToken cancellationToken = default)
        => (await ResolveAsync(context, user, cancellationToken)).Contains(organizationId);
}
