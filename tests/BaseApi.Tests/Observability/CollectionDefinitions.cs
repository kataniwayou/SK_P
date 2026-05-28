using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// xUnit collection marker that serializes Phase 5 / Phase 11 observability tests so they
/// do not race against the shared ES + Prom backends during polling cycles. Every
/// observability test class (LogExport, LogLevelFilter, Metrics, Health, SchemasLogsE2E,
/// SchemasMetricsE2E) declares <c>[Collection("Observability")]</c>. Phase 11 Plan 11-05
/// retired the prior file-exporter coupling; serialization invariant retained for shared
/// backend determinism.
/// </summary>
[CollectionDefinition("Observability", DisableParallelization = true)]
public sealed class ObservabilityCollection
{
}
