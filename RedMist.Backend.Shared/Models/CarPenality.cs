using System.Text.Json.Serialization;

namespace RedMist.Backend.Shared.Models;

public class CarPenality(int warnings, int laps)
{
    [JsonPropertyName("w")]
    public int Warnings { get; set; } = warnings;
    [JsonPropertyName("l")]
    public int Laps { get; set; } = laps;
}
