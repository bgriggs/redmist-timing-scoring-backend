using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RedMist.StatusLoadTest;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(Options.Help);
            return 0;
        }

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine();
            Console.WriteLine("Stopping...");
            stopping.Cancel();
        };

        PrintHeader(options);

        var recorder = new Recorder(options.Clients);
        var viewers = new VirtualViewer[options.Clients];
        for (var i = 0; i < options.Clients; i++)
            viewers[i] = new VirtualViewer(i, options, recorder);

        var reporter = Task.Run(() => ReportProgressAsync(options, recorder, stopping.Token));

        await RampUpAsync(options, viewers, recorder, stopping.Token);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(options.DurationSeconds), stopping.Token);
        }
        catch (OperationCanceledException)
        {
        }

        stopping.Cancel();
        await reporter;

        Console.WriteLine();
        Console.WriteLine("Closing connections...");
        await Task.WhenAll(viewers.Select(v => v.StopAsync()));

        var report = Report.Build(options, recorder);
        Console.WriteLine();
        report.Print();

        if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            // A distribution with no samples is NaN on purpose, and the default serializer
            // throws rather than write it - which would lose the file for every --no-poll,
            // --no-websocket or otherwise quiet run.
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            });
            await File.WriteAllTextAsync(options.OutputPath, json, CancellationToken.None);
            Console.WriteLine($"Wrote {options.OutputPath}");
        }

        // Only judge what the run actually exercised: a polling-only run opens no websocket, so
        // its connection count is zero by design and says nothing about whether it went well.
        var connectionsShort = options.WebSocket && report.ConnectedClients != options.Clients;
        return connectionsShort ? 1 : 0;
    }

    private static void PrintHeader(Options options)
    {
        Console.WriteLine("=== RedMist status load test ===");
        Console.WriteLine($"  api        {options.ApiUrl}");
        Console.WriteLine($"  hub        {options.HubUrl}");
        Console.WriteLine($"  event      {options.EventId}");
        Console.WriteLine($"  clients    {options.Clients}   ramp {options.RampSeconds:0.#}s   duration {options.DurationSeconds:0.#}s");
        Console.WriteLine($"  websocket  {(options.WebSocket ? "yes" : "no")}   poll {(options.Poll ? $"every {options.PollIntervalSeconds:0.#}s" : "no")}");
        Console.WriteLine($"  spoof ip   {(options.SpoofClientIp ? "yes (198.18.0.0/15 per client)" : "no")}");
        Console.WriteLine();
    }

    /// <summary>
    /// Brings the clients up over the ramp window. A ramp of zero is the case worth testing on
    /// purpose: everyone reloading at a restart, which arrives as one burst of handshakes and one
    /// burst of full-state fetches rather than as a steady audience.
    /// </summary>
    private static async Task RampUpAsync(Options options, VirtualViewer[] viewers, Recorder recorder,
        CancellationToken cancellationToken)
    {
        var gap = options.Clients > 1 && options.RampSeconds > 0
            ? TimeSpan.FromSeconds(options.RampSeconds / options.Clients)
            : TimeSpan.Zero;

        var starts = new List<Task>(viewers.Length);
        foreach (var viewer in viewers)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            starts.Add(viewer.StartAsync(cancellationToken));
            if (gap > TimeSpan.Zero)
                await Task.Delay(gap, CancellationToken.None);
        }

        await Task.WhenAll(starts);
        recorder.RampCompleteTicks = Stopwatch.GetTimestamp();
        Console.WriteLine($"  ramp complete: {recorder.ConnectedCount}/{options.Clients} connected");
        Console.WriteLine();
    }

    private static async Task ReportProgressAsync(Options options, Recorder recorder, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.ReportIntervalSeconds));
        var started = Stopwatch.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
                var rateLimited = recorder.Clients.Sum(c => c.PollStatuses.GetValueOrDefault(429));
                var failed = recorder.Clients.Sum(c => c.PollErrors)
                    + recorder.Clients.Sum(c => c.PollStatuses.Where(s => s.Key >= 500).Sum(s => s.Value));
                Console.WriteLine(
                    $"[{elapsed,5:0}s] connected {recorder.ConnectedCount,4}/{options.Clients}" +
                    $"  session {recorder.TotalSessionPatches,7}  cars {recorder.TotalCarPatchMessages,7}" +
                    $"  polls {recorder.TotalPolls,7}  429 {rateLimited,5}  fail {failed,4}" +
                    $"  drops {recorder.Clients.Sum(c => c.Drops),4}");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
