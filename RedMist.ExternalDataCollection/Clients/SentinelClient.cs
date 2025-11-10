using RedMist.ExternalDataCollection.Models;
using RestSharp;

namespace RedMist.ExternalDataCollection.Clients;

public class SentinelClient
{
    private readonly RestClient restClient;


    public SentinelClient(IConfiguration configuration)
    {
        var url = configuration["SentinelApiUrl"] ?? throw new ArgumentNullException("SentinelApiUrl");
        var options = new RestClientOptions(url);
        restClient = new RestClient(options);
    }
    

    public async Task<List<PublicStreams>> GetStreamsAsync()
    {
        var request = new RestRequest("getPublicStreams", Method.Get);
        return await restClient.GetAsync<List<PublicStreams>>(request) ?? [];
    }
}
