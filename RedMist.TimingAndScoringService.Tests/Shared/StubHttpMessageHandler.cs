using System.Net;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a script instead of the network, and keeps
/// the requests it was given so a test can assert on the URL, verb and headers that were produced.
/// </summary>
public sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Answers every request with the same status code.</summary>
    public static StubHttpMessageHandler Returning(HttpStatusCode status, string content = "")
        => new(_ => new HttpResponseMessage(status) { Content = new StringContent(content) });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }
        return Task.FromResult(respond(request));
    }

    /// <summary>An <see cref="IHttpClientFactory"/> that hands out clients backed by this handler.</summary>
    public IHttpClientFactory AsClientFactory() => new Factory(this);

    private sealed class Factory(StubHttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
