namespace RedMist.EventProcessor.Tests.EventStatus.RMonitor;

internal class TestDataReader
{
    private List<string> data = [];

    public TestDataReader(string file)
    {
        var log = File.ReadAllText(file);
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("$"))
            {
                data.Add(line);
            }
        }
    }

    public List<string> GetData()
    {
        return data;
    }
}