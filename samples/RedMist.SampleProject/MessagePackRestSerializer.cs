using MessagePack;
using RestSharp;
using RestSharp.Serializers;

namespace RedMist.SampleProject;

internal sealed class MessagePackRestSerializer : IRestSerializer, ISerializer, IDeserializer
{
    static readonly string[] supportedContentTypes = ["application/x-msgpack", "application/msgpack"];

    // IRestSerializer
    public ISerializer Serializer => this;
    public IDeserializer Deserializer => this;
    public string[] AcceptedContentTypes => supportedContentTypes;
    public SupportsContentType SupportsContentType => contentType =>
        supportedContentTypes.Any(ct => string.Equals(ct, contentType, StringComparison.OrdinalIgnoreCase));
    public DataFormat DataFormat => DataFormat.None;
    public string? Serialize(Parameter parameter) => null;

    // ISerializer
    ContentType ISerializer.ContentType { get; set; } = "application/x-msgpack";
    public string? Serialize(object obj) => null;

    // IDeserializer
    public T? Deserialize<T>(RestResponse response)
    {
        if (response.RawBytes is { Length: > 0 })
            return MessagePackSerializer.Deserialize<T>(response.RawBytes);
        return default;
    }
}
