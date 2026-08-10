using RedMist.ExternalDataCollection.Models;
using RestSharp;

namespace RedMist.ExternalDataCollection.Clients;

public class SentinelClient
{
    private readonly RestClient restClient;


    public SentinelClient(IConfiguration configuration) : this(configuration, null)
    {
    }

    /// <summary>
    /// Allows a message handler to be supplied so the HTTP transport can be stubbed for testing.
    /// </summary>
    internal SentinelClient(IConfiguration configuration, HttpMessageHandler? messageHandler)
    {
        var url = configuration["SentinelApiUrl"] ?? throw new ArgumentNullException("SentinelApiUrl");
        var options = new RestClientOptions(url);
        if (messageHandler != null)
        {
            options.ConfigureMessageHandler = _ => messageHandler;
        }
        restClient = new RestClient(options);
    }


    public virtual async Task<List<PublicStreams>> GetStreamsAsync()
    {
        var request = new RestRequest("getPublicStreams", Method.Get);
        return await restClient.GetAsync<List<PublicStreams>>(request) ?? [];
    }
}
