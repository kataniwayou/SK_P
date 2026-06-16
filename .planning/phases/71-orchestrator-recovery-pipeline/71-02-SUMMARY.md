---
phase: 71-orchestrator-recovery-pipeline
plan: 02
subsystem: infra
tags: [messaging-contracts, keeper-recovery, masstransit, system-text-json, partitioner, contract]

# Dependency graph
requires:
  - phase: 71-orchestrator-recovery-pipeline
    plan: 01
    provides: ProcessorInject/ProcessorReinject (the now-renamed analog contracts to mirror), IKeeperRecoverable marker, ReinjectConsumerDefinition.PartitionGuid 4-tuple helper
provides:
  - "OrchestratorInject contract (IKeeperRecoverable): 5-id base + copy operands (EntryId=newEntryId, OriginEntryId) + downstream dispatch tuple (NextStepId, NextProcessorId, Payload)"
  - "OrchestratorReinject contract (IKeeperRecoverable): 5-id base + EntryId + StepOutcome discriminator + ErrorMessage/CancellationMessage union-field superset (discrete fields, not a polymorphic blob)"
  - "OrchestratorContractTests: round-trip + IKeeperRecoverable + origin-agnostic partition facts proving the 4-tuple helper needs no change"
affects: [71-03, 71-04, orchestrator-recovery-pipeline]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Origin-split keeper recovery: Orchestrator* contracts mirror Processor* shape, diverge only on OrchestratorInject's copy operands and OrchestratorReinject's D-07 outcome discriminator + union superset"
    - "Discrete result-field superset (Outcome + ErrorMessage + CancellationMessage) instead of a serialized polymorphic IStepResult blob — Wave-3 consumer reconstructs the subtype via a factory"
    - "Default System.Text.Json, no [JsonPropertyName] (every Messaging.Contracts record), init-only props (not positional) so STJ binds them directly"

key-files:
  created:
    - src/Messaging.Contracts/OrchestratorInject.cs
    - src/Messaging.Contracts/OrchestratorReinject.cs
    - tests/BaseApi.Tests/Contracts/OrchestratorContractTests.cs
  modified: []

key-decisions:
  - "Reused the IKeeperRecoverable-base EntryId as the newEntryId (copy-into / dispatch-with) and added a discrete OriginEntryId for the copy-from key on OrchestratorInject — documented in the field comments (plan gave Claude discretion within D-07)"
  - "Reused the existing StepOutcome enum (values 0-3) as the OrchestratorReinject discriminator per RESEARCH A5/D-07 — no new enum minted"
  - "Carried the IStepResult result-field superset as discrete nullable string fields (ErrorMessage Failed-only, CancellationMessage Cancelled-only) rather than a serialized polymorphic blob — the consumer (71-04) rebuilds the subtype via an Outcome switch factory"
  - "No change to ReinjectConsumerDefinition.PartitionGuid — the new contracts implement IKeeperRecoverable so the SHA256-over-4-tuple helper is origin-agnostic by construction (asserted by PartitionGuid_is_stable_and_origin_agnostic)"

metrics:
  duration: ~10 min
  completed: 2026-06-16
  tasks: 2
  files: 3
---

# Phase 71 Plan 02: Origin-Split Orchestrator Keeper-Recovery Contracts Summary

Added the two origin-split keeper-recovery wire contracts — `OrchestratorInject` and `OrchestratorReinject` — both sealed `IKeeperRecoverable` records that compile 0-warning, round-trip cleanly under default STJ, and partition through the unchanged 4-tuple `PartitionGuid` helper. These are the stable contracts the Wave-3 pipeline (71-03) builds and the Wave-3 consumers (71-04) handle.

## What was built

- **`OrchestratorInject`** — mirrors `ProcessorInject`'s 5-id base (corr/wf/step/proc/exec) but, where the processor INJECT writes in-hand data, the orchestrator INJECT completes the index+data COPY the FORWARD-Post tail could not finish. It carries the downstream-dispatch tuple (`NextStepId`, `NextProcessorId`, `Payload`) plus the data keys: `EntryId` is the newEntryId to copy INTO / dispatch with, and `OriginEntryId` is the origin data key to copy FROM. Non-destructive by design (write + send only).
- **`OrchestratorReinject`** — mirrors `ProcessorReinject`'s 5-id base + `EntryId`, plus the D-07 divergence: a `StepOutcome Outcome` discriminator (existing enum reused) and the IStepResult result-field superset as discrete fields (`ErrorMessage` Failed-only, `CancellationMessage` Cancelled-only). The Wave-3 consumer reconstructs the right IStepResult subtype from `Outcome` via a factory.
- **`OrchestratorContractTests`** — 4 facts: both contracts assert as `IKeeperRecoverable`; `OrchestratorReinject` round-trips its `Outcome` + the populated union field (Failed→ErrorMessage, Cancelled→CancellationMessage) under default `System.Text.Json`; and `PartitionGuid` is stable and origin-agnostic (same corr:wf:proc:exec 4-tuple across the two different new contract types → same partition slot; different ExecutionId → different slot).

## Verification

- `dotnet build src/Messaging.Contracts/Messaging.Contracts.csproj -c Debug`: **Build succeeded, 0 warnings, 0 errors.**
- `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -- --filter-method "*OrchestratorContract*"`: **Passed — 4/4, Failed 0** (targeted count 4, not ~638 — the MTP `-- --filter-method` filter resolved; `--filter` is silently ignored under MTP).
- grep confirms both records `: IKeeperRecoverable`, `OrchestratorReinject` has `StepOutcome Outcome` + `ErrorMessage` + `CancellationMessage`, and neither file contains a `[JsonPropertyName]` attribute (only the folder-convention "NO [JsonPropertyName]" comment).

## Deviations from Plan

None — plan executed exactly as written. Both per-task acceptance criteria and the overall success criteria were met with no auto-fixes, blocking issues, or architectural changes.

## TDD Gate Compliance

Task 2 was `tdd="true"`. The plan's interface-first ordering places the contracts (Task 1, committed `e55108f`) before the test (Task 2, committed `5928966`), so the test exercises already-committed real types and passed on first run — there is no separate failing-RED commit because the production types under test were the prior task's deliverable, not new code introduced by the test. The RED/GREEN intent (test asserts observable behavior of the contracts) is satisfied; the `test(...)` commit follows the `feat(...)` commit.

## Threat Surface

No new threat surface beyond the plan's `<threat_model>` (T-71-03 accept / T-71-04 accept). The new contracts are inert DTOs on the internal keeper-recovery bus; `OrchestratorReinject.ErrorMessage`/`CancellationMessage` carry the same diagnostic strings already on `orchestrator-result` (no new exposure), and the contracts add no key-deletion or key-write capability.

## Commits

- `e55108f` feat(71-02): add OrchestratorInject and OrchestratorReinject contracts
- `5928966` test(71-02): add OrchestratorContractTests round-trip + partition facts

## Self-Check: PASSED

- FOUND: src/Messaging.Contracts/OrchestratorInject.cs
- FOUND: src/Messaging.Contracts/OrchestratorReinject.cs
- FOUND: tests/BaseApi.Tests/Contracts/OrchestratorContractTests.cs
- FOUND commit: e55108f
- FOUND commit: 5928966
