namespace Messaging.Contracts;

/// <summary>
/// Execution-scoped correlation (D-01) — the segregated interface Phase 19 D-01 deferred to "the
/// Processor milestone where the ids are real". Extends ICorrelated with the execution id-set the
/// orchestrator->processor dispatch carries. ExecutionId is Guid.Empty and EntryId is the empty
/// string on entry-step dispatch (Phase 31 D-01: EntryId is now a content-addressed 64-hex string,
/// empty when no input is carried).
/// </summary>
public interface IExecutionCorrelated : ICorrelated
{
    Guid ExecutionId { get; }
    Guid WorkflowId  { get; }
    Guid StepId      { get; }
    Guid ProcessorId { get; }
    string EntryId   { get; }

    /// <summary>The stable per-message content hash (64-hex), used as the cross-message recovery key. Declared on the concrete records; hoisted here so the generic Keeper recovery body can read it (KHARD-03).</summary>
    string H { get; }
}
