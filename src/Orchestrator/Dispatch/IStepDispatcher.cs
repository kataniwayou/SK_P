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
    /// Phase 43 (D-03): <c>entryId</c> is a <c>Guid</c> (the L2 data key; <c>Guid.Empty</c> = the
    /// source-step sentinel). The retired <c>H</c>/flag dedup machinery is gone — the dispatch is built
    /// and Sent straight-through with no deterministic-identity compute and no <c>flag[H]</c> pre-write.
    /// </para>
    /// </summary>
    Task DispatchAsync(Guid workflowId, Guid stepId, Guid processorId, string payload,
        Guid correlationId, Guid executionId, Guid entryId, CancellationToken ct);
}
