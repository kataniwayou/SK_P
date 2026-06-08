namespace Messaging.Contracts;

/// <summary>D-12: marker exposing the partition 4-tuple (corr:wf:proc:exec == the composite-backup
/// key) the Phase-46 MassTransit UsePartitioner consumes for per-key ordering. stepId rides as a
/// plain property on each record, NOT part of the partition key. The four members are declared
/// directly here (not inherited) so the marker reflects exactly the 4-tuple — interface
/// <c>GetProperties()</c> does not surface base-interface members.</summary>
public interface IKeeperRecoverable
{
    Guid CorrelationId { get; }
    Guid WorkflowId    { get; }
    Guid ProcessorId   { get; }
    Guid ExecutionId   { get; }
}
