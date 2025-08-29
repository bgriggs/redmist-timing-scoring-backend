using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RedMist.Backend.Shared;

public static class StartupLoggerExtensions
{
    public static void LogAssemblyInfo<T>(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILogger<T>>();
        var assembly = typeof(T).Assembly;

        var name = assembly.GetName().Name ?? "unknown";
        var version = assembly.GetName().Version?.ToString() ?? "unknown";

        logger.LogInformation("Service starting...");
        logger.LogInformation("Assembly: {AssemblyName}, Version: {Version}", name, version);
    }
}