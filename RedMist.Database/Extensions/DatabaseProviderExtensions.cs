using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace RedMist.Database.Extensions;

/// <summary>
/// Extension methods for configuring database providers
/// </summary>
public static class DatabaseProviderExtensions
{
    public const string SqlServerProvider = "SqlServer";
    public const string PostgreSqlProvider = "PostgreSQL";

    /// <summary>
    /// Configure the database context to use the specified provider from configuration
    /// </summary>
    public static DbContextOptionsBuilder ConfigureDatabase(this DbContextOptionsBuilder optionsBuilder, IConfiguration configuration, string? providerOverride = null)
    {
        var provider = providerOverride ?? configuration["DatabaseProvider"] ?? SqlServerProvider;
        var connectionString = configuration[$"ConnectionStrings:{provider}"]
            ?? configuration["ConnectionStrings:Default"]
            ?? throw new InvalidOperationException($"Connection string for {provider} not found");

        return provider.ToUpperInvariant() switch
        {
            "SQLSERVER" => optionsBuilder.UseSqlServer(connectionString),
            "POSTGRESQL" => optionsBuilder.UseNpgsql(connectionString),
            _ => throw new InvalidOperationException($"Unsupported database provider: {provider}")
        };
    }

    /// <summary>
    /// Configure the database context factory to use the specified provider
    /// </summary>
    public static IServiceCollection AddTsContextWithProvider(this IServiceCollection services, IConfiguration configuration, string? providerOverride = null)
    {
        var provider = providerOverride ?? configuration["DatabaseProvider"] ?? SqlServerProvider;

        services.AddDbContextFactory<TsContext>(options =>
        {
            options.ConfigureDatabase(configuration, providerOverride);
        });

        return services;
    }

    /// <summary>
    /// Check if the current provider is SQL Server
    /// </summary>
    public static bool IsSqlServer(this DbContext context)
    {
        return context.Database.IsSqlServer();
    }

    /// <summary>
    /// Check if the current provider is PostgreSQL
    /// </summary>
    public static bool IsNpgsql(this DbContext context)
    {
        return context.Database.IsNpgsql();
    }
}
