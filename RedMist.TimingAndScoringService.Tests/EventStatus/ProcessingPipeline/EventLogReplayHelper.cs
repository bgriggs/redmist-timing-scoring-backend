using System.Globalization;

namespace RedMist.EventProcessor.Tests.EventStatus.ProcessingPipeline;

/// <summary>
/// Replays a capture of the EventStatusLogs table, which holds every message an event processor
/// received in the order it received them. Unlike the RMonitor captures the base class reads, each
/// record here carries the message type it was logged under, so a capture can interleave the timing
/// feed with the other sources an event runs on.
/// </summary>
/// <remarks>
/// Format is the RMonitor capture's, with the type appended to the timestamp line:
/// <code>
/// ##2026-08-02 13:08:50.305|flags
/// [{"f":2,"s":"2026-08-02T09:08:50","e":null}]
/// </code>
/// A record runs to the next timestamp line, so a payload may span lines. A line missing the type
/// is read as RMonitor, matching the older captures. A malformed record throws rather than being
/// absorbed into its neighbour: a capture that replays all but one of its messages would show up as
/// an unexplained assertion failure somewhere downstream.
/// </remarks>
internal class EventLogReplayHelper(string filePath) : RMonitorTestDataHelper(filePath)
{
    public override async Task LoadAsync()
    {
        events.Clear();
        replayIndex = 0;

        DateTime ts = default;
        string type = string.Empty;
        var data = new List<string>();

        foreach (var line in await File.ReadAllLinesAsync(filePath))
        {
            // Only a timestamp line starts a record; anything else is payload, so a JSON body
            // containing a # is not mistaken for the start of the next one.
            if (!line.StartsWith("##", StringComparison.Ordinal))
            {
                data.Add(line);
                continue;
            }

            AddRecord(ts, type, data);
            (ts, type) = ParseHeader(line);
            data.Clear();
        }

        AddRecord(ts, type, data);
    }

    private void AddRecord(DateTime ts, string type, List<string> data)
    {
        // The first header has no record before it.
        if (type.Length == 0)
            return;

        var payload = string.Join('\n', data).Trim();
        if (payload.Length == 0)
            throw new InvalidDataException($"Record at {ts:yyyy-MM-dd HH:mm:ss.fff} in {filePath} has no payload.");

        events.Add((ts, payload, type));
    }

    private (DateTime Timestamp, string Type) ParseHeader(string line)
    {
        var header = line[2..].Trim();
        var type = Backend.Shared.Consts.RMONITOR_TYPE;

        var separator = header.IndexOf('|');
        if (separator >= 0)
        {
            type = header[(separator + 1)..].Trim();
            header = header[..separator].Trim();
        }

        if (type.Length == 0 || !DateTime.TryParseExact(header, "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
        {
            throw new InvalidDataException($"Malformed record header in {filePath}: {line}");
        }

        return (ts, type);
    }
}
