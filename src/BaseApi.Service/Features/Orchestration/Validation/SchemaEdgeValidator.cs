using BaseApi.Service.Features.Orchestration;

namespace BaseApi.Service.Features.Orchestration.Validation;

/// <summary>
/// Schema-edge compatibility gate (D-09 / L1-VALIDATE-05). An INDEPENDENT edge walk over EVERY
/// <c>(parent → child)</c> edge across EVERY entry in <c>parent.NextStepIds</c> — not just the first.
/// <para>
/// For each edge, resolves <c>parent.Processor.OutputSchemaId</c> and <c>child.Processor.InputSchemaId</c>
/// and requires STRICT <see cref="Guid"/> equality. A <c>null</c> on EITHER side passes the edge
/// (source / sink / unconfigured processor — preserves Phase 10 semantics). A mismatch (both non-null,
/// different Guid) throws <see cref="OrchestrationValidationException.SchemaEdge"/> with the offending
/// <c>(parentStepId, childStepId)</c> pair → HTTP 422.
/// </para>
/// <para>
/// <b>Independent of the cycle DFS (D-07 / Phase 13 D-03):</b> this gate does NOT call
/// <see cref="CycleDetector"/> and does NOT build a shared traversal abstraction. It is a flat,
/// per-edge equality check. A dangling child (referenced via <c>NextStepIds</c> but absent from the
/// graph) is the cycle/missing-step gate's concern — that gate runs FIRST in the locked validation
/// order — so this walk defensively skips an unresolved child rather than raising a different gate's error.
/// </para>
/// </summary>
internal sealed class SchemaEdgeValidator
{
    /// <summary>
    /// Walks every <c>(parent → child)</c> edge in the snapshot, throwing
    /// <see cref="OrchestrationValidationException.SchemaEdge"/> on the first mismatched edge.
    /// </summary>
    public void Validate(WorkflowGraphSnapshot snapshot)
    {
        foreach (var parent in snapshot.Steps.Values)
        {
            foreach (var childId in parent.NextStepIds ?? Enumerable.Empty<Guid>())
            {
                if (!snapshot.Steps.TryGetValue(childId, out var child))
                {
                    // Dangling child — the cycle/missing-step gate (which runs first) owns this error.
                    continue;
                }

                var parentOut = snapshot.Processors.TryGetValue(parent.ProcessorId, out var pproc)
                    ? pproc.OutputSchemaId
                    : (Guid?)null;
                var childIn = snapshot.Processors.TryGetValue(child.ProcessorId, out var cproc)
                    ? cproc.InputSchemaId
                    : (Guid?)null;

                // Null on either side passes (source / sink / unconfigured processor — Phase 10).
                if (parentOut is null || childIn is null)
                {
                    continue;
                }

                // Strict Guid equality. Mismatch → 422 with the offending edge.
                if (parentOut.Value != childIn.Value)
                {
                    throw OrchestrationValidationException.SchemaEdge(parent.Id, child.Id);
                }
            }
        }
    }
}
