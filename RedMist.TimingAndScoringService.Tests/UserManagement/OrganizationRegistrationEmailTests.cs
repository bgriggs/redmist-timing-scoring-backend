using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.UserManagement.Controllers;

namespace RedMist.TimingAndScoringService.Tests.UserManagement;

/// <summary>
/// What the registration emails say and who receives them. Both the organization and the API
/// registration confirm to the person who signed up and blind-copy Red Mist support; the tests
/// below pin that, and the credentials each mail carries, without a mail server or Keycloak.
/// </summary>
[TestClass]
public class OrganizationRegistrationEmailTests
{
    private const string UserEmail = "driver@example.com";
    private const string ExpectedFrom = "Red Mist <support@redmist.racing>";
    private const string ExpectedBcc = "brian@bigmissionmotorsports.com";

    private RecordingOrganizationController controller = null!;

    /// <summary>
    /// Replaces the two outside dependencies of the email path only - the Keycloak secret lookup and
    /// the mail transport. Subject construction, body rendering, the recipient/BCC choice and the
    /// swallowing of failures all still run for real, and no test can reach live SMTP or Keycloak.
    /// </summary>
    private sealed class RecordingOrganizationController : OrganizationControllerBase
    {
        public RecordingOrganizationController(IConfiguration configuration,
            IDbContextFactory<TsContext> tsContext, AssetsCdn assetsCdn, IHttpClientFactory httpClientFactory)
            : base(NullLoggerFactory.Instance, tsContext, configuration, assetsCdn, httpClientFactory)
        {
        }

        public List<(string Subject, string Body, string To, string From, string? Bcc)> Sent { get; } = [];
        public string? SecretToReturn { get; set; } = "s3cr3t";
        public List<string> SecretsRequestedFor { get; } = [];
        public Exception? SecretLookupFailure { get; set; }
        public Exception? SendFailure { get; set; }

        protected override Task<string?> LoadKeycloakServiceSecret(string name)
        {
            SecretsRequestedFor.Add(name);
            if (SecretLookupFailure != null)
            {
                return Task.FromException<string?>(SecretLookupFailure);
            }
            return Task.FromResult(SecretToReturn);
        }

        protected override Task SendEmailAsync(string subject, string bodyHtml, string to, string from, string? bcc)
        {
            Sent.Add((subject, bodyHtml, to, from, bcc));
            if (SendFailure != null)
            {
                return Task.FromException(SendFailure);
            }
            return Task.CompletedTask;
        }

        public Task SendOrganizationEmailAsync(string userEmail, string relayClientId, string organizationName)
            => SendOrganizationRegistrationEmailAsync(userEmail, relayClientId, organizationName);

        public Task SendApiEmailAsync(string userEmail, string apiClientId)
            => SendApiRegistrationEmailAsync(userEmail, apiClientId);
    }

    [TestInitialize]
    public void Setup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Keycloak:AuthServerUrl"] = "https://auth.example.com",
                ["Keycloak:ClientId"] = "user-management",
                ["Keycloak:ClientSecret"] = "kc-secret",
                ["Keycloak:Realm"] = "redmist",
                ["Assets:StorageZoneName"] = "zone",
                ["Assets:StorageAccessKey"] = "storage-key",
                ["Assets:MainReplicationRegion"] = "ny",
                ["Assets:ApiAccessKey"] = "api-key",
                ["Assets:CdnId"] = "cdn-1",
            })
            .Build();

        var httpClientFactory = new Mock<IHttpClientFactory>().Object;
        var assetsCdn = new AssetsCdn(configuration, NullLoggerFactory.Instance, httpClientFactory);
        var dbFactory = new Mock<IDbContextFactory<TsContext>>().Object;

        controller = new RecordingOrganizationController(configuration, dbFactory, assetsCdn, httpClientFactory);
    }

    [TestMethod]
    public async Task OrganizationRegistration_SendsToTheRegistrantAndBccsSupport()
    {
        await controller.SendOrganizationEmailAsync(UserEmail, "relay-acme", "Acme Racing");

        var sent = controller.Sent.Single();
        Assert.AreEqual(UserEmail, sent.To);
        Assert.AreEqual(ExpectedFrom, sent.From);
        Assert.AreEqual(ExpectedBcc, sent.Bcc);
        Assert.AreEqual("Red Mist Organization Registration", sent.Subject);
    }

    [TestMethod]
    public async Task OrganizationRegistration_CarriesTheRelayCredentialsAndOrganizationName()
    {
        controller.SecretToReturn = "relay-secret-value";

        await controller.SendOrganizationEmailAsync(UserEmail, "relay-acme", "Acme Racing");

        var body = controller.Sent.Single().Body;
        StringAssert.Contains(body, "Acme Racing");
        StringAssert.Contains(body, "relay-acme");
        StringAssert.Contains(body, "relay-secret-value");
        CollectionAssert.AreEqual(new[] { "relay-acme" }, controller.SecretsRequestedFor);
    }

    [TestMethod]
    public async Task OrganizationRegistration_IncludesTheCommunityLinks()
    {
        await controller.SendOrganizationEmailAsync(UserEmail, "relay-acme", "Acme Racing");

        var body = controller.Sent.Single().Body;
        StringAssert.Contains(body, "https://discord.gg/9m3unnqw5Z");
        StringAssert.Contains(body, "https://www.facebook.com/profile.php?id=61586424808299");
    }

    [TestMethod]
    public async Task OrganizationRegistration_EscapesTheOrganizationNameIntoTheHtmlBody()
    {
        await controller.SendOrganizationEmailAsync(UserEmail, "relay-acme", "Acme <b>Racing</b> & Co");

        var body = controller.Sent.Single().Body;
        StringAssert.Contains(body, "Acme &lt;b&gt;Racing&lt;/b&gt; &amp; Co");
        Assert.IsFalse(body.Contains("<b>Racing</b>"), "The organization name must not reach the body as live markup.");
    }

    /// <summary>
    /// Keycloak provisioning failures are only logged, so registration can succeed with no client
    /// secret behind it. A mail whose whole purpose is delivering that secret must not go out with a
    /// blank one - to the registrant it would be indistinguishable from a working registration.
    /// </summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public async Task OrganizationRegistration_WithNoClientSecret_SendsNothing(string? secret)
    {
        controller.SecretToReturn = secret;

        await controller.SendOrganizationEmailAsync(UserEmail, "relay-acme", "Acme Racing");

        Assert.AreEqual(0, controller.Sent.Count);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public async Task ApiRegistration_WithNoClientSecret_SendsNothing(string? secret)
    {
        controller.SecretToReturn = secret;

        await controller.SendApiEmailAsync(UserEmail, "api-acme");

        Assert.AreEqual(0, controller.Sent.Count);
    }

    /// <summary>
    /// The send is fire-and-forget off the request thread, so a failure must not escape and take
    /// down an unobserved task - the registration itself has already been committed by this point.
    /// </summary>
    [TestMethod]
    public async Task OrganizationRegistration_WhenTheSecretLookupFails_SwallowsTheException()
    {
        controller.SecretLookupFailure = new HttpRequestException("keycloak down");

        await controller.SendOrganizationEmailAsync(UserEmail, "relay-acme", "Acme Racing");

        Assert.AreEqual(0, controller.Sent.Count);
    }

    [TestMethod]
    public async Task OrganizationRegistration_WhenTheSendFails_SwallowsTheException()
    {
        controller.SendFailure = new InvalidOperationException("smtp down");

        await controller.SendOrganizationEmailAsync(UserEmail, "relay-acme", "Acme Racing");

        Assert.AreEqual(1, controller.Sent.Count);
    }

    [TestMethod]
    public async Task ApiRegistration_StillSendsToTheRegistrantAndBccsSupport()
    {
        controller.SecretToReturn = "api-secret-value";

        await controller.SendApiEmailAsync(UserEmail, "api-acme");

        var sent = controller.Sent.Single();
        Assert.AreEqual(UserEmail, sent.To);
        Assert.AreEqual(ExpectedFrom, sent.From);
        Assert.AreEqual(ExpectedBcc, sent.Bcc);
        Assert.AreEqual("Red Mist API Registration", sent.Subject);
        StringAssert.Contains(sent.Body, "api-acme");
        StringAssert.Contains(sent.Body, "api-secret-value");
        StringAssert.Contains(sent.Body, "https://discord.gg/9m3unnqw5Z");
    }
}
