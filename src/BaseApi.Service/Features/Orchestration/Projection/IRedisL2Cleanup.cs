namespace BaseApi.Service.Features.Orchestration.Projection;

/// <summary>
/// Shared L2 (Redis) cleanup routine (D-06 / D-07). For ONE workflow id it GETs the root key,
/// breadth-first walks the step graph in Redis via GET-and-follow (reading <c>nextStepIds</c> off
/// each deserialized step value; cycle-safe), collects every reachable per-step key, then
/// batch-deletes the root + per-step keys. Processor keys are NEVER touched.
/// <para>
/// <b>ALWAYS tolerant.</b> An absent root is a no-op (no throw); a dangling/absent per-step key is
/// skipped and the walk continues. The 422 existence gate (D-04) lives in
/// <c>OrchestrationService.StopAsync</c>, NOT here (Pitfall D) — this keeps the routine reusable by
/// its two callers (Plan 04): the Stop endpoint (runs only after the all-exist gate passes) and
/// Start's pre-clean (tolerant by design — it is the GC for shrunk graphs, ORCH-START-05
/// delete-then-write).
/// </para>
/// <para>
/// The walk uses ONLY GET-and-follow (<c>StringGetAsync</c>) — NO <c>KEYS</c> / <c>IServer.Keys()</c>
/// enumeration (L2-PROJECT-07).
/// </para>
/// </summary>
internal interface IRedisL2Cleanup
{
    /// <summary>
    /// Tolerant traverse-and-delete for a single workflow id. Absent root → no-op; dangling step →
    /// skip. Deletes root + reachable per-step keys; never processor keys.
    /// </summary>
    Task StopCleanupAsync(Guid workflowId, CancellationToken ct);
}
