using RedMist.Database.Models;
using RedMist.EventManagement.Models;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RedMist.TimingAndScoringService.Tests.EventManagement.Controllers;

/// <summary>
/// Pins how the review DTOs put their enums on the wire.
/// </summary>
/// <remarks>
/// System.Text.Json writes an enum as its numeric value by default, and this service configures no
/// JSON options, so the review page received <c>"state": 1</c> while its client was written against
/// <c>"state": "PendingReview"</c>. Nothing failed loudly: the request side binds enum names through
/// model binding regardless, so filtering worked, the list arrived populated, and only rendering a
/// row broke. Every test here passed at the time, because none of them looked at the JSON.
///
/// The numbers are also the fragile half of the contract -- reordering a member renumbers every
/// value after it, which changes the meaning of already-stored requests -- so the names are what
/// this API promises.
/// </remarks>
[TestClass]
public class SocialWireFormatTests
{
    /// <summary>
    /// What ASP.NET Core actually serializes responses with. Constructed the same way here so that
    /// this test cannot pass under settings the service does not use.
    /// </summary>
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void Summary_WritesStateAsItsName()
    {
        var json = JsonSerializer.Serialize(
            new SocialPostSummary { State = SocialPostState.PendingReview }, WebOptions);

        StringAssert.Contains(json, "\"state\":\"PendingReview\"",
            "The review client compares state against names and calls toLowerCase on it.");
    }

    [TestMethod]
    public void Detail_WritesStateAsItsName()
    {
        var json = JsonSerializer.Serialize(
            new SocialPostDetail { State = SocialPostState.Publishing }, WebOptions);

        StringAssert.Contains(json, "\"state\":\"Publishing\"");
    }

    [TestMethod]
    public void Summary_WritesKindAndChannelAsNames()
    {
        var json = JsonSerializer.Serialize(
            new SocialPostSummary { Kind = SocialPostKind.EventResults, Channel = SocialChannel.Facebook },
            WebOptions);

        StringAssert.Contains(json, "\"kind\":\"EventResults\"");
        StringAssert.Contains(json, "\"channel\":\"Facebook\"");
    }

    /// <summary>
    /// Every state has to survive the round trip, not just the one a spot check happens to name.
    /// </summary>
    [TestMethod]
    public void EveryStateRoundTripsThroughItsName()
    {
        foreach (var state in Enum.GetValues<SocialPostState>())
        {
            var json = JsonSerializer.Serialize(new SocialPostDetail { State = state }, WebOptions);

            StringAssert.Contains(json, $"\"state\":\"{state}\"",
                $"{state} did not serialize as its name.");

            var back = JsonSerializer.Deserialize<SocialPostDetail>(json, WebOptions);
            Assert.AreEqual(state, back!.State, $"{state} did not survive the round trip.");
        }
    }

    /// <summary>
    /// The converter is applied per property, so a DTO gaining an enum without it would reintroduce
    /// exactly this bug for that field alone -- and, as before, only where a page renders it.
    /// </summary>
    [TestMethod]
    public void EveryEnumPropertyOnTheReviewDtosDeclaresTheNameConverter()
    {
        Type[] dtos = [typeof(SocialPostSummary), typeof(SocialPostDetail)];

        var unconverted = dtos
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => (Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType).IsEnum)
                .Where(p => p.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType
                            != typeof(JsonStringEnumConverter))
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        Assert.AreEqual(0, unconverted.Count,
            "These serialize as numbers, which the review client cannot read: "
            + string.Join(", ", unconverted));
    }
}
