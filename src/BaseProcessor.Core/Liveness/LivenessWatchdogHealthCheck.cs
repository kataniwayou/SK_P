using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BaseProcessor.Core.Liveness;

/// <summary>
/// Processor self-watchdog liveness probe (PROBE-01/02, D-01/02/03/04). Mirrors BusReadyHealthCheck(_outer):
/// constructed with the OUTER IServiceProvider and resolves the singleton <see cref="IProcessorLivenessState"/>
/// + <see cref="TimeProvider"/> AT CHECK TIME (never captured at registration — RESEARCH Pitfall 4 / T-61-06).
///
/// <para>
/// Reads ONLY the in-process L1 holder — never Redis/RMQ (D-01). A silently-crashed startup/heartbeat loop
/// (host still up) makes <c>Current</c> stale, flipping this check Unhealthy so the future K8s liveness probe
/// can restart the pod.
/// <list type="bullet">
///   <item><c>Current == null</c> ⇒ Unhealthy ("liveness loop not started") — the loop crashed before its
///   first write (D-02).</item>
///   <item><c>now &gt;= Current.Timestamp + Current.Interval×2</c> ⇒ Unhealthy ("liveness loop stale") — the
///   loop went silent (D-03 / WR-01, identical math to the orchestration-start gate: fresh iff
///   <c>deadline &gt; now</c>, stale at-or-past the boundary instant — agreeing with the gate's
///   <c>deadline &lt;= now =&gt; stale</c>).</item>
///   <item>else ⇒ Healthy ("live").</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Info-disclosure guard (T-61-04 / T-18-08).</b> Descriptions are STATIC literals; the data dictionary
/// carries ONLY the three SchemaOutcome strings from the per-schema summary (D-04) — never the instanceId, a
/// connection string, or a stack trace. The summary rides into the /health/live body via the listener's
/// UIResponseWriter per-check data serialization (PROBE-02).
/// </para>
/// </summary>
public sealed class LivenessWatchdogHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _outer;

    /// <param name="outer">
    /// The OUTER host's service provider. The singleton <see cref="IProcessorLivenessState"/> +
    /// <see cref="TimeProvider"/> are resolved from here at check time (they live in the outer container,
    /// not the inner listener container).
    /// </param>
    public LivenessWatchdogHealthCheck(IServiceProvider outer) => _outer = outer;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var state = _outer.GetRequiredService<IProcessorLivenessState>();   // singleton, resolved at check time
        var clock = _outer.GetRequiredService<TimeProvider>();
        var current = state.Current;
        if (current is null)
        {
            // D-02: the loop crashed before ever writing — boot coverage is the future K8s startupProbe.
            return Task.FromResult(HealthCheckResult.Unhealthy("liveness loop not started"));
        }

        var data = new Dictionary<string, object>                          // D-04: summary in body (PROBE-02)
        {
            ["inputSchema"]  = current.Summary.InputSchema,
            ["outputSchema"] = current.Summary.OutputSchema,
            ["configSchema"] = current.Summary.ConfigSchema,
        };

        var now = clock.GetUtcNow().UtcDateTime;                            // D-03: same clock discipline as writer + gate
        // WR-01: fresh iff deadline > now — boundary-aligned with the gate's `deadline <= now => stale`
        // (ProcessorLivenessValidator.cs). Strict `>=` here so both sides agree at the exact boundary instant.
        if (now >= current.Timestamp.AddSeconds(current.Interval * 2))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("liveness loop stale", data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("live", data: data));
    }
}
