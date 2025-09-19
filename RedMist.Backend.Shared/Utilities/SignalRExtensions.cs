using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace RedMist.Backend.Shared.Utilities;

public static class SignalRExtensions
{
    public static ISignalRServerBuilder AddRedMistSignalR(this IServiceCollection services, string redisConnectionString)
    {
        return services.AddSignalR(o => o.MaximumParallelInvocationsPerClient = 3)
            .AddStackExchangeRedis(redisConnectionString, options =>
            {
                options.Configuration.ChannelPrefix = RedisChannel.Literal(Consts.STATUS_CHANNEL_PREFIX);
            })
            .AddMessagePackProtocol();
    }
}
