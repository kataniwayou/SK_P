using System.Text.Json.Serialization;

namespace BaseApi.Service.Features.Orchestration.Projection;

/// <summary>
/// L2 per-processor projection value for the <c>{prefix}{processorId}</c> key
/// (L2-PROJECT-05). Field names are exactly <c>inputDefinition</c> / <c>outputDefinition</c>
/// (NOT <c>definitionIn</c>/<c>definitionOut</c> — locked PROJECT.md constraint). The
/// <c>[property: JsonPropertyName]</c> targets are load-bearing (RESEARCH Pitfall 1).
/// </summary>
internal sealed record ProcessorProjection(
    [property: JsonPropertyName("inputDefinition")]  string? InputDefinition,
    [property: JsonPropertyName("outputDefinition")] string? OutputDefinition,
    [property: JsonPropertyName("liveness")]         LivenessProjection Liveness);
