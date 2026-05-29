using System.Text.Json.Serialization;

namespace Messaging.Contracts.Projections;

/// <summary>
/// L2 root projection value for the <c>{prefix}{workflowId}</c> key (L2-PROJECT-03).
/// <c>correlationId</c> is the originating Start POST's <c>X-Correlation-Id</c>, letting
/// consumers trace a projection back to its build request. The <c>[property: JsonPropertyName]</c>
/// targets are load-bearing (RESEARCH Pitfall 1).
/// </summary>
public sealed record WorkflowRootProjection(
    [property: JsonPropertyName("entryStepIds")] List<Guid> EntryStepIds,
    [property: JsonPropertyName("cron")]          string? Cron,
    [property: JsonPropertyName("jobId")]         Guid JobId,
    [property: JsonPropertyName("liveness")]      LivenessProjection Liveness,
    [property: JsonPropertyName("correlationId")] string CorrelationId);
