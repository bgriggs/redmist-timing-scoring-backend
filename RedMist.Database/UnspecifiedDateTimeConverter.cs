using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RedMist.Database;

public class UnspecifiedDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Parse without time zone
        var raw = reader.GetString();
        if (string.IsNullOrEmpty(raw))
        {
            return DateTime.MinValue;
        }
        var dt = DateTime.Parse(raw, null, DateTimeStyles.AdjustToUniversal);
        return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        // Write in ISO 8601 format without time zone info
        writer.WriteStringValue(value.ToString("yyyy-MM-ddTHH:mm:ss"));
    }
}
