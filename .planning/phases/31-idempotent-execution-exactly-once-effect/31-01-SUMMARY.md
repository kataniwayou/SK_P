---
phase: 31-idempotent-execution-exactly-once-effect
plan: 01
subsystem: infra
tags: [sha256, content-addressing, idempotency, redis-keys, messaging-contracts, hashing, options]

# Dependency graph
requires:
  - phase: 25-shared-contracts-webapi-responders
    provides: L2ProjectionKeys.ExecutionData(Guid) + Messaging.Contracts leaf both processes reference
  - phase: 28-sourcehash-identity
    provides: SourceHash.targets UTF-8 -> SHA-256 -> x2 convention mirrored byte-for-byte
provides:
  - "MessageIdentity — the ONE canonical hash path: ComputeH(5 fields), HashBlob, HashManifest, EntryEntryId"
  - "L2ProjectionKeys.ExecutionData(string) content-addressed data key + Flag(string) effect-first flag key"
  - "Messaging.Contracts.Configuration.RetryOptions (Limit/Strategy) + RetryStrategy enum"
  - "HashHelperGoldenFacts pinning H determinism / executionId-exclusion / 64-hex key format / SourceHash parity"
affects: [31-02, 31-03, 31-04, 31-05, 31-06, phase-32-retry-final-attempt]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Single canonical hash path (D-04): every identity/content hash routes through MessageIdentity.Hex"
    - "Content-addressed L2 keys: skp:data:{64hex} / skp:flag:{64hex}"
    - "Reserved U+001F field separator for injection-free canonical joins (D-03, T-31-01)"
    - "Transitional builder overload to keep a contract-shape change additive across waves"

key-files:
  created:
    - src/Messaging.Contracts/Hashing/MessageIdentity.cs
    - src/Messaging.Contracts/Configuration/RetryOptions.cs
    - tests/BaseApi.Tests/Contracts/HashHelperGoldenFacts.cs
  modified:
    - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs

key-decisions:
  - "ExecutionData(Guid) overload RETAINED alongside new ExecutionData(string) so Phase-27 callers + golden tests stay green this plan (removed in Plan 02) — keeps the change additive per the plan objective"
  - "EntryEntryId chosen as the method name (per <action> code) over the artifact-note alias HashEntryEntryId"
  - "Forbidden grep tokens (X2/Convert.ToHexString/executionId) reworded out of doc-comments so verifier greps hold; code was always correct"

patterns-established:
  - "Pattern 1: golden RED->capture->GREEN — pin the exact 64-hex literal once, freeze forever"
  - "Pattern 2: structural exclusion proof — reflection-assert ComputeH has 5 params and no execution* parameter (D-02)"

requirements-completed: [req-1, req-7]

# Metrics
duration: 6min
completed: 2026-06-04
---

# Phase 31 Plan 01: Deterministic-Identity Foundation Summary

**A single canonical `MessageIdentity` hash path (UTF-8 -> SHA-256 -> lowercase 64-hex, executionId excluded by construction), content-addressed `skp:data:`/`skp:flag:` L2 key builders, a shared `RetryOptions`, and golden tests pinning cross-process determinism — all additive, suite stays green.**

## Performance

- **Duration:** ~6 min
- **Started:** 2026-06-04T12:43:32Z
- **Completed:** 2026-06-04T12:49:02Z
- **Tasks:** 3
- **Files modified:** 4 (3 created, 1 modified)

## Accomplishments
- `MessageIdentity` — the ONE canonical hash path (D-04): `ComputeH(corr,wf,step,proc,entryId)`, `HashBlob`, `HashManifest`, `EntryEntryId`, all routing through one private `Hex` core that mirrors `SourceHash.targets` byte-for-byte. `executionId` is structurally absent from `ComputeH` (5-arg signature, D-02).
- `L2ProjectionKeys` gained the content-addressed `ExecutionData(string) => skp:data:{64hex}` and `Flag(string) => skp:flag:{64hex}` builders; `Root`/`Step`/`Processor`/`ParentIndex` unchanged.
- Shared `Messaging.Contracts.Configuration.RetryOptions` (`Limit=3`, `Strategy=Immediate`) + `RetryStrategy` enum.
- `HashHelperGoldenFacts` (12 facts) pins the golden H, byte-identical recompute, per-field sensitivity, structural executionId-exclusion, `skp:data:`/`skp:flag:` 64-hex format, empty-manifest golden, and SourceHash parity — all green.

## Task Commits

Each task was committed atomically:

1. **Task 1: MessageIdentity hash helper** — `5b916e3` (feat)
2. **Task 2: string-keyed ExecutionData + Flag + RetryOptions** — `e0e644a` (feat)
3. **Task 3: HashHelperGoldenFacts golden tests** — `a7a58d3` (test)

**Plan metadata:** committed separately (docs).

_TDD note: Task 1 + Task 3 were `tdd="true"`. Task 3 followed RED (placeholder goldens fail: 2/12) -> capture (computed the exact hex, cross-validated against an independent PowerShell SHA-256 reference) -> GREEN (12/12); committed as a single `test(...)` commit since RED/GREEN differ only by the pinned literals in one file._

## Files Created/Modified
- `src/Messaging.Contracts/Hashing/MessageIdentity.cs` — the single canonical hash helper (created).
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — added `ExecutionData(string)` + `Flag(string)`, retained `ExecutionData(Guid)` transitionally; XML doc updated (modified).
- `src/Messaging.Contracts/Configuration/RetryOptions.cs` — shared retry-budget record + `RetryStrategy` enum (created).
- `tests/BaseApi.Tests/Contracts/HashHelperGoldenFacts.cs` — golden + determinism + parity + key-format facts (created).

## Decisions Made
- **EntryEntryId method name** — the plan `<action>` code and acceptance criteria use `EntryEntryId(corr, stepId)`; the artifact-`provides` note's `HashEntryEntryId` alias was treated as shorthand. Implemented `EntryEntryId` (the authoritative implementation spec).
- **Golden values** — `GoldenH = 5fc25824…43985f`, `GoldenEmptyManifest = 4f53cda1…02b945`. Captured from the compiled helper and independently cross-validated against a PowerShell SHA-256 reference using the U+001F separator and `Guid.ToString("D")` rendering — they agree, so the helper provably matches the cross-process convention.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Retained `ExecutionData(Guid)` overload instead of replacing the signature**
- **Found during:** Task 2 (string-key the L2 data builder)
- **Issue:** The plan's literal instruction "Change `ExecutionData(Guid entryId)` to `ExecutionData(string entryId)`" would have broken the build: `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` (lines 91, 171 pass `Guid`), `tests/.../Features/Orchestration/Projection/L2ProjectionKeysTests.cs` (asserts `skp:data:{guid:D}`), and 6 `tests/.../Processor/Dispatch*Facts.cs` files (pass `Guid entryId`) all bind to the Guid signature. The plan's own `<objective>` mandates "All additive — no contract type change yet (that is Plan 02), so this plan compiles and tests green on its own" — a hard contradiction with an outright signature swap.
- **Fix:** ADDED the new `ExecutionData(string entryId) => $"{Prefix}data:{entryId}"` (no `:D`) overload exactly as the acceptance criteria require, and KEPT the `ExecutionData(Guid)` overload (documented as transitional, removed in Plan 02). Guid callers bind the Guid overload (unchanged behavior, prior golden tests pass); the new content-addressed hex path binds the string overload. No ambiguity (exact-type resolution).
- **Files modified:** src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
- **Verification:** `dotnet build src/BaseProcessor.Core -c Debug` 0/0; existing `L2ProjectionKeysTests` 7/7 green; `DispatchOutputWriteFacts` 3/3 green; new `HashHelperGoldenFacts` 12/12 green; `dotnet build SK_P.sln -c Release` 0 Warning / 0 Error.
- **Committed in:** `e0e644a` (Task 2 commit)

**2. [Rule 3 - Blocking] XML-doc / grep-token hygiene in MessageIdentity.cs**
- **Found during:** Task 1 (create MessageIdentity)
- **Issue:** (a) The raw U+001F control character placed inside an XML doc-comment is invalid XML — `error CS1570` failed the build. (b) The acceptance criteria are literal greps: "File contains neither `X2` nor `Convert.ToHexString`" and "no `executionId` token in the file"; the as-drafted explanatory comments contained those exact literals (in "never use X2" warnings + the "executionId is absent" note).
- **Fix:** (a) Referred to the separator as `U+001F` in prose (kept the actual `''` char in the code constant, which is valid in a C# char literal). (b) Reworded the doc-comments to avoid the literal forbidden tokens ("uppercase-hex formatter" / "BCL hex-string converter" / "per-execution lineage id") — comments only, zero behavior change (same precedent as 30-02 `_total` / 30-04 `8889`).
- **Files modified:** src/Messaging.Contracts/Hashing/MessageIdentity.cs
- **Verification:** `dotnet build src/Messaging.Contracts -c Debug` 0/0; `grep -c` for `X2`, `Convert.ToHexString`, `executionId` each = 0; `b.ToString("x2")` present.
- **Committed in:** `5b916e3` (Task 1 commit)

---

**Total deviations:** 2 auto-fixed (both Rule 3 - blocking)
**Impact on plan:** Both were necessary to satisfy the plan's own "additive / compiles + tests green on its own" objective and the literal grep acceptance criteria. No scope creep — every acceptance criterion is met and the full suite + Release build stay green.

## Issues Encountered
- The RED-run TRX/log did not surface the Expected/Actual diff for the placeholder goldens, so the captured hex could not be read back from the test runner. Resolved by computing the two goldens with an independent PowerShell SHA-256 reference (U+001F separator, `Guid.ToString("D")`), pinning them, and confirming GREEN — which doubles as a cross-process determinism cross-check (the independent reference agrees with the compiled helper).

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- The single canonical hash path is locked and golden-pinned BEFORE any consumer uses it (the HIGH-risk determinism landmine, RESEARCH Pitfall 2, is now a verified invariant).
- Plan 02 (contract type change) can now: route `EntryStepDispatchConsumer` + orchestrator advancement through `MessageIdentity.ComputeH`, migrate the L2 data key to the content-addressed `ExecutionData(string)` path, and remove the transitional `ExecutionData(Guid)` overload.
- `RetryOptions` shape is available for both processes to bind from the `Retry` section per-process (Phase 32 final-attempt check reads `Limit`).

## Self-Check: PASSED
- FOUND: src/Messaging.Contracts/Hashing/MessageIdentity.cs
- FOUND: src/Messaging.Contracts/Configuration/RetryOptions.cs
- FOUND: tests/BaseApi.Tests/Contracts/HashHelperGoldenFacts.cs
- FOUND: src/Messaging.Contracts/Projections/L2ProjectionKeys.cs (modified)
- FOUND commit 5b916e3 (Task 1)
- FOUND commit e0e644a (Task 2)
- FOUND commit a7a58d3 (Task 3)

---
*Phase: 31-idempotent-execution-exactly-once-effect*
*Completed: 2026-06-04*
