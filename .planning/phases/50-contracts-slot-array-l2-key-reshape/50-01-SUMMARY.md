---
phase: 50-contracts-slot-array-l2-key-reshape
plan: 01
subsystem: api
tags: [messaging-contracts, l2-redis-keys, keeper-recovery, slot-array, masstransit, csharp]

# Dependency graph
requires:
  - phase: 43-message-contracts-l2-key-reshape
    provides: L2ProjectionKeys (ExecutionData discriminated-key builder) + KeeperInject 5-id base + IKeeperRecoverable marker
provides:
  - "L2ProjectionKeys.MessageIndex(Guid) -> skp:msg:{messageId:D} slot-array allocation-index key builder (D-04/D-06/D-07)"
  - "KeeperInject A18 INJECT id-set: EntryId (Guid), Data (string), DeleteEntryId (Guid) (D-08)"
  - "Golden test pinning the exact skp:msg:{messageId:D} string"
affects: [51-processor-forward-recovery-pipeline, 52-3-state-keeper, 50-02-model-b-deletions]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Single-source-of-truth L2 key builder ($\"{Prefix}{discriminator}:{guid:D}\", no TTL baked in)"
    - "5-id positional record + init-prop extras (string extras default = \"\")"

key-files:
  created: []
  modified:
    - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
    - src/Messaging.Contracts/KeeperInject.cs
    - tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs

key-decisions:
  - "MessageIndex uses a required `msg:` namespace discriminator mirroring the `data:` precedent (bare skp:{guid} collides with Root/Processor)"
  - "No TTL baked into MessageIndex (caller concern, mirrors ExecutionData; random TTL lands in Phase 51)"
  - "KeeperInject.Data continues the deleted-in-Plan-02 KeeperUpdate.ValidatedData raw-JSON-string role"
  - "Literal name DeleteEntryId (NOT SourceEntryId) tracks the A18 spec literal `deleteEntryId`"
  - "New KeeperInject fields are init with defaults so ProcessorPipeline.BuildInject compiles unchanged (additive, non-breaking)"
  - "CompositeBackup builder + its golden test left untouched (Model-B deletions deferred to Plan 02 — build never breaks mid-wave)"

patterns-established:
  - "Slot-array allocation-index key: skp:msg:{messageId:D} (a Redis HASH of int-slot -> entryId)"
  - "A18 INJECT id-set on the KeeperInject wire record (forward-only, populated Phase 51, consumed Phase 52)"

requirements-completed: [RETIRE-02]

# Metrics
duration: 14min
completed: 2026-06-11
---

# Phase 50 Plan 01: Contracts & Slot-Array L2 Key Reshape (Additive Half) Summary

**Additively introduced the v5.0.0 slot-array recovery contract surface — `L2ProjectionKeys.MessageIndex(Guid) -> skp:msg:{messageId:D}` (golden-pinned) and the A18 INJECT id-set (`EntryId`/`Data`/`DeleteEntryId`) on `KeeperInject` — with zero deletions, solution 0-warning Release + Debug.**

## Performance

- **Duration:** 14 min
- **Started:** 2026-06-11T10:24:00Z
- **Completed:** 2026-06-11T10:38:38Z
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments
- `L2ProjectionKeys.MessageIndex(Guid messageId) => $"{Prefix}msg:{messageId:D}"` — the processor-owned slot-array allocation-index key builder (D-04/D-06/D-07), mirroring the `ExecutionData` discriminated-key precedent (no TTL baked in).
- A golden `[Fact]` pinning the exact `skp:msg:55555555-5555-5555-5555-555555555555` string, mirroring the `ExecutionData_Produces_*` template.
- `KeeperInject` gained the three A18 INJECT `init` properties: `EntryId` (Guid), `Data` (string, `= ""`), `DeleteEntryId` (Guid) (D-08), mirroring the `KeeperReinject` 5-id-base + init-prop-extras idiom verbatim.
- Class `<list>` doc-comment updated with a `MessageIndex` `<item>`; the retained no-TTL GUID data key `ExecutionData` is unchanged; the `CompositeBackup` builder + its golden test left untouched (deferred to Plan 02).

## Task Commits

Each task was committed atomically:

1. **Task 1: Add MessageIndex slot-array key builder + golden pin (D-04/D-06/D-07)** - `6892ea2` (feat)
2. **Task 2: Add the A18 INJECT id-set to KeeperInject (D-08)** - `3c6b5d9` (feat)

_TDD note: Task 1's golden pin was authored RED-first (the test referencing the not-yet-existent `MessageIndex` failed to compile = RED, CS0117), then the builder was added to reach GREEN (516 passed / 0 failed). Task 2's behavior is a compile-time contract shape; its RED/GREEN gate is the 0-warning solution build with `BuildInject` still compiling. Both tasks were a single additive edit each, so each is one feat commit (no separate refactor commit needed)._

## Files Created/Modified
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` - Added the `MessageIndex(Guid)` builder + its `<summary>` doc and a `<list>` `<item>`; `CompositeBackup` left intact.
- `src/Messaging.Contracts/KeeperInject.cs` - Added `EntryId`/`Data`/`DeleteEntryId` `init` props + reshaped the record `<summary>` doc to the A18 INJECT id-set.
- `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs` - Added the `MessageIndex_Produces_Prefix_Msg_Discriminator_Plus_HyphenatedGuid` golden `[Fact]`.

## Decisions Made
None beyond the plan — followed the plan and PATTERNS.md analog excerpts verbatim. The locked design decisions (the `msg:` discriminator, no-TTL caller-concern, the `Data` raw-JSON string role, the `DeleteEntryId` A18 literal, init-default-compatibility for `BuildInject`, and CompositeBackup deferral to Plan 02) are recorded in the frontmatter `key-decisions`.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
- The Bash tool runs bash (not PowerShell) in this environment, so the initial `Select-String` pipe failed (exit 127); switched to `grep` for build/test output filtering. Tooling adaptation only — no impact on the work.

## TDD Gate Compliance
Plan `type` is `execute` (not a plan-level `type: tdd`), with per-task `tdd="true"`. Task 1 followed RED (CS0117 compile failure on the missing-member golden pin) -> GREEN (builder added, 516/516 pass) -> no refactor needed. The combined RED+GREEN landed as one additive `feat` commit (`6892ea2`) since the golden pin and the builder are a single atomic additive change for a pure string-format builder. No gate skipped.

## Known Stubs
None - both changes are fully wired pure additions (a string-format builder and three init properties). No hardcoded empty UI values, no placeholder text, no dangling data sources. Population of the new `KeeperInject` fields is Phase 51 and consumption is Phase 52 (by design, per the milestone's wave plan — this plan locks only the contract shapes).

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- The slot-array key vocabulary (`MessageIndex`) and the A18 INJECT id-set are locked and golden-pinned — Phase 51 (processor forward/recovery pipeline) and Phase 52 (3-state keeper) can build against them.
- Plan 02 (Wave 2) of this phase still owns the Model-B deletions (`CompositeBackup`, `KeeperUpdate`, `KeeperCleanup`, `BackupOptions`) + the build-keeping cascade. The build is intact and 0-warning at this mid-wave point, as designed.

## Self-Check: PASSED

- FOUND: 50-01-SUMMARY.md, L2ProjectionKeys.cs, KeeperInject.cs, L2ProjectionKeysTests.cs
- FOUND commits: 6892ea2 (Task 1), 3c6b5d9 (Task 2)
- grep acceptance OK: `msg:{messageId:D}`, `public static string MessageIndex(Guid messageId)`, golden string `skp:msg:55555555-...`

---
*Phase: 50-contracts-slot-array-l2-key-reshape*
*Completed: 2026-06-11*
