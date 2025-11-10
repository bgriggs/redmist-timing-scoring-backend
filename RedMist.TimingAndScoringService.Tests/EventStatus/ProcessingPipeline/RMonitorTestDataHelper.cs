using System.Globalization;

namespace RedMist.EventProcessor.Tests.EventStatus.ProcessingPipeline;

internal class RMonitorTestDataHelper(string filePath)
{
    protected readonly List<(DateTime ts, string data, string type)> events = [];
    protected int replayIndex = 0;
    public int Count => events.Count;
    public bool IsFinished => replayIndex >= events.Count;


    public async Task LoadAsync()
    {
        var raw = await File.ReadAllTextAsync(filePath);
        var packets = raw.Split("##");

        foreach (var packet in packets)
        {
            var tsLineEnd = packet.IndexOf('\n');
            if (tsLineEnd < 0)
                continue;
            var tsLine = packet[..(tsLineEnd + 1)].Trim();
            var ts = DateTime.ParseExact(tsLine, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var data = packet[(tsLineEnd + 1)..];
            events.Add((ts, data, "rmonitor"));
        }
    }

    public (DateTime ts, string data, string type) GetNextRecord()
    {
        if (replayIndex >= events.Count)
            return (DateTime.MinValue, string.Empty, string.Empty);
        var (ts, data, type) = events[replayIndex];
        replayIndex++;
        return (ts, data, type);
    }
}
