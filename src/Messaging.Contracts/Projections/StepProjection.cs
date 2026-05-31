using System.Text.Json.Serialization;

namespace Messaging.Contracts.Projections;

/// <summary>
/// Reader-consumable L2 per-step projection for the {prefix}{workflowId}:{stepId} key
/// (ORCH-CONTRACT-01). Hoisted shape of the writer-internal BaseApi.Service StepProjection so the
/// Orchestrator (references only Messaging.Contracts) can deserialize step values — single source
/// of truth per HARDEN-03. EntryCondition is typed <c>int</c> (NOT the writer's StepEntryCondition enum):
/// no string-enum converter is registered anywhere, so the enum serializes as its underlying int
/// and both records are byte-identical on the wire (RESEARCH Pitfall 7). The writer is NOT refactored
/// to consume this record (out of scope). [property: JsonPropertyName] targets are load-bearing
/// (RESEARCH Pitfall 1 — a bare [JsonPropertyName] on a positional record binds the ctor param,
/// which STJ ignores).
/// </summary>
public sealed record StepProjection(
    [property: JsonPropertyName("entryCondition")] int EntryCondition,
    [property: JsonPropertyName("processorId")]    Guid ProcessorId,
    [property: JsonPropertyName("payload")]        string Payload,
    [property: JsonPropertyName("nextStepIds")]    List<Guid> NextStepIds);
