using System.Net;
using System.Text;

namespace RedMist.TimingAndScoringService.Tests.ExternalDataCollection;

/// <summary>
/// Snapshot of a request seen by <see cref="StubHttpMessageHandler"/>. The body is copied eagerly
/// because the underlying content is disposed once the client finishes with the request.
/// </summary>
internal sealed record CapturedRequest(HttpMethod Method, Uri? Uri, string? ContentType, byte[] Body);

/// <summary>
/// Deterministic stand-in for the network. Records every request and returns a canned response
/// (or throws) so client code can be exercised without any real I/O.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;
    private readonly List<CapturedRequest> requests = [];
    private readonly object sync = new();

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        this.responder = responder;
    }

    public IReadOnlyList<CapturedRequest> Requests
    {
        get { lock (sync) { return [.. requests]; } }
    }

    /// <summary>Responds with the supplied status and JSON body.</summary>
    public static StubHttpMessageHandler Json(HttpStatusCode status, string body) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    /// <summary>Responds with the supplied status and no body.</summary>
    public static StubHttpMessageHandler Status(HttpStatusCode status) =>
        new(_ => new HttpResponseMessage(status));

    /// <summary>Simulates a transport failure.</summary>
    public static StubHttpMessageHandler Throws(Exception exception) =>
        new(_ => throw exception);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        lock (sync)
        {
            requests.Add(new CapturedRequest(request.Method, request.RequestUri, request.Content?.Headers.ContentType?.MediaType, body));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return responder(request);
    }
}
