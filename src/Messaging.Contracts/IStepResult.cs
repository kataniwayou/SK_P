namespace Messaging.Contracts;

/// <summary>D-06c: marker grouping the four typed step-result records
/// (StepCompleted/StepFailed/StepCancelled/StepProcessing) so they co-locate on
/// OrchestratorQueues.Result and the InboundExecutionScopeConsumeFilter (keyed on
/// IExecutionCorrelated) covers them unchanged. IExecutionCorrelated already extends ICorrelated,
/// so IStepResult transitively requires CorrelationId + the execution id-set + Guid EntryId.</summary>
public interface IStepResult : IExecutionCorrelated { }
