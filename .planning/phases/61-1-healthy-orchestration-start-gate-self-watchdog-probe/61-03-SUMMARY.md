---
phase: 61-1-healthy-orchestration-start-gate-self-watchdog-probe
plan: 03
subsystem: testing
tags: [healthcheck, liveness, watchdog, integration, kestrel, probe, generic-host]

# Dependency graph
requires:
  - phase: 61-02
    provides: "LivenessWatchdogHealthCheck + live-tagged HealthCheckDescriptor registered transitively in AddBaseProcessor + EmbeddedHealthEndpointService fold of outer descriptors onto /health/live"
  - phase: 60-dual-loop-writer-l1-liveness-record
    provides: "IProcessorLivenessState singleton L1 holder (Current null-until-first-write) + ProcessorLivenessEntry.Create the fixture seeds"
  - phase: 18-baseconsole-core
    provides: "ConsoleTestHostFixture Generic-Host harness (free-port embedded listener + HttpClient + overridable ConfigureBuilder)"
provides:
  - "ProcessorConsoleTestHostFixture — processor Generic-Host fixture composing AddBaseProcessor (watchdog descriptor arrives transitively on /health/live) over a dead-dep boot, with a SeedLiveness L1 helper"
  - "End-to-end proof that AddBaseProcessor -> HealthCheckDescriptor -> embedded Kestrel /health/live surfaces the watchdog verdict (null/stale -> 503, fresh -> 200) and carries the per-schema summary in the body (PROBE-01/02)"
affects: [62-live-proof-close-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Processor Generic-Host integration fixture: subclass ConsoleTestHostFixture, override ConfigureBuilder to mirror Processor.Sample/Program.cs (AddBaseConsoleObservability + AddBaseProcessor + the one concrete BaseProcessor seam), then strip the two background startup/heartbeat loops so the in-process host boots without a build-time embedded SourceHash"
    - "Isolated null-verdict via a dedicated never-seeding IClassFixture subclass (separate test class) so a sibling fact's fresh seed cannot leak into the null case under shared-fixture ordering"
    - "Re-seed-then-GET within each fact (watchdog re-resolves Current every check) makes the shared-fixture stale/fresh facts order-independent"

key-files:
  created:
    - tests/BaseApi.Tests/Console/ProcessorConsoleTestHostFixture.cs
    - tests/BaseApi.Tests/Console/ProcessorHealthLiveTests.cs
  modified: []

key-decisions:
  - "Reused Processor.Sample's SampleProcessor as the concrete BaseProcessor seam (already a test ProjectReference) rather than authoring a throwaway no-op processor — matches Processor.Sample/Program.cs verbatim"
  - "[Rule 3] Stripped ProcessorStartupOrchestrator + ProcessorLivenessHeartbeat hosted services from the fixture: the in-process MTP host's entry assembly (BaseApi.Tests) carries no embedded SourceHash, so the startup loop's AssemblyMetadataSourceHashProvider.Get() threw inside Host.StartAsync; the loops are the only OTHER L1 writers and the test seeds L1 directly, so removing them is behavior-neutral for the watchdog wiring under proof"
  - "Null verdict isolated in its own test class (ProcessorHealthLiveNullTests + NeverSeedingFixture) — IClassFixture shares one instance across facts and xUnit ordering is nondeterministic, so a sibling fresh seed would otherwise pollute the null case"

requirements-completed: [PROBE-01, PROBE-02]

# Metrics
duration: 4min
completed: 2026-06-13
---

# Phase 61 Plan 03: Processor /health/live Generic-Host Integration Proof Summary

**A processor Generic-Host fixture composes `AddBaseProcessor` so the Plan-02 watchdog descriptor lands transitively on the embedded `/health/live`, and a Phase=61 integration class proves null/stale L1 -> 503 Unhealthy, fresh L1 -> 200 Healthy with the per-schema summary in the body, and no secret leak — closing the full `AddBaseProcessor -> HealthCheckDescriptor -> Kestrel listener` wiring path end-to-end.**

## Performance

- **Duration:** ~4 min
- **Started:** 2026-06-13T14:16:46Z
- **Completed:** 2026-06-13T14:21:09Z
- **Tasks:** 2
- **Files modified:** 2 (2 created, 0 modified)

## Accomplishments
- `ProcessorConsoleTestHostFixture` subclasses `ConsoleTestHostFixture` and overrides `ConfigureBuilder` to compose `AddBaseConsoleObservability` + `AddBaseProcessor` + `AddSingleton<BaseProcessor, SampleProcessor>` exactly as `Processor.Sample/Program.cs` does — so the Plan-02 `liveness-watchdog` `HealthCheckDescriptor` arrives transitively and the embedded `EmbeddedHealthEndpointService` folds it onto `/health/live`, over the inherited dead-Redis/dead-RMQ boot (D-01: the watchdog reads only the in-process L1, never Redis/RMQ).
- `SeedLiveness(timestamp, interval)` resolves the singleton `IProcessorLivenessState` from the running host and swaps in `ProcessorLivenessEntry.Create(null,null,null,...)` so a fact can deterministically drive the fresh/stale verdict; leaving `Current` unseeded exercises the null ("liveness loop not started") path.
- `ProcessorHealthLiveTests` (`[Trait("Phase","61")]`) proves stale L1 -> 503 + `"status":"Unhealthy"`, fresh L1 -> 200 + body contains `inputSchema`/`outputSchema`/`configSchema` (PROBE-02), and a no-secrets guard (no `Password=`, `abortConnect`, or `   at ` stack frame — T-61-07).
- `ProcessorHealthLiveNullTests` isolates the null verdict in its own class against a never-seeding fixture subclass, proving `Current == null` -> 503 + Unhealthy without sibling-seed pollution (PROBE-01).

## Task Commits

Each task was committed atomically:

1. **Task 1: Processor Generic-Host fixture composing AddBaseProcessor** - `3001dec` (test)
2. **Task 2: /health/live integration facts (null/stale -> 503, fresh -> 200 + summary + no-secrets)** - `47f256c` (test)

## Files Created/Modified
- `tests/BaseApi.Tests/Console/ProcessorConsoleTestHostFixture.cs` - new processor Generic-Host fixture: `ConfigureBuilder` override (observability + AddBaseProcessor + SampleProcessor seam), `SeedLiveness` L1 helper, and the Rule-3 removal of the two background loops so the in-process host boots
- `tests/BaseApi.Tests/Console/ProcessorHealthLiveTests.cs` - the Phase=61 integration class (stale/fresh/no-secrets) plus the isolated `ProcessorHealthLiveNullTests` + `NeverSeedingFixture` for the null verdict

## Decisions Made
- Reused `Processor.Sample`'s `SampleProcessor` (already a test `ProjectReference`) as the concrete `BaseProcessor` seam — no throwaway no-op processor needed; matches the canonical `Processor.Sample/Program.cs` composition.
- Per the plan's isolation guidance, picked the per-test-deterministic approach: stale/fresh/no-secret facts share the seeding fixture and re-seed immediately before each GET (the watchdog re-resolves `Current` every check), while the null fact gets its own never-seeding fixture class so no sibling fresh seed leaks in.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Stripped the two processor background loops from the fixture so the in-process host boots**
- **Found during:** Task 2 (first test run)
- **Issue:** `AddBaseProcessor` registers `ProcessorStartupOrchestrator` as a hosted service whose first act (`ExecuteAsync`) calls `AssemblyMetadataSourceHashProvider.Get()`, which reads the ENTRY assembly's `[assembly: AssemblyMetadata("SourceHash", ...)]`. The Phase-28 MSBuild embed target emits that attribute only onto a real `Processor.<Purpose>.dll`, never onto the MTP test entry assembly (`BaseApi.Tests`). The provider throws `InvalidOperationException`, which surfaces inside `Host.StartAsync`, so the fixture's `InitializeAsync` failed and all 4 facts errored before any GET.
- **Fix:** Added a `RemoveHostedService<T>` helper to the fixture's `ConfigureBuilder` that removes the `ProcessorStartupOrchestrator` and `ProcessorLivenessHeartbeat` hosted-service wrappers (matched by their BaseProcessor.Core-authored factory) plus the concrete singletons. These two loops are the ONLY other writers of the L1 holder; the test seeds L1 directly via `SeedLiveness`, and the watchdog descriptor + embedded listener under proof are registered separately and left fully intact. Behavior-neutral for the wiring being proven; the embedded `EmbeddedHealthEndpointService` and the MassTransit bus hosted service survive.
- **Files modified:** tests/BaseApi.Tests/Console/ProcessorConsoleTestHostFixture.cs
- **Commit:** 47f256c

## Authentication Gates

None.

## Verification
- `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug` -> 0 warnings / 0 errors (Task 1 + Task 2).
- `dotnet build SK_P.sln -c Release` -> 0 warnings / 0 errors (plan verification).
- `ProcessorHealthLiveTests` + `ProcessorHealthLiveNullTests` (4 facts) -> 4/4 GREEN via the MTP `--filter-class` host (the documented VSTest `--filter`-ignored pitfall).
- No regression: `ConsoleHealthLiveTests` (the self-only `/health/live` listener) + `LivenessWatchdogHealthCheckTests` (the Plan-02 pure unit) -> 5/5 GREEN together.

## Known Stubs

None. The fixture seeds real `ProcessorLivenessEntry` values and asserts against the live embedded listener; no placeholder data flows to the assertions.

## Next Phase Readiness
- PROBE-01/02 are now proven both in isolation (Plan 02 pure unit) and end-to-end through the real embedded Kestrel listener (this plan). Phase 62 (Live Proof & Close Gate) will exercise the same watchdog against a real stale L1 on the live stack alongside the ≥1-healthy gate from 61-01.
- The fixture's dead-dep boot + L1-seed pattern is reusable for any future processor `/health/live` integration fact.

## Self-Check: PASSED

- FOUND: tests/BaseApi.Tests/Console/ProcessorConsoleTestHostFixture.cs
- FOUND: tests/BaseApi.Tests/Console/ProcessorHealthLiveTests.cs
- FOUND: .planning/phases/61-1-healthy-orchestration-start-gate-self-watchdog-probe/61-03-SUMMARY.md
- FOUND commit: 3001dec (Task 1)
- FOUND commit: 47f256c (Task 2)

---
*Phase: 61-1-healthy-orchestration-start-gate-self-watchdog-probe*
*Completed: 2026-06-13*
