using Microsoft.OpenApi;
using RedMist.EventManagement.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RedMist.EventManagement;

/// <summary>
/// Describes the enums that go on the wire as names as strings in the document too.
/// </summary>
/// <remarks>
/// Swashbuckle builds an enum's schema from the serializer options the service was configured with,
/// and knows nothing about a <see cref="JsonConverterAttribute"/> applied to an individual property.
/// The review DTOs use exactly that, deliberately -- so that the endpoints the landing site and the
/// apps already read keep the format they were built against -- which leaves the document saying
/// <c>integer</c> about responses that carry <c>"PendingReview"</c>.
///
/// The review client is written by hand against the names, so nothing is broken by the document
/// today. It is worth correcting anyway: the document is what anyone reaches for who has not read
/// the controller, and one that describes a response as an integer is a working set of instructions
/// for reproducing the bug this filter exists because of.
/// </remarks>
public sealed class StringEnumSchemaFilter : ISchemaFilter
{
    /// <summary>
    /// Every enum the review models convert to names, read from the models themselves rather than
    /// listed, so the document cannot disagree with the attributes that decide the actual format --
    /// including for a model added later. <c>SocialWireFormatTests</c> guards the other direction.
    /// </summary>
    private static readonly HashSet<Type> NameSerialized =
    [
        .. ReviewModels
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType
                        == typeof(JsonStringEnumConverter))
            .Select(p => Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType)
            .Where(t => t.IsEnum)
    ];

    /// <summary>
    /// The models the review endpoints answer with. The whole namespace, because it holds nothing
    /// else -- and a namespace is what a new model joins without anybody remembering a list here.
    /// </summary>
    public static IEnumerable<Type> ReviewModels =>
        typeof(SocialPostSummary).Assembly
            .GetTypes()
            .Where(t => t.IsClass && t.IsPublic && t.Namespace == typeof(SocialPostSummary).Namespace);

    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        // The interface is the read-only view; only the concrete schema can be rewritten, and that is
        // what the generator passes for a type it built.
        if (!NameSerialized.Contains(context.Type) || schema is not OpenApiSchema writable)
            return;

        writable.Type = JsonSchemaType.String;
        writable.Format = null;
        writable.Enum = [.. Enum.GetNames(context.Type).Select(name => (JsonNode)name)];
    }
}
