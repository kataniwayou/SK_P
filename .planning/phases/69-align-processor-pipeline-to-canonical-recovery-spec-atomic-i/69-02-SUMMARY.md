---
phase: 69-align-processor-pipeline-to-canonical-recovery-spec-atomic-i
plan: 02
subsystem: infra
tags: [redis, processor-pipeline, keeper-recovery, gated-cleanup, race-elimination, nsubstitute, xunit]

# Dependency graph
requires:
  - phase: 69-align-processor-pipeline-to-canonical-recovery-spec-atomic-i
    plan: 01
    provides: "the atomic forward-Post write + the single forward-Post INJECT exhaust site this plan gates the cleanup tail on (escalated = true is set there)"
  - phase: 54-terminal-index-delete-atomic-keeper-gc
    provides: "DeleteTerminalAsync — the atomic two-key (data + index) cleanup tail now gated on !escalated"
provides:
  - "Gated forward cleanup: the DeleteTerminalAsync tail runs ONLY when no item escalated this pass (local bool escalated, set ONLY at the forward-Post INJECT site) — eliminates the processor/keeper race on the index key (spec §4.3 final ¶, §10 bullet 2 / GATE-01)"
  - "EscalatedItem_SkipsCleanup fact — inverts HappyTail_DeletesSource via AtomicWriteFaultL2, proving db.DidNotReceive().KeyDeleteAsync(RedisKey[]) when an item escalates"
affects: [processor-keeper-recovery-spec alignment, T-69-02 index-key race]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Gated cleanup tail: a local bool escalated set ONLY at the single escalation (INJECT) site guards the terminal DEL; skipped cleanup leaves index + input keys for the keeper + a later Recovery pass / index-TTL to reclaim (no KeyPersist on the skip path → the atomic write's TTL stands)"
    - "Inverted-tail fact: reuse the escalation fault mux (AtomicWriteFaultL2) + assert DidNotReceive on the array KeyDeleteAsync overload (stubbed .Returns(2L) → a true never-called assertion, not an unstubbed false-green)"

key-files:
  created: []
  modified:
    - "src/BaseProcessor.Core/Processing/ProcessorPipeline.cs - local bool escalated declared at the Post foreach; set true at the forward-Post INJECT site only; the DeleteTerminalAsync tail gated on if (!escalated); class XML doc updated"
    - "tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs - added EscalatedItem_SkipsCleanup (the inverted HappyTail_DeletesSource)"

key-decisions:
  - "escalated set ONLY at the forward-Post INJECT site (Pitfall 5) — NOT on the Pre REINJECT, the input-schema business-fail tail (a business failure is not an escalation → its tail SHOULD still run), the In-stage exception tails, or the DELETE-exhaust inside DeleteTerminalAsync; those paths already return before the gated tail"
  - "No KeyPersist on the skip path: the index keeps the TTL its atomic write (Plan 01) set, so a skipped cleanup is bounded by the index/data TTL, never an unbounded leak (T-69-05 accepted, TTL-backstopped)"
  - "The Recovery-pass DeleteTerminalAsync (inside RunRecoveryAsync, behind its own anyInfra branch) is NOT gated — left unchanged"

patterns-established:
  - "Gated forward cleanup tail: if (!escalated) await DeleteTerminalAsync(...) — escalation flag set at the single INJECT site"

requirements-completed:
  - "Spec §4.3 gated forward cleanup (GATE-01)"
  - "Spec §4.3/§5 skipped-cleanup is recovery/TTL-safe (GATE-02)"

# Metrics
duration: 10min
completed: 2026-06-15
---

# Phase 69 Plan 02: Gated Forward Cleanup Tail Summary

**The forward cleanup tail `DeleteTerminalAsync` now runs ONLY when no item escalated this pass — a local `bool escalated`, set exclusively at the forward-Post INJECT site, gates the terminal two-key DEL, leaving the index + input keys for the keeper / a later Recovery pass / index-TTL and eliminating the processor/keeper race on the index key (spec §4.3 final ¶, §10 bullet 2).**

## Performance

- **Duration:** ~10 min
- **Started:** 2026-06-15T21:50Z
- **Completed:** 2026-06-15T22:01Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- Gated the forward cleanup tail on a local `bool escalated` flag: when any forward-Post item escalates to the keeper (INJECT) this pass, the `DeleteTerminalAsync` atomic two-key DEL is SKIPPED — the index `L2[messageId]` and input `L2[inputEntryId]` keys are left intact for the keeper to complete the escalated item and for a later Recovery pass (redelivery) / index-TTL to reclaim. This closes spec §4.3's final paragraph and §10 bullet 2, removing the processor/keeper race on the index key (T-69-02).
- Set the `escalated` flag at the SINGLE forward-Post INJECT site only (Pitfall 5) — never on the Pre REINJECT, the input-schema business-fail tail (which still runs — a business failure is not an escalation), the In-stage exception tails, or the DELETE-exhaust inside `DeleteTerminalAsync`. Those paths already `return` before the gated tail.
- Added `EscalatedItem_SkipsCleanup` — the inverted analog of `HappyTail_DeletesSource`: it builds with `AtomicWriteFaultL2` (forcing a forward-Post INJECT), then asserts `db.DidNotReceive().KeyDeleteAsync(RedisKey[], CommandFlags)` (tail skipped) and no `KeeperDelete` (no tail → no DELETE escalation). `HappyTail_DeletesSource` (the non-escalated contrast — tail DOES run) is retained unchanged.

## Task Commits

Each task was committed atomically:

1. **Task 1: Gate the forward cleanup tail on a local `bool escalated` flag (production)** — `f9f9dae` (feat)
2. **Task 2: Add EscalatedItem_SkipsCleanup fact (GATE-01)** — `b41bd42` (test)

_Note: This is a `tdd="true"` plan, but the unit under test is a behavioral gate on existing production code. Task 1 lands the gate (BaseProcessor.Core builds clean); Task 2 adds the fact that exercises it. The fact would not pass without Task 1's gate, so the plan correctly serializes production-then-fact._

## Files Created/Modified
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — declared `var escalated = false` at the Post `foreach` (next to `var slot = 0`); set `escalated = true` at the forward-Post `if (!write.Succeeded)` INJECT block alongside the existing `SendKeeper(BuildInject(...))`; gated the tail as `if (!escalated) await DeleteTerminalAsync(d, messageId, db, limit, ct)`; updated the class XML FORWARD source-delete-tail paragraph to note the tail is now gated on "no forward-Post INJECT this pass."
- `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs` — added `EscalatedItem_SkipsCleanup` (inverted `HappyTail_DeletesSource`, `AtomicWriteFaultL2` mux, `DidNotReceive().KeyDeleteAsync(RedisKey[], CommandFlags)` + empty-`KeeperDelete` assertions).

## Decisions Made
See `key-decisions` frontmatter. Headline: the flag is set ONLY at the single INJECT site (Pitfall 5); no `KeyPersist` on the skip path (the index keeps its atomic-write TTL — TTL-bounded, never an unbounded leak); the Recovery-pass `DeleteTerminalAsync` (its own `anyInfra` branch) is left ungated.

## Deviations from Plan

None — plan executed exactly as written. The acceptance criteria were met verbatim:
- `var escalated = false` present once; `escalated = true` present once (inside the forward-Post `if (!write.Succeeded)` block); `if (!escalated)` guards the forward `DeleteTerminalAsync(d, messageId, db, limit, ct)`; the Recovery-pass tail is unchanged.

**Total deviations:** 0.

## Issues Encountered
- The class XML doc edit (cosmetic, terse note on the gated tail) could not be applied via the Edit tool because the surrounding lines carry an em-dash (`—`) and a Unicode arrow (`→`) that the exact-match engine would not reconcile across encodings. Applied the equivalent terse note via a UTF-8-safe in-place text replace on a special-char-free anchor instead. No functional impact — doc-only.

## Verification Results
- **Task 1 gate:** `dotnet build src/BaseProcessor.Core/BaseProcessor.Core.csproj -c Release` — succeeded, **0 warnings / 0 errors**.
- **Full solution:** `dotnet build SK_P.sln -c Release` — succeeded, **0 warnings / 0 errors**.
- **Select-String pre-check:** both `HappyTail_DeletesSource` and `EscalatedItem_SkipsCleanup` present — PASS.
- `dotnet test ... --filter-method "*PipelineForward*"` — **8/8 green** (was 7/7; `EscalatedItem_SkipsCleanup` added, `HappyTail_DeletesSource` still green — the non-escalated contrast).
- `dotnet test ... --filter-method "*PipelineRecovery*"` — **5/5 green** (GATE-02 — skipped cleanup is recovery/TTL-safe; the Recovery facts idempotently re-emit from the intact index on redelivery, unchanged).
- `dotnet test ... --filter-method "*Pipeline*"` — 35/37; the 2 failures are `UseBaseApiPipelineFacts.Probe_ApiV1Tests_*` (Npgsql connect to 127.0.0.1:5433 — Postgres not running), a pre-existing real-stack WebAPI infra dependency, identical to the 69-01-SUMMARY result and unrelated to this plan's changes.

_MTP note: `--filter-not-trait Category=RealStack` is NOT honored by this xunit.v3/MTP runner (per the project memory note that `dotnet test --filter` is silently ignored), so a blanket suite run executes the infra-dependent RealStack/WebAPI tests too (broker/Postgres/Redis absent locally → expected failures). The hermetic gate for this plan is the `*PipelineForward*` 8/8, `*PipelineRecovery*` 5/5, and `*Pipeline*` 35/37 (only the documented Postgres-:5433 pair failing) filtered runs. The full hermetic green run is operator/CI-gated with infra up, consistent with prior phases._

## User Setup Required
None — no external service configuration required for the hermetic gate.

## Next Phase Readiness
- The gated forward cleanup completes spec §4.3 (atomic write + gated cleanup) and §10 bullets 1–2 over the Plan-01 atomic forward write. The processor↔keeper index-key race (T-69-02) is closed; skipped-cleanup keys are TTL-backstopped (T-69-05 accepted) and idempotently reclaimed by the Recovery pass on redelivery (GATE-02). OUT-OF-SCOPE items (D-2 INJECT contract, D-3 forward retirement, In-Process contract) were correctly left untouched per the plan.

---
*Phase: 69-align-processor-pipeline-to-canonical-recovery-spec-atomic-i*
*Completed: 2026-06-15*

## Self-Check: PASSED

- Commits verified present: `f9f9dae`, `b41bd42`.
- Files verified present: `ProcessorPipeline.cs`, `PipelineForwardFacts.cs`, `69-02-SUMMARY.md`.
