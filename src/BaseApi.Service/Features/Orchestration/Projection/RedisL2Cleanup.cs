using System.Text.Json;
using Messaging.Contracts.Projections;
using StackExchange.Redis;

namespace BaseApi.Service.Features.Orchestration.Projection;

/// <summary>
/// Shared, ALWAYS-tolerant L2 cleanup routine (D-06 / D-07). Mirrors the
/// <c>WorkflowGraphLoader</c> iterative wave-BFS (Loading/WorkflowGraphLoader.cs) but walks Redis
/// GET-and-follow instead of the Postgres <c>StepNextSteps</c> junction: the <c>visited</c> guard is
/// a <see cref="List{Guid}"/> (a plain list, NOT a hash-set — REQ convention, terminates on cycles),
/// the next wave collects ALL <c>nextStepIds</c> (multi-child fan-out), and ids are
/// <c>Distinct()</c>-deduped.
/// <para>
/// Collect-then-delete: traverse the full reachable step set first, THEN issue one
/// <see cref="IDatabase.CreateBatch"/> deleting the root key unconditionally + the per-step keys in a
/// single <c>KeyDeleteAsync(RedisKey[])</c> (which auto-selects UNLINK on the 7.4.x server) only when
/// at least one per-step key was collected (Pitfall 7). Processor-key formatters are NEVER
/// constructed and processor keys are NEVER deleted. The walk targets keys by GET-and-follow only —
/// it never enumerates the keyspace via a server scan or wildcard match (L2-PROJECT-07).
/// </para>
/// <para>
/// <b>Parent index (Phase 22 L2IDX-01 / D-10):</b> the routine FIRST <c>SREM</c>s the workflow id
/// (rendered <c>D</c>-format) from <c>RedisProjectionKeys.ParentIndex()</c> — hoisted ABOVE the
/// absent-root early-return so the GC is idempotent (the workflow leaves the index even when the
/// root is already gone).
/// </para>
/// </summary>
internal sealed class RedisL2Cleanup : IRedisL2Cleanup
{
    private readonly IConnectionMultiplexer _multiplexer;

    public RedisL2Cleanup(IConnectionMultiplexer multiplexer)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
    }

    public async Task StopCleanupAsync(Guid workflowId, CancellationToken ct)
    {
        var db = _multiplexer.GetDatabase();

        // Parent index (L2IDX-01 / D-10 step 1) — SREM this workflow id, HOISTED above the
        // absent-root early-return so the index entry is removed even when the root is already
        // gone (idempotent GC). Do NOT also SREM in the delete batch below (no double-SREM).
        await db.SetRemoveAsync(RedisProjectionKeys.ParentIndex(), workflowId.ToString("D"));

        // GET the root. Absent root → tolerant no-op (no throw). The 422 existence gate lives in
        // StopAsync, never here (Pitfall D).
        var rootJson = await db.StringGetAsync(RedisProjectionKeys.Root(workflowId));
        if (rootJson.IsNullOrEmpty) return;

        var entryStepIds = JsonSerializer.Deserialize<WorkflowRootProjection>(rootJson!)!.EntryStepIds;

        // BFS GET-and-follow over the Redis step keyspace (cycle-safe via the visited List).
        var visited = new List<Guid>();                          // plain list, not a hash-set (mirror loader)
        var stepKeysToDelete = new List<RedisKey>();
        var currentWave = (entryStepIds ?? new List<Guid>())
            .Where(id => id != Guid.Empty).Distinct().ToList();

        while (currentWave.Count > 0)
        {
            var toLoad = currentWave.Where(id => !visited.Contains(id)).Distinct().ToList();
            if (toLoad.Count == 0) break;

            var nextWave = new List<Guid>();
            foreach (var stepId in toLoad)
            {
                visited.Add(stepId);                             // mark visited (termination on cycle)
                var key = RedisProjectionKeys.Step(workflowId, stepId);
                var stepJson = await db.StringGetAsync(key);
                if (stepJson.IsNullOrEmpty) continue;            // dangling step → skip + continue (Pitfall 4)
                stepKeysToDelete.Add(key);
                var next = JsonSerializer.Deserialize<StepProjection>(stepJson!)!.NextStepIds
                    ?? new List<Guid>();
                nextWave.AddRange(next.Where(id => !visited.Contains(id)));
            }
            currentWave = nextWave.Distinct().ToList();
        }

        // R4 reachability invariant (24.1 / D-24.1-04): L2 holds only the validated reachable graph
        // and is NEVER mutated between Start and Stop (the result path is L1-only, D-08; first-win
        // forbids overwrite-in-place, so a re-Start of an existing root is a no-op). The reachable
        // BFS walk above therefore visits the COMPLETE key set — no unreachable orphan per-step key
        // can exist — and this single atomic batch deletes all-or-nothing (a crash mid-delete leaves
        // either the whole graph or none of it, never a half-deleted residue).
        //
        // ATOMICITY SCOPE (24.1): ONLY this final root+steps delete batch is atomic. The parent-index
        // SREM at line 45 is a SEPARATE, EARLIER, NON-batched op (it is deliberately hoisted above the
        // absent-root early return for idempotent GC — see the comment there). So this routine is NOT
        // atomic across its full SREM → discover-GETs → delete-batch sequence: a Redis fault during the
        // discovery GETs — after the SREM succeeded but before the delete batch — leaves the parent
        // index WITHOUT this id while the root + step keys are still fully present in L2. Consistency
        // across that SREM→delete window is CALLER-ENFORCED, not self-contained: the StopAsync caller
        // catches the RedisException and SADDs the id back (OrchestrationService.cs R2 compensation),
        // restoring index/L2 consistency. (The Start-path pre-clean caller catches and rethrows WITHOUT
        // re-adding — acceptable only because that path fails the whole request anyway.) Given the
        // locked single-replica assumption + the StopAsync SADD compensation, runtime risk is low.
        //
        // Collect-then-delete: one batch deleting the root unconditionally + the per-step keys
        // (UNLINK-style array delete) only when there is at least one (Pitfall 7). The root MUST have
        // been read above (it is the discovery entry point) — the caller PROBES (KeyExistsAsync), it
        // never pre-deletes the root, so the root survives to be read here then deleted in this batch.
        var batch = db.CreateBatch();
        var delTasks = new List<Task> { batch.KeyDeleteAsync(RedisProjectionKeys.Root(workflowId)) };
        if (stepKeysToDelete.Count > 0) delTasks.Add(batch.KeyDeleteAsync(stepKeysToDelete.ToArray()));
        batch.Execute();
        await Task.WhenAll(delTasks);
    }
}
