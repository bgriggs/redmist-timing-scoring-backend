using Microsoft.Extensions.Configuration;
using RedMist.Backend.Shared.Utilities;
using System.Net;
using System.Text;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// The Graph transport carries registration credentials to new organizations, and a failure there is
/// only logged, so a request that is quietly wrong means a registrant who never hears from us.
/// </summary>
[TestClass]
public class EmailHelperGraphTests
{
    private const string Sender = "support@example.com";

    /// <summary>
    /// Tokens are cached per tenant and client for the life of the process, so each test gets its
    /// own tenant to keep one test's token out of the next.
    /// </summary>
    private static Dictionary<string, string?> GraphSettings() => new()
    {
        ["Email:Provider"] = "Graph",
        ["Email:Graph:TenantId"] = Guid.NewGuid().ToString(),
        ["Email:Graph:ClientId"] = "client-id",
        ["Email:Graph:ClientSecret"] = "client-secret",
        ["Email:Graph:SenderAddress"] = Sender,
    };

    private static IConfiguration Configuration(IDictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    /// <summary>Answers the token and sendMail endpoints and records every request it was sent.</summary>
    private sealed class FakeGraphHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];
        public HttpStatusCode SendMailStatus { get; set; } = HttpStatusCode.Accepted;
        public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));

            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                return TokenStatus == HttpStatusCode.OK
                    ? Json("""{"token_type":"Bearer","expires_in":3599,"access_token":"token-1"}""")
                    : new HttpResponseMessage(TokenStatus) { Content = new StringContent("""{"error":"invalid_client"}""") };
            }

            return SendMailStatus == HttpStatusCode.Accepted
                ? new HttpResponseMessage(HttpStatusCode.Accepted)
                : new HttpResponseMessage(SendMailStatus) { Content = new StringContent("""{"error":{"code":"ErrorAccessDenied"}}""") };
        }

        private static HttpResponseMessage Json(string json)
            => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        public IEnumerable<(HttpRequestMessage Request, string Body)> SendMailRequests
            => Requests.Where(r => r.Request.RequestUri!.Host == "graph.microsoft.com");

        public IEnumerable<(HttpRequestMessage Request, string Body)> TokenRequests
            => Requests.Where(r => r.Request.RequestUri!.Host == "login.microsoftonline.com");
    }

    private sealed class TestableEmailHelper(IConfiguration configuration, FakeGraphHandler handler) : EmailHelper(configuration)
    {
        private readonly HttpClient client = new(handler);
        protected override HttpClient GetHttpClient() => client;
    }

    private static (TestableEmailHelper Helper, FakeGraphHandler Handler) CreateHelper(Dictionary<string, string?>? settings = null)
    {
        var handler = new FakeGraphHandler();
        return (new TestableEmailHelper(Configuration(settings ?? GraphSettings()), handler), handler);
    }

    [TestMethod]
    public async Task SendEmailAsync_PostsToSendMailForTheSenderMailboxWithTheToken()
    {
        var (helper, handler) = CreateHelper();

        await helper.SendEmailAsync("Welcome", "<p>hi</p>", "new@example.org", "Red Mist <support@example.com>");

        var (request, _) = handler.SendMailRequests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("https://graph.microsoft.com/v1.0/users/support%40example.com/sendMail", request.RequestUri!.AbsoluteUri);
        Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
        Assert.AreEqual("token-1", request.Headers.Authorization.Parameter);
    }

    [TestMethod]
    public async Task SendEmailAsync_RequestsAClientCredentialsTokenForGraph()
    {
        var settings = GraphSettings();
        var (helper, handler) = CreateHelper(settings);

        await helper.SendEmailAsync("Welcome", "<p>hi</p>", "new@example.org", Sender);

        var (request, body) = handler.TokenRequests.Single();
        Assert.AreEqual($"https://login.microsoftonline.com/{settings["Email:Graph:TenantId"]}/oauth2/v2.0/token", request.RequestUri!.AbsoluteUri);
        StringAssert.Contains(body, "grant_type=client_credentials");
        StringAssert.Contains(body, "client_id=client-id");
        StringAssert.Contains(body, "client_secret=client-secret");
        StringAssert.Contains(body, "scope=https%3A%2F%2Fgraph.microsoft.com%2F.default");
    }

    [TestMethod]
    public async Task SendEmailAsync_BuildsTheMessageFromTheSuppliedAddressesAndHtmlBody()
    {
        var (helper, handler) = CreateHelper();

        await helper.SendEmailAsync("Welcome", "<p>hi</p>", "Pat <new@example.org>", "Red Mist <support@example.com>", "audit@example.com");

        using var json = JsonDocument.Parse(handler.SendMailRequests.Single().Body);
        var message = json.RootElement.GetProperty("message");
        Assert.AreEqual("Welcome", message.GetProperty("subject").GetString());
        Assert.AreEqual("HTML", message.GetProperty("body").GetProperty("contentType").GetString());
        Assert.AreEqual("<p>hi</p>", message.GetProperty("body").GetProperty("content").GetString());

        var to = message.GetProperty("toRecipients").EnumerateArray().Single().GetProperty("emailAddress");
        Assert.AreEqual("new@example.org", to.GetProperty("address").GetString());
        Assert.AreEqual("Pat", to.GetProperty("name").GetString());

        var bcc = message.GetProperty("bccRecipients").EnumerateArray().Single().GetProperty("emailAddress");
        Assert.AreEqual("audit@example.com", bcc.GetProperty("address").GetString());

        Assert.IsFalse(json.RootElement.GetProperty("saveToSentItems").GetBoolean());
    }

    /// <summary>
    /// Archive alerts pass noreply@ as the From. Graph refuses a From the app has no send-as right
    /// for, so the address must always be the configured mailbox; only the display name carries over.
    /// </summary>
    [TestMethod]
    public async Task SendEmailAsync_WithAFromOtherThanTheSender_SendsAsTheSenderKeepingTheName()
    {
        var (helper, handler) = CreateHelper();

        await helper.SendEmailAsync("Archive failed", "<p>x</p>", "ops@example.org", "Alerts <noreply@example.com>");

        using var json = JsonDocument.Parse(handler.SendMailRequests.Single().Body);
        var from = json.RootElement.GetProperty("message").GetProperty("from").GetProperty("emailAddress");
        Assert.AreEqual(Sender, from.GetProperty("address").GetString());
        Assert.AreEqual("Alerts", from.GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task SendEmailAsync_WithABareFromAddress_UsesRedMistAsTheName()
    {
        var (helper, handler) = CreateHelper();

        await helper.SendEmailAsync("Archive failed", "<p>x</p>", "ops@example.org", "noreply@example.com");

        using var json = JsonDocument.Parse(handler.SendMailRequests.Single().Body);
        var from = json.RootElement.GetProperty("message").GetProperty("from").GetProperty("emailAddress");
        Assert.AreEqual("Red Mist", from.GetProperty("name").GetString());
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public async Task SendEmailAsync_WithABlankBcc_SendsWithoutOne(string? bcc)
    {
        var (helper, handler) = CreateHelper();

        await helper.SendEmailAsync("Welcome", "<p>hi</p>", "new@example.org", Sender, bcc);

        using var json = JsonDocument.Parse(handler.SendMailRequests.Single().Body);
        Assert.IsFalse(json.RootElement.GetProperty("message").TryGetProperty("bccRecipients", out _));
    }

    [TestMethod]
    public async Task SendEmailAsync_ReusesTheTokenAcrossSends()
    {
        var (helper, handler) = CreateHelper();

        await helper.SendEmailAsync("One", "<p>1</p>", "new@example.org", Sender);
        await helper.SendEmailAsync("Two", "<p>2</p>", "new@example.org", Sender);

        Assert.AreEqual(1, handler.TokenRequests.Count());
        Assert.AreEqual(2, handler.SendMailRequests.Count());
    }

    /// <summary>Callers only log a failed send, so a rejected one must throw rather than look sent.</summary>
    [TestMethod]
    public async Task SendEmailAsync_WhenGraphRejectsTheSend_Throws()
    {
        var (helper, handler) = CreateHelper();
        handler.SendMailStatus = HttpStatusCode.Forbidden;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => helper.SendEmailAsync("Welcome", "<p>hi</p>", "new@example.org", Sender));

        Assert.AreEqual(HttpStatusCode.Forbidden, ex.StatusCode);
        StringAssert.Contains(ex.Message, "ErrorAccessDenied");
    }

    [TestMethod]
    public async Task SendEmailAsync_WhenTheTokenRequestFails_ThrowsWithoutSending()
    {
        var (helper, handler) = CreateHelper();
        handler.TokenStatus = HttpStatusCode.Unauthorized;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => helper.SendEmailAsync("Welcome", "<p>hi</p>", "new@example.org", Sender));

        StringAssert.Contains(ex.Message, "invalid_client");
        Assert.IsFalse(ex.Message.Contains("client-secret"), "the secret must not end up in logs");
        Assert.IsFalse(handler.SendMailRequests.Any());
    }

    /// <summary>A rotated secret leaves a dead cached token; a 401 must force a fresh one next time.</summary>
    [TestMethod]
    public async Task SendEmailAsync_AfterAnUnauthorizedSend_FetchesANewToken()
    {
        var (helper, handler) = CreateHelper();
        handler.SendMailStatus = HttpStatusCode.Unauthorized;
        await Assert.ThrowsAsync<HttpRequestException>(
            () => helper.SendEmailAsync("One", "<p>1</p>", "new@example.org", Sender));

        handler.SendMailStatus = HttpStatusCode.Accepted;
        await helper.SendEmailAsync("Two", "<p>2</p>", "new@example.org", Sender);

        Assert.AreEqual(2, handler.TokenRequests.Count());
    }

    /// <summary>
    /// Several services resolve the helper from DI at startup. A missing Graph secret must fail the
    /// send, where callers catch and log it, not the constructor, where it would stop the service.
    /// </summary>
    [TestMethod]
    [DataRow("Email:Graph:TenantId")]
    [DataRow("Email:Graph:ClientId")]
    [DataRow("Email:Graph:ClientSecret")]
    [DataRow("Email:Graph:SenderAddress")]
    public async Task MissingGraphSetting_FailsTheSendNotTheConstructor(string missingKey)
    {
        var settings = GraphSettings();
        settings.Remove(missingKey);

        var (helper, handler) = CreateHelper(settings);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => helper.SendEmailAsync("Welcome", "<p>hi</p>", "new@example.org", Sender));
        StringAssert.Contains(ex.Message, missingKey);
        Assert.IsFalse(handler.Requests.Any());
    }

    [TestMethod]
    public void Constructor_WithGraphProvider_DoesNotRequireSmtpSettings()
    {
        _ = new EmailHelper(Configuration(GraphSettings()));
    }

    [TestMethod]
    public async Task ProviderName_IsCaseInsensitive()
    {
        var settings = GraphSettings();
        settings["Email:Provider"] = "graph";
        var (helper, handler) = CreateHelper(settings);

        await helper.SendEmailAsync("Welcome", "<p>hi</p>", "new@example.org", Sender);

        Assert.AreEqual(1, handler.SendMailRequests.Count());
    }
}
