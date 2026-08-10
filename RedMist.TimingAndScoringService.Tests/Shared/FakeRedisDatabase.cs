using Moq;
using StackExchange.Redis;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// A working in-memory stand-in for the Redis <see cref="IDatabase"/> the hubs use, plus a record of
/// every write.
/// </summary>
/// <remarks>
/// The hubs read back what they wrote — <c>StatusHub</c> stores a connection record on connect and
/// then mutates it on every subscribe — so a pure call-recorder is not enough; hashes and strings
/// behave like real ones here. StackExchange.Redis also exposes several <c>StreamAddAsync</c>
/// overloads that differ only in the nullability of <c>maxLength</c>, and which one the compiler
/// picks is not something a test should have to know, so every shape funnels into one capture list.
/// </remarks>
public sealed class FakeRedisDatabase
{
    public sealed record StreamWrite(string Key, string Field, string Value, long? MaxLength, bool Approximate);
    public sealed record HashWrite(string Key, string Field, string Value);

    private readonly Dictionary<string, Dictionary<string, string>> hashes = [];
    private readonly Dictionary<string, string> strings = [];

    public Mock<IConnectionMultiplexer> Mux { get; } = new();
    public Mock<IDatabase> Db { get; } = new();

    public List<StreamWrite> StreamWrites { get; } = [];
    public List<HashWrite> HashWrites { get; } = [];
    public List<(string Key, string Field)> HashDeletes { get; } = [];
    public List<string> KeyDeletes { get; } = [];
    public List<(string Key, string Field, long By)> HashIncrements { get; } = [];
    public List<(string Key, TimeSpan? Expiry)> KeyExpires { get; } = [];
    public List<(string Key, string Value)> SetAdds { get; } = [];

    public FakeRedisDatabase()
    {
        Mux.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(Db.Object);

        Db.Setup(x => x.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue field, RedisValue value, When when, CommandFlags flags) =>
            {
                SeedHash(key.ToString(), field.ToString(), value.ToString());
                HashWrites.Add(new HashWrite(key.ToString(), field.ToString(), value.ToString()));
                return Task.FromResult(true);
            });

        Db.Setup(x => x.HashGetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue field, CommandFlags flags) =>
                Task.FromResult(hashes.TryGetValue(key.ToString(), out var h) && h.TryGetValue(field.ToString(), out var v)
                    ? new RedisValue(v)
                    : RedisValue.Null));

        Db.Setup(x => x.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, CommandFlags flags) =>
                Task.FromResult(hashes.TryGetValue(key.ToString(), out var h)
                    ? h.Select(kvp => new HashEntry(kvp.Key, kvp.Value)).ToArray()
                    : []));

        Db.Setup(x => x.HashDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue field, CommandFlags flags) =>
            {
                HashDeletes.Add((key.ToString(), field.ToString()));
                var removed = hashes.TryGetValue(key.ToString(), out var h) && h.Remove(field.ToString());
                return Task.FromResult(removed);
            });

        Db.Setup(x => x.HashIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue field, long by, CommandFlags flags) =>
            {
                HashIncrements.Add((key.ToString(), field.ToString(), by));
                if (!hashes.TryGetValue(key.ToString(), out var h))
                {
                    h = [];
                    hashes[key.ToString()] = h;
                }
                var current = h.TryGetValue(field.ToString(), out var raw) && long.TryParse(raw, out var n) ? n : 0;
                var updated = current + by;
                h[field.ToString()] = updated.ToString();
                return Task.FromResult(updated);
            });

        Db.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, CommandFlags flags) =>
                Task.FromResult(strings.TryGetValue(key.ToString(), out var v) ? new RedisValue(v) : RedisValue.Null));

        Db.Setup(x => x.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, CommandFlags flags) =>
            {
                KeyDeletes.Add(key.ToString());
                var removed = hashes.Remove(key.ToString()) | strings.Remove(key.ToString());
                return Task.FromResult(removed);
            });

        Db.Setup(x => x.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, TimeSpan? expiry, CommandFlags flags) =>
            {
                KeyExpires.Add((key.ToString(), expiry));
                return Task.FromResult(true);
            });

        Db.Setup(x => x.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, TimeSpan? expiry, ExpireWhen when, CommandFlags flags) =>
            {
                KeyExpires.Add((key.ToString(), expiry));
                return Task.FromResult(true);
            });

        Db.Setup(x => x.SetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue value, CommandFlags flags) =>
            {
                SetAdds.Add((key.ToString(), value.ToString()));
                return Task.FromResult(true);
            });

        // int? maxLength overload
        Db.Setup(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue field, RedisValue value, RedisValue? id, int? maxLength, bool approximate, CommandFlags flags) =>
            {
                StreamWrites.Add(new StreamWrite(key.ToString(), field.ToString(), value.ToString(), maxLength, approximate));
                return Task.FromResult(RedisValue.Null);
            });

        // long? maxLength overload
        Db.Setup(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                It.IsAny<RedisValue?>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<long?>(),
                It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue field, RedisValue value, RedisValue? id, long? maxLength, bool approximate,
                long? limit, StreamTrimMode trimMode, CommandFlags flags) =>
            {
                StreamWrites.Add(new StreamWrite(key.ToString(), field.ToString(), value.ToString(), maxLength, approximate));
                return Task.FromResult(RedisValue.Null);
            });
    }

    public void SeedHash(string key, string field, string value)
    {
        if (!hashes.TryGetValue(key, out var h))
        {
            h = [];
            hashes[key] = h;
        }
        h[field] = value;
    }

    public void SeedString(string key, string value) => strings[key] = value;

    public string? GetHashValue(string key, string field)
        => hashes.TryGetValue(key, out var h) && h.TryGetValue(field, out var v) ? v : null;

    /// <summary>Makes every hash write fail, to exercise the hubs' cache-failure handling.</summary>
    public void FailHashWrites()
    {
        Db.Setup(x => x.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "cache down"));
    }

    /// <summary>Makes every hash read fail, to exercise the hubs' cache-failure handling.</summary>
    public void FailHashReads()
    {
        Db.Setup(x => x.HashGetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "cache down"));
    }

    /// <summary>Makes every hash delete fail, to exercise the hubs' cache-failure handling.</summary>
    public void FailHashDeletes()
    {
        Db.Setup(x => x.HashDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "cache down"));
    }
}
