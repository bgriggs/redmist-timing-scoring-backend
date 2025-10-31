using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RedMist.Database.PostgreSQL;

/// <summary>
/// Factory for creating TsContextPostgreSQL instances at design time (for EF Core migrations)
/// </summary>
public class TsContextPostgreSQLFactory : IDesignTimeDbContextFactory<TsContextPostgreSQL>
{
    public TsContextPostgreSQL CreateDbContext(string[] args)
    {
        // Enable legacy timestamp behavior for compatibility with SQL Server DateTime values
        // This must be set before any Npgsql operations
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        var optionsBuilder = new DbContextOptionsBuilder<TsContextPostgreSQL>();

        string? connectionString = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--connection" && i + 1 < args.Length)
            {
                connectionString = args[i + 1];
            }
        }

        if (string.IsNullOrEmpty(connectionString))
        {
            connectionString = "Host=localhost;Database=redmist-timing-dev;Username=postgres;Password=postgres";
            Console.WriteLine("Using default PostgreSQL connection string for design-time operations");
        }

        optionsBuilder.UseNpgsql(connectionString);
        Console.WriteLine("Configured for PostgreSQL");

        return new TsContextPostgreSQL(optionsBuilder.Options);
    }
}
