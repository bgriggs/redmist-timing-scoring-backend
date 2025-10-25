using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace RedMist.Backend.Shared.Services;

/// <summary>
/// Extension methods for registering the custom mediator.
/// </summary>
public static class MediatorExtensions
{
    /// <summary>
    /// Adds the custom mediator and scans for notification handlers in the specified assemblies.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="assemblies">Assemblies to scan for handlers</param>
    /// <returns>The service collection</returns>
    public static IServiceCollection AddMediator(this IServiceCollection services, params Assembly[] assemblies)
    {
        services.AddSingleton<IMediator, Mediator>();

        // If no assemblies provided, scan the calling assembly
        if (assemblies.Length == 0)
        {
            assemblies = [Assembly.GetCallingAssembly()];
        }

        // Scan assemblies for notification handlers
        foreach (var assembly in assemblies)
        {
            var handlerTypes = assembly.GetTypes()
                .Where(type => !type.IsAbstract && !type.IsInterface)
                .Where(type => type.GetInterfaces()
                    .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INotificationHandler<>)))
                .ToList();

            foreach (var handlerType in handlerTypes)
            {
                var handlerInterfaces = handlerType.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INotificationHandler<>));

                foreach (var handlerInterface in handlerInterfaces)
                {
                    services.AddTransient(handlerInterface, handlerType);
                }
            }
        }

        return services;
    }

    /// <summary>
    /// Adds the custom mediator and scans for notification handlers in the specified types.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection</returns>
    public static IServiceCollection AddMediatorFromAssemblyContaining<T>(this IServiceCollection services)
    {
        return services.AddMediator(typeof(T).Assembly);
    }

    /// <summary>
    /// Adds the custom mediator and scans for notification handlers in multiple assemblies by marker types.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="markerTypes">Types from assemblies to scan</param>
    /// <returns>The service collection</returns>
    public static IServiceCollection AddMediatorFromAssembliesContaining(this IServiceCollection services, params Type[] markerTypes)
    {
        var assemblies = markerTypes.Select(t => t.Assembly).Distinct().ToArray();
        return services.AddMediator(assemblies);
    }
}