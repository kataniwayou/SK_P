using System.Reflection;
using System.Text.Json;
using Messaging.Contracts;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Contract round-trip + shape facts for the Phase 24 result vocabulary (ORCH-RESULT-01).
/// Mirrors the EntryStepDispatch contract-test shape: pure System.Text.Json serialization,
/// no harness. Proves the bus envelope serializes int-for-enum (no string converter),
/// preserves every field, carries no output/payload field, and implements IExecutionCorrelated.
/// </summary>
public sealed class ExecutionResultContractTests
{
    [Fact]
    public void ExecutionResult_round_trips_every_field()
    {
        var original = new ExecutionResult(
            WorkflowId: Guid.NewGuid(),
            StepId: Guid.NewGuid(),
            ProcessorId: Guid.NewGuid(),
            Outcome: StepOutcome.Completed)
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),
            ErrorMessage = "err",
            CancellationMessage = "cancel",
        };

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<ExecutionResult>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(original, roundTripped); // record value-equality covers every member
        Assert.Equal(original.WorkflowId, roundTripped!.WorkflowId);
        Assert.Equal(original.StepId, roundTripped.StepId);
        Assert.Equal(original.ProcessorId, roundTripped.ProcessorId);
        Assert.Equal(original.Outcome, roundTripped.Outcome);
        Assert.Equal(original.CorrelationId, roundTripped.CorrelationId);
        Assert.Equal(original.ExecutionId, roundTripped.ExecutionId);
        Assert.Equal(original.EntryId, roundTripped.EntryId);
        Assert.Equal(original.ErrorMessage, roundTripped.ErrorMessage);
        Assert.Equal(original.CancellationMessage, roundTripped.CancellationMessage);
    }

    [Fact]
    public void ExecutionResult_preserves_real_execution_and_entry_ids()
    {
        // The processor copies real execution ids forward — they are NOT forced Guid.Empty.
        var executionId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var original = new ExecutionResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), StepOutcome.Processing)
        {
            ExecutionId = executionId,
            EntryId = entryId,
        };

        var roundTripped = JsonSerializer.Deserialize<ExecutionResult>(JsonSerializer.Serialize(original));

        Assert.NotEqual(Guid.Empty, roundTripped!.ExecutionId);
        Assert.NotEqual(Guid.Empty, roundTripped.EntryId);
        Assert.Equal(executionId, roundTripped.ExecutionId);
        Assert.Equal(entryId, roundTripped.EntryId);
    }

    [Theory]
    [InlineData(StepOutcome.Processing, 0)]
    [InlineData(StepOutcome.Completed, 1)]
    [InlineData(StepOutcome.Failed, 2)]
    [InlineData(StepOutcome.Cancelled, 3)]
    public void StepOutcome_int_values_mirror_StepEntryCondition_Previous_subset(StepOutcome outcome, int expected)
    {
        Assert.Equal(expected, (int)outcome);
    }

    [Fact]
    public void StepOutcome_serializes_as_its_underlying_int()
    {
        // No JsonStringEnumConverter is registered — the enum must serialize as a number.
        var json = JsonSerializer.Serialize(
            new ExecutionResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), StepOutcome.Failed));

        Assert.Contains("\"Outcome\":2", json);
    }

    [Fact]
    public void ExecutionResult_has_no_output_or_payload_property()
    {
        Assert.Null(typeof(ExecutionResult).GetProperty("Payload"));
        Assert.Null(typeof(ExecutionResult).GetProperty("Output"));

        var hasOutputLike = typeof(ExecutionResult)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => p.Name.Contains("Output", StringComparison.OrdinalIgnoreCase)
                      || p.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase));

        Assert.False(hasOutputLike);
    }

    [Fact]
    public void ExecutionResult_implements_IExecutionCorrelated()
    {
        Assert.True(typeof(IExecutionCorrelated).IsAssignableFrom(typeof(ExecutionResult)));
    }

    [Fact]
    public void Failed_result_preserves_error_message_and_leaves_cancellation_null()
    {
        var failed = new ExecutionResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), StepOutcome.Failed)
        {
            ErrorMessage = "boom",
        };

        var roundTripped = JsonSerializer.Deserialize<ExecutionResult>(JsonSerializer.Serialize(failed));

        Assert.Equal("boom", roundTripped!.ErrorMessage);
        Assert.Null(roundTripped.CancellationMessage);
    }

    [Fact]
    public void Cancelled_result_preserves_cancellation_message_and_leaves_error_null()
    {
        var cancelled = new ExecutionResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), StepOutcome.Cancelled)
        {
            CancellationMessage = "stopped",
        };

        var roundTripped = JsonSerializer.Deserialize<ExecutionResult>(JsonSerializer.Serialize(cancelled));

        Assert.Equal("stopped", roundTripped!.CancellationMessage);
        Assert.Null(roundTripped.ErrorMessage);
    }

    [Fact]
    public void Both_diagnostic_messages_are_null_when_unset()
    {
        var result = new ExecutionResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), StepOutcome.Completed);

        var roundTripped = JsonSerializer.Deserialize<ExecutionResult>(JsonSerializer.Serialize(result));

        Assert.Null(roundTripped!.ErrorMessage);
        Assert.Null(roundTripped.CancellationMessage);
    }
}
