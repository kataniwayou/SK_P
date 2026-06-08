using System.Linq;
using System.Text.Json;
using Messaging.Contracts;
using Xunit;

namespace BaseApi.Tests.Contracts;

/// <summary>
/// Phase 43 Wave-0 RED Nyquist proof for SC-1/SC-2 (D-06/D-06a/D-06b). Pins the four typed
/// step-result records (StepCompleted/StepFailed/StepCancelled/StepProcessing): six ids, NO H,
/// : IStepResult : IExecutionCorrelated, Guid.Empty EntryId defaults on the non-Completed records,
/// diagnostic-field placement, and the present-zero-GUID sentinel serialization (Pitfall 1).
///
/// These reference symbols that do not exist until Plan 02 (StepCompleted/StepFailed/StepCancelled/
/// StepProcessing/IStepResult/Guid EntryId on IExecutionCorrelated) and are deliberately RED until then.
/// Mirrors the ExecutionResultContractTests shape: pure reflection + STJ round-trip, no harness.
/// </summary>
[Trait("Phase", "43")]
public sealed class StepResultContractTests
{
    private static readonly string[] SixIds =
        { "CorrelationId", "WorkflowId", "StepId", "ProcessorId", "ExecutionId", "EntryId" };

    // ---- SC-1: H is absent on all four records ------------------------------------------------

    [Fact]
    public void StepCompleted_has_no_H_property()
        => Assert.Null(typeof(StepCompleted).GetProperty("H"));

    [Fact]
    public void StepFailed_has_no_H_property()
        => Assert.Null(typeof(StepFailed).GetProperty("H"));

    [Fact]
    public void StepCancelled_has_no_H_property()
        => Assert.Null(typeof(StepCancelled).GetProperty("H"));

    [Fact]
    public void StepProcessing_has_no_H_property()
        => Assert.Null(typeof(StepProcessing).GetProperty("H"));

    // ---- SC-1: each record carries exactly the six ids (+ its one diagnostic field) ----------

    [Fact]
    public void StepCompleted_carries_exactly_the_six_ids_and_no_diagnostic_field()
    {
        var props = typeof(StepCompleted).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var id in SixIds)
            Assert.Contains(id, props);
        Assert.Null(typeof(StepCompleted).GetProperty("ErrorMessage"));
        Assert.Null(typeof(StepCompleted).GetProperty("CancellationMessage"));
    }

    [Fact]
    public void StepProcessing_carries_exactly_the_six_ids_and_no_diagnostic_field()
    {
        var props = typeof(StepProcessing).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var id in SixIds)
            Assert.Contains(id, props);
        Assert.Null(typeof(StepProcessing).GetProperty("ErrorMessage"));
        Assert.Null(typeof(StepProcessing).GetProperty("CancellationMessage"));
    }

    [Fact]
    public void StepFailed_carries_the_six_ids_plus_ErrorMessage_and_no_CancellationMessage()
    {
        var props = typeof(StepFailed).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var id in SixIds)
            Assert.Contains(id, props);
        Assert.NotNull(typeof(StepFailed).GetProperty("ErrorMessage"));   // D-06b
        Assert.Null(typeof(StepFailed).GetProperty("CancellationMessage"));
    }

    [Fact]
    public void StepCancelled_carries_the_six_ids_plus_CancellationMessage_and_no_ErrorMessage()
    {
        var props = typeof(StepCancelled).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var id in SixIds)
            Assert.Contains(id, props);
        Assert.NotNull(typeof(StepCancelled).GetProperty("CancellationMessage"));   // D-06b
        Assert.Null(typeof(StepCancelled).GetProperty("ErrorMessage"));
    }

    // ---- SC-1: interface layering -------------------------------------------------------------

    [Fact]
    public void All_four_records_implement_IStepResult_and_IExecutionCorrelated()
    {
        foreach (var t in new[] { typeof(StepCompleted), typeof(StepFailed), typeof(StepCancelled), typeof(StepProcessing) })
        {
            Assert.True(typeof(IStepResult).IsAssignableFrom(t), $"{t.Name} must implement IStepResult");
            Assert.True(typeof(IExecutionCorrelated).IsAssignableFrom(t), $"{t.Name} must implement IExecutionCorrelated");
        }
    }

    // ---- SC-2 / D-06a: Guid.Empty EntryId defaults on the non-Completed records ---------------

    [Fact]
    public void StepFailed_defaults_EntryId_to_Guid_Empty_sentinel()
        => Assert.Equal(Guid.Empty, new StepFailed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()).EntryId);

    [Fact]
    public void StepCancelled_defaults_EntryId_to_Guid_Empty_sentinel()
        => Assert.Equal(Guid.Empty, new StepCancelled(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()).EntryId);

    [Fact]
    public void StepProcessing_defaults_EntryId_to_Guid_Empty_sentinel()
        => Assert.Equal(Guid.Empty, new StepProcessing(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()).EntryId);

    // StepCompleted's EntryId is the REAL data key (D-06a) — NO Guid.Empty default asserted.

    // ---- Pitfall 1: the sentinel is a PRESENT zero-GUID on the wire, not an omitted field -----

    [Fact]
    public void StepFailed_default_EntryId_serializes_to_the_present_zero_Guid()
    {
        var failed = new StepFailed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var json = JsonSerializer.Serialize(failed);
        Assert.Contains("\"EntryId\":\"00000000-0000-0000-0000-000000000000\"", json);
    }
}
