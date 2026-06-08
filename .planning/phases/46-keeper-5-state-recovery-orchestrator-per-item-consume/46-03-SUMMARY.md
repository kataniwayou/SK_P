---
phase: 46-keeper-5-state-recovery-orchestrator-per-item-consume
plan: 03
subsystem: keeper
tags: [masstransit, partitioner, dotnet, keeper, recovery, dead-letter, endpoint-config]

# Dependency graph
requires:
  - phase: 46-02
    provides: the five RecoveryConsumer classes + RecoveryConsumerBase + RecoveryOptions + RecoveryDataGoneException
  - phase: 46-01
    provides: RetryLoop in BaseConsole.Core + KeeperReinject.Payload + RED partition stub
  - phase: 45-keeper-bit-health-gate-global-pause-resume
    provides: IL2HealthGate (consumed by the recovery base)
  - phase: 43-message-contracts-l2-key-reshape
    provides: IKeeperRecoverable 4-tuple + KeeperQueues.Recovery + the five Keeper records
provides:
  - Five Keeper recovery ConsumerDefinitions on the shared queue:keeper-recovery endpoint
  - Single-owner endpoint config (UseMessageRetry + five shared-Partitioner UsePartitioner<T>) on UpdateConsumerDefinition
  - Public PartitionKey (4-tuple string) + PartitionGuid (deterministic Guid) helpers
  - RecoveryOptions bound from the "Recovery" appsettings section; five consumers registered (additive to the reactive path)
  - RecoveryPartitionFacts (KEEP-09) + RecoveryDeadLetterFacts (D-04) GREEN
affects: [46-04, 47-resilience-dlq1, 48-retire-reactive-recovery, 49-close-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Single-owner endpoint config: ONE of five co-located ConsumerDefinitions owns UseMessageRetry + the five UsePartitioner<T>; the other four ConfigureConsumer are intentional no-ops (Pitfalls 1 & 4, FaultEntryStepDispatchConsumerDefinition precedent)"
    - "Shared Partitioner(N, Murmur3UnsafeHashGenerator) keyed on the IKeeperRecoverable 4-tuple (StepId excluded) so all five states for one exec serialize into the same slot"
    - "8.5.5 endpoint-level (IConsumePipeConfigurator) UsePartitioner keys on a Guid; derive a deterministic SHA256-over-the-4-tuple-string Guid so the string stays the single source of truth"

key-files:
  created:
    - src/Keeper/Recovery/UpdateConsumerDefinition.cs
    - src/Keeper/Recovery/ReinjectConsumerDefinition.cs
    - src/Keeper/Recovery/InjectConsumerDefinition.cs
    - src/Keeper/Recovery/DeleteConsumerDefinition.cs
    - src/Keeper/Recovery/CleanupConsumerDefinition.cs
    - tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs
  modified:
    - src/Keeper/Program.cs
    - src/Keeper/appsettings.json
    - tests/BaseApi.Tests/Keeper/RecoveryPartitionFacts.cs

key-decisions:
  - "MassTransit 8.5.5 Partitioner + Murmur3UnsafeHashGenerator live in namespace MassTransit.Middleware (NOT MassTransit) — verified vs the installed assembly (RESEARCH A1/A2 resolved)"
  - "The endpoint-level shared-IPartitioner UsePartitioner overload keys on Guid; the string+Encoding overloads bind to the consumer pipe, not the endpoint — so PartitionGuid = new Guid(SHA256(PartitionKey)[..16]) preserves the exact 4-tuple collision semantics while satisfying the Guid-keyed endpoint API"
  - "PartitionKey/PartitionGuid are public static (pure key helpers, no DI/state) rather than internal-via-InternalsVisibleTo — adding IVT to Keeper exposed Keeper's top-level Program to the test assembly and collided with BaseApi.Service's Program (Rule 3 blocking fix)"

requirements-completed: [KEEP-09]

# Metrics
duration: 18min
completed: 2026-06-09
---

# Phase 46 Plan 03: Wire Recovery Consumers onto keeper-recovery with Per-Key Ordering Summary

**Wired the five Keeper recovery consumers onto the shared queue:keeper-recovery endpoint with per-key ordering (KEEP-09): UpdateConsumerDefinition is the single endpoint-config owner — it registers UseMessageRetry plus five UsePartitioner<T> calls sharing one Partitioner(PartitionCount, Murmur3UnsafeHashGenerator) keyed on the IKeeperRecoverable 4-tuple (corr:wf:proc:exec, StepId excluded), while the other four definitions leave ConfigureConsumer an intentional no-op (Pitfalls 1 & 4). RecoveryOptions binds from the new "Recovery" appsettings section and all five consumers register additively alongside the surviving reactive FaultEntryStepDispatchConsumer. The partition-key fact and a data-gone dead-letter integration fact are GREEN.**

## Performance
- **Duration:** ~18 min
- **Tasks:** 3
- **Files created/modified:** 9 (6 created, 3 modified)

## Accomplishments
- **Five ConsumerDefinitions (Task 1):** all set `EndpointName = KeeperQueues.Recovery`. `UpdateConsumerDefinition` owns the endpoint config — `UseMessageRetry(r => r.Immediate(Limit))` + five `UsePartitioner<KeeperUpdate/Reinject/Inject/Delete/Cleanup>(partition, p => PartitionGuid(p.Message))` over one shared `Partitioner`. The other four `ConfigureConsumer` are empty no-ops with the Pitfall-1/4 doc-comment.
- **Partition key (KEEP-09 / D-12):** `PartitionKey(IKeeperRecoverable)` = `$"{corr:D}:{wf:D}:{proc:D}:{exec:D}"` (the composite-backup shape, StepId excluded). `PartitionGuid` derives a deterministic Guid (`new Guid(SHA256(PartitionKey)[..16])`) for the 8.5.5 Guid-keyed endpoint overload — same 4-tuple → same Guid → same slot.
- **Registration + options (Task 2):** `Program.cs` binds `RecoveryOptions` from `GetSection("Recovery")` and registers the five `AddConsumer<*Consumer, *ConsumerDefinition>()` additively (the reactive `FaultEntryStepDispatchConsumer` is retained — Phase 46 coexists, reactive path retired in Phase 48). `appsettings.json` gains `"Recovery": { "PartitionCount": 8, "GateWaitSeconds": 300 }`.
- **Tests (Task 3):** `RecoveryPartitionFacts` pins the 4-tuple shape and the StepId-excluded/ExecutionId-distinct collision behavior for both `PartitionKey` and `PartitionGuid`. `RecoveryDeadLetterFacts` wires the real `ReinjectConsumer` on an in-memory `ITestHarness` (open gate, empty L2) reproducing the consolidated error pipeline, and asserts the data-gone `KeeperReinject` faults with `RecoveryDataGoneException` AND lands in the consolidated `ConsolidatedFault`/skp-dlq-1 sink.

## Task Commits
1. **Task 1: Five recovery ConsumerDefinitions — single-owner partitioner + retry** - `c4e429c` (feat)
2. **Task 2: Register five consumers + bind RecoveryOptions** - `df65065` (feat)
3. **Task 3: Partition-key fact + data-gone dead-letter fact** - `114bbd8` (test)

## Decisions Made
- **8.5.5 signature verification (RESEARCH A1/A2 resolved):** reflection over the installed `MassTransit.dll` confirmed `Partitioner(int partitionCount, IHashGenerator)` and `Murmur3UnsafeHashGenerator()` are public — but in namespace `MassTransit.Middleware`, not `MassTransit`. Added `using MassTransit.Middleware;`.
- **Guid-keyed endpoint overload:** the only shared-`IPartitioner` overload reachable from `IReceiveEndpointConfigurator` (`IConsumePipeConfigurator`) keys on `Func<ConsumeContext<T>, Guid>`; the `string`-key overloads require `Encoding` and bind to the consumer pipe (`IPipeConfigurator<T>`), not the endpoint. Resolved by deriving a deterministic `Guid` from the canonical 4-tuple string (the string stays the pinned source of truth) — identical collision semantics, StepId still excluded.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Partitioner namespace + Guid-keyed endpoint overload (vs the plan's `MassTransit` / string-key pseudocode)**
- **Found during:** Task 1 (first Keeper build)
- **Issue:** The plan/interfaces pseudocode placed `Partitioner`/`Murmur3UnsafeHashGenerator` in namespace `MassTransit` and used a string-keyed `UsePartitioner<T>(partition, p => PartitionKey(...))`. In the installed 8.5.5 assembly the types live in `MassTransit.Middleware`, and the endpoint-level shared-`IPartitioner` overload keys on `Guid` (string overloads need `Encoding` and target the consumer pipe).
- **Fix:** Added `using MassTransit.Middleware;`; kept `PartitionKey` (string) as the canonical pinned shape and added `PartitionGuid = new Guid(SHA256(PartitionKey)[..16])` for the Guid-keyed `UsePartitioner<T>` calls. This is the documented A2 fallback intent (adapt to the available overload) and preserves the 4-tuple collision semantics exactly.
- **Files modified:** src/Keeper/Recovery/UpdateConsumerDefinition.cs
- **Commit:** c4e429c

**2. [Rule 3 - Blocking] Test access to PartitionKey/PartitionGuid without an InternalsVisibleTo Program collision**
- **Found during:** Task 3 (test project build)
- **Issue:** The plan's read_first suggested making the key fn `internal static` + relying on `[assembly: InternalsVisibleTo]`. Keeper had NO IVT; adding one (mirroring BaseApi.Service) made Keeper's implicit top-level `Program` visible to `BaseApi.Tests`, colliding with `BaseApi.Service.Program` (CS0433 in WebAppFactory).
- **Fix:** Removed the added `Keeper/Properties/AssemblyInfo.cs` and made `PartitionKey`/`PartitionGuid` `public static` (the plan explicitly permits "a small public static helper"). They are pure key helpers (no DI/state), so the public surface is harmless.
- **Files modified:** src/Keeper/Recovery/UpdateConsumerDefinition.cs (created then deleted src/Keeper/Properties/AssemblyInfo.cs)
- **Commit:** 114bbd8

## Verification
- **`dotnet build src/Keeper/Keeper.csproj`:** Build succeeded, 0 warnings, 0 errors (after both Task 1 + Task 2).
- **`dotnet build SK_P.sln`:** Build succeeded, 0 warnings, 0 errors (no ripple).
- **`dotnet run --project tests/BaseApi.Tests -- --filter-class "*RecoveryPartitionFacts" --filter-class "*RecoveryDeadLetterFacts"`:** 3/3 passed.
- **`dotnet run --project tests/BaseApi.Tests -- --filter-trait "Phase=46"`:** 18/18 passed.
- **Discipline grep:** no `ConfigureError`/`SetQueueArgument` in `src/Keeper/Recovery` (only doc-comment references). Exactly one definition registers `UsePartitioner`/`UseMessageRetry`.

Note: a bare full `dotnet test` would show the 2 pre-existing unrelated E2E failures (`SampleRoundTripE2ETests`, `MetricsRoundTripE2ETests`, need docker) per the phase-notes caveat — NOT regressions from this plan.

## Next Phase Readiness
- The keeper-recovery endpoint is fully wired with per-key ordering. Plan 04 builds the orchestrator `TypedResultConsumer<T>` family. Phase 47 (RESIL-02/03) consolidates `_DLQ1`; Phase 48 retires the reactive path; Phase 49 TEST-01 carries the deferred literal-skp-dlq-1 + live-partitioner serialization proofs.

## Self-Check: PASSED
- All 6 created files verified present on disk (5 ConsumerDefinitions + RecoveryDeadLetterFacts) plus this SUMMARY.
- All 3 task commits verified in git history: `c4e429c`, `df65065`, `114bbd8`.
- No accidental file deletions in any task commit (the transient Keeper/Properties/AssemblyInfo.cs was created and removed within Task 3's working set, never committed).
