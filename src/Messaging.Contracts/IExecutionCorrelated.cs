namespace Messaging.Contracts;

/// <summary>
/// Execution-scoped correlation (D-01) — the segregated interface Phase 19 D-01 deferred to "the
/// Processor milestone where the ids are real". Extends ICorrelated with the execution id-set the
/// orchestrator->processor dispatch carries. executionId/entryId are Guid.Empty on entry-step
/// dispatch (SPEC ORCH-CONTRACT-02).
/// </summary>
public interface IExecutionCorrelated : ICorrelated
{
    Guid ExecutionId { get; }
    Guid WorkflowId  { get; }
    Guid StepId      { get; }
    Guid ProcessorId { get; }
    Guid EntryId     { get; }
}
