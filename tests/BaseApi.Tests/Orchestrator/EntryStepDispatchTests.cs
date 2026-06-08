using MassTransit;
using Messaging.Contracts;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Pins the <see cref="EntryStepDispatch"/> orchestrator->processor message shape (ORCH-CONTRACT-02)
/// after the Phase 43 reshape (D-04/D-05): a record carrying a per-fire CorrelationId (D-05, minted via
/// <c>NewId.NextGuid()</c>) whose ExecutionId defaults to <see cref="Guid.Empty"/> and whose EntryId is
/// now a <see cref="Guid"/> defaulting to <see cref="Guid.Empty"/> on entry dispatch — with NO H field
/// (RETIRE-01) — implementing the segregated <see cref="IExecutionCorrelated"/> interface (D-01).
/// </summary>
public sealed class EntryStepDispatchTests
{
    [Fact]
    public void Fields_ExecutionEmpty_EntryIdGuidEmpty_NoH()
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

        // D-04: EntryId is now a Guid defaulting to Guid.Empty (the source-step sentinel), not "".
        Assert.Equal(Guid.Empty, dispatch.EntryId);

        // RETIRE-01: H is gone from the wire contract entirely.
        Assert.Null(typeof(EntryStepDispatch).GetProperty("H"));
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

        // The interface exposes the full execution id-set.
        IExecutionCorrelated correlated = dispatch;
        Assert.Equal(workflowId, correlated.WorkflowId);
        Assert.Equal(stepId, correlated.StepId);
        Assert.Equal(processorId, correlated.ProcessorId);
        Assert.NotEqual(Guid.Empty, correlated.CorrelationId);
        Assert.Equal(Guid.Empty, correlated.ExecutionId);
        Assert.Equal(Guid.Empty, correlated.EntryId);
    }
}
