namespace Messaging.Contracts;

/// <summary>
/// Execution-scoped correlation (D-01) — the segregated interface Phase 19 D-01 deferred to "the
/// Processor milestone where the ids are real". Extends ICorrelated with the execution id-set the
/// orchestrator->processor dispatch carries. ExecutionId is Guid.Empty on entry-step dispatch
/// (lineage only). EntryId is now a GUID data key (D-04/D-05): Guid.Empty is the source-step
/// sentinel (recognized via <see cref="SourceStep.IsSource"/>), else the L2 data-key id. The legacy
/// content-hash <c>H</c> member was removed in v4.0.0 (RETIRE-01 — no more H-based dedup).
/// </summary>
public interface IExecutionCorrelated : ICorrelated
{
    Guid ExecutionId { get; }
    Guid WorkflowId  { get; }
    Guid StepId      { get; }
    Guid ProcessorId { get; }
    Guid EntryId     { get; }
}
