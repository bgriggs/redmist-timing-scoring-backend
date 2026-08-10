using MessagePack.AspNetCoreMvcFormatter;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RedMist.Backend.Shared.Extensions;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// The API serves both JSON and MessagePack to the same routes. Mobile clients request MessagePack
/// by Accept header or by the "msgpack" format, so losing either the formatters or the negotiation
/// settings silently downgrades every client to JSON.
/// </summary>
[TestClass]
public class ServiceCollectionExtensionsTests
{
    private static MvcOptions BuildMvcOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllersWithMessagePack();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<MvcOptions>>().Value;
    }

    [TestMethod]
    public void AddControllersWithMessagePack_RegistersFormattersForBothDirections()
    {
        var options = BuildMvcOptions();

        Assert.IsTrue(options.InputFormatters.OfType<MessagePackInputFormatter>().Any());
        Assert.IsTrue(options.OutputFormatters.OfType<MessagePackOutputFormatter>().Any());
    }

    /// <summary>
    /// Negotiation has to honor the Accept header, and an unmatched one has to fall back to a
    /// formatter rather than answering 406.
    /// </summary>
    [TestMethod]
    public void AddControllersWithMessagePack_NegotiatesOnAcceptButNeverRefuses()
    {
        var options = BuildMvcOptions();

        Assert.IsTrue(options.RespectBrowserAcceptHeader);
        Assert.IsFalse(options.ReturnHttpNotAcceptable);
    }

    [TestMethod]
    public void AddControllersWithMessagePack_MapsTheMsgpackFormatToItsMediaType()
    {
        var options = BuildMvcOptions();

        Assert.AreEqual("application/x-msgpack", options.FormatterMappings.GetMediaTypeMappingForFormat("msgpack"));
    }
}
