using System.Text.Json.Serialization;

namespace RedMist.ExternalDataCollection.Models;

public class PublicStreams
{
    [JsonPropertyName("youtube_public_url")]
    public string YouTubeUrl{get; set; } = string.Empty;

    [JsonPropertyName("svnPublicURL")]
    public string SvnUrl { get; set; } = string.Empty;

    [JsonPropertyName("transponderNumber")]
    public string TransponderIdStr { get; set; } = string.Empty;

    [JsonPropertyName("driverName")]
    public string DriverName { get; set; } = string.Empty;

    [JsonIgnore]
    public uint TransponderId
    {
        get
        {
            _ = uint.TryParse(TransponderIdStr, out var id);
            return id;
        }
    }
}
