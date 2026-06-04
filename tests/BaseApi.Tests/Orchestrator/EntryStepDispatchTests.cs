using MassTransit;
using Messaging.Contracts;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Pins the <see cref="EntryStepDispatch"/> orchestrator->processor message shape (ORCH-CONTRACT-02):
/// a record carrying a per-fire CorrelationId (D-05, minted via <c>NewId.NextGuid()</c>) whose
/// ExecutionId defaults to <see cref="Guid.Empty"/> and whose EntryId/H default to the empty string
/// on entry dispatch (Phase 31 D-01/D-02), implementing the segregated
/// <see cref="IExecutionCorrelated"/> interface (D-01).
/// </summary>
public sealed class EntryStepDispatchTests
{
    [Fact]
    public void Fields_ExecutionEmpty_EntryAndHashEmptyStrings()
    {
        var workflowId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();
        var correlationId = NewId.NextGuid();

        var dispatch = new EntryStepDispatch(workflowId, stepId, processorId, "payload")
        {
            CorrelationId = correlationId,
        };

        Assert.Equal(workflowId, dispatch.WorkflowId);
        Assert.Equal(stepId, dispatch.StepId);
        Assert.Equal(processorId, dispatch.ProcessorId);
        Assert.Equal("payload", dispatch.Payload);
        Assert.Equal(correlationId, dispatch.CorrelationId);
        Assert.NotEqual(Guid.Empty, dispatch.CorrelationId);
        Assert.Equal(Guid.Empty, dispatch.ExecutionId);
        Assert.Equal("", dispatch.EntryId);
        Assert.Equal("", dispatch.H);
    }

    [Fact]
    public void ImplementsIExecutionCorrelated()
    {
        var workflowId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();

        var dispatch = new EntryStepDispatch(workflowId, stepId, processorId, "payload")
        {
            CorrelationId = NewId.NextGuid(),
        };

        Assert.IsAssignableFrom<IExecutionCorrelated>(dispatch);
        Assert.IsAssignableFrom<ICorrelated>(dispatch);

        // The interface exposes the full execution id-set (7 logical fields incl. Payload on the record).
        IExecutionCorrelated correlated = dispatch;
        Assert.Equal(workflowId, correlated.WorkflowId);
        Assert.Equal(stepId, correlated.StepId);
        Assert.Equal(processorId, correlated.ProcessorId);
        Assert.NotEqual(Guid.Empty, correlated.CorrelationId);
        Assert.Equal(Guid.Empty, correlated.ExecutionId);
        Assert.Equal("", correlated.EntryId);
    }
}
