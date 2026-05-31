using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Phase 22 (D-22) xUnit collection marker that SERIALIZES every test class touching the shared
/// <c>skp:</c> parent-index SET so they never race on it. With the L2 prefix now the compile-time
/// const <c>L2ProjectionKeys.Prefix == "skp:"</c> (no per-class isolation prefix), per-workflow /
/// step / processor keys stay collision-free via unique GUIDs (D-21) — the ONLY contention point on
/// the now-shared keyspace is the single shared parent-index SET (<c>SADD</c> on Start, <c>SREM</c>
/// on Stop, <c>SMEMBERS</c> in asserts). Classes that touch it carry <c>[Collection("ParentIndex")]</c>
/// (<see cref="RedisProjectionWriterFacts"/>, <see cref="StopCleanupFacts"/>,
/// <see cref="GateNoWriteFacts"/>, <see cref="ProcessorLivenessFacts"/>) and each SREMs its own wf id
/// so the SET is empty between tests — the triple-SHA close gate (<c>redis-cli --scan</c> BEFORE==AFTER)
/// is the fail-loud proof (T-22-15).
/// </summary>
[CollectionDefinition("ParentIndex", DisableParallelization = true)]
public sealed class ParentIndexCollection
{
}
