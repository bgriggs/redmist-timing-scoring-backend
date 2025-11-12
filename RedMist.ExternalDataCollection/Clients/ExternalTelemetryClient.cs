using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarVideo;
using RestSharp;
using BigMission.Shared.Auth;
using MessagePack;

namespace RedMist.ExternalDataCollection.Clients;

public class ExternalTelemetryClient
{
    private readonly RestClient restClient;


    public ExternalTelemetryClient(IConfiguration configuration)
    {
        var url = configuration["Server:ExtTelemUrl"] ?? throw new ArgumentException("Server ExtTelemUrl is not configured.");
        var authUrl = configuration["Keycloak:AuthServerUrl"] ?? throw new ArgumentException("Keycloak URL is not configured.");
        var realm = configuration["Keycloak:Realm"] ?? throw new ArgumentException("Keycloak realm is not configured.");
        var clientId = configuration["Keycloak:ClientId"] ?? throw new ArgumentException("Keycloak client ID is not configured.");
        var clientSecret = configuration["Keycloak:ClientSecret"] ?? throw new ArgumentException("Keycloak client secret is not configured.");

        var options = new RestClientOptions(url)
        {
            Authenticator = new KeycloakServiceAuthenticator(string.Empty, authUrl, realm, clientId, clientSecret)
        };
        restClient = new RestClient(options);

        // Add default Accept header for all requests (MessagePack preferred, JSON fallback)
        restClient.AddDefaultHeader("Accept", "application/msgpack, application/json");
    }


    /// <summary>
    /// Assigns a driver to a car.
    /// </summary>
    /// <returns>The task result is <see langword="true"/> if the driver was successfully assigned; otherwise, <see langword="false"/>.</returns>
    public virtual async Task<bool> UpdateDriversAsync(List<DriverInfo> drivers, CancellationToken stoppingToken = default)
    {
        var request = new RestRequest("UpdateDrivers", Method.Post);
        var serialized = MessagePackSerializer.Serialize(drivers, cancellationToken: stoppingToken);
        request.AddBody(serialized, "application/x-msgpack");
        var result = await restClient.ExecutePostAsync(request, stoppingToken);
        return result.IsSuccessful;
    }

    /// <summary>
    /// Adds video information to cars.
    /// </summary>
    /// <returns>The task result is <see langword="true"/> if the driver was successfully assigned; otherwise, <see langword="false"/>.</returns>
    public virtual async Task<bool> UpdateCarVideosAsync(List<VideoMetadata> videos, CancellationToken stoppingToken = default)
    {
        var request = new RestRequest("UpdateCarVideos", Method.Post);
        var serialized = MessagePackSerializer.Serialize(videos, cancellationToken: stoppingToken);
        request.AddBody(serialized, "application/x-msgpack");
        var result = await restClient.ExecutePostAsync(request, stoppingToken);
        return result.IsSuccessful;
    }
}

