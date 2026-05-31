using System.Text.Json;
using Messaging.Contracts.Projections;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Proves the reader <see cref="StepProjection"/> round-trips a writer-produced L2 step value
/// (ORCH-CONTRACT-01). The writer serializes <c>StepEntryCondition.PreviousCompleted</c> as its
/// underlying int (= 1) — no string-enum converter is registered anywhere — so the reader's
/// <c>int EntryCondition</c> binds the same wire value (RESEARCH Pitfall 7). Every
/// <c>[property: JsonPropertyName]</c> target must bind (guards Pitfall 1 — a bare attribute on a
/// positional record binds the ctor param, which STJ ignores).
/// </summary>
public sealed class StepProjectionReaderTests
{
    // Reproduces the writer's value shape: the enum (StepEntryCondition.PreviousCompleted) serializes
    // as int 1, with the same camelCase property names as the writer's StepProjection record.
    private static string WriterStepJson(Guid processorId, Guid stepId) =>
        JsonSerializer.Serialize(new
        {
            entryCondition = 1,
            processorId,
            payload = "p",
            nextStepIds = new[] { stepId },
        });

    [Fact]
    public void DeserializesWriterValue_EntryConditionRoundTripsAsInt()
    {
        var processorId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var json = WriterStepJson(processorId, stepId);

        var projection = JsonSerializer.Deserialize<StepProjection>(json);

        Assert.NotNull(projection);
        Assert.Equal(1, projection!.EntryCondition);
        Assert.Equal(processorId, projection.ProcessorId);
        Assert.Equal("p", projection.Payload);
        Assert.Equal(stepId, projection.NextStepIds.Single());
    }

    [Fact]
    public void AllFieldsCamelCaseBound()
    {
        var processorId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var json = WriterStepJson(processorId, stepId);

        var projection = JsonSerializer.Deserialize<StepProjection>(json);

        Assert.NotNull(projection);
        Assert.NotEqual(Guid.Empty, projection!.ProcessorId);   // processorId bound
        Assert.NotNull(projection.Payload);                     // payload bound
        Assert.NotEmpty(projection.NextStepIds);                // nextStepIds bound
    }
}
