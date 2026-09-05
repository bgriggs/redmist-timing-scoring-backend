using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.UserManagement;
using RedMist.UserManagement.Controllers;
using RedMist.UserManagement.Models;
using System.Security.Claims;

namespace RedMist.TimingAndScoringService.Tests.UserManagement;

/// <summary>
/// A registration controller with only its out-of-process dependencies replaced: Keycloak
/// provisioning, the Keycloak client and secret lookups, the CDN upload and the mail transport.
/// Everything the tests are about - name resolution, what gets written to the database, what the
/// registration emails say and who receives them - still runs for real, and no test can reach a
/// live Keycloak, bunny.net or SMTP whatever order exceptions happen to be thrown in.
/// </summary>
internal sealed class RecordingOrganizationController : OrganizationControllerBase
{
    private RecordingOrganizationController(IConfiguration configuration, IDbContextFactory<TsContext> tsContext,
        AssetsCdn assetsCdn, IHttpClientFactory httpClientFactory)
        : base(NullLoggerFactory.Instance, tsContext, configuration, assetsCdn, httpClientFactory)
    {
    }

    // The registration email is sent from a Task.Run the action does not await, so these are written
    // from a thread other than the test's. Recording under a lock keeps a test that reads them after
    // WaitForEmailAsync from racing the list's internal state.
    private readonly Lock recordLock = new();

    public List<(string Subject, string Body, string To, string From, string? Bcc)> Sent { get; } = [];
    public List<string> ClientsCreated { get; } = [];
    public List<string> ClientsLookedUpFor { get; } = [];
    public List<string> SecretsRequestedFor { get; } = [];

    /// <summary>Client IDs that <see cref="LoadKeycloakClientAsync"/> should report as taken.</summary>
    public HashSet<string> ExistingClients { get; } = [];

    public string? SecretToReturn { get; set; } = "s3cr3t";
    public Exception? SecretLookupFailure { get; set; }
    public Exception? SendFailure { get; set; }

    private readonly TaskCompletionSource emailSent = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override Task<ClientRepresentation?> LoadKeycloakClientAsync(string clientName)
    {
        ClientsLookedUpFor.Add(clientName);
        return Task.FromResult(ExistingClients.Contains(clientName)
            ? new ClientRepresentation { ClientId = clientName }
            : null);
    }

    protected override Task<bool> CreateKeycloakClientAsync(string clientId, string userName, UserType type)
    {
        ClientsCreated.Add(clientId);
        return Task.FromResult(true);
    }

    protected override Task<string?> LoadKeycloakServiceSecret(string name)
    {
        lock (recordLock)
        {
            SecretsRequestedFor.Add(name);
        }
        if (SecretLookupFailure != null)
        {
            return Task.FromException<string?>(SecretLookupFailure);
        }
        return Task.FromResult(SecretToReturn);
    }

    protected override Task SendEmailAsync(string subject, string bodyHtml, string to, string from, string? bcc)
    {
        lock (recordLock)
        {
            Sent.Add((subject, bodyHtml, to, from, bcc));
        }
        emailSent.TrySetResult();
        if (SendFailure != null)
        {
            return Task.FromException(SendFailure);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The registration email is sent fire-and-forget off the request thread, so a test that asserts
    /// on it has to wait for that task rather than for the action to return. The budget is only ever
    /// spent when the mail is genuinely never sent.
    /// </summary>
    public async Task WaitForEmailAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await emailSent.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("No registration email was sent.");
        }
    }

    // The two registration email bodies are built by private methods behind the fire-and-forget
    // hand-off, so these expose them for the tests that are only about the message itself.
    public Task SendOrganizationEmailAsync(string userEmail, string relayClientId, string organizationName)
        => SendOrganizationRegistrationEmailAsync(userEmail, relayClientId, organizationName);

    public Task SendApiEmailAsync(string userEmail, string apiClientId)
        => SendApiRegistrationEmailAsync(userEmail, apiClientId);

    /// <summary>
    /// Builds a controller wired to a fresh in-memory database and an authenticated caller.
    /// </summary>
    public static (RecordingOrganizationController Controller, TsContext Db) Create(string userEmail)
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

        // AssetsCdn.SaveLogoAsync must stay virtual: a non-virtual member cannot be intercepted, so
        // the proxy would run the real body and perform a live bunny.net upload.
        var mockAssetsCdn = new Mock<AssetsCdn>(configuration, NullLoggerFactory.Instance, httpClientFactory);
        mockAssetsCdn.Setup(x => x.SaveLogoAsync(It.IsAny<int>(), It.IsAny<byte[]>())).ReturnsAsync(true);

        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var dbFactory = new TestDbContextFactory(options);

        var controller = new RecordingOrganizationController(configuration, dbFactory, mockAssetsCdn.Object, httpClientFactory);

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, userEmail),
            new Claim("preferred_username", userEmail),
        ], "TestAuthType");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        return (controller, dbFactory.CreateDbContext());
    }

    public static OrganizationDto NewOrg(string shortName) =>
        new() { Name = "Acme Racing", ShortName = shortName, Website = "https://acme.example" };
}
