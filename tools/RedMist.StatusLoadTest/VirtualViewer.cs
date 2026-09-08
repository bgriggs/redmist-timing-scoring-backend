using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Text;

namespace RedMist.StatusLoadTest;

/// <summary>
/// One simulated browser on the timing page.
///
/// Deliberately mirrors what the Angular viewer does rather than what is convenient to write: a
/// websocket opened with negotiation skipped and the MessagePack protocol, an immediate full-state
/// fetch, a subscribe, and then a full-state poll on the same interval. Anything the browser sends
/// that costs the server work - gzip, the msgpack accept header - is sent here too, because leaving
/// it out is how a load test comes back green on a path production never takes.
/// </summary>
public sealed class VirtualViewer
{
    private const string AccessCodeHeader = "X-Event-Access-Code";

    private readonly Options options;
    private readonly ClientStats stats;
    private readonly Recorder recorder;
    private readonly HttpClient http;
    private HubConnection? hub;
    private Task? pollLoop;
    private volatile bool stopping;

    public VirtualViewer(int index, Options options, Recorder recorder)
    {
        this.options = options;
        this.recorder = recorder;
        stats = recorder[index];
        stats.ClientIp = SyntheticIp(index);

        // A connection pool each, rather than one shared across the run. A shared pool would carry
        // every client's polls over a handful of sockets, and since the service load balances per
        // connection that would pin the whole audience's traffic onto a couple of replicas - the
        // opposite of what is being measured.
        http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            UseCookies = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RedMist-StatusLoadTest/1.0");
    }

    /// <summary>
    /// A distinct address per client out of 198.18.0.0/15, the range reserved for benchmarking.
    /// It is not routable, so a synthetic client can never land in the same rate limiter partition
    /// as a real viewer watching the same event while the run is going on.
    /// </summary>
    private static string SyntheticIp(int index) => $"198.18.{(index / 256) % 256}.{index % 256}";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.WebSocket)
            await ConnectAsync(cancellationToken);
        if (options.Poll)
            pollLoop = Task.Run(() => PollLoopAsync(cancellationToken), CancellationToken.None);
    }

    #region Websocket

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        hub = BuildConnection();
        RegisterHandlers(hub);

        var sw = Stopwatch.StartNew();
        stats.ConnectAttempts++;
        try
        {
            await hub.StartAsync(cancellationToken);
            stats.ConnectMs = sw.Elapsed.TotalMilliseconds;
            stats.EverConnected = true;
            stats.Subscribed = await SubscribeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            stats.ConnectError = ex.GetType().Name + ": " + ex.Message;
        }
    }

    private HubConnection BuildConnection()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(options.HubUrl, o =>
            {
                // The viewer skips negotiation so the websocket upgrade is the only request, which
                // is what makes it work across replicas without sticky sessions.
                o.SkipNegotiation = true;
                o.Transports = HttpTransportType.WebSockets;
                if (options.SpoofClientIp)
                    o.Headers["X-Forwarded-For"] = stats.ClientIp;
                if (!string.IsNullOrEmpty(options.AccessToken))
                    o.AccessTokenProvider = () => Task.FromResult<string?>(options.AccessToken);
            })
            .AddMessagePackProtocol(o =>
            {
                o.SerializerOptions = MessagePackSerializerOptions.Standard
                    .WithResolver(ContractlessStandardResolver.Instance);
            })
            .WithAutomaticReconnect(new ViewerRetryPolicy())
            .Build();

        connection.Reconnecting += _ =>
        {
            stats.Drops++;
            return Task.CompletedTask;
        };
        connection.Reconnected += async _ =>
        {
            stats.Reconnects++;
            stats.Subscribed = await SubscribeAsync(CancellationToken.None);
        };
        connection.Closed += _ =>
        {
            // Closed also fires for the teardown at the end of a run, which is not a drop.
            if (!stopping)
                stats.Drops++;
            return Task.CompletedTask;
        };

        return connection;
    }

    /// <summary>
    /// Handlers take the payload untyped on purpose. Binding the real patch types would make the
    /// run fail on any model change between this tool and the deployed server - the exact drift a
    /// load test is least able to afford - and the identity of a broadcast is all that is needed
    /// here, which its content gives without knowing the schema.
    /// </summary>
    private void RegisterHandlers(HubConnection connection)
    {
        connection.On("ReceiveSessionPatch", [typeof(object)], args =>
        {
            var now = Stopwatch.GetTimestamp();
            stats.SessionPatches++;
            if (stats.LastPatchTicks != 0)
                stats.PatchGapsMs.Add(Elapsed(stats.LastPatchTicks, now).TotalMilliseconds);
            stats.LastPatchTicks = now;
            recorder.RecordBroadcast(stats, "session", Describe(args[0]), now);
            return Task.CompletedTask;
        });

        connection.On("ReceiveCarPatches", [typeof(object)], args =>
        {
            var now = Stopwatch.GetTimestamp();
            stats.CarPatchMessages++;
            stats.CarPatchCars += args[0] is object?[] cars ? cars.Length : 1;
            if (stats.LastPatchTicks != 0)
                stats.PatchGapsMs.Add(Elapsed(stats.LastPatchTicks, now).TotalMilliseconds);
            stats.LastPatchTicks = now;
            recorder.RecordBroadcast(stats, "cars", Describe(args[0]), now);
            return Task.CompletedTask;
        });

        connection.On("ReceiveReset", () => { stats.Resets++; });
    }

/// <summary>
    /// Reports whether the client is actually subscribed, which is not the same as connected: a
    /// socket that opened but whose subscribe failed receives nothing, and counting it as part of
    /// the audience would report every broadcast as having missed someone.
    /// </summary>
    private async Task<bool> SubscribeAsync(CancellationToken cancellationToken)
    {
        if (hub == null)
            return false;
        try
        {
            if (string.IsNullOrEmpty(options.AccessCode))
                await hub.InvokeAsync("SubscribeToEventV2", options.EventId, cancellationToken);
            else
                await hub.InvokeAsync("SubscribeToEventV2WithCode", options.EventId, options.AccessCode, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            stats.ConnectError ??= "subscribe: " + ex.Message;
            return false;
        }
    }

    /// <summary>Renders a decoded payload to a string that is equal for equal payloads.</summary>
    private static string Describe(object? value)
    {
        var sb = new StringBuilder();
        Append(sb, value);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append('~');
                break;
            case string s:
                sb.Append('"').Append(s).Append('"');
                break;
            case IDictionary<object, object> map:
                sb.Append('{');
                foreach (var pair in map)
                {
                    Append(sb, pair.Key);
                    sb.Append(':');
                    Append(sb, pair.Value);
                    sb.Append(',');
                }
                sb.Append('}');
                break;
            case System.Collections.IEnumerable list:
                sb.Append('[');
                foreach (var item in list)
                {
                    Append(sb, item);
                    sb.Append(',');
                }
                sb.Append(']');
                break;
            default:
                sb.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
        }
    }

    #endregion

    #region Polling

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        // The viewer loads the event once, fetches full state, and only then starts its timer.
        await SendAsync($"{options.ApiUrl}/v2/Events/LoadEvent?eventId={options.EventId}", cancellationToken);

        var period = TimeSpan.FromSeconds(options.PollIntervalSeconds);
        var url = $"{options.ApiUrl}/v2/Events/GetCurrentSessionState?eventId={options.EventId}";
        await SendAsync(url, cancellationToken);

        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await SendAsync(url, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendAsync(string url, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/x-msgpack");
        if (options.SpoofClientIp)
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", stats.ClientIp);
        if (!string.IsNullOrEmpty(options.AccessCode))
            request.Headers.TryAddWithoutValidation(AccessCodeHeader, options.AccessCode);
        if (!string.IsNullOrEmpty(options.AccessToken))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + options.AccessToken);

        var start = Stopwatch.GetTimestamp();
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            stats.Polls++;
            stats.PollBytes += body.Length;
            stats.PollMs.Add(Elapsed(start, Stopwatch.GetTimestamp()).TotalMilliseconds);
            var code = (int)response.StatusCode;
            stats.PollStatuses[code] = stats.PollStatuses.GetValueOrDefault(code) + 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The run is stopping; a poll abandoned at teardown is not a result.
        }
        catch (Exception)
        {
            // Deliberately catches HttpClient's own timeout, which arrives as a
            // TaskCanceledException with the caller's token still unset. Treating that as a
            // cancellation would let a service that has stopped answering altogether report as a
            // run with no failures - the one outcome a load test must never hide.
            stats.Polls++;
            stats.PollErrors++;
        }
    }

    #endregion

    public async Task StopAsync()
    {
        stopping = true;
        if (hub != null)
        {
            try
            {
                await hub.DisposeAsync();
            }
            catch
            {
                // A connection torn down at the end of a run has nothing left to report.
            }
        }

        // Join the poll loop before the caller reads this client's samples. The report enumerates
        // PollMs and PollStatuses, and a straggler request still writing to them would fault that
        // enumeration at the last possible moment - after the whole run has been paid for.
        if (pollLoop != null)
        {
            try
            {
                await pollLoop;
            }
            catch
            {
                // Whatever the loop hit is already recorded as a poll error.
            }
        }

        // Only safe once nothing can still be sending on it.
        http.Dispose();
    }

    private static TimeSpan Elapsed(long from, long to) => Stopwatch.GetElapsedTime(from, to);

    /// <summary>The viewer's backoff, so a mid-run blip recovers on the same schedule production does.</summary>
    private sealed class ViewerRetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays =
        [
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            retryContext.PreviousRetryCount < Delays.Length
                ? Delays[retryContext.PreviousRetryCount]
                : TimeSpan.FromSeconds(30);
    }
}
