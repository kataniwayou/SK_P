namespace Messaging.Contracts;

/// <summary>D-12: marker exposing the partition 4-tuple (corr:wf:proc:exec == the composite-backup
/// key) the Phase-46 MassTransit UsePartitioner consumes for per-key ordering. stepId rides as a
/// plain property on each record, NOT part of the partition key. Extends ICorrelated (CorrelationId
/// inherited, not redeclared — mirrors IExecutionCorrelated : ICorrelated).</summary>
public interface IKeeperRecoverable : ICorrelated
{
    Guid WorkflowId  { get; }
    Guid ProcessorId { get; }
    Guid ExecutionId { get; }
}
