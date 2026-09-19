using Microsoft.EntityFrameworkCore;
using RedMist.Database;

namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// Whether a signed-in person belongs to an organization.
/// </summary>
/// <remarks>
/// One implementation because the comparison is the whole thing, and it has to be case-insensitive.
/// PostgreSQL compares text case-sensitively, so a Keycloak account whose username differs in case
/// from the mapping row - <c>Brian@example.com</c> against <c>brian@example.com</c> - is the same
/// person to everyone except the database. Everywhere else in the system usernames and roles are
/// already matched without regard to case.
/// </remarks>
public static class OrganizationMembership
{
    /// <summary>
    /// Whether <paramref name="username"/> is mapped to <paramref name="organizationId"/>.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="username">The signed-in user's Keycloak username, normally an email address.</param>
    /// <param name="organizationId">The organization being checked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<bool> IsMemberAsync(TsContext context, string? username, int organizationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return Task.FromResult(false);
        }

        var normalized = username.ToLowerInvariant();
        return context.UserOrganizationMappings
            .AsNoTracking()
            .AnyAsync(u => u.OrganizationId == organizationId && u.Username.ToLower() == normalized, cancellationToken);
    }
}
