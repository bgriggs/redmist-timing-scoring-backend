using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;

namespace RedMist.MoveLogosToCdn;

internal class Program
{
    /// <summary>
    /// Takes organization logos that are in the database and pushed them to the CDN.
    /// </summary>
    static async Task<int> Main(string[] args)
    {
        try
        {
            var builder = Host.CreateApplicationBuilder(args);
            builder.Configuration.AddUserSecrets<Program>();

            // Clear default providers and add NLog
            builder.Logging.ClearProviders();
            builder.Logging.AddNLog("NLog");

            // Configure the database connection
            string sqlConn = builder.Configuration["ConnectionStrings:Default"] ?? throw new ArgumentNullException("SQL Connection");

            // Enable legacy timestamp behavior for PostgreSQL
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

            builder.Services.AddDbContext<TsContext>(options =>
                options.UseNpgsql(sqlConn));

            builder.Services.AddHttpClient();
            builder.Services.AddTransient<AssetsCdn>();

            var host = builder.Build();
            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
            var config = host.Services.GetRequiredService<IConfiguration>();

            logger.LogInformation("Move Logos to CDN utility starting...");

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TsContext>();

            var orgs = db.Organizations.ToList();
            var defaultLogo = db.DefaultOrgImages.FirstOrDefault();

            if (defaultLogo == null || defaultLogo.ImageData == null || defaultLogo.ImageData.Length == 0)
            {
                logger.LogError("No default logo found.");
                return 1;
            }

            var assetsCdn = scope.ServiceProvider.GetRequiredService<AssetsCdn>();

            foreach (var org in orgs)
            {
                var bytes = org.Logo;
                if (org.Logo == null || org.Logo.Length == 0)
                {
                    bytes = defaultLogo.ImageData;
                    logger.LogInformation("Set default logo for organization {OrgId} - {OrgName}", org.Id, org.Name);
                }
                var result = await assetsCdn.SaveLogoAsync(org.Id, bytes!);
                if (!result)
                {
                    logger.LogWarning("Failed to save logo for organization {OrgId} - {OrgName}", org.Id, org.Name);
                }
            }

            logger.LogInformation("Logo migration completed successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }
}
