using BaseApi.Tests.Orchestrator;
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
public sealed class ObservabilityCollection : ICollectionFixture<RealStackNetZeroSweepFixture>
{
    // ICollectionFixture<RealStackNetZeroSweepFixture>: runs the host-stack net-zero sweep ONCE after every
    // test in this (DisableParallelization) collection finishes — the cron round-trip tests (SC1/SC2 +
    // SampleRoundTrip) live here, so their TTL-bound skp:data:* output residue + any skp-dlq-1 dead-letters
    // are actively reclaimed before the close gate's net-zero snapshot. Best-effort on a hermetic-only run.
}
