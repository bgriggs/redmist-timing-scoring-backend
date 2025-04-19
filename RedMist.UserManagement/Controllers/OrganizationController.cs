using BigMission.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.TimingCommon.Models.Configuration;
using RedMist.UserManagement.Models;

namespace RedMist.UserManagement.Controllers;

[ApiController]
[Route("[controller]/[action]")]
[Authorize]
public class OrganizationController : ControllerBase
{
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly IConfiguration configuration;
    private readonly string keycloakUrl;
    private readonly string clientId;
    private readonly string clientSecret;
    private readonly string realm;

    private ILogger Logger { get; }


    public OrganizationController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IConfiguration configuration)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.configuration = configuration;

        keycloakUrl = configuration["Keycloak:AuthServerUrl"] ?? throw new InvalidOperationException("Keycloak URL is not configured.");
        clientId = configuration["Keycloak:ClientId"] ?? throw new InvalidOperationException("Keycloak Client ID is not configured.");
        clientSecret = configuration["Keycloak:ClientSecret"] ?? throw new InvalidOperationException("Keycloak Client Secret is not configured.");
        realm = configuration["Keycloak:Realm"] ?? throw new InvalidOperationException("Keycloak Realm is not configured.");
    }


    [HttpGet]
    [ProducesResponseType<OrganizationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrganizationDto>> LoadUserOrganization()
    {
        Logger.LogInformation("LoadUserOrganization");
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

        return new OrganizationDto
        {
            Id = org.Id,
            Name = org.Name,
            Website = org.Website,
            Logo = org.Logo,
        };
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<bool>> RelayClientNameExists(string name)
    {
        Logger.LogInformation("RelayClientNameExists {name}", name);
        var clientId = string.Format(Consts.RELAY_CLIENT_ID, name);
        var client = await LoadKeycloakClient(clientId);
        return client != null;
    }

    [HttpPost]
    [ProducesResponseType<int>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<int>> SaveNewOrganization(OrganizationDto newOrganization)
    {
        Logger.LogInformation("SaveNewOrganization {organization}", newOrganization.Name);

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
            ShortName = newOrganization.ShortName,
            Website = newOrganization.Website,
            Logo = newOrganization.Logo,
            ControlLogType = "Default",
            ControlLogParams = string.Empty
        };
        context.Organizations.Add(organization);
        await context.SaveChangesAsync();
        Logger.LogInformation("New organization created with ID {organizationId}", organization.Id);

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
        var relayClientId = await CreateRelayClient(newOrganization.ShortName);
        Logger.LogInformation("Relay client created with ID {relayClientId}", relayClientId);

        return Ok(organization.Id);
    }

    private async Task<string?> CreateRelayClient(string name)
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
            ServiceAccountsEnabled = true, // Enable service accounts for this client
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

        Logger.LogInformation($"Created client with ID: {clientId}");
        client = await LoadKeycloakClient(clientId);
        Logger.LogInformation($"Loaded client with ID: {client?.Id}");
        if (client == null)
        {
            Logger.LogError("Failed to create or load Keycloak client {clientId}", clientId);
            return null;
        }

        var relayRole = await LoadKeycloakRole("relay-svc");
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

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> UpdateOrganization(OrganizationDto organizationDto)
    {
        Logger.LogInformation("UpdateOrganization {organization}", organizationDto.Name);

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
        return Ok();
    }

    [HttpGet]
    [ProducesResponseType<RelayConnectionInfoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<RelayConnectionInfoDto>> LoadRelayConnection(int organizationId)
    {
        Logger.LogInformation("LoadRelayConnection for organization {organizationId}", organizationId);
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
    /// Determines if the user is authorized for the specified organization.
    /// </summary>
    /// <param name="organizationId"></param>
    /// <returns>true if authorized</returns>
    private async Task<bool> ValidateUserOrganization(int organizationId)
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

    private async Task<HttpClient> GetHttpClient()
    {
        var token = await KeycloakServiceToken.RequestClientToken(keycloakUrl, realm, clientId, clientSecret);
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        return httpClient;
    }

    /// <summary>
    /// Get a Keycloak client by name.
    /// </summary>
    /// <param name="clientName"></param>
    /// <returns></returns>
    public async Task<ClientRepresentation?> LoadKeycloakClient(string clientName)
    {
        using HttpClient httpClient = await GetHttpClient();
        var keycloak = new KeycloakClient(keycloakUrl, httpClient);
        Logger.LogInformation($"Checking for keycloak client with name: {clientName}");
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
    /// Get a Keycloak role by name.
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    private async Task<RoleRepresentation?> LoadKeycloakRole(string name)
    {
        using HttpClient httpClient = await GetHttpClient();
        var keycloak = new KeycloakClient(keycloakUrl, httpClient);
        var roles = await keycloak.RolesAll2Async(null, null, null, name, realm);
        return roles.FirstOrDefault(r => r.Name == name);
    }

    /// <summary>
    /// Get the secret for a Keycloak service client.
    /// </summary>
    /// <param name="name">text name of the client</param>
    /// <returns>secret or null</returns>
    private async Task<string?> LoadKeycloakServiceSecret(string name)
    {
        var client = await LoadKeycloakClient(name);
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
