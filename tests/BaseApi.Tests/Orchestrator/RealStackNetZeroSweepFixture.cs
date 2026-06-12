using System.Diagnostics;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Collection-fixture NET-ZERO SWEEP for the RealStack round-trip tests. Runs ONCE, after EVERY test in a
/// host-stack collection has finished, via <see cref="IAsyncLifetime.DisposeAsync"/>. Wired into BOTH
/// host-stack collections — <c>"Observability"</c> (SC1/SC2 + SampleRoundTrip) and <c>"RedisOutageSerial"</c>
/// (SC3) — which are both <c>DisableParallelization = true</c>, so the sweep can NEVER race a live RealStack
/// test (no other collection runs concurrently, and only RealStack tests write the host <c>skp:</c> keyspace).
/// Whichever host-stack collection finishes LAST leaves a clean state regardless of run order.
/// <para>
/// WHY this is needed (close-gate net-zero): the round-trip tests seed a <c>* * * * *</c> (every-minute) cron
/// so the orchestrator actually fires the dispatch. While each test runs (multi-minute ES poll windows), the
/// cron RE-FIRES, and each fire mints a terminal OUTPUT key <c>skp:data:{entryId}</c> that the live processor
/// leaves TTL-bound (a terminal output has no downstream consumer to delete it — the A19 two-key DEL reclaims
/// the INPUT + index, not the output). A test only registers the ONE output key it observed for teardown, so
/// the extra fires leave TTL-bound residue. The phase-55 close gate snapshots the redis keyspace with NO
/// settle-wait, so that residue surfaces as a <c>redis --scan</c> SHA mismatch. Likewise the consolidated DLQ
/// <c>skp-dlq-1</c> has NO consumer in production (operator-drained), so any message routed there during a run
/// (e.g. transport exhaustion across SC3's redis outage) persists and trips the gate's <c>depth==0</c> check.
/// </para>
/// <para>
/// This sweep is the deterministic, ACTIVE cleanup (not a TTL wait) that the gate's net-zero discipline
/// expects in teardown: it deletes residual <c>skp:data:*</c> + <c>skp:msg:*</c> host keys and purges
/// <c>skp-dlq-1</c>. It does NOT touch the steady-state <c>skp:{procId:D}</c> liveness key (the live container
/// keeps re-writing it; it is excluded from the gate SHA). Fully BEST-EFFORT: a missing host stack
/// (a hermetic-only run with no docker) is swallowed so it never breaks a no-stack run.
/// </para>
/// </summary>
public sealed class RealStackNetZeroSweepFixture : IAsyncLifetime
{
    private const string HostRedis = "localhost:6380,abortConnect=false,connectTimeout=5000";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await SweepResidualHostRedisAsync();
        await PurgeConsolidatedDlqAsync();
    }

    /// <summary>Delete residual slot-array OUTPUT (skp:data:*) + INDEX (skp:msg:*) keys left on host Redis.</summary>
    private static async Task SweepResidualHostRedisAsync()
    {
        try
        {
            await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
            var db = mux.GetDatabase();
            var toDelete = new List<RedisKey>();
            foreach (var ep in mux.GetEndPoints())
            {
                var server = mux.GetServer(ep);
                if (!server.IsConnected || server.IsReplica)
                {
                    continue;
                }

                foreach (var pattern in new[] { "skp:data:*", "skp:msg:*" })
                {
                    foreach (var key in server.Keys(pattern: pattern))
                    {
                        toDelete.Add(key);
                    }
                }
            }

            if (toDelete.Count > 0)
            {
                await db.KeyDeleteAsync(toDelete.ToArray());
            }
        }
        catch
        {
            // Best-effort: a hermetic-only run has no host Redis on :6380 — nothing to sweep.
        }
    }

    /// <summary>Purge the consolidated DLQ (skp-dlq-1) via <c>docker exec sk-rabbitmq rabbitmqctl purge_queue</c>
    /// — production skp-dlq-1 has NO consumer, so a test-routed dead-letter must be actively drained.</summary>
    private static async Task PurgeConsolidatedDlqAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "exec", "sk-rabbitmq", "rabbitmqctl", "-q", "purge_queue", "skp-dlq-1" })
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return;
            }

            await proc.WaitForExitAsync();
        }
        catch
        {
            // Best-effort: no docker / no broker on a hermetic-only run.
        }
    }
}
