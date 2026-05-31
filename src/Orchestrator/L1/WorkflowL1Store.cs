using System.Collections.Concurrent;

namespace Orchestrator.L1;

/// <summary>
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>-backed singleton implementation of
/// <see cref="IWorkflowL1Store"/> (D-06). Entries live in one map keyed by workflowId; a parallel
/// map holds a per-workflowId <see cref="SemaphoreSlim"/>(1,1) stripe for the drop-if-held lifecycle
/// guard (D-14, RESEARCH "Lock striping").
/// <para>
/// <b>Drop-if-held (never blocking):</b> <see cref="TryAcquire"/> uses <c>Wait(0)</c> — it returns
/// immediately whether or not the stripe was free, so no thread ever parks (T-23-06 DoS mitigation).
/// A bare blocking <c>Wait()</c> is intentionally absent.
/// </para>
/// <para>
/// <b>Never-dispose stripes (Pitfall 5):</b> <see cref="Remove"/> drops the entry but leaves the
/// stripe in place. A <c>Wait(0)</c>-only <see cref="SemaphoreSlim"/> holds no kernel handle, and the
/// stripe population is bounded by the finite parent-index workflow set, so never disposing is safe
/// and avoids the dispose-vs-acquire race.
/// </para>
/// <para><c>public sealed</c> so <c>AddSingleton&lt;IWorkflowL1Store, WorkflowL1Store&gt;()</c>
/// (Plan 04) resolves across the assembly boundary. NO static/global lock — ORCH-SCALE-01.</para>
/// </summary>
public sealed class WorkflowL1Store : IWorkflowL1Store
{
    private readonly ConcurrentDictionary<Guid, WorkflowL1> _entries = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _stripes = new();

    /// <inheritdoc/>
    public void Upsert(Guid workflowId, WorkflowL1 entry) => _entries[workflowId] = entry;

    /// <inheritdoc/>
    public bool TryGet(Guid workflowId, out WorkflowL1 entry) => _entries.TryGetValue(workflowId, out entry!);

    /// <inheritdoc/>
    public void Remove(Guid workflowId) => _entries.TryRemove(workflowId, out _);

    /// <inheritdoc/>
    public int Count => _entries.Count;

    /// <inheritdoc/>
    public IReadOnlyCollection<Guid> WorkflowIds => _entries.Keys.ToArray();

    /// <inheritdoc/>
    public bool TryAcquire(Guid workflowId) =>
        _stripes.GetOrAdd(workflowId, static _ => new SemaphoreSlim(1, 1)).Wait(0); // drop-if-held — NEVER blocking

    /// <inheritdoc/>
    public void Release(Guid workflowId)
    {
        if (_stripes.TryGetValue(workflowId, out var stripe))
        {
            stripe.Release();
        }
    }
}
