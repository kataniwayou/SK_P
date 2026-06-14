using Keeper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Keeper.Health;

/// <summary>
/// Minimal keeper self-watchdog. Mirrors <c>LivenessWatchdogHealthCheck(_outer)</c> in SHAPE — constructed
/// with the OUTER host's <see cref="IServiceProvider"/> and resolving the singleton
/// <see cref="IKeeperLivenessState"/> + <see cref="TimeProvider"/> + <see cref="ProbeOptions"/> AT CHECK
/// TIME (never captured at registration) — but deliberately timestamp-ONLY: no per-schema summary, no Data
/// dictionary on any verdict.
/// <para>
/// Reads ONLY the in-process L1 timestamp — never Redis/RMQ. A silently-stalled BitHealthLoop (host still
/// up) makes <see cref="IKeeperLivenessState.Current"/> stale, flipping this check Unhealthy so a future
/// K8s liveness probe can restart the pod.
/// <list type="bullet">
///   <item><c>Current == null</c> ⇒ Unhealthy ("BIT loop not started") — no tick yet.</item>
///   <item><c>now &gt;= Current + Probe:DelaySeconds×2</c> ⇒ Unhealthy ("BIT loop stale") — strict
///   <c>&gt;=</c> so the exact boundary instant counts stale (mirrors the processor watchdog).</item>
///   <item>else ⇒ Healthy ("live").</item>
/// </list>
/// Descriptions are STATIC literals; NO <c>data:</c> argument is passed on any branch.
/// </para>
/// </summary>
public sealed class KeeperLivenessWatchdogHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _outer;

    /// <param name="outer">
    /// The OUTER host's service provider. The singleton <see cref="IKeeperLivenessState"/> +
    /// <see cref="TimeProvider"/> + <see cref="IOptions{ProbeOptions}"/> are resolved from here at check
    /// time (they live in the outer container, not the inner listener container).
    /// </param>
    public KeeperLivenessWatchdogHealthCheck(IServiceProvider outer) => _outer = outer;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var state = _outer.GetRequiredService<IKeeperLivenessState>();   // singleton, resolved at check time
        var clock = _outer.GetRequiredService<TimeProvider>();
        var opts = _outer.GetRequiredService<IOptions<ProbeOptions>>();

        var current = state.Current;
        if (current is null)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("BIT loop not started"));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var graceSeconds = opts.Value.DelaySeconds * 2;
        // Strict >= so the exact boundary instant (now == Current + grace) counts stale.
        if (now >= current.Value.AddSeconds(graceSeconds))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("BIT loop stale"));
        }

        return Task.FromResult(HealthCheckResult.Healthy("live"));
    }
}
