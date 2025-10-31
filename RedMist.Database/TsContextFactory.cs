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
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();

        // Check if a connection string was provided via command line args
        // Format: --connection "connection_string_here"
        string? connectionString = null;
        bool usePostgreSQL = true; // Default to PostgreSQL for new migrations

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--connection" && i + 1 < args.Length)
            {
                connectionString = args[i + 1];
            }
            else if (args[i] == "--provider" && i + 1 < args.Length)
            {
                usePostgreSQL = args[i + 1].Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase);
            }
        }

        // If no connection string provided, use default based on provider
        if (string.IsNullOrEmpty(connectionString))
        {
            if (usePostgreSQL)
            {
                connectionString = "Host=localhost;Database=redmist-timing-dev;Username=postgres;Password=";
                Console.WriteLine("Using default PostgreSQL connection string for design-time operations");
            }
            else
            {
                connectionString = "Server=localhost;Database=redmist-timing-dev;User Id=sa;Password=;TrustServerCertificate=True";
                Console.WriteLine("Using default SQL Server connection string for design-time operations");
            }
        }

        // Configure the appropriate provider
        if (usePostgreSQL)
        {
            optionsBuilder.UseNpgsql(connectionString);
            Console.WriteLine("Configured for PostgreSQL");
        }
        else
        {
            optionsBuilder.UseSqlServer(connectionString);
            Console.WriteLine("Configured for SQL Server");
        }

        return new TsContext(optionsBuilder.Options);
    }
}
