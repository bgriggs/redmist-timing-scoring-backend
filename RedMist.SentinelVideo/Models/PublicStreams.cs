using System.Text.Json.Serialization;

namespace RedMist.SentinelVideo.Models;

public class PublicStreams
{
    [JsonPropertyName("youtube_public_url")]
    public string YouTubeUrl{get; set; } = string.Empty;

    [JsonPropertyName("svnPublicURL")]
    public string SvnUrl { get; set; } = string.Empty;

    [JsonPropertyName("transponderNumber")]
    public uint TransponderId { get; set; }

    [JsonPropertyName("driverName")]
    public string DriverName { get; set; } = string.Empty;
}
