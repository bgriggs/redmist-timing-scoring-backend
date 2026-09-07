using Keycloak.AuthServices.Authentication;
using Keycloak.AuthServices.Authorization;
using Keycloak.AuthServices.Common;

namespace RedMist.EventManagement;

/// <summary>
/// Authentication and authorization wiring for this service.
/// </summary>
/// <remarks>
/// Extracted from Program so the tests can exercise the wiring the service actually runs rather than
/// a copy of it. The two settings below have to name the same claim, and a copy that drifts from the
/// original would let a test go on passing while every role check in production refuses every
/// caller. See <c>SiteAdminRoleTests</c>.
/// </remarks>
public static class KeycloakAuthSetup
{
    public static IServiceCollection AddRedMistKeycloakAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddKeycloakWebApiAuthentication(configuration);
        services.AddAuthorization().AddKeycloakAuthorization(options =>
        {
            options.EnableRolesMapping = RolesClaimTransformationSource.Realm;
            // Note, this should correspond to role configured with KeycloakAuthenticationOptions
            options.RoleClaimType = KeycloakConstants.RoleClaimType;
        });

        return services;
    }
}
