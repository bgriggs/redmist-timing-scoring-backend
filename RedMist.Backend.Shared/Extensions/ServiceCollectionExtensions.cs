using MessagePack.AspNetCoreMvcFormatter;
using MessagePack.Resolvers;
using Microsoft.Extensions.DependencyInjection;

namespace RedMist.Backend.Shared.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds MVC controllers with MessagePack support and content negotiation configured.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>IMvcBuilder for further configuration</returns>
    public static IMvcBuilder AddControllersWithMessagePack(this IServiceCollection services)
    {
        return services.AddControllers(options =>
        {
            // Enable content negotiation based on Accept header
            options.RespectBrowserAcceptHeader = true;
            options.ReturnHttpNotAcceptable = false; // Return first formatter if Accept header doesn't match

            // Add MessagePack formatter
            options.InputFormatters.Add(new MessagePackInputFormatter(ContractlessStandardResolver.Options));
            options.OutputFormatters.Add(new MessagePackOutputFormatter(ContractlessStandardResolver.Options));
            options.FormatterMappings.SetMediaTypeMappingForFormat("msgpack", "application/x-msgpack");
        });
    }
}
