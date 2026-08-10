using MessagePack.Resolvers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Utilities;
using StackExchange.Redis;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// Every service builds its SignalR stack through this one call, so these pin the settings whose
/// values are load bearing: change any of them and the symptom shows up as dropped connections or
/// messages that never cross between replicas, not as a failure here.
/// </summary>
[TestClass]
public class SignalRExtensionsTests
{
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRedMistSignalR("localhost:6379,abortConnect=false");
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The relay sends a whole Flagtronics field in one message; at ~500 bytes a car an 80 car field
    /// is past the 32KB default and the connection is aborted mid-race.
    /// </summary>
    [TestMethod]
    public void AddRedMistSignalR_AcceptsMessagesLargerThanTheFrameworkDefault()
    {
        using var provider = BuildServices();

        var options = provider.GetRequiredService<IOptions<HubOptions>>().Value;
        Assert.AreEqual(1024L * 1024, options.MaximumReceiveMessageSize);
        Assert.AreEqual(3, options.MaximumParallelInvocationsPerClient);
    }

    /// <summary>
    /// The channel prefix has to be identical in every service or the Redis backplane silently
    /// partitions and clients on one replica stop seeing another replica's messages.
    /// </summary>
    [TestMethod]
    public void AddRedMistSignalR_PutsTheBackplaneOnTheSharedChannelPrefix()
    {
        using var provider = BuildServices();

        var redis = provider.GetRequiredService<IOptions<RedisOptions>>().Value;
        Assert.AreEqual(RedisChannel.Literal(Consts.STATUS_CHANNEL_PREFIX), redis.Configuration.ChannelPrefix);
    }

    /// <summary>
    /// Services have to start when Redis is not up yet, and keep retrying, rather than failing their
    /// startup probe.
    /// </summary>
    [TestMethod]
    public void AddRedMistSignalR_KeepsRetryingTheBackplaneInsteadOfFailingToStart()
    {
        using var provider = BuildServices();

        var configuration = provider.GetRequiredService<IOptions<RedisOptions>>().Value.Configuration;
        Assert.IsFalse(configuration.AbortOnConnectFail);
        Assert.AreEqual(10, configuration.ConnectRetry);
        Assert.IsInstanceOfType<ExponentialRetry>(configuration.ReconnectRetryPolicy);
    }

    /// <summary>
    /// The contractless resolver is what lets clients deserialize by property name. Falling back to
    /// the standard resolver would require MessagePack attributes on every model and break every
    /// existing client.
    /// </summary>
    [TestMethod]
    public void AddRedMistSignalR_SerializesWithTheContractlessResolver()
    {
        using var provider = BuildServices();

        var messagePack = provider.GetRequiredService<IOptions<MessagePackHubProtocolOptions>>().Value;
        Assert.AreSame(ContractlessStandardResolver.Instance, messagePack.SerializerOptions.Resolver);
        Assert.IsTrue(provider.GetServices<IHubProtocol>().Any(p => p.Name == "messagepack"));
    }
}
