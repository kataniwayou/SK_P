namespace BaseApi.Service.Features.Step;

/// <summary>
/// Entry condition for a <see cref="StepEntity"/> within a workflow DAG. Encodes when the
/// step is permitted to begin processing based on the previous step's outcome. Per
/// ENTITY-06 verbatim, all 6 values carry EXPLICIT numeric assignments — the assignments
/// are preserved across future migrations so the DB integer values remain stable.
/// <para>
/// <see cref="PreviousCompleted"/> (= 1) is the C# default value used to seed
/// <see cref="StepEntity.EntryCondition"/> per ENTITY-05; EF Core sees the default and
/// emits <c>DEFAULT 1</c> on the column in the Wave C 08-07 InitialCreate migration.
/// </para>
/// </summary>
public enum StepEntryCondition
{
    PreviousProcessing = 0,
    PreviousCompleted = 1,
    PreviousFailed = 2,
    PreviousCancelled = 3,
    Always = 4,
    Never = 5,
}
