using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace RedMist.Database.Extensions;

/// <summary>
/// Extension methods for configuring PostgreSQL database
/// </summary>
public static class DatabaseProviderExtensions
{
    /// <summary>
    /// Configure the database context to use PostgreSQL
    /// </summary>
    public static DbContextOptionsBuilder ConfigureDatabase(this DbContextOptionsBuilder optionsBuilder, IConfiguration configuration)
    {
        var connectionString = configuration["ConnectionStrings:PostgreSQL"]
            ?? configuration["ConnectionStrings:Default"]
            ?? throw new InvalidOperationException("PostgreSQL connection string not found");

        return optionsBuilder.UseNpgsql(connectionString);
    }

    /// <summary>
    /// Configure the database context factory to use PostgreSQL
    /// </summary>
    public static IServiceCollection AddTsContextWithProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContextFactory<TsContext>(options =>
        {
            options.ConfigureDatabase(configuration);
        });

        return services;
    }

    /// <summary>
    /// Check if the current provider is PostgreSQL
    /// </summary>
    public static bool IsNpgsql(this DbContext context)
    {
        return context.Database.IsNpgsql();
    }
}
