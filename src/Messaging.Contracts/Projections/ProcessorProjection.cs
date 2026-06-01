using System.Text.Json.Serialization;

namespace Messaging.Contracts.Projections;

/// <summary>
/// L2 per-processor projection value for the <c>{prefix}{processorId}</c> key
/// (L2-PROJECT-05). Field names are exactly <c>inputDefinition</c> / <c>outputDefinition</c>
/// (NOT <c>definitionIn</c>/<c>definitionOut</c> — locked PROJECT.md constraint). The
/// <c>[property: JsonPropertyName]</c> targets are load-bearing (RESEARCH Pitfall 1).
/// Relocated from <c>BaseApi.Service.Features.Orchestration.Projection</c> into the leaf and
/// made public (CONTRACT-01 / D-01) so the WebApi (reader) and the Phase 26 processor (writer)
/// share ONE type and cannot desync on field names.
/// </summary>
public sealed record ProcessorProjection(
    [property: JsonPropertyName("inputDefinition")]  string? InputDefinition,
    [property: JsonPropertyName("outputDefinition")] string? OutputDefinition,
    [property: JsonPropertyName("liveness")]         LivenessProjection Liveness);
