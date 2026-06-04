namespace Orchestrator.Dispatch;

/// <summary>
/// The single owner of the orchestrator->processor dispatch shape (D-01): build one
/// <c>EntryStepDispatch</c> and <c>Send</c> (NOT publish) it to <c>queue:{processorId}</c>.
/// Both the cron fire job (initial fire — executionId/entryId <c>Guid.Empty</c>) and the
/// result consumer (continuation — real executionId/entryId copied from the result) call this,
/// so the build-and-Send convention lives in exactly one place.
/// <para>
/// An infra fault on <c>Send</c> (broker unreachable) propagates so the caller's retry pipeline
/// can react — the contract is preserved across the cron fire path and the result-continuation path.
/// </para>
/// </summary>
public interface IStepDispatcher
{
    /// <summary>
    /// Builds an <c>EntryStepDispatch</c> for the target step and sends it to
    /// <c>queue:{processorId:D}</c>. <paramref name="executionId"/>/<paramref name="entryId"/> are
    /// parameterized (NOT forced empty) so the result-continuation path can carry the real values.
    /// <para>
    /// Phase 31 (D-02/D-06): the deterministic effect identity <c>H</c> is computed INTERNALLY from
    /// (correlationId, workflowId, stepId, processorId, entryId) — executionId excluded — and stamped on
    /// the dispatch, and <c>flag[H]="Pending"</c> is pre-written before the Send (the sender-pre-write
    /// half of the symmetric effect-first dedup). Callers do not supply or see H.
    /// </para>
    /// </summary>
    Task DispatchAsync(Guid workflowId, Guid stepId, Guid processorId, string payload,
        Guid correlationId, Guid executionId, string entryId, CancellationToken ct);
}
