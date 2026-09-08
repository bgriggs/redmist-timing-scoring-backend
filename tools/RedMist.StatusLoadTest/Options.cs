using System.Globalization;

namespace RedMist.StatusLoadTest;

/// <summary>
/// What a run looks like. The defaults describe one browser sitting on the timing page: a
/// websocket subscribed to the event, and a full-state poll every five seconds.
/// </summary>
public class Options
{
    /// <summary>Base address of the status API, e.g. http://redmist-test-redmist-status-api:8080.</summary>
    public string ApiUrl { get; set; } = "";

    /// <summary>
    /// Hub address. Defaults to <see cref="ApiUrl"/> + /event-status with the scheme swapped for ws/wss,
    /// which is how the browser derives it.
    /// </summary>
    public string HubUrl { get; set; } = "";

    public int EventId { get; set; }

    public int Clients { get; set; } = 200;

    /// <summary>
    /// Seconds to spread the connects over. Zero is the race-restart case where everyone reloads
    /// at once; the default spreads them out, which is what an audience arriving normally looks like.
    /// </summary>
    public double RampSeconds { get; set; } = 30;

    public double DurationSeconds { get; set; } = 300;

    /// <summary>Matches the 5s interval the timing viewer polls GetCurrentSessionState on.</summary>
    public double PollIntervalSeconds { get; set; } = 5;

    public bool Poll { get; set; } = true;
    public bool WebSocket { get; set; } = true;

    /// <summary>
    /// Give each virtual client its own X-Forwarded-For. The polling endpoint's rate limiter
    /// partitions on the client IP, so without this every client shares one partition and the run
    /// measures the rate limiter rather than the backend. Only has an effect when the request
    /// reaches the service without passing through something that sets its own forwarding headers.
    /// </summary>
    public bool SpoofClientIp { get; set; } = true;

    /// <summary>Optional bearer token. Public viewers are anonymous, which is the default.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Access code for a private event.</summary>
    public string? AccessCode { get; set; }

    /// <summary>Seconds between progress lines.</summary>
    public double ReportIntervalSeconds { get; set; } = 10;

    /// <summary>Optional path to write the full result as JSON.</summary>
    public string? OutputPath { get; set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            string Next() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"{key} needs a value");

            switch (key)
            {
                case "--api-url": o.ApiUrl = Next(); break;
                case "--hub-url": o.HubUrl = Next(); break;
                case "--event-id": o.EventId = Int(Next()); break;
                case "--clients": o.Clients = Int(Next()); break;
                case "--ramp": o.RampSeconds = Double(Next()); break;
                case "--duration": o.DurationSeconds = Double(Next()); break;
                case "--poll-interval": o.PollIntervalSeconds = Double(Next()); break;
                case "--report-interval": o.ReportIntervalSeconds = Double(Next()); break;
                case "--access-token": o.AccessToken = Next(); break;
                case "--access-code": o.AccessCode = Next(); break;
                case "--out": o.OutputPath = Next(); break;
                case "--no-poll": o.Poll = false; break;
                case "--no-websocket": o.WebSocket = false; break;
                case "--no-spoof-ip": o.SpoofClientIp = false; break;
                case "--help" or "-h": o.ShowHelp = true; break;
                default: throw new ArgumentException($"Unknown argument '{key}'. Try --help.");
            }
        }

        if (o.ShowHelp)
            return o;

        // Environment variables let the k8s Job be configured without rebuilding the args list.
        o.ApiUrl = Fallback(o.ApiUrl, "LOADTEST_API_URL");
        o.HubUrl = Fallback(o.HubUrl, "LOADTEST_HUB_URL");
        if (o.EventId == 0 && int.TryParse(Environment.GetEnvironmentVariable("LOADTEST_EVENT_ID"),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var envEvent))
            o.EventId = envEvent;

        if (string.IsNullOrWhiteSpace(o.ApiUrl))
            throw new ArgumentException("--api-url is required");
        if (o.EventId <= 0)
            throw new ArgumentException("--event-id is required and must be positive");
        if (o.Clients <= 0)
            throw new ArgumentException("--clients must be positive");
        if (!o.Poll && !o.WebSocket)
            throw new ArgumentException("--no-poll and --no-websocket together leave nothing to run");
        // A non-positive period reaches PeriodicTimer, which throws inside the fire-and-forget poll
        // loop: every client would stop after its two priming requests and the run would report
        // that handful of polls as though it were the whole thing.
        if (o.PollIntervalSeconds <= 0)
            throw new ArgumentException("--poll-interval must be positive");
        if (o.ReportIntervalSeconds <= 0)
            throw new ArgumentException("--report-interval must be positive");
        if (o.RampSeconds < 0)
            throw new ArgumentException("--ramp cannot be negative");
        if (o.DurationSeconds < 0)
            throw new ArgumentException("--duration cannot be negative");

        o.ApiUrl = o.ApiUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(o.HubUrl))
            o.HubUrl = DeriveHubUrl(o.ApiUrl);

        return o;
    }

    public bool ShowHelp { get; set; }

    private static int Int(string value) => int.Parse(value, CultureInfo.InvariantCulture);

    private static double Double(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    private static string Fallback(string value, string environmentVariable) =>
        string.IsNullOrWhiteSpace(value) ? Environment.GetEnvironmentVariable(environmentVariable) ?? "" : value;

    private static string DeriveHubUrl(string apiUrl)
    {
        var uri = new Uri(apiUrl + "/event-status");
        var scheme = uri.Scheme == "https" ? "wss" : "ws";
        return new UriBuilder(uri) { Scheme = scheme }.Uri.ToString();
    }

    public const string Help = """
        Simulates browser viewers against the status API: a websocket subscribed to an event plus
        the timing page's periodic full-state poll.

          --api-url <url>          Status API base address, including any path base (required)
          --hub-url <url>          Hub address (default: <api-url>/event-status over ws/wss)
          --event-id <id>          Event to subscribe to (required)
          --clients <n>            Virtual viewers (default 200)
          --ramp <seconds>         Spread the connects over this long (default 30, 0 = all at once)
          --duration <seconds>     How long to hold the load (default 300)
          --poll-interval <sec>    Full-state poll period (default 5, matches the viewer)
          --report-interval <sec>  Progress line period (default 10)
          --access-token <jwt>     Bearer token; omit to connect anonymously like a public viewer
          --access-code <code>     Access code for a private event
          --out <path>             Write the full result as JSON
          --no-poll                Websockets only
          --no-websocket           Polling only
          --no-spoof-ip            Do not send a per-client X-Forwarded-For

        The polling endpoint rate limits per client IP, so a run without a per-client
        X-Forwarded-For measures the rate limiter instead of the backend. Anything that sets its
        own forwarding headers in front of the service (a CDN, a tunnel) overrides the spoofed one,
        so point this at the service or ingress directly rather than at the public hostname.
        """;
}
