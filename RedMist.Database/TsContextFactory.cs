using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RedMist.Database;

/// <summary>
/// Factory for creating TsContext instances at design time (for EF Core migrations)
/// </summary>
public class TsContextFactory : IDesignTimeDbContextFactory<TsContext>
{
    public TsContext CreateDbContext(string[] args)
    {
        // Enable legacy timestamp behavior for compatibility
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();

        // Check if a connection string was provided via command line args
        // Format: --connection "connection_string_here"
        string? connectionString = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--connection" && i + 1 < args.Length)
            {
                connectionString = args[i + 1];
            }
        }

        // If no connection string provided, use default
        if (string.IsNullOrEmpty(connectionString))
        {
            connectionString = "Host=localhost;Database=redmist-timing-dev;Username=postgres;Password=postgres";
            Console.WriteLine("Using default PostgreSQL connection string for design-time operations");
        }

        optionsBuilder.UseNpgsql(connectionString);
        Console.WriteLine("Configured for PostgreSQL");

        return new TsContext(optionsBuilder.Options);
    }
}
