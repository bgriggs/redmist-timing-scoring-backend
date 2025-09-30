using RedMist.TimingAndScoringService.EventStatus.InCarDriverMode;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarDriverMode;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.InCarDriverMode;

/// <summary>
/// Test implementation of ICarPositionProvider for unit testing
/// </summary>
public class TestCarPositionProvider : ICarPositionProvider
{
    private readonly List<CarPosition> carPositions;

    public TestCarPositionProvider(List<CarPosition> carPositions)
    {
        this.carPositions = carPositions;
    }

    public IReadOnlyList<CarPosition> GetCarPositions()
    {
        return carPositions.AsReadOnly();
    }

    public CarPosition? GetCarByNumber(string carNumber)
    {
        return carPositions.FirstOrDefault(c => c.Number == carNumber);
    }
}

/// <summary>
/// Test implementation of ICompetitorMetadataProvider for unit testing
/// </summary>
public class TestCompetitorMetadataProvider : ICompetitorMetadataProvider
{
    private readonly Dictionary<string, CompetitorMetadata> metadata;

    public TestCompetitorMetadataProvider(Dictionary<string, CompetitorMetadata>? metadata = null)
    {
        this.metadata = metadata ?? new Dictionary<string, CompetitorMetadata>();
    }

    public Task<CompetitorMetadata?> GetCompetitorMetadataAsync(int eventId, string carNumber)
    {
        metadata.TryGetValue(carNumber, out var result);
        return Task.FromResult(result);
    }

    public void AddMetadata(string carNumber, CompetitorMetadata metadata)
    {
        this.metadata[carNumber] = metadata;
    }
}

/// <summary>
/// Test implementation of IInCarUpdateSender for unit testing
/// </summary>
public class TestInCarUpdateSender : IInCarUpdateSender
{
    private readonly List<List<InCarPayload>> sentUpdates = new();

    public Task SendUpdatesAsync(List<InCarPayload> changes, CancellationToken cancellationToken = default)
    {
        sentUpdates.Add(new List<InCarPayload>(changes));
        return Task.CompletedTask;
    }

    public IReadOnlyList<List<InCarPayload>> GetSentUpdates()
    {
        return sentUpdates.AsReadOnly();
    }

    public int UpdateCallCount => sentUpdates.Count;

    public List<InCarPayload> GetLastSentUpdate()
    {
        return sentUpdates.LastOrDefault() ?? new List<InCarPayload>();
    }

    public void Clear()
    {
        sentUpdates.Clear();
    }
}