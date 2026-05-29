using System.Text.Json;
using BaseApi.Service.Features.Orchestration;
using BaseApi.Service.Features.Orchestration.Validation;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Phase 14 SC#2 (L1-VALIDATE-04 / D-08) WHITE-BOX tests for the missing-step gate. A real
/// Postgres-backed graph cannot easily produce a dangling <c>NextStepId</c> — the
/// <c>StepNextSteps</c> junction has FK-Restrict — so we construct a crafted in-memory
/// <see cref="WorkflowGraphSnapshot"/> and call <see cref="CycleDetector.Validate"/> directly.
/// <c>InternalsVisibleTo("BaseApi.Tests")</c> grants access to the internal seam + record.
/// <para>
/// Two facts:
/// <list type="number">
///   <item><c>MissingNextStep_Throws_WithParentAndMissingChild</c> — a Step whose
///     <c>NextStepIds</c> references an id absent from <c>Steps</c> throws
///     <see cref="OrchestrationValidationException"/> with <c>gate == "missingStep"</c> and an
///     offending body carrying <c>parentStepId</c> + <c>missingChildId</c>.</item>
///   <item><c>TerminalNullNextStepIds_Passes</c> — Steps with null OR empty <c>NextStepIds</c>
///     are terminal and do NOT throw.</item>
/// </list>
/// </para>
/// </summary>
[Trait("Phase", "14")]
public sealed class MissingStepFacts
{
    private static StepReadDto Step(Guid id, List<Guid>? nextStepIds) => new(
        Id: id,
        Name: "step",
        Version: "1.0.0",
        Description: null,
        ProcessorId: Guid.NewGuid(),
        NextStepIds: nextStepIds,
        EntryCondition: StepEntryCondition.PreviousCompleted,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow,
        CreatedBy: null,
        UpdatedBy: null);

    private static WorkflowReadDto Workflow(Guid id, List<Guid> entryStepIds) => new(
        Id: id,
        Name: "wf",
        Version: "1.0.0",
        Description: null,
        EntryStepIds: entryStepIds,
        AssignmentIds: null,
        CronExpression: null,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow,
        CreatedBy: null,
        UpdatedBy: null);

    [Fact]
    public void MissingNextStep_Throws_WithParentAndMissingChild()
    {
        var parentId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var wfId = Guid.NewGuid();

        var snapshot = new WorkflowGraphSnapshot(NullLogger<WorkflowGraphSnapshot>.Instance)
        {
            Steps = new Dictionary<Guid, StepReadDto>
            {
                [parentId] = Step(parentId, new List<Guid> { missingId }), // missingId NOT a key in Steps
            },
            Workflows = new Dictionary<Guid, WorkflowReadDto>
            {
                [wfId] = Workflow(wfId, new List<Guid> { parentId }),
            },
        };

        var ex = Assert.Throws<OrchestrationValidationException>(
            () => new CycleDetector().Validate(snapshot));

        Assert.Equal("missingStep", ex.Gate);

        // Serialize the offending envelope and parse it (rather than reflecting over the
        // anonymous/record shape) to assert parentStepId + missingChildId.
        var json = JsonSerializer.Serialize(ex.ErrorsExtension);
        using var doc = JsonDocument.Parse(json);
        var offending = doc.RootElement.GetProperty("offending");
        Assert.Equal(parentId, Guid.Parse(offending.GetProperty("parentStepId").GetString()!));
        Assert.Equal(missingId, Guid.Parse(offending.GetProperty("missingChildId").GetString()!));
    }

    [Fact]
    public void TerminalNullNextStepIds_Passes()
    {
        var nullStepId = Guid.NewGuid();
        var emptyStepId = Guid.NewGuid();
        var wfId = Guid.NewGuid();

        var snapshot = new WorkflowGraphSnapshot(NullLogger<WorkflowGraphSnapshot>.Instance)
        {
            Steps = new Dictionary<Guid, StepReadDto>
            {
                [nullStepId] = Step(nullStepId, nextStepIds: null),               // null → terminal
                [emptyStepId] = Step(emptyStepId, nextStepIds: new List<Guid>()), // empty → terminal
            },
            Workflows = new Dictionary<Guid, WorkflowReadDto>
            {
                [wfId] = Workflow(wfId, new List<Guid> { nullStepId, emptyStepId }),
            },
        };

        // Both terminal forms pass — no exception.
        var ex = Record.Exception(() => new CycleDetector().Validate(snapshot));
        Assert.Null(ex);
    }
}
