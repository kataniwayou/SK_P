namespace Orchestrator.L1;

/// <summary>
/// Singleton in-memory L1 store (D-06): per-workflow root+step state keyed by workflowId, plus a
/// per-workflowId drop-if-held concurrency stripe (D-14). All state is per-instance — there is NO
/// static/global singleton lock and NO process-uniqueness assumption (ORCH-SCALE-01).
/// </summary>
public interface IWorkflowL1Store
{
    /// <summary>Insert or replace the L1 entry for <paramref name="workflowId"/>.</summary>
    void Upsert(Guid workflowId, WorkflowL1 entry);

    /// <summary>Try to read the L1 entry for <paramref name="workflowId"/>.</summary>
    bool TryGet(Guid workflowId, out WorkflowL1 entry);

    /// <summary>Remove the L1 entry for <paramref name="workflowId"/> (stripe is never disposed — Pitfall 5).</summary>
    void Remove(Guid workflowId);

    /// <summary>Current number of L1 entries.</summary>
    int Count { get; }

    /// <summary>Snapshot of the currently-held workflow ids.</summary>
    IReadOnlyCollection<Guid> WorkflowIds { get; }

    /// <summary>
    /// Non-blocking <c>SemaphoreSlim.Wait(0)</c> drop-if-held acquire (D-14): returns true iff the
    /// per-workflowId stripe was free and is now held. NEVER blocks.
    /// </summary>
    bool TryAcquire(Guid workflowId);

    /// <summary>Release the per-workflowId stripe acquired via <see cref="TryAcquire"/>.</summary>
    void Release(Guid workflowId);
}
