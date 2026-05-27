using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// xUnit collection marker that serializes Phase 5 observability tests so they do not
/// interleave writes to <c>tests/.otel-out/telemetry.jsonl</c>. Every Phase 5 test class
/// must declare <c>[Collection("Observability")]</c>.
/// </summary>
[CollectionDefinition("Observability", DisableParallelization = true)]
public sealed class ObservabilityCollection
{
}
