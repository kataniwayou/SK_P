---
phase: 52-three-state-keeper
plan: 02
subsystem: keeper-recovery-endpoint
tags: [masstransit, connect-receive-endpoint, endpoint-pause-resume, exhaustion-policy, redis, keeper, recovery]

# Dependency graph
requires:
  - phase: 52-three-state-keeper
    plan: 01
    provides: "ExhaustionPolicy enum (Dlq1/SustainedOutage), KeeperMetrics, de-gated recovery consumer ctors, repurposed RecoveryDeadLetterFacts"
  - phase: 51-processor-forward-recovery-pipeline
    provides: "ConnectReceiveEndpoint analog (ProcessorStartupOrchestrator runtime-bind)"
provides:
  - "keeper-recovery runtime-bound via IReceiveEndpointConnector.ConnectReceiveEndpoint (RecoveryEndpointBinder), NOT static AddConsumer auto-config — pausable for KEEP-04"
  - "RecoveryEndpointHandle DI singleton holding the connected HostReceiveEndpointHandle for Plan 03 Stop/Start"
  - "ExhaustionPolicy-conditional connect callback: Dlq1 (Immediate retry -> skp-dlq-1) vs SustainedOutage (large-finite interval retry, no dead-letter)"
  - "KeeperMetrics DI registration (AddSingleton + AddMeter) — Plan 01 handoff completed"
  - "KEEP-04 pause/accumulate fact + KEEP-05 SustainedOutage no-dead-letter fact (both hermetic ITestHarness, green)"
affects: [52-03, 53, keeper-recovery, bit-health-loop, endpoint-pause-resume]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Runtime endpoint bind via ConnectReceiveEndpoint + ExcludeFromConfigureEndpoints (mirrors ProcessorStartupOrchestrator) — the only 8.5.5 runtime-pausable endpoint shape"
    - "ExhaustionPolicy-conditional retry posture in the connect callback (compile-time-shaped if on a startup enum)"
    - "SustainedOutage = large-but-finite interval retry (NOT int.MaxValue — Interval pre-allocates a TimeSpan[count] and OOMs); the accepted gate-open poison-op spin"

key-files:
  created:
    - src/Keeper/Recovery/RecoveryEndpointHandle.cs
    - src/Keeper/Recovery/RecoveryEndpointBinder.cs
    - tests/BaseApi.Tests/Keeper/KeeperPauseAccumulateFacts.cs
    - tests/BaseApi.Tests/Keeper/SustainedOutageFacts.cs
  modified:
    - src/Keeper/Program.cs
    - src/Keeper/Recovery/ReinjectConsumerDefinition.cs
  deleted:
    - src/Keeper/Recovery/InjectConsumerDefinition.cs
    - src/Keeper/Recovery/DeleteConsumerDefinition.cs

key-decisions:
  - "OQ-2 / D-03 SustainedOutage realization: a large-but-finite interval retry (1,000,000 x 1s) so a faulting op never reaches the error transport (no skp-dlq-1). int.MaxValue OOMs MassTransit's pre-allocated TimeSpan[count] (discovered + fixed in Task 3). A real outage is handled by KEEP-04 endpoint PAUSE (Plan 03), not the retry loop, so a large-finite count is faithful to D-03's accepted gate-open spin."
  - "Startup posture (Pitfall 3): connect STARTED (matches the processor analog); the fail-safe-closed gate + Plan 03's first-healthy-edge Start keeps the BIT model coherent. The brief pre-first-probe consumable window is accepted (T-52-08) because consume mutates L2 only on success."
  - "ReinjectConsumerDefinition collapsed from a ConsumerDefinition to a static helper class retaining only PartitionKey/PartitionGuid (RecoveryPartitionFacts pins them); the endpoint config re-homed into the binder. The two no-op sibling definitions deleted (consumers now ExcludeFromConfigureEndpoints)."
  - "Task 2(a) (repurpose RecoveryDeadLetterFacts to op-exhaustion->dead-letter) was already done by Plan 01 (InfraFault_reinject_faults_and_routes_to_dead_letter, Redis-exception-driven, no RecoveryDataGoneException) — acceptance met, no edit needed."

patterns-established:
  - "KEEP-04 mechanism: endpoint Stop/Start via the connected HostReceiveEndpointHandle (Plan 03 wires the BIT-edge driver)"
  - "Testing an infinite-requeue mode hermetically: assert within a bounded window, then bounded-token harness.Stop to force-cancel the accepted spin (no test hang)"

requirements-completed: [KEEP-04, KEEP-05]

# Metrics
duration: 21min
completed: 2026-06-11
---

# Phase 52 Plan 02: Keeper-Recovery Endpoint Pause/Resume + Exhaustion Policy Summary

**keeper-recovery is now RUNTIME-bound via `ConnectReceiveEndpoint` (the only 8.5.5 runtime-pausable shape) with its `HostReceiveEndpointHandle` stored in a singleton for the BIT loop, and the connect callback branches `ExhaustionPolicy` — Dlq1 (Immediate retry -> skp-dlq-1) vs SustainedOutage (large-finite interval retry, no dead-letter) — proven by green hermetic pause/accumulate and no-dead-letter facts.**

## Performance

- **Duration:** ~21 min
- **Started:** 2026-06-11T16:28:06Z
- **Completed:** 2026-06-11T16:49:55Z
- **Tasks:** 3
- **Files:** 6 (4 created, 2 modified, 2 deleted)

## Accomplishments
- **OQ-1 / D-04 (KEEP-04 mechanism):** converted keeper-recovery from static `AddConsumer(def)` auto-config to a runtime `IReceiveEndpointConnector.ConnectReceiveEndpoint` in a new `RecoveryEndpointBinder` BackgroundService (mirroring `ProcessorStartupOrchestrator`), so the returned `HostReceiveEndpointHandle` is `Stop`/`Start`-able. The three consumers are registered `ExcludeFromConfigureEndpoints()` so EXACTLY ONE source configures the endpoint (Pitfall 1 / T-52-05).
- **Handle singleton:** new `RecoveryEndpointHandle` DI singleton holds the connected handle (set after `await handle.Ready`) for Plan 03's BitHealthLoop Stop/Start driver.
- **KEEP-05 / D-01..D-03:** the connect callback branches on `RecoveryOptions.ExhaustionPolicy` — Dlq1 (default) wires `UseMessageRetry(Immediate(limit))` (exhaustion re-throws -> inherited `ConsolidatedErrorTransportFilter` -> skp-dlq-1); SustainedOutage wires a large-finite interval retry that never exhausts (no dead-letter; the accepted spin).
- **Plan 01 handoff completed:** `Program.cs` now registers `AddSingleton<KeeperMetrics>()` + `ConfigureOpenTelemetryMeterProvider(AddMeter(KeeperMetrics.MeterName))` so `ReinjectConsumer` resolves the meter at runtime.
- **Facts:** `KeeperPauseAccumulateFacts` (KEEP-04 — started endpoint consumes, stopped endpoint does NOT, Start resumes) and `SustainedOutageFacts` (KEEP-05 — NO ConsolidatedFault + read retried >1, bounded stop). Both green; full Keeper namespace 18/18, full hermetic suite 520/520.
- **Cleanup:** `ReinjectConsumerDefinition` collapsed to a static-helper class (PartitionKey/PartitionGuid preserved); the two no-op sibling definitions deleted.

## Task Commits

1. **Task 1: runtime-bind keeper-recovery via ConnectReceiveEndpoint + exhaustion-policy branch** - `fd72d9d` (feat)
2. **Task 2: KEEP-04 endpoint pause/accumulate fact** - `266b80c` (test)
3. **Task 3: KEEP-05 SustainedOutage no-dead-letter fact + OOM-safe retry count** - `5f24bb9` (test, includes the Rule-1 binder fix)

## Files Created/Modified
- `src/Keeper/Recovery/RecoveryEndpointHandle.cs` (created) - mutable DI singleton holding the connected `HostReceiveEndpointHandle` (D-04)
- `src/Keeper/Recovery/RecoveryEndpointBinder.cs` (created) - BackgroundService that `ConnectReceiveEndpoint(keeper-recovery)` with the re-homed retry + 3x partitioner + 3x ConfigureConsumer and the ExhaustionPolicy branch; stores the handle after `await handle.Ready`
- `tests/BaseApi.Tests/Keeper/KeeperPauseAccumulateFacts.cs` (created) - KEEP-04 Stop-blocks-consume / Start-resumes fact
- `tests/BaseApi.Tests/Keeper/SustainedOutageFacts.cs` (created) - KEEP-05 SustainedOutage no-ConsolidatedFault + redelivery fact
- `src/Keeper/Program.cs` - `ExcludeFromConfigureEndpoints()` for the three consumers; register the handle singleton + binder hosted service + KeeperMetrics AddSingleton/AddMeter; OpenTelemetry usings
- `src/Keeper/Recovery/ReinjectConsumerDefinition.cs` - collapsed to a static helper class retaining only PartitionKey/PartitionGuid; endpoint config re-homed to the binder
- `src/Keeper/Recovery/InjectConsumerDefinition.cs` (deleted) - pure no-op sibling, no longer attached
- `src/Keeper/Recovery/DeleteConsumerDefinition.cs` (deleted) - pure no-op sibling, no longer attached

## Decisions Made
- **SustainedOutage retry count (OQ-2 / D-03):** chose a large-but-finite count (`SustainedOutageRetryCount = 1,000,000`, x1s ~= 11.5 days) over `int.MaxValue`. MassTransit's `Interval(count, interval)` pre-allocates a `TimeSpan[count]`, so `int.MaxValue` throws `OutOfMemoryException` at bus build (verified). A genuine outage is handled by the KEEP-04 endpoint PAUSE (Plan 03 stops the endpoint so messages accumulate on the broker, not in the retry loop), so the retry only needs to outlast gate-OPEN transients — a large-finite count is faithful to D-03's "accepted poison-op spin while L2 is healthy."
- **Startup posture (Pitfall 3):** connect STARTED (simplest; matches the processor analog). The fail-safe-closed gate + Plan 03's first-healthy-edge Start keeps the BIT model coherent; the brief pre-first-probe consumable window is the accepted T-52-08 residual.
- **ReinjectConsumerDefinition shell (option ii):** kept it as a static-helper class for the PartitionKey/PartitionGuid statics that `RecoveryPartitionFacts` pins, deleting the two no-op siblings — minimal churn, no InternalsVisibleTo.
- **Pause/accumulate proof ordering:** the in-memory transport does not redeliver a message published to a STOPPED endpoint, so the fact proves the consume/no-consume SHAPE (started consumes / stopped does not / Start resumes) rather than literal Stop->Start backlog drain (the live-RabbitMQ drain is Phase-54 Manual-Only per 52-VALIDATION).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] SustainedOutage `Interval(int.MaxValue, ...)` OOMs at bus build**
- **Found during:** Task 3 (the SustainedOutageFacts harness build crashed with `System.OutOfMemoryException: Array dimensions exceeded supported range` from `RetryConfigurationExtensions.Interval`)
- **Issue:** Task 1's binder (and the planned SustainedOutage shape) used `r.Interval(int.MaxValue, TimeSpan.FromSeconds(1))`. MassTransit pre-allocates a `TimeSpan[count]`, so `int.MaxValue` throws at bus creation — the keeper would crash on startup in SustainedOutage mode.
- **Fix:** Replaced with a documented `SustainedOutageRetryCount = 1_000_000` constant (binder) and `10_000` (test, short interval) — both large enough to never realistically exhaust within scope, sized for the accepted gate-open spin (a real outage is handled by KEEP-04 pause).
- **Files modified:** src/Keeper/Recovery/RecoveryEndpointBinder.cs, tests/BaseApi.Tests/Keeper/SustainedOutageFacts.cs
- **Verification:** Keeper Debug + full-solution Release build 0/0; SustainedOutageFacts green.
- **Committed in:** 5f24bb9

**2. [Rule 3 - Blocking] `IReceiveEndpoint.Start` returns a `ReceiveEndpointHandle`, not a Task**
- **Found during:** Task 2 (the pause/accumulate fact's `await handle.ReceiveEndpoint.Start(ct)` failed CS1061 — no `GetAwaiter`)
- **Issue:** In 8.5.5 `IReceiveEndpoint.Stop(ct)` returns an awaitable Task but `Start(ct)` returns a `ReceiveEndpointHandle` (with `.Ready`).
- **Fix:** `var started = handle.ReceiveEndpoint.Start(ct); await started.Ready;`
- **Files modified:** tests/BaseApi.Tests/Keeper/KeeperPauseAccumulateFacts.cs
- **Verification:** test builds + green.
- **Committed in:** 266b80c

### Plan-vs-Plan-01 reconciliation
- **Task 2(a)** (repurpose `RecoveryDeadLetterFacts` data-gone fact to op-exhaustion->dead-letter, drop `RecoveryDataGoneException`, update the duplicate-reinject ctor) was ALREADY completed by Plan 01 (the fact is `InfraFault_reinject_faults_and_routes_to_dead_letter`, Redis-exception-driven, `Consumed.Any<ConsolidatedFault>` true, no `RecoveryDataGoneException`). All Task 2(a) acceptance criteria were satisfied on entry — no edit needed. The plan's `files_modified` over-counted; the only NEW Task-2 work was the pause/accumulate fact.
- **Task 1(e)** (appsettings: remove GateWaitSeconds, add ExhaustionPolicy) was ALREADY done by Plan 01's hygiene commit (`003e117`). Verified present (`ExhaustionPolicy: Dlq1`) and absent (`GateWaitSeconds`).

**Total deviations:** 2 auto-fixed (1 bug, 1 blocking API shape). No scope creep; no architectural changes; no auth gates.

## Threat Model Coverage
- **T-52-05 (duplicate-endpoint):** mitigated — all three consumers `ExcludeFromConfigureEndpoints()`; exactly one source (RecoveryEndpointBinder) configures keeper-recovery. Acceptance grep + build prove no static+connect collision.
- **T-52-06 (SustainedOutage data-loss):** mitigated — OQ-2 verified against the installed 8.5.5 assembly (Interval pre-allocation OOM caught); SustainedOutageFacts asserts the read is retried >1 (redelivered, NOT acked/discarded).
- **T-52-07 (Dlq1 routing integrity):** mitigated — Dlq1 inherits the consolidated skp-dlq-1 filter (no per-consumer ConfigureError); RecoveryDeadLetterFacts asserts a single ConsolidatedFault.
- **T-52-08 (startup race):** accepted — connect STARTED; the brief pre-first-probe window is documented in the binder xml-doc; Plan 03 wires Stop/Start on edges.

## Threat Flags
None — no new trust-boundary surface beyond the plan's `<threat_model>` (the endpoint queue name, message types, and skp-dlq-1 route are unchanged; only the endpoint's BIND mechanism moved from static to runtime).

## Known Stubs
None. The endpoint is runtime-bound, the policy branch is live, the handle singleton + KeeperMetrics DI are registered. The BIT-edge driver (BitHealthLoop calling handle Stop/Start) is the scoped Plan 03 deliverable (D-08) — the handle is exposed here for it. Per D-08, the processor-side latch, the global UseMessageRetry=none rule, and RETIRE-03 remain Phase 53.

## Verification
- **Keeper namespace facts:** 18/18 green (16 from Plan 01 + KeeperPauseAccumulate + SustainedOutage).
- **Full hermetic suite** (`--filter-not-trait Category=RealStack --filter-not-trait Category=E2E`): 520/520 green, 0 failed.
- **dotnet build SK_P.sln -c Release:** 0 warnings, 0 errors.
- **Acceptance greps:** Program.cs has `ExcludeFromConfigureEndpoints` x3 + no `,*ConsumerDefinition>` AddConsumer + RecoveryEndpointHandle/Binder/KeeperMetrics/AddMeter registrations; RecoveryEndpointHandle has `HostReceiveEndpointHandle`; RecoveryEndpointBinder has `ConnectReceiveEndpoint`, `handle.Ready`, `holder.Handle =`, both ExhaustionPolicy branches, 3x ConfigureConsumer<, 3x UsePartitioner<; ReinjectConsumerDefinition keeps both partition statics; InjectConsumerDefinition.cs/DeleteConsumerDefinition.cs deleted; appsettings has ExhaustionPolicy, no GateWaitSeconds; SustainedOutageFacts has `Assert.False(...Any<ConsolidatedFault>...)` + bounded CTS.

## Next Phase Readiness
- **Plan 03 (KEEP-04 driver):** inject `RecoveryEndpointHandle` into `BitHealthLoop` and call `holder.Handle?.ReceiveEndpoint.Stop(ct)` on the unhealthy edge / `.Start(ct)` on the healthy edge (preserve WR-01 prevHealthy-not-advanced-on-failure + the `prevHealthy=null` first-tick semantics). Consider the stricter "connect-stopped, first-healthy-edge starts it" posture if desired (the binder connects started today; documented as the accepted T-52-08 residual).
- **Phase 53 (D-08):** global `UseMessageRetry=none` rule, processor-side latch removal, Model-B remnant sweep (RETIRE-03).

## Self-Check: PASSED

- Created files exist: RecoveryEndpointHandle.cs, RecoveryEndpointBinder.cs, KeeperPauseAccumulateFacts.cs, SustainedOutageFacts.cs, 52-02-SUMMARY.md — all FOUND.
- Deleted files confirmed: InjectConsumerDefinition.cs, DeleteConsumerDefinition.cs — both removed.
- Task commits exist: fd72d9d, 266b80c, 5f24bb9 — all FOUND.

---
*Phase: 52-three-state-keeper*
*Completed: 2026-06-11*
