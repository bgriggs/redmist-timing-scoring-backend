using BigMission.Shared.Auth;
using Microsoft.Extensions.Configuration;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarDriverMode;
using RestSharp;

namespace RedMist.SampleProject;

internal class StatusClient
{
    private readonly RestClient restClient;

    public StatusClient(IConfiguration configuration)
    {
        var url = configuration["Server:EventUrl"] ?? throw new InvalidOperationException("Server EventUrl is not configured.");
        var authUrl = configuration["Keycloak:AuthServerUrl"] ?? throw new InvalidOperationException("Keycloak URL is not configured.");
        var realm = configuration["Keycloak:Realm"] ?? throw new InvalidOperationException("Keycloak realm is not configured.");
        var clientId = configuration["Keycloak:ClientId"] ?? throw new InvalidOperationException("Keycloak client ID is not configured.");
        var clientSecret = configuration["Keycloak:ClientSecret"] ?? throw new InvalidOperationException("Keycloak client secret is not configured.");

        var options = new RestClientOptions(url)
        {
            Authenticator = new KeycloakServiceAuthenticator(string.Empty, authUrl, realm, clientId, clientSecret)
        };
        restClient = new RestClient(options);

        // Add default Accept header for all requests (MessagePack preferred, JSON fallback)
        restClient.AddDefaultHeader("Accept", "application/msgpack, application/json");
    }

    public async Task<List<EventListSummary>> LoadRecentEventsAsync()
    {
        var request = new RestRequest("LoadLiveAndRecentEvents", Method.Get);
        return await restClient.GetAsync<List<EventListSummary>>(request) ?? [];
    }

    public async Task<Event?> LoadEventAsync(int eventId)
    {
        var request = new RestRequest("LoadEvent", Method.Get);
        request.AddQueryParameter("eventId", eventId);
        return await restClient.GetAsync<Event?>(request);
    }

    public async Task<SessionState?> LoadEventStatusAsync(int eventId)
    {
        var request = new RestRequest("GetCurrentSessionState", Method.Get);
        request.AddQueryParameter("eventId", eventId);
        return await restClient.GetAsync<SessionState?>(request);
    }

    public async Task<List<CarPosition>> LoadCarLapsAsync(int eventId, int sessionId, string carNumber)
    {
        var request = new RestRequest("LoadCarLaps", Method.Get);
        request.AddQueryParameter("eventId", eventId);
        request.AddQueryParameter("sessionId", sessionId);
        request.AddQueryParameter("carNumber", carNumber);
        return await restClient.GetAsync<List<CarPosition>>(request) ?? [];
    }

    public async Task<List<Session>> LoadSessionsAsync(int eventId)
    {
        var request = new RestRequest("LoadSessions", Method.Get);
        request.AddQueryParameter("eventId", eventId);
        return await restClient.GetAsync<List<Session>>(request) ?? [];
    }

    public async Task<SessionState?> LoadSessionResultsAsync(int eventId, int sessionId)
    {
        var request = new RestRequest("LoadSessionResults", Method.Get);
        request.AddQueryParameter("eventId", eventId);
        request.AddQueryParameter("sessionId", sessionId);
        return await restClient.GetAsync<SessionState?>(request);
    }

    public async Task<CompetitorMetadata?> LoadCompetitorMetadataAsync(int eventId, string car)
    {
        var request = new RestRequest("LoadCompetitorMetadata", Method.Get);
        request.AddQueryParameter("eventId", eventId);
        request.AddQueryParameter("car", car);
        return await restClient.GetAsync<CompetitorMetadata?>(request);
    }

    public async Task<List<ControlLogEntry>> LoadControlLogAsync(int eventId)
    {
        var request = new RestRequest("LoadControlLog", Method.Get);
        request.AddQueryParameter("eventId", eventId);
        return await restClient.GetAsync<List<ControlLogEntry>>(request) ?? [];
    }

    public async Task<CarControlLogs?> LoadCarControlLogsAsync(int eventId, string car)
    {
        var request = new RestRequest("LoadCarControlLogs", Method.Get);
        request.AddQueryParameter("eventId", eventId);
        request.AddQueryParameter("car", car);
        return await restClient.GetAsync<CarControlLogs?>(request);
    }

    public async Task<InCarPayload?> LoadInCarDriverModePayloadAsync(int eventId, string car)
    {
        var request = new RestRequest("LoadInCarPayload", Method.Get);
        request.AddQueryParameter("eventId", eventId);
        request.AddQueryParameter("car", car);
        return await restClient.GetAsync<InCarPayload?>(request);
    }

    public async Task<List<FlagDuration>> LoadFlagsAsync(int eventId, int sessionId)
    {
        var request = new RestRequest("LoadFlags", Method.Get);
        request.AddQueryParameter("eventId", eventId);
        request.AddQueryParameter("sessionId", sessionId);
        return await restClient.GetAsync<List<FlagDuration>>(request) ?? [];
    }
}
