using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RedMist.Social.Generation;

/// <summary>
/// Generates copy with the Anthropic Messages API.
/// </summary>
/// <remarks>
/// Hand-rolled against the documented HTTP contract rather than taking an SDK dependency: this uses
/// one endpoint and four fields of it, and the surface is small enough that a client library would be
/// more to keep current than to maintain. Retries transport failures only -- whether the copy is any
/// good is <see cref="PostComposer"/>'s question, not this one's.
/// </remarks>
public sealed class ClaudeCopyGenerator : ICopyGenerator
{
    /// <summary>Name of the <see cref="HttpClient"/> this generator resolves from the factory.</summary>
    public const string HttpClientName = "Claude";

    private const string MessagesEndpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiKeyHeader = "x-api-key";
    private const string VersionHeader = "anthropic-version";
    private const string ApiVersion = "2023-06-01";

    /// <summary>
    /// Attempts made against a failing endpoint before giving up on the event for this run. Bounded
    /// low: the job runs on a schedule, so a genuinely down API costs one cycle rather than the post.
    /// </summary>
    private const int MaxTransportAttempts = 4;

    /// <summary>Cap on a server-supplied Retry-After, so a large value cannot stall the whole job.</summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory httpClientFactory;
    private readonly string apiKey;
    private readonly ILogger logger;
    private readonly TimeProvider clock;

    /// <param name="httpClientFactory">Supplies the pooled handler, so repeated runs cannot exhaust sockets.</param>
    /// <param name="apiKey">
    /// Anthropic API key. Held here and attached per request rather than baked into the named client
    /// so it stays out of any handler that might be reused or logged; it is never written to a log.
    /// </param>
    /// <param name="loggerFactory">Logging.</param>
    /// <param name="timeProvider">Clock, so retry backoff can be exercised in tests without waiting.</param>
    public ClaudeCopyGenerator(
        IHttpClientFactory httpClientFactory,
        string apiKey,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        this.httpClientFactory = httpClientFactory;
        this.apiKey = apiKey;
        logger = loggerFactory.CreateLogger<ClaudeCopyGenerator>();
        clock = timeProvider ?? TimeProvider.System;
    }

    public async Task<GeneratedCopy> GenerateAsync(CopyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payload = new MessagesRequest(
            Model: request.Model,
            MaxTokens: request.MaxTokens,
            System: request.SystemPrompt,
            Messages: [.. request.Messages.Select(m => new MessageDto(ToApiRole(m.Role), m.Text))]);

        var body = JsonSerializer.Serialize(payload, SerializerOptions);
        var client = httpClientFactory.CreateClient(HttpClientName);

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Rebuilt each time: an HttpRequestMessage cannot be sent twice.
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            httpRequest.Headers.Add(ApiKeyHeader, apiKey);
            httpRequest.Headers.Add(VersionHeader, ApiVersion);

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(httpRequest, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < MaxTransportAttempts)
            {
                await DelayBeforeRetryAsync(attempt, retryAfter: null, ex.Message, cancellationToken);
                continue;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A client timeout, which HttpClient surfaces as a cancellation rather than an
                // HttpRequestException. It is the single most retryable failure there is, and letting
                // it out as an OperationCanceledException would also make it indistinguishable from
                // host shutdown to every caller above.
                if (attempt >= MaxTransportAttempts)
                    throw new CopyGenerationException($"Anthropic API did not respond within the client timeout after {attempt} attempts.", ex);

                await DelayBeforeRetryAsync(attempt, retryAfter: null, "the request timed out", cancellationToken);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    return Parse(content, request.Model);
                }

                var error = await ReadErrorAsync(response, cancellationToken);

                if (IsRetryable(response.StatusCode) && attempt < MaxTransportAttempts)
                {
                    await DelayBeforeRetryAsync(
                        attempt, response.Headers.RetryAfter?.Delta, $"HTTP {(int)response.StatusCode}: {error}",
                        cancellationToken);
                    continue;
                }

                throw new CopyGenerationException(
                    $"Anthropic API returned {(int)response.StatusCode} {response.StatusCode}: {error}");
            }
        }
    }

    /// <summary>
    /// 408, 409, 429 and 5xx are worth another go; any other 4xx means the request itself is wrong and
    /// will be wrong again. 529 is Anthropic's "overloaded", which is squarely transient.
    /// </summary>
    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests
        || (int)status >= 500;

    private async Task DelayBeforeRetryAsync(
        int attempt, TimeSpan? retryAfter, string reason, CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
        var delay = retryAfter is { } supplied && supplied > TimeSpan.Zero ? supplied : backoff;
        if (delay > MaxRetryDelay)
            delay = MaxRetryDelay;

        logger.LogWarning(
            "Anthropic request attempt {Attempt} of {MaxAttempts} failed ({Reason}); retrying in {Delay}s",
            attempt, MaxTransportAttempts, reason, delay.TotalSeconds);

        await Task.Delay(delay, clock, cancellationToken);
    }

    /// <summary>
    /// Reads an error body for the exception message. Anthropic's error payloads describe the request,
    /// never echo credentials, and are the only useful diagnostic when a model name or a quota is
    /// wrong, so they are worth carrying through.
    /// </summary>
    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(body) ? "(no body)" : body.Trim();
        }
        catch (Exception ex)
        {
            return $"(error body unreadable: {ex.Message})";
        }
    }

    private static GeneratedCopy Parse(string content, string requestedModel)
    {
        MessagesResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<MessagesResponse>(content, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new CopyGenerationException("Anthropic API returned a body that is not valid JSON.", ex);
        }

        if (parsed is null)
            throw new CopyGenerationException("Anthropic API returned an empty body.");

        // Concatenated rather than taking the first block: the API may split a reply across several
        // text blocks, and taking only one would silently publish a fragment.
        var text = string.Concat(
            (parsed.Content ?? []).Where(b => b.Type == "text").Select(b => b.Text ?? string.Empty));

        return new GeneratedCopy(
            Text: text.Trim(),
            Model: parsed.Model ?? requestedModel,
            WasTruncated: string.Equals(parsed.StopReason, "max_tokens", StringComparison.Ordinal),
            StopReason: parsed.StopReason);
    }

    private static string ToApiRole(CopyRole role) => role switch
    {
        CopyRole.User => "user",
        CopyRole.Assistant => "assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown conversation role"),
    };

    private sealed record MessagesRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("messages")] IReadOnlyList<MessageDto> Messages);

    private sealed record MessageDto(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record MessagesResponse(
        [property: JsonPropertyName("content")] List<ContentBlock>? Content,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("stop_reason")] string? StopReason);

    private sealed record ContentBlock(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("text")] string? Text);
}

/// <summary>Raised when the generation API could not be made to produce a candidate.</summary>
public class CopyGenerationException : Exception
{
    public CopyGenerationException(string message) : base(message) { }

    public CopyGenerationException(string message, Exception innerException) : base(message, innerException) { }

    public CopyGenerationException() { }
}
