using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace RedMist.Backend.Shared.Utilities;

public static class SignalRExtensions
{
    public static ISignalRServerBuilder AddRedMistSignalR(this IServiceCollection services, string redisConnectionString)
    {
        return services.AddSignalR(o => 
        {
            o.MaximumParallelInvocationsPerClient = 3;
            // The relay sends a full Flagtronics vehicle state as a single message. At ~500 bytes
            // per car an 80 car field is ~40KB, past the 32KB default, and the connection is aborted.
            o.MaximumReceiveMessageSize = 1024 * 1024;
            // Critical settings for multi-replica scenarios
            o.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
            o.KeepAliveInterval = TimeSpan.FromSeconds(15);
            o.HandshakeTimeout = TimeSpan.FromSeconds(30);
            //o.EnableDetailedErrors = true; // Enable for debugging multi-replica issues
        })
        .AddStackExchangeRedis(redisConnectionString, options =>
        {
            options.Configuration.ChannelPrefix = RedisChannel.Literal(Consts.STATUS_CHANNEL_PREFIX);

            // Enhanced Redis configuration for reliable backplane operation
            options.Configuration.ConnectRetry = 10;
            options.Configuration.ConnectTimeout = 10000;
            options.Configuration.SyncTimeout = 10000;
            options.Configuration.AsyncTimeout = 10000;
            options.Configuration.AbortOnConnectFail = false;
            options.Configuration.KeepAlive = 60;
            options.Configuration.ReconnectRetryPolicy = new ExponentialRetry(5000);
        })
        .AddMessagePackProtocol(options =>
        {
            options.SerializerOptions = MessagePackSerializerOptions.Standard
                .WithResolver(ContractlessStandardResolver.Instance);
        });
    }
}
