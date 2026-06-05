using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

public enum ProbeOutcome { Recovered, GaveUp }

/// <summary>
/// PROBE-01/02 (D-02/03/04): bounded L2 read+write probe loop, awaited inside Consume. Each iteration does a
/// READ (skp:data:{entryId} — value need NOT exist) AND a WRITE-then-delete of the scratch key
/// (skp:keeper:probe:{h}, short TTL = crash net-zero net). Success ONLY if BOTH ops complete with no Redis
/// exception. A RedisException (the superset of RedisConnectionException + RedisTimeoutException) = keep
/// looping after a DelaySeconds delay. Do NOT catch Exception (a genuine bug must not masquerade as "down").
/// The IConnectionMultiplexer is the SAME singleton AddBaseConsole already registers (mirrors ResultConsumer).
/// </summary>
public sealed class L2ProbeRecovery(IConnectionMultiplexer redis, IOptions<ProbeOptions> opts)
{
    public async Task<ProbeOutcome> RunAsync(string entryId, string h, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var max = opts.Value.MaxAttempts;
        for (var attempt = 0; attempt < max; attempt++)
        {
            try
            {
                _ = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(entryId));          // READ — value need NOT exist (D-02)
                var scratch = (RedisKey)L2ProjectionKeys.KeeperProbe(h);
                await db.StringSetAsync(scratch, "1", expiry: TimeSpan.FromSeconds(30));        // WRITE w/ short TTL (D-03)
                await db.KeyDeleteAsync(scratch);                                               // then delete (net-zero)
                return ProbeOutcome.Recovered;                                                  // both ops, no exception (D-02)
            }
            catch (RedisException)   // RedisConnectionException + RedisTimeoutException both derive from this
            {
                if (attempt + 1 < max)
                    await Task.Delay(TimeSpan.FromSeconds(opts.Value.DelaySeconds), ct);
            }
        }
        return ProbeOutcome.GaveUp;
    }
}
