using BigMission.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.TimingCommon.Models;
using RedMist.UserManagement.Models;

namespace RedMist.UserManagement.Controllers;

/// <summary>
/// Base controller for user and organization management operations.
/// Provides endpoints for managing user-organization relationships, organization creation, and Keycloak integration.
/// </summary>
/// <remarks>
/// This is an abstract base controller inherited by versioned controllers.
/// Handles organization provisioning including Keycloak relay client creation.
/// </remarks>
[ApiController]
[Authorize]
public abstract class OrganizationControllerBase : ControllerBase
{
    protected readonly IDbContextFactory<TsContext> tsContext;
    protected readonly IConfiguration configuration;
    private readonly AssetsCdn assetsCdn;
    protected readonly string keycloakUrl;
    protected readonly string clientId;
    protected readonly string clientSecret;
    protected readonly string realm;
    protected ILogger Logger { get; }


    /// <summary>
    /// Initializes a new instance of the <see cref="OrganizationControllerBase"/> class.
    /// </summary>
    /// <param name="loggerFactory">Factory to create loggers.</param>
    /// <param name="tsContext">Database context factory for timing and scoring data.</param>
    /// <param name="configuration">Application configuration containing Keycloak settings.</param>
    /// <param name="assetsCdn"></param>
    /// <exception cref="InvalidOperationException">Thrown when required Keycloak configuration is missing.</exception>
    protected OrganizationControllerBase(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, 
        IConfiguration configuration, AssetsCdn assetsCdn)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.configuration = configuration;
        this.assetsCdn = assetsCdn;

        keycloakUrl = configuration["Keycloak:AuthServerUrl"] ?? throw new InvalidOperationException("Keycloak URL is not configured.");
        clientId = configuration["Keycloak:ClientId"] ?? throw new InvalidOperationException("Keycloak Client ID is not configured.");
        clientSecret = configuration["Keycloak:ClientSecret"] ?? throw new InvalidOperationException("Keycloak Client Secret is not configured.");
        realm = configuration["Keycloak:Realm"] ?? throw new InvalidOperationException("Keycloak Realm is not configured.");
    }


    /// <summary>
    /// Loads the organization associated with the authenticated user.
    /// </summary>
    /// <returns>Organization details including ID, name, website, and logo.</returns>
    /// <response code="200">Returns the organization details.</response>
    /// <response code="404">User organization mapping or organization not found.</response>
    /// <remarks>
    /// The user's identity (username) is extracted from authentication claims to find their organization.
    /// </remarks>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType<OrganizationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<OrganizationDto>> LoadUserOrganization()
    {
        Logger.LogMethodEntry();
        var clientId = User.Identity?.Name;
        if (string.IsNullOrEmpty(clientId))
        {
            return NotFound("Client Identity not found in user claims.");
        }

        using var context = await tsContext.CreateDbContextAsync();

        var userOrganization = await context.UserOrganizationMappings.FirstOrDefaultAsync(u => u.Username == clientId);
        if (userOrganization == null)
        {
            return NotFound("User organization mapping not found.");
        }

        var org = await context.Organizations
            .Where(o => o.Id == userOrganization.OrganizationId)
            .Select(o => new { o.Id, o.Name, o.Website, o.Logo })
            .FirstOrDefaultAsync();

        if (org == null)
        {
            return NotFound($"Organization with ID {userOrganization.OrganizationId} not found.");
        }

        byte[] defaultLogo = [];
        if (org.Logo == null)
        {
            defaultLogo = context.DefaultOrgImages.FirstOrDefault()?.ImageData ?? [];
        }

        return new OrganizationDto
        {
            Id = org.Id,
            Name = org.Name,
            Website = org.Website,
            Logo = org.Logo ?? defaultLogo
        };
    }

    /// <summary>
    /// Loads all organization roles for the authenticated user.
    /// </summary>
    /// <returns>A list of organization IDs and roles that the user belongs to.</returns>
    /// <response code="200">Returns the list of user organization roles.</response>
    /// <response code="404">User identity not found in claims.</response>
    /// <remarks>
    /// Users may belong to multiple organizations with different roles.
    /// </remarks>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType<List<UserOrganizationDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<List<UserOrganizationDto>>> LoadUserOrganizationRoles()
    {
        Logger.LogMethodEntry();
        var clientId = User.Identity?.Name;
        if (string.IsNullOrEmpty(clientId))
        {
            return NotFound("Client Identity not found in user claims.");
        }

        using var context = await tsContext.CreateDbContextAsync();

        var userOrganizations = await context.UserOrganizationMappings
            .Where(u => u.Username.ToUpper() == clientId.ToUpper())
            .Join(context.Organizations, uom => uom.OrganizationId, org => org.Id,
                (uom, org) => new UserOrganizationDto
                {
                    OrganizationId = org.Id,
                    Role = uom.Role
                })
              .ToListAsync();

        return userOrganizations;
    }

    /// <summary>
    /// Checks if a relay client name already exists in Keycloak.
    /// </summary>
    /// <param name="name">The relay client name to check.</param>
    /// <returns>True if the client name exists, false otherwise.</returns>
    /// <response code="200">Returns boolean indicating existence.</response>
    /// <remarks>
    /// Used to validate organization short names before creating a new organization.
    /// Relay client IDs follow the format: relay-{name}
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public virtual async Task<ActionResult<bool>> RelayClientNameExists(string name)
    {
        Logger.LogMethodInfo("RelayClientNameExists {name}", name);
        var clientId = string.Format(Consts.RELAY_CLIENT_ID, name);
        var client = await LoadKeycloakClientAsync(clientId);
        return client != null;
    }

    /// <summary>
    /// Creates a new organization for the authenticated user and provisions necessary infrastructure.
    /// </summary>
    /// <param name="newOrganization">The organization details to create.</param>
    /// <returns>The ID of the newly created organization.</returns>
    /// <response code="200">Returns the new organization ID.</response>
    /// <response code="404">User identity not found in claims.</response>
    /// <remarks>
    /// <para>This endpoint performs the following operations:</para>
    /// <list type="number">
    /// <item>Creates the organization in the database</item>
    /// <item>Associates the authenticated user with the organization</item>
    /// <item>Provisions a Keycloak relay client for data ingestion</item>
    /// <item>Assigns appropriate service account roles</item>
    /// </list>
    /// <para>If no logo is provided, a default image will be used.</para>
    /// </remarks>
    [HttpPost]
    [Produces("application/json")]
    [ProducesResponseType<int>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<int>> SaveNewOrganization(OrganizationDto newOrganization)
    {
        Logger.LogMethodInfo("SaveNewOrganization {organization}", newOrganization.Name);

        var clientId = User.Identity?.Name;
        if (string.IsNullOrEmpty(clientId))
        {
            return NotFound("Client ID not found in user claims.");
        }

        using var context = await tsContext.CreateDbContextAsync();

        // Create a new organization
        var organization = new Organization
        {
            Name = newOrganization.Name,
            ClientId = string.Format(Consts.RELAY_CLIENT_ID, newOrganization.ShortName),
            ShortName = newOrganization.ShortName,
            Website = newOrganization.Website,
            Logo = newOrganization.Logo,
            ControlLogType = "Default",
            ControlLogParams = string.Empty
        };

        // When there isn't data for an image, set to null to use default image
        if (organization.Logo != null && organization.Logo.Length < 2)
        {
            organization.Logo = null;
        }

        context.Organizations.Add(organization);
        await context.SaveChangesAsync();
        Logger.LogInformation("New organization created with ID {organizationId}", organization.Id);

        // Update organization logo in CDN if provided
        await UpdateLogoInCdnAsync(context, organization);

        // Add user to organization
        var userOrganization = new UserOrganizationMapping
        {
            Username = clientId,
            OrganizationId = organization.Id,
            Role = Consts.DEFAULT_ORGANIZATION_ROLE
        };
        context.UserOrganizationMappings.Add(userOrganization);
        await context.SaveChangesAsync();
        Logger.LogInformation("User {clientId} mapped to organization {organizationId} with role {role}",
            clientId, organization.Id, userOrganization.Role);

        // Provision Keycloak relay client
        Logger.LogInformation("Creating Keycloak relay client for organization {organizationId}...", organization.Id);
        var relayClientId = await CreateRelayClientAsync(newOrganization.ShortName);
        Logger.LogInformation("Relay client created with ID {relayClientId}", relayClientId);

        return Ok(organization.Id);
    }

    private async Task UpdateLogoInCdnAsync(TsContext context, Organization organization)
    {
        if (organization.Logo != null && organization.Logo.Length > 2)
        {
            Logger.LogInformation("Uploading organization logo to CDN for organization ID {organizationId}...", organization.Id);
            await assetsCdn.SaveLogoAsync(organization.Id, organization.Logo);
            Logger.LogInformation("Organization logo uploaded to CDN for organization ID {organizationId}", organization.Id);
        }
        else // Save default image
        {
            var defaultLogo = await context.DefaultOrgImages.FirstOrDefaultAsync();
            if (defaultLogo != null)
            {
                Logger.LogInformation("Uploading default organization logo to CDN for organization ID {organizationId}...", organization.Id);
                await assetsCdn.SaveLogoAsync(organization.Id, defaultLogo.ImageData);
                Logger.LogInformation("Default organization logo uploaded to CDN for organization ID {organizationId}", organization.Id);
            }
        }
    }

    /// <summary>
    /// Creates a Keycloak service account client for relay data ingestion.
    /// </summary>
    /// <param name="name">The short name for the organization (used in client ID).</param>
    /// <returns>The Keycloak client ID, or null if creation failed.</returns>
    /// <remarks>
    /// <para>The relay client is configured with:</para>
    /// <list type="bullet">
    /// <item>Service account authentication (client credentials flow)</item>
    /// <item>Relay service role assignment</item>
    /// <item>OpenID Connect protocol</item>
    /// </list>
    /// </remarks>
    protected async Task<string?> CreateRelayClientAsync(string name)
    {
        using HttpClient httpClient = await GetHttpClient();
        var keycloak = new KeycloakClient(keycloakUrl, httpClient);
        var clientId = string.Format(Consts.RELAY_CLIENT_ID, name);

        var client = new ClientRepresentation
        {
            ClientId = clientId,
            Name = "Relay Client",
            Enabled = true,
            Protocol = "openid-connect",
            PublicClient = false,
            SurrogateAuthRequired = false,
            AlwaysDisplayInConsole = false,
            ClientAuthenticatorType = "client-secret",
            RedirectUris = ["/*"],
            WebOrigins = ["/*"],
            ConsentRequired = false,
            StandardFlowEnabled = true,
            ImplicitFlowEnabled = false,
            DirectAccessGrantsEnabled = false,
            ServiceAccountsEnabled = true,
            AuthorizationServicesEnabled = false,
            FrontchannelLogout = true,
            FullScopeAllowed = true,
            RootUrl = string.Empty,
            Attributes = new Dictionary<string, string>
            {
                { "oidc.ciba.grant.enabled", "false" },
                { "backchannel.logout.session.required", "true" },
                { "display.on.consent.screen", "false" },
                { "oauth2.device.authorization.grant.enabled", "false" },
                { "backchannel.logout.revoke.offline.tokens", "false" }
            }
        };
        await keycloak.ClientsPOST3Async(client, realm);

        Logger.LogInformation("Created client with ID: {clientId}", clientId);
        client = await LoadKeycloakClientAsync(clientId);
        Logger.LogInformation("Loaded client with ID: {clientId}", client?.Id);
        if (client == null)
        {
            Logger.LogError("Failed to create or load Keycloak client {clientId}", clientId);
            return null;
        }

        var relayRole = await LoadKeycloakRoleAsync("relay-svc");
        if (relayRole != null)
        {
            Logger.LogInformation("Assigning relay role to service account user for client {clientId}", clientId);
            var serviceAccountUserId = await keycloak.ServiceAccountUserAsync(realm, client.Id);
            var roles = new List<RoleRepresentation> { relayRole };
            await keycloak.RealmPOST5Async(roles, realm, serviceAccountUserId.Id);
        }
        else
        {
            Logger.LogWarning("Relay role relay-svc not found.");
        }

        return client?.Id;
    }

    /// <summary>
    /// Updates organization details.
    /// </summary>
    /// <param name="organizationDto">The organization with updated details.</param>
    /// <returns>No content on success.</returns>
    /// <response code="200">Organization updated successfully.</response>
    /// <response code="404">Organization not found.</response>
    /// <response code="401">User is not authorized to update this organization.</response>
    /// <remarks>
    /// Users can only update organizations they belong to.
    /// Updatable fields: Name, Website, Logo.
    /// </remarks>
    [HttpPost]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public virtual async Task<ActionResult> UpdateOrganization(OrganizationDto organizationDto)
    {
        Logger.LogMethodInfo($"UpdateOrganization {organizationDto.Name}");

        // Ensure user is authorized for this organization
        if (!await ValidateUserOrganization(organizationDto.Id))
        {
            return Unauthorized("User is not authorized to update this organization.");
        }

        using var context = await tsContext.CreateDbContextAsync();
        var organization = await context.Organizations.FindAsync(organizationDto.Id);
        if (organization == null)
        {
            return NotFound($"Organization with ID {organizationDto.Id} not found.");
        }

        organization.Name = organizationDto.Name;
        organization.Website = organizationDto.Website;
        organization.Logo = organizationDto.Logo;
        context.Organizations.Update(organization);
        await context.SaveChangesAsync();

        // Update organization logo in CDN if provided
        await UpdateLogoInCdnAsync(context, organization);

        return Ok();
    }

    /// <summary>
    /// Loads the relay connection information for an organization.
    /// Provides the client ID and secret needed for relay software to connect.
    /// </summary>
    /// <param name="organizationId">The unique identifier of the organization.</param>
    /// <returns>Relay connection details including client ID and secret.</returns>
    /// <response code="200">Returns relay connection information.</response>
    /// <response code="404">Organization or relay client not found.</response>
    /// <response code="401">User is not authorized to access this organization.</response>
    /// <remarks>
    /// This endpoint provides sensitive credentials and should only be accessible to authorized organization members.
    /// The relay client is used by track-side software to send timing data to the cloud.
    /// </remarks>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType<RelayConnectionInfoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public virtual async Task<ActionResult<RelayConnectionInfoDto>> LoadRelayConnection(int organizationId)
    {
        Logger.LogMethodInfo($"LoadRelayConnection for organization {organizationId}");
        // Ensure user is authorized for this organization
        if (!await ValidateUserOrganization(organizationId))
        {
            return Unauthorized("User is not authorized to access this organization.");
        }
        using var context = await tsContext.CreateDbContextAsync();
        var organization = await context.Organizations.FindAsync(organizationId);
        if (organization == null)
        {
            return NotFound($"Organization with ID {organizationId} not found.");
        }
        var clientId = string.Format(Consts.RELAY_CLIENT_ID, organization.ShortName);
        var clientSecret = await LoadKeycloakServiceSecret(clientId);
        if (clientSecret == null)
        {
            return NotFound($"Relay client secret for organization {organizationId} not found.");
        }
        return new RelayConnectionInfoDto
        {
            OrgId = organizationId,
            ClientId = clientId,
            ClientSecret = clientSecret
        };
    }

    /// <summary>
    /// Validates that the authenticated user is authorized to access the specified organization.
    /// </summary>
    /// <param name="organizationId">The unique identifier of the organization.</param>
    /// <returns>True if the user is authorized, false otherwise.</returns>
    protected async Task<bool> ValidateUserOrganization(int organizationId)
    {
        var clientId = User.Identity?.Name;
        if (string.IsNullOrEmpty(clientId))
        {
            return false;
        }

        using var context = await tsContext.CreateDbContextAsync();
        var userOrganization = await context.UserOrganizationMappings
            .Where(u => u.Username == clientId && u.OrganizationId == organizationId)
            .Select(u => u.Role)
            .FirstOrDefaultAsync();
        return userOrganization != null;
    }

    /// <summary>
    /// Creates an HTTP client with Keycloak service account authentication.
    /// </summary>
    /// <returns>An authenticated HTTP client.</returns>
    protected async Task<HttpClient> GetHttpClient()
    {
        var token = await KeycloakServiceToken.RequestClientToken(keycloakUrl, realm, clientId, clientSecret);
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        return httpClient;
    }

    /// <summary>
    /// Retrieves a Keycloak client by its client ID.
    /// </summary>
    /// <param name="clientName">The client ID to search for.</param>
    /// <returns>The Keycloak client representation, or null if not found.</returns>
    protected async Task<ClientRepresentation?> LoadKeycloakClientAsync(string clientName)
    {
        using HttpClient httpClient = await GetHttpClient();
        var keycloak = new KeycloakClient(keycloakUrl, httpClient);
        Logger.LogInformation("Checking for keycloak client with name: {clientName}", clientName);
        var clients = await keycloak.ClientsAll3Async(clientName, null, null, null, false, false, realm);
        if (clients != null && clients.Count > 0)
        {
            var client = clients.FirstOrDefault(c => c.ClientId == clientName);
            if (client != null)
            {
                return client;
            }
        }
        return null;
    }

    /// <summary>
    /// Retrieves a Keycloak realm role by name.
    /// </summary>
    /// <param name="name">The role name to search for.</param>
    /// <returns>The Keycloak role representation, or null if not found.</returns>
    protected async Task<RoleRepresentation?> LoadKeycloakRoleAsync(string name)
    {
        using HttpClient httpClient = await GetHttpClient();
        var keycloak = new KeycloakClient(keycloakUrl, httpClient);
        var roles = await keycloak.RolesAll2Async(null, null, null, name, realm);
        return roles.FirstOrDefault(r => r.Name == name);
    }

    /// <summary>
    /// Retrieves the client secret for a Keycloak service account client.
    /// </summary>
    /// <param name="name">The client ID of the service account.</param>
    /// <returns>The client secret, or null if not found.</returns>
    protected async Task<string?> LoadKeycloakServiceSecret(string name)
    {
        var client = await LoadKeycloakClientAsync(name);
        if (client != null)
        {
            using HttpClient httpClient = await GetHttpClient();
            var keycloak = new KeycloakClient(keycloakUrl, httpClient);
            var secret = await keycloak.ClientSecretGETAsync(realm, client.Id);
            return secret.Value;
        }
        return null;
    }
}
