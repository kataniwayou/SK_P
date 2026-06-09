using System;
using Messaging.Contracts.Projections;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>
/// PROBE-01 (v4 BIT-probe helper): a single L2 read+write-then-delete reachability probe, the live
/// dependency of the Keeper <c>BitHealthLoop</c> (the v4 health gate driver). A READ
/// (skp:data:{entryId} — value need NOT exist) AND a WRITE-then-delete of the scratch key
/// (skp:keeper:probe:{h}, short TTL = crash net-zero net). Success ONLY if BOTH ops complete with no
/// Redis exception. A RedisException (the superset of RedisConnectionException + RedisTimeoutException)
/// reports L2 down (false); a non-Redis throw PROPAGATES (a genuine bug must not masquerade as "down").
/// The IConnectionMultiplexer is the SAME singleton AddBaseConsole already registers.
/// </summary>
public sealed class L2ProbeRecovery(IConnectionMultiplexer redis)
{
    // Sentinel for the standing BIT probe: a reserved fixed key. The read target need NOT exist —
    // a present/absent read still proves L2 reachability. The scratch h is a constant "bit" tag.
    private static readonly Guid BitProbeEntryId = Guid.Empty;     // reserved sentinel — read need not exist
    private const string BitProbeH = "bit";                       // constant scratch tag for the standing probe

    /// <summary>One L2 BIT probe (KEEP-01 core). READ + WRITE-then-DELETE scratch.
    /// true = both ops, no exception; false = RedisException (L2 down). A non-Redis throw PROPAGATES
    /// (a genuine bug must not masquerade as "down" — Pitfall 5). entryId/h default to BIT sentinels so the
    /// standing loop needs no inbound message.</summary>
    public async Task<bool> ProbeOnceAsync(CancellationToken ct, Guid? entryId = null, string? h = null)
    {
        var db = redis.GetDatabase();
        try
        {
            _ = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(entryId ?? BitProbeEntryId));  // READ — value need NOT exist
            var scratch = (RedisKey)L2ProjectionKeys.KeeperProbe(h ?? BitProbeH);
            await db.StringSetAsync(scratch, "1", expiry: TimeSpan.FromSeconds(30));                   // WRITE w/ short TTL
            await db.KeyDeleteAsync(scratch);                                                          // then delete (net-zero)
            return true;                                                                               // both ops, no exception
        }
        catch (RedisException)   // RedisConnectionException + RedisTimeoutException both derive from this
        {
            return false;        // L2 down — NOT a crash. NO catch(Exception): a genuine bug propagates.
        }
    }
}
