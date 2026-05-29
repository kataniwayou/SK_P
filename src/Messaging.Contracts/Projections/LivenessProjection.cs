using System.Text.Json.Serialization;

namespace Messaging.Contracts.Projections;

/// <summary>
/// L2 liveness sub-document nested inside the root and processor projections
/// (L2-PROJECT-03/05). Field shapes only — no scheduler integration this milestone.
/// The <c>[property: JsonPropertyName]</c> target is load-bearing: on a positional record
/// a bare attribute binds to the ctor parameter and STJ ignores it (RESEARCH Pitfall 1).
/// </summary>
public sealed record LivenessProjection(
    [property: JsonPropertyName("timestamp")] DateTime Timestamp,
    [property: JsonPropertyName("interval")]  int Interval,
    [property: JsonPropertyName("status")]    string Status);
