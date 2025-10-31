using Microsoft.EntityFrameworkCore;
using RedMist.TimingCommon.Models.Configuration;

namespace RedMist.Database.Extensions;

/// <summary>
/// Extension methods for querying database views
/// </summary>
public static class TsContextViewExtensions
{
    /// <summary>
    /// Query the OrganizationExtView database view which includes the default logo fallback.
    /// </summary>
    /// <param name="context">The database context</param>
    /// <returns>Queryable collection of organizations from the view</returns>
    public static IQueryable<Organization> OrganizationExtView(this TsContext context)
    {
   var isPostgreSQL = context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ?? false;
        var viewName = isPostgreSQL ? "public.\"OrganizationExtView\"" : "[dbo].[OrganizationExtView]";
    return context.Organizations.FromSqlRaw($"SELECT * FROM {viewName}");
    }
}
