namespace RedMist.TimingAndScoringService.Models;

public class EventProcessorInstance
{
    public string PodName { get; set; } = string.Empty;
    public List<string> Events { get; set; } = [];
}
