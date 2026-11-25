using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// Sends email through one of two transports, picked by <c>Email:Provider</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Smtp</c> (the default, used when the setting is absent) connects with MailKit to
/// <c>Email:Host</c>/<c>Email:Port</c> over StartTLS and authenticates with <c>Email:Username</c> and
/// <c>Email:Password</c>.
/// </para>
/// <para>
/// <c>Graph</c> posts the message to Microsoft Graph <c>sendMail</c> over HTTPS, as the mailbox in
/// <c>Email:Graph:SenderAddress</c>, using an Entra app's client credentials
/// (<c>Email:Graph:TenantId</c>, <c>Email:Graph:ClientId</c>, <c>Email:Graph:ClientSecret</c>). The app
/// needs the Graph <c>Mail.Send</c> application permission. This exists because SMTP relays such as
/// Mailjet refuse connections from the cluster's node IPs, while HTTPS to Microsoft is not filtered.
/// </para>
/// <para>
/// Graph settings are checked when a message is sent, not here. The helper is resolved from DI at
/// startup in several services, and a missing mail secret must cost the mail, not the service.
/// </para>
/// </remarks>
public class EmailHelper
{
    private const string GraphProvider = "Graph";
    private const string GraphScope = "https://graph.microsoft.com/.default";
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

    /// <summary>
    /// Shared across instances: the helper is created per send in some callers, and a client per
    /// instance would leak sockets. Pooled connections are recycled so a long-running pod picks up
    /// Microsoft's front-door IP changes instead of holding a dead connection until it restarts.
    /// </summary>
    private static readonly HttpClient sharedHttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Access tokens, keyed by tenant and client, reused until shortly before they expire.</summary>
    private static readonly Dictionary<string, (string Token, DateTimeOffset ExpiresAt)> tokenCache = [];
    private static readonly SemaphoreSlim tokenLock = new(1, 1);

    private readonly bool useGraph;

    private readonly string host = string.Empty;
    private readonly int port;
    private readonly string username = string.Empty;
    private readonly string password = string.Empty;

    private readonly string? graphTenantId;
    private readonly string? graphClientId;
    private readonly string? graphClientSecret;
    private readonly string? graphSenderAddress;


    public EmailHelper(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        useGraph = string.Equals(configuration["Email:Provider"], GraphProvider, StringComparison.OrdinalIgnoreCase);
        if (useGraph)
        {
            graphTenantId = configuration["Email:Graph:TenantId"];
            graphClientId = configuration["Email:Graph:ClientId"];
            graphClientSecret = configuration["Email:Graph:ClientSecret"];
            graphSenderAddress = configuration["Email:Graph:SenderAddress"];
            return;
        }

        host = configuration["Email:Host"] ?? throw new ArgumentNullException(nameof(configuration));
        port = int.Parse(configuration["Email:Port"] ?? throw new ArgumentNullException(nameof(configuration)));
        username = configuration["Email:Username"] ?? throw new ArgumentNullException(nameof(configuration));
        password = configuration["Email:Password"] ?? throw new ArgumentNullException(nameof(configuration));
    }

    public async Task SendEmailAsync(string subject, string bodyHtml, string to, string from)
    {
        await SendEmailAsync(subject, bodyHtml, to, from, bcc: null);
    }

    /// <summary>
    /// Creates the SMTP client a send runs through.
    /// </summary>
    /// <remarks>
    /// Virtual only to give tests a seam; without it there is no way to exercise a send without a
    /// real mail server. Production always uses MailKit's <see cref="SmtpClient"/>.
    /// </remarks>
    protected virtual ISmtpClient CreateSmtpClient() => new SmtpClient();

    /// <summary>
    /// The HTTP client Graph requests go through.
    /// </summary>
    /// <remarks>
    /// Virtual only to give tests a seam. Production always uses one shared client.
    /// </remarks>
    protected virtual HttpClient GetHttpClient() => sharedHttpClient;

    public async Task SendEmailAsync(string subject, string bodyHtml, string to, string from, string? bcc)
    {
        // Parsed up front for both transports, so a malformed address fails the same way either way.
        var fromAddress = MailboxAddress.Parse(from);
        var toAddress = MailboxAddress.Parse(to);
        var bccAddress = string.IsNullOrWhiteSpace(bcc) ? null : MailboxAddress.Parse(bcc);

        if (useGraph)
        {
            await SendWithGraphAsync(subject, bodyHtml, toAddress, fromAddress, bccAddress);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(fromAddress);
        message.To.Add(toAddress);
        if (bccAddress != null)
        {
            message.Bcc.Add(bccAddress);
        }
        message.Subject = subject;

        var bodyBuilder = new BodyBuilder
        {
            HtmlBody = bodyHtml
        };
        message.Body = bodyBuilder.ToMessageBody();

        using var client = CreateSmtpClient();
        await client.ConnectAsync(host, port, SecureSocketOptions.StartTls);
        await client.AuthenticateAsync(username, password);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    #region Microsoft Graph

    private async Task SendWithGraphAsync(string subject, string bodyHtml, MailboxAddress to, MailboxAddress from, MailboxAddress? bcc)
    {
        var tenantId = RequireGraphSetting(graphTenantId, "Email:Graph:TenantId");
        var clientId = RequireGraphSetting(graphClientId, "Email:Graph:ClientId");
        var clientSecret = RequireGraphSetting(graphClientSecret, "Email:Graph:ClientSecret");
        var sender = RequireGraphSetting(graphSenderAddress, "Email:Graph:SenderAddress");

        // Graph sends as the mailbox in the URL, and rejects a From it has no send-as right for. Callers
        // pass several From addresses (support@, noreply@), so the address is always the configured
        // mailbox and only the caller's display name is kept.
        var fromName = string.IsNullOrWhiteSpace(from.Name) ? "Red Mist" : from.Name;

        var request = new GraphSendMailRequest(
            new GraphMessage(
                subject,
                new GraphBody("HTML", bodyHtml),
                From: new GraphRecipient(new GraphEmailAddress(sender, fromName)),
                ToRecipients: [ToRecipient(to)],
                BccRecipients: bcc == null ? null : [ToRecipient(bcc)]),
            SaveToSentItems: false);

        var token = await GetGraphTokenAsync(tenantId, clientId, clientSecret);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post,
            $"{GraphBaseUrl}/users/{Uri.EscapeDataString(sender)}/sendMail")
        {
            Content = JsonContent.Create(request, options: jsonOptions),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await GetHttpClient().SendAsync(httpRequest);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // A revoked or rotated secret leaves a cached token that will never work again.
                await ForgetTokenAsync(tenantId, clientId);
            }

            var error = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Graph sendMail as {sender} failed with {(int)response.StatusCode} {response.StatusCode}: {error}",
                inner: null, response.StatusCode);
        }
    }

    private static GraphRecipient ToRecipient(MailboxAddress address)
        => new(new GraphEmailAddress(address.Address, string.IsNullOrWhiteSpace(address.Name) ? null : address.Name));

    private static string RequireGraphSetting(string? value, string key)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Email:Provider is Graph but {key} is not set.")
            : value;

    private static string TokenCacheKey(string tenantId, string clientId) => $"{tenantId}|{clientId}";

    private async Task<string> GetGraphTokenAsync(string tenantId, string clientId, string clientSecret)
    {
        var key = TokenCacheKey(tenantId, clientId);
        await tokenLock.WaitAsync();
        try
        {
            if (tokenCache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return cached.Token;
            }

            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post,
                $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["scope"] = GraphScope,
                }),
            };

            using var response = await GetHttpClient().SendAsync(tokenRequest);
            if (!response.IsSuccessStatusCode)
            {
                // The token endpoint's error body names the problem (bad secret, wrong tenant) and does
                // not echo the secret back.
                var error = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Graph token request for client {clientId} failed with {(int)response.StatusCode} {response.StatusCode}: {error}",
                    inner: null, response.StatusCode);
            }

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>()
                ?? throw new InvalidOperationException("Graph token response was empty.");
            if (string.IsNullOrEmpty(token.AccessToken))
            {
                throw new InvalidOperationException("Graph token response had no access_token.");
            }

            // Refresh a few minutes early so a token never expires between the check and the send.
            var lifetime = TimeSpan.FromSeconds(Math.Max(0, token.ExpiresIn - 300));
            tokenCache[key] = (token.AccessToken, DateTimeOffset.UtcNow + lifetime);
            return token.AccessToken;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private static async Task ForgetTokenAsync(string tenantId, string clientId)
    {
        await tokenLock.WaitAsync();
        try
        {
            tokenCache.Remove(TokenCacheKey(tenantId, clientId));
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record GraphSendMailRequest(GraphMessage Message, bool SaveToSentItems);

    private sealed record GraphMessage(
        string Subject,
        GraphBody Body,
        GraphRecipient From,
        GraphRecipient[] ToRecipients,
        GraphRecipient[]? BccRecipients);

    private sealed record GraphBody(string ContentType, string Content);

    private sealed record GraphRecipient(GraphEmailAddress EmailAddress);

    private sealed record GraphEmailAddress(string Address, string? Name);

    #endregion
}
