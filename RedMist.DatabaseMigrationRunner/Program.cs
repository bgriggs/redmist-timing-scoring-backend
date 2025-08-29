using Microsoft.EntityFrameworkCore;
using NLog.Extensions.Logging;
using RedMist.Backend.Shared;
using RedMist.Database;

namespace RedMist.DatabaseMigrationRunner;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddNLog("NLog");

        // Configure the database connection
        string sqlConn = builder.Configuration.GetConnectionString("Default")
            ?? throw new ArgumentNullException("ConnectionStrings:Default is required");

        builder.Services.AddDbContext<TsContext>(options => 
            options.UseSqlServer(sqlConn)
                   //.EnableSensitiveDataLogging() // Only in development
                   .LogTo(Console.WriteLine, LogLevel.Information));

        var app = builder.Build();
        app.LogAssemblyInfo<Program>();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            Console.Title = "Event Management";
            app.UseDeveloperExceptionPage();
        }

        // Run migrations and exit
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Starting database migrations...");

        try
        {
            using var scope = app.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TsContext>();
            await context.Database.MigrateAsync();
            logger.LogInformation("Migrations completed successfully.");
            await Task.Delay(3000);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while applying migrations.");
            return 1;
        }

        return 0;
    }
}
