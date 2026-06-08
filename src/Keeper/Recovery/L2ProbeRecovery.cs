using System;
using System.Collections.Generic;
using Keeper.Observability;
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
public sealed class L2ProbeRecovery(IConnectionMultiplexer redis, IOptions<ProbeOptions> opts, KeeperMetrics metrics)
{
    // OPEN QUESTION (RESEARCH OQ-1) RESOLVED: thread a `procId` param so BOTH keeper_in_flight and
    // keeper_l2_probe_failed carry the bounded {ProcessorId} label (consistent with the consumers; D-03/D-05).
    // Both consumer call sites pass inner.ProcessorId.ToString("D").
    public async Task<ProbeOutcome> RunAsync(Guid entryId, string h, string procId, CancellationToken ct)
    {
        var procTag = new KeyValuePair<string, object?>(KeeperMetricTags.ProcessorId, procId);

        metrics.InFlight.Add(1, procTag);   // D-05: +1 on probe-loop entry
        try
        {
            var max = opts.Value.MaxAttempts;
            for (var attempt = 0; attempt < max; attempt++)
            {
                if (await ProbeOnceAsync(ct, entryId, h))                                           // one probe; RedisException → false INSIDE (D-02)
                    return ProbeOutcome.Recovered;                                                  // both ops, no exception (D-02)

                metrics.L2ProbeFailed.Add(1, procTag);   // D-03: per failed probe attempt
                if (attempt + 1 < max)
                    await Task.Delay(TimeSpan.FromSeconds(opts.Value.DelaySeconds), ct);
            }
            return ProbeOutcome.GaveUp;
        }
        finally { metrics.InFlight.Add(-1, procTag); }   // D-05: -1 on terminal (recovered OR gave-up)
    }

    // Sentinel for the standing BIT probe (OQ-1): a reserved fixed key. The read target need NOT exist —
    // a present/absent read still proves L2 reachability. The scratch h is a constant "bit" tag.
    private static readonly Guid BitProbeEntryId = Guid.Empty;     // reserved sentinel — read need not exist
    private const string BitProbeH = "bit";                       // constant scratch tag for the standing probe

    /// <summary>One L2 BIT probe (KEEP-01 core, extracted from RunAsync). READ + WRITE-then-DELETE scratch.
    /// true = both ops, no exception; false = RedisException (L2 down). A non-Redis throw PROPAGATES
    /// (a genuine bug must not masquerade as "down" — Pitfall 5). entryId/h default to BIT sentinels so the
    /// standing loop needs no inbound message; RunAsync passes the real fault-context values.</summary>
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
