using Microsoft.OpenApi;
using RedMist.Database.Models;
using RedMist.EventManagement;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace RedMist.TimingAndScoringService.Tests.EventManagement;

/// <summary>
/// The OpenAPI document has to agree with what the endpoints actually send.
/// </summary>
/// <remarks>
/// Swashbuckle derives an enum's schema from the service's serializer options and does not see a
/// converter applied to a single property, so without this filter the document described the review
/// enums as integers while the responses carried names. Clients in this codebase are generated from
/// documents like this one, so a generated review client would have been typed to numbers and would
/// have broken the same way the hand-written one did.
/// </remarks>
[TestClass]
public class StringEnumSchemaFilterTests
{
    /// <summary>
    /// Applies the filter to a type. The context carries a good deal that a schema filter may use;
    /// this one reads only <c>Type</c>, so an uninitialized instance is enough and avoids standing up
    /// a generator to test four lines.
    /// </summary>
    private static OpenApiSchema Apply(Type type, JsonSchemaType startingType = JsonSchemaType.Integer)
    {
        var schema = new OpenApiSchema { Type = startingType, Format = "int32" };

        // The filter reads only Type; the generator and repository are what a filter would need to
        // build a nested schema, and standing those up would test Swashbuckle rather than this.
        var context = new SchemaFilterContext(
            type, schemaGenerator: null!, schemaRepository: null!, memberInfo: null, parameterInfo: null);

        new StringEnumSchemaFilter().Apply(schema, context);
        return schema;
    }

    [TestMethod]
    public void AReviewEnum_IsDescribedAsItsNames()
    {
        var schema = Apply(typeof(SocialPostState));

        Assert.AreEqual(JsonSchemaType.String, schema.Type);
        Assert.IsNull(schema.Format, "int32 would survive as a format and contradict the type.");

        CollectionAssert.AreEquivalent(
            Enum.GetNames<SocialPostState>(),
            schema.Enum!.Select(v => v!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    public void KindAndChannel_AreDescribedAsTheirNames()
    {
        Assert.AreEqual(JsonSchemaType.String, Apply(typeof(SocialPostKind)).Type);
        Assert.AreEqual(JsonSchemaType.String, Apply(typeof(SocialChannel)).Type);
    }

    /// <summary>
    /// The filter reads the DTOs, so it must not reach an enum that no review DTO converts -- those
    /// really are integers on the wire, and describing them otherwise would break the clients that
    /// already read them.
    /// </summary>
    [TestMethod]
    public void AnEnumTheReviewDtosDoNotUse_IsLeftAlone()
    {
        var schema = Apply(typeof(FlagsEnumForTest));

        Assert.AreEqual(JsonSchemaType.Integer, schema.Type);
        Assert.AreEqual("int32", schema.Format);
    }

    private enum FlagsEnumForTest
    {
        Something,
    }
}
