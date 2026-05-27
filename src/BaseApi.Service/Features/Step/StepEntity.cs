using BaseApi.Core.Entities;

namespace BaseApi.Service.Features.Step;

/// <summary>
/// Step domain entity — couples a <c>ProcessorEntity</c> to next steps in a workflow DAG.
/// ENTITY-05 verbatim: 2 new scalar properties on top of <see cref="BaseEntity"/>.
/// <para>
/// <c>ProcessorId</c> is a NON-nullable FK to <c>ProcessorEntity</c>. Postgres FK
/// constraint <c>fk_step_processor_id</c> (wired by <c>StepEntityConfiguration</c>) uses
/// <c>OnDelete(Restrict)</c> — deleting a Processor while a Step references it returns
/// 23503 → 422 via Phase 4 mapper. Non-nullable + Restrict differs from Processor's
/// nullable FKs which use SetNull.
/// </para>
/// <para>
/// <c>EntryCondition</c> is the <see cref="StepEntryCondition"/> enum stored as int
/// (Claude's Discretion per 08-CONTEXT line 204; preserves ENTITY-06 numeric values).
/// Default initializer is <see cref="StepEntryCondition.PreviousCompleted"/> per ENTITY-05.
/// </para>
/// <para>
/// <b>The next-step collection is deliberately NOT a property on this entity</b> per
/// RESEARCH §5 Open Risk #1 + ENTITY-09 (no nav props between entities). The M2M self-ref
/// relationship is expressed by the <c>StepNextSteps</c> junction table; the next-step
/// collection lives on the DTOs only and is synchronized by
/// <c>StepService.SyncJunctionsAsync</c>.
/// </para>
/// </summary>
public sealed class StepEntity : BaseEntity
{
    public Guid ProcessorId { get; set; }
    public StepEntryCondition EntryCondition { get; set; } = StepEntryCondition.PreviousCompleted;
}
