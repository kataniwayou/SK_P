---
phase: 70-processor-inject-cleanup
plan: 01
subsystem: infra
tags: [keeper-recovery, masstransit, redis, messaging-contracts, refactor]

# Dependency graph
requires:
  - phase: 69-align-processor-pipeline-to-canonical-recovery-spec
    provides: "Processor skips its cleanup tail on keeper escalation (spec §4.3), which makes the INJECT source-delete redundant"
provides:
  - "Non-destructive keeper INJECT path (InjectConsumer.HandleAsync = exactly two Guarded effects: write L2[EntryId]=Data, send StepCompleted)"
  - "Reduced KeeperInject contract (5-id base + EntryId + Data; DeleteEntryId field removed)"
  - "ProcessorPipeline.BuildInject mints KeeperInject without DeleteEntryId"
  - "src/ tree free of DeleteEntryId — the contract reshape Plan 70-02's test edits assert against"
affects: [70-02-test-reshape, orchestrator-recovery-pipeline, keeper-delete-invariant]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "DELETE is the only keeper recovery state that deletes keys (spec §8) — INJECT and REINJECT are non-destructive"
    - "Default-STJ bus envelope field-drop is wire-tolerant (no [JsonPropertyName], no version/migration step)"

key-files:
  created: []
  modified:
    - src/Keeper/Recovery/InjectConsumer.cs
    - src/Messaging.Contracts/KeeperInject.cs
    - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs

key-decisions:
  - "Removed the InjectConsumer op-3 source-delete (KeyDeleteAsync L2[DeleteEntryId]) — INJECT now writes data + sends StepCompleted only"
  - "Dropped KeeperInject.DeleteEntryId with no wire migration (default STJ drops unknown properties on in-flight pre-deploy envelopes — D-03)"
  - "Did NOT add the spec §8 INJECT index-slot write — deferred, out of scope per CONTEXT.md Deferred Ideas"

patterns-established:
  - "Keeper INJECT is non-destructive: two Guarded effects, zero KeyDelete"
  - "Wire-tolerant contract narrowing via default STJ — no versioning shim"

requirements-completed: [KINJ-01, KINJ-02]

# Metrics
duration: 4min
completed: 2026-06-16
---

# Phase 70 Plan 01: Processor INJECT Cleanup (source half) Summary

**Keeper INJECT path made non-destructive — removed the trailing source-delete from InjectConsumer, dropped the vestigial KeeperInject.DeleteEntryId field, and stopped BuildInject from supplying it; src/ is now free of DeleteEntryId.**

## Performance

- **Duration:** ~4 min
- **Started:** 2026-06-16T05:30:04Z
- **Completed:** 2026-06-16T05:33:43Z
- **Tasks:** 3
- **Files modified:** 3

## Accomplishments
- `InjectConsumer.HandleAsync` now performs exactly two Guarded effects (write `L2[EntryId]=Data`, send `StepCompleted`) and issues no `KeyDelete*` — INJECT matches the canonical recovery spec §8 (DELETE is the only deleting keeper state) and the already-non-destructive REINJECT path (KINJ-01).
- `KeeperInject` reduced to the 5-id base + `EntryId` (Guid) + `Data` (string); the vestigial `DeleteEntryId` field is gone from the record and its XML doc (KINJ-02 source half).
- `ProcessorPipeline.BuildInject` mints `KeeperInject` without `DeleteEntryId`; `grep -rn "DeleteEntryId" src/` returns ZERO hits across the whole tree.
- No wire-version/migration step added — `KeeperInject` remains a default-STJ bus envelope, wire-tolerant by design.

## Task Commits

Each task was committed atomically:

1. **Task 1: Remove the source-delete op from InjectConsumer (D-01)** - `3b69a2e` (feat)
2. **Task 2: Drop DeleteEntryId from the KeeperInject contract (D-02)** - `559b90e` (feat)
3. **Task 3: Stop supplying DeleteEntryId in ProcessorPipeline.BuildInject (D-03)** - `5de7b1a` (feat)

## Files Created/Modified
- `src/Keeper/Recovery/InjectConsumer.cs` - Deleted the op-3 `KeyDeleteAsync(L2[DeleteEntryId])` block; rewrote the class XML doc to the two-effect non-destructive body (dropped the Pitfall-5 "source delete is the tail" rationale).
- `src/Messaging.Contracts/KeeperInject.cs` - Removed the `DeleteEntryId` property; dropped its clause from the record XML doc; kept the "NO [JsonPropertyName], default STJ" wire-tolerance comment.
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` - Dropped the `DeleteEntryId = d.EntryId` initializer line from `BuildInject`; rewrote the INFRA-02/Pitfall-1 builder comment; cleaned the class-level XML doc id-set mention.

## Decisions Made
- Followed D-01..D-03 verbatim. The INJECT index-slot write (spec §8 divergence) was deliberately NOT implemented — it is a noted observed gap deferred to a future phase (CONTEXT.md Deferred Ideas).
- No wire-versioning/migration step added (D-03 note) — STJ drops the unknown `DeleteEntryId` on in-flight pre-deploy envelopes harmlessly.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Cleaned stray DeleteEntryId in the ProcessorPipeline class-level XML doc**
- **Found during:** Task 3 (BuildInject edit)
- **Issue:** Beyond the plan-cited `:437` initializer and `:428-429` builder comment, the class-level XML doc at `ProcessorPipeline.cs:47` also named the `EntryId/Data/DeleteEntryId` id-set. Leaving it would have left a `DeleteEntryId` hit in `grep -rn "DeleteEntryId" src/`, breaking Task 3's acceptance criterion ("grep returns ZERO hits across the entire src/ tree").
- **Fix:** Changed the doc string from "EntryId/Data/DeleteEntryId id-set" to "EntryId/Data id-set".
- **Files modified:** src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
- **Verification:** `grep -rn "DeleteEntryId" src/` returns zero hits; BaseProcessor.Core builds 0-warning.
- **Committed in:** 5de7b1a (Task 3 commit)

---

**Total deviations:** 1 auto-fixed (1 blocking)
**Impact on plan:** The fix was required to satisfy the plan's own zero-hit grep success criterion. It is a documentation-only string change in the same file Task 3 already edited — no behavior change, no scope creep.

## Issues Encountered
None.

## Verification

- `dotnet build src/Keeper -c Debug` → Build succeeded, 0 Warning, 0 Error.
- `dotnet build src/Messaging.Contracts -c Debug` → Build succeeded, 0 Warning, 0 Error.
- `dotnet build src/BaseProcessor.Core -c Debug` → Build succeeded, 0 Warning, 0 Error.
- `grep -rn "DeleteEntryId" src/` → ZERO hits.
- `grep -n "KeyDelete" src/Keeper/Recovery/InjectConsumer.cs` → ZERO hits.
- `InjectConsumer.cs` still contains `StringSetAsync` (op 1) and `GetSendEndpoint` (op 2); `KeeperInject.cs` still has `public Guid EntryId` + `public string Data` + the default-STJ comment; `BuildInject` still constructs `new KeeperInject`.
- `ReinjectConsumer.cs`, `DeleteConsumer.cs`, `L2ProbeRecovery.cs` untouched (not in this plan).

> The full-solution build is intentionally NOT green at this point: `tests/` still reference the dropped `DeleteEntryId` field. This is expected per the phase guardrails — Plan 70-02 (Wave 2) reshapes the test references. The three source projects build clean in isolation.

## Known Stubs
None.

## Next Phase Readiness
- Plan 70-02 can proceed: the reduced `KeeperInject` shape and non-destructive INJECT body are in place for the test reshape (D-04..D-10) and the new `KeeperDeleteInvariantFacts` invariant fact to assert against.
- After 70-02 the full solution builds 0-warning and the hermetic suite goes green.

## Self-Check: PASSED

All 3 modified source files and the SUMMARY exist; all 3 task commits (`3b69a2e`, `559b90e`, `5de7b1a`) are present in git history.

---
*Phase: 70-processor-inject-cleanup*
*Completed: 2026-06-16*
