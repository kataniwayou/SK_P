namespace Messaging.Contracts;

/// <summary>The frozen correlation vocabulary shared by future ICorrelated messages.</summary>
public interface ICorrelated
{
    Guid CorrelationId { get; }
    Guid ExecutionId   { get; }
    Guid WorkflowId    { get; }
    Guid StepId        { get; }
    Guid ProcessorId   { get; }
    Guid EntryId       { get; }
}
