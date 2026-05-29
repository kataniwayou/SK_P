using System.Text.Json.Serialization;
using BaseApi.Service.Features.Step;

namespace BaseApi.Service.Features.Orchestration.Projection;

/// <summary>
/// L2 per-step projection value for the <c>{prefix}{workflowId}:{stepId}</c> key
/// (L2-PROJECT-04). <c>entryCondition</c> MUST serialize as an int — no string-enum
/// converter is registered anywhere (L2-PROJECT-04). <c>nextStepIds</c> is a list
/// because StepNextSteps is many-to-many; empty/null = terminal step. The
/// <c>[property: JsonPropertyName]</c> targets are load-bearing (RESEARCH Pitfall 1).
/// </summary>
internal sealed record StepProjection(
    [property: JsonPropertyName("entryCondition")] StepEntryCondition EntryCondition,
    [property: JsonPropertyName("processorId")]    Guid ProcessorId,
    [property: JsonPropertyName("payload")]        string Payload,
    [property: JsonPropertyName("nextStepIds")]    List<Guid> NextStepIds);
