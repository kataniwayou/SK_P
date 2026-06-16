namespace Messaging.Contracts;

// bus envelope — NO [JsonPropertyName], default STJ serialization.
/// <summary>ORCV-06 / D-06/D-07: the origin-split Keeper INJECT state for the ORCHESTRATOR FORWARD
/// escalation. Mirrors <see cref="ProcessorInject"/>'s 5-id base (corr/wf/step/proc/exec) and
/// implements <see cref="IKeeperRecoverable"/> so the existing 4-tuple partitioner serializes it
/// unchanged (origin-agnostic). Where the processor INJECT writes in-hand data, the orchestrator
/// INJECT completes the index+data COPY the FORWARD-Post tail could not finish: it carries the
/// next-step dispatch tuple (<see cref="NextStepId"/>, <see cref="NextProcessorId"/>,
/// <see cref="Payload"/>) plus the data keys — <see cref="EntryId"/> is the newEntryId to copy INTO
/// (and dispatch with) and <see cref="OriginEntryId"/> is the origin data key to copy FROM. INJECT is
/// non-destructive (write + send only — no source delete). StepId rides as a record property but is
/// NOT on the IKeeperRecoverable partition marker (D-12).</summary>
public sealed record OrchestratorInject(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; }   // the newEntryId to copy-into / dispatch with
    public Guid OriginEntryId { get; init; }   // the origin data key to copy FROM
    public Guid NextStepId      { get; init; } // downstream-dispatch: next step
    public Guid NextProcessorId { get; init; } // downstream-dispatch: target queue:{nextProcessorId}
    public string Payload     { get; init; } = "";   // downstream-dispatch: step config payload
}
