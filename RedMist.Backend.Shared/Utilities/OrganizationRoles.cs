using RedMist.Database.Models;
using System.Linq.Expressions;

namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// What a role in <see cref="UserOrganizationMapping"/> means.
/// </summary>
/// <remarks>
/// <para>
/// One place, because the question "is this role an administrator" was being asked in three
/// different spellings across two private constants that happened to hold the same string, and a
/// client re-implementing it in another language would have been the fourth. A rule stated four
/// times is a rule that will eventually be four different rules.
/// </para>
/// <para>
/// Both spellings are in the data - organization creation writes "Admin", the administrator screen
/// writes "admin" - so the comparison cannot be exact. It does not trim: <c>Role</c> is never free
/// text, every write in the solution is one of those two constants, so whitespace is not reachable
/// and trimming would only hide a row that should never exist.
/// </para>
/// </remarks>
public static class OrganizationRoles
{
    /// <summary>The stored value that confers administration.</summary>
    public const string Admin = "admin";

    /// <summary>
    /// Administrator mappings, as a query predicate.
    /// </summary>
    /// <remarks>
    /// A second form is unavoidable: <see cref="IsAdmin"/> cannot be called inside an expression
    /// tree, and <c>ToLower()</c> is what Npgsql translates to SQL <c>lower()</c>. A test drives
    /// both over the same inputs, so the two cannot drift in what they accept - but it compares
    /// this expression as the CLR runs it, which is the only way a test can execute it without a
    /// database. The SQL side is settled by inspection: Npgsql renders it <c>lower("Role")</c>,
    /// which folds ASCII identically to OrdinalIgnoreCase.
    ///
    /// <c>Role</c> is dereferenced unguarded. In SQL a null yields null and the row drops out,
    /// agreeing with <see cref="IsAdmin"/>; the column is non-nullable, so neither is reachable.
    /// </remarks>
    public static readonly Expression<Func<UserOrganizationMapping, bool>> AdministratorMappings =
        m => m.Role.ToLower() == Admin;

    /// <summary>
    /// Whether a stored role confers administration. For values already in memory.
    /// </summary>
    public static bool IsAdmin(string? role) => string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase);
}
