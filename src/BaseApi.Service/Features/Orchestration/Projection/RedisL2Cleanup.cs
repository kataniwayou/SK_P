using System.Text.Json;
using BaseApi.Core.Configuration;
using Microsoft.Extensions.Options;
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
/// </summary>
internal sealed class RedisL2Cleanup : IRedisL2Cleanup
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly RedisProjectionOptions _options;

    public RedisL2Cleanup(IConnectionMultiplexer multiplexer, IOptions<RedisProjectionOptions> options)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _options     = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task StopCleanupAsync(Guid workflowId, CancellationToken ct)
    {
        var prefix = _options.KeyPrefix;
        var db = _multiplexer.GetDatabase();

        // GET the root. Absent root → tolerant no-op (no throw). The 422 existence gate lives in
        // StopAsync, never here (Pitfall D).
        var rootJson = await db.StringGetAsync(RedisProjectionKeys.Root(prefix, workflowId));
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
                var key = RedisProjectionKeys.Step(prefix, workflowId, stepId);
                var stepJson = await db.StringGetAsync(key);
                if (stepJson.IsNullOrEmpty) continue;            // dangling step → skip + continue (Pitfall 4)
                stepKeysToDelete.Add(key);
                var next = JsonSerializer.Deserialize<StepProjection>(stepJson!)!.NextStepIds
                    ?? new List<Guid>();
                nextWave.AddRange(next.Where(id => !visited.Contains(id)));
            }
            currentWave = nextWave.Distinct().ToList();
        }

        // Collect-then-delete: one batch deleting the root unconditionally + the per-step keys
        // (UNLINK-style array delete) only when there is at least one (Pitfall 7).
        var batch = db.CreateBatch();
        var delTasks = new List<Task> { batch.KeyDeleteAsync(RedisProjectionKeys.Root(prefix, workflowId)) };
        if (stepKeysToDelete.Count > 0) delTasks.Add(batch.KeyDeleteAsync(stepKeysToDelete.ToArray()));
        batch.Execute();
        await Task.WhenAll(delTasks);
    }
}
