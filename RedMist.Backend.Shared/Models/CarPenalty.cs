using System.Text.Json.Serialization;

namespace RedMist.Backend.Shared.Models;

public class CarPenalty(int warnings, int laps, int blackFlags)
{
    [JsonPropertyName("w")]
    public int Warnings { get; set; } = warnings;
    [JsonPropertyName("l")]
    public int Laps { get; set; } = laps;
    [JsonPropertyName("bf")]
    public int BlackFlags { get; set; } = blackFlags;
}
