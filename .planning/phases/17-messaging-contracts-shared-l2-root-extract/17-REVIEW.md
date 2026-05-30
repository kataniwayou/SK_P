---
phase: 17-messaging-contracts-shared-l2-root-extract
reviewed: 2026-05-30T00:00:00Z
depth: standard
files_reviewed: 19
files_reviewed_list:
  - Directory.Packages.props
  - SK_P.sln
  - scripts/phase-17-close.ps1
  - src/BaseApi.Service/BaseApi.Service.csproj
  - src/BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs
  - src/BaseApi.Service/Features/Orchestration/Projection/RedisL2Cleanup.cs
  - src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs
  - src/Messaging.Contracts/CorrelationKeys.cs
  - src/Messaging.Contracts/ICorrelated.cs
  - src/Messaging.Contracts/Messaging.Contracts.csproj
  - src/Messaging.Contracts/Projections/LivenessProjection.cs
  - src/Messaging.Contracts/Projections/WorkflowRootProjection.cs
  - src/Messaging.Contracts/StartOrchestration.cs
  - src/Messaging.Contracts/StopOrchestration.cs
  - tests/BaseApi.Tests/Features/Orchestration/HappyPathE2EFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/IdempotencyFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/Projection/ProjectionRecordRoundTripTests.cs
  - tests/BaseApi.Tests/Features/Orchestration/RedisProjectionWriterFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/StopCleanupFacts.cs
findings:
  critical: 0
  warning: 0
  info: 2
  total: 2
status: issues_found
---

# Phase 17: Code Review Report

**Reviewed:** 2026-05-30T00:00:00Z
**Depth:** standard
**Files Reviewed:** 19
**Status:** issues_found (2 Info only — no Critical or Warning)

## Summary

Phase 17 is a behavior-preserving extract that creates a new pure-POCO `Messaging.Contracts`
leaf library and moves two L2 projection records into it. I verified the extract against the
stated intent (no logic change) and confirmed it holds:

- **Verbatim move confirmed.** `git diff -M` reports `LivenessProjection.cs` and
  `WorkflowRootProjection.cs` as renames (similarity 85% / 87%), with the only content deltas
  being the `namespace` line and the `internal` -> `public` accessibility bump. Record shapes,
  positional parameters, and every `[property: JsonPropertyName(...)]` pin are byte-identical to
  the pre-move definitions. No second/orphaned copy of either record survives in
  `BaseApi.Service` (grep confirms the records now resolve only under `Messaging.Contracts.Projections`).
- **Net-new contracts are minimal and correct.** `ICorrelated`, `StartOrchestration`,
  `StopOrchestration`, `CorrelationKeys` are pure POCO/interface declarations with no logic,
  no MassTransit dependency, and no framework reference — consistent with the leaf-library
  intent (MSG-CONTRACTS-01 / D-01).
- **Consumer using-swap is complete and consistent.** All 8 consumers (3 service files +
  5 test files) that reference the moved records carry `using Messaging.Contracts.Projections;`.
  The three unlisted orchestration test files (`StartOrchestrationFacts`, `StopOrchestrationFacts`,
  `StopScanFacts`) were checked and do NOT reference the moved records, so they correctly require
  no swap — there are no missed consumers.
- **Project wiring is correct.** The new project is registered in `SK_P.sln` (GUID
  `97B07C49-...`, nested under the `src` solution folder) with both Debug/Release configs, and
  `BaseApi.Service.csproj` adds the `ProjectReference`. The new csproj is a clean SDK leaf with
  no `ItemGroup` and no framework reference, matching its documented contract.
- **No accidental type migration.** `StepProjection`, `ProcessorProjection`,
  `RedisProjectionKeys`, `RedisProjectionOptions`, and `WorkflowGraphSnapshot` correctly stayed
  in `BaseApi.Service` (grep of `Messaging.Contracts` finds none of them), preserving the
  intended seam: only the cross-service read-shape records moved.

No correctness, security, or maintainability defects were found. The two Info items below are
observations about pre-existing properties of the moved/swapped code, not regressions introduced
by this phase. Both are out of scope to fix in a behavior-preserving extract and are recorded
only for traceability.

## Info

### IN-01: `WorkflowRootProjection.EntryStepIds` deserializes as nullable despite a non-nullable declared type

**File:** `src/Messaging.Contracts/Projections/WorkflowRootProjection.cs:12`
**Issue:** `EntryStepIds` is declared `List<Guid>` (non-nullable), but System.Text.Json will
populate it with `null` if the `entryStepIds` field is absent or explicitly `null` in the wire
JSON. The consumer `RedisL2Cleanup.cs:46,51` correctly defends against this with
`?? new List<Guid>()`, so there is no live null-deref. This is a pre-existing property of the
record (unchanged by the move) and the record is now `public`, so external consumers gain the
same latent contract. Not a regression and not in scope for a behavior-preserving extract.
**Fix:** No action this phase. If hardened later, consider documenting the "may be null on
deserialize" contract on the XML doc, or having consumers treat it as `List<Guid>?`. Do NOT
change it here — it would alter the round-trip surface the Phase 17 close gate snapshots.

### IN-02: `internal` -> `public` accessibility widening is intentional but expands the API surface

**File:** `src/Messaging.Contracts/Projections/LivenessProjection.cs:11`,
`src/Messaging.Contracts/Projections/WorkflowRootProjection.cs:11`
**Issue:** Both records changed from `internal` to `public` as part of the extract (required so
the future Orchestrator consumer and the cross-project `BaseApi.Service` can reference them
without `InternalsVisibleTo`). This is the correct and necessary change for a shared-contracts
leaf, but it permanently widens the committed public API surface of `Messaging.Contracts` — the
field shapes and `[JsonPropertyName]` pins are now a public wire contract that downstream
services will bind to. The close-gate's `ProjectionRecordRoundTripTests` wire-shape guard and the
`redis-cli --scan` SHA invariant correctly lock this surface, so the risk is controlled.
**Fix:** No action — this is the intended behavior of the phase. Flagged only so future schema
edits to these records are treated as breaking public-contract changes, not internal refactors.

---

_Reviewed: 2026-05-30T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
