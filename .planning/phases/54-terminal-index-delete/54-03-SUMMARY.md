---
phase: 54-terminal-index-delete
plan: 03
subsystem: api
tags: [csharp, dotnet, redis, stackexchange-redis, masstransit, keeper, processor-pipeline, l2-key-lifecycle]

# Dependency graph
requires:
  - phase: 54-terminal-index-delete
    provides: 54-02 added KeeperDelete.MessageId init prop (the field BuildDelete stamps + DeleteConsumer reads); 54-01 test kits mock the array-DEL + persist overloads so the production reshape compiles against existing facts
  - phase: 51-processor-forward-recovery-pipeline
    provides: the ProcessorPipeline forward+recovery tails (DeleteSourceTail local fn, recovery all-clear inline delete) this plan unifies; SlotTtl/D-06 TTL writes; RetryLoop/SendKeeper/BuildDelete builders
  - phase: 52-three-state-keeper
    provides: DeleteConsumer + RecoveryConsumerBase.Guard<T> the both-key array DEL routes through
provides:
  - "Unified ProcessorPipeline.DeleteTerminalAsync(d, messageId, db, limit, ct): ONE atomic two-key DEL [ExecutionData(entryId), MessageIndex(messageId)] + best-effort persist-on-escalate + unconditional KeeperDelete(messageId) handoff (A19/GC-01)"
  - "Both the forward tail (4 call sites) and the recovery all-clear path converge on the single DeleteTerminalAsync — no duplicated inline delete (D-01)"
  - "Source-step early-return removed: the index DEL runs for source steps too; ExecutionData(Guid.Empty) is a harmless absent operand (D-06)"
  - "REINJECT gate stays upstream of the unified tail — REINJECT never deletes either key (GC-02, delivered for free)"
  - "BuildDelete(d, messageId) stamps MessageId on the escalated KeeperDelete (D-05)"
  - "DeleteConsumer.HandleAsync issues the both-key atomic DEL [ExecutionData(m.EntryId), MessageIndex(m.MessageId)] via the inherited Guard, drop-on-absent (GC-03)"
affects: [54-04, terminal-index-delete, A19, keeper-delete-consumer, processor-pipeline-tail, GC-01, GC-02, GC-03]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Single shared private terminal-tail method (DeleteTerminalAsync) both the forward and recovery paths call — two-key DEL + persist-on-escalate + keeper handoff live in ONE place, never duplicated inline"
    - "Inline RedisKey[] built at the call site and wrapped in RetryLoop — no array-returning member added to L2ProjectionKeys (it stays string-only, D-02)"
    - "Best-effort-persist-then-unconditional-escalate: KeyPersist outcome never short-circuits the keeper send (D-03)"

key-files:
  created: []
  modified:
    - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
    - src/Keeper/Recovery/DeleteConsumer.cs

key-decisions:
  - "Extracted ONE DeleteTerminalAsync near SendKeeper; both tails call it (D-01). The recovery all-clear if(!IsSource){...} block and the forward DeleteSourceTail local fn were both DELETED and replaced by the call"
  - "RedisKey[] built inline at the call site inside RetryLoop; no L2ProjectionKeys array helper added (D-02)"
  - "On DEL exhaustion: RetryLoop-wrap KeyPersistAsync(MessageIndex(messageId)) THEN SendKeeper(BuildDelete(d, messageId)) UNCONDITIONALLY — persist outcome is ignored for the send decision (D-03)"
  - "Source-step early-return removed from BOTH tails (D-06); the D-07 per-slot TTL writes at the alloc/refresh sites left byte-identical (count == 2, AC-8)"
  - "DeleteConsumer both-key array DEL via the inherited Guard<long>; no new try/catch (Guard owns RetryLoop + re-throw → skp-dlq-1) (GC-03)"

patterns-established:
  - "Unified terminal tail: a single private DeleteTerminalAsync both the forward happy/business-fail/in-exception exits and the recovery all-clear path converge on"
  - "Both-key atomic Redis DEL via inline RedisKey[] (drop-on-absent inherent) for end-of-message index reclamation on both the processor and keeper sides"

requirements-completed: []  # GC-01/GC-02/GC-03 behavior SHIPS here but the proving facts land in Plan 04 — left open per phase guardrails (see Deviations)

# Metrics
duration: 16min
completed: 2026-06-11
---

# Phase 54 Plan 03: Terminal Two-Key DEL — Unified Pipeline Tail + Keeper Both-Key DEL Summary

**Extracted ONE shared `ProcessorPipeline.DeleteTerminalAsync` that issues a single atomic two-key `DEL [ExecutionData(entryId), MessageIndex(messageId)]` (inline `RedisKey[]` in `RetryLoop`, D-02), best-effort `KeyPersist`s the index on DEL exhaust then UNCONDITIONALLY escalates a `KeeperDelete` carrying `MessageId` (D-03); both the forward tail (4 call sites) and the recovery all-clear path now converge on it (D-01), the source-step early-return is gone (D-06), the per-slot TTL writes are untouched (D-07); and `DeleteConsumer.HandleAsync` re-issues the same both-key array DEL via the inherited `Guard` (GC-03). Solution builds 0-warning Release AND Debug.**

## Performance

- **Duration:** ~16 min
- **Started:** 2026-06-11T22:23:54Z
- **Completed:** 2026-06-11T22:40:08Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- **A19/GC-01 — unified terminal tail:** added `private async Task DeleteTerminalAsync(EntryStepDispatch d, Guid messageId, IDatabase db, int limit, CancellationToken ct)` to `ProcessorPipeline`. It issues ONE `db.KeyDeleteAsync(new RedisKey[]{ ExecutionData(d.EntryId), MessageIndex(messageId) })` in `RetryLoop` (D-02 inline array, no `L2ProjectionKeys` helper); on `!del.Succeeded` it `RetryLoop`-wraps `KeyPersistAsync(MessageIndex(messageId))` then calls `SendKeeper(BuildDelete(d, messageId))` **unconditionally** (D-03).
- **D-01 convergence:** the forward `DeleteSourceTail()` local fn was DELETED and all four call sites (business-fail, `ProcessStatusException`, unexpected, happy Post tail) now call `DeleteTerminalAsync(d, messageId, db, limit, ct)`; the recovery all-clear `if (!SourceStep.IsSource(d.EntryId)){ … }` block was replaced by the same single call. The two-key DEL + escalate logic lives in ONE place.
- **D-06:** the source-step early-return was removed from BOTH tails — the index DEL now runs even for a source step (`ExecutionData(Guid.Empty)` is a drop-on-absent no-op operand).
- **GC-02 (free):** the REINJECT gate (`anyInfra` at the recovery tail, forward read-exhaust, existence/HGETALL exhausts) stays upstream and `return`s BEFORE the unified tail — no REINJECT path deletes either key.
- **D-05:** `BuildDelete` gained a `Guid messageId` param and stamps `MessageId = messageId` on the `KeeperDelete` (mirroring the `EntryId = d.EntryId` line).
- **GC-03 — keeper both-key DEL:** `DeleteConsumer.HandleAsync` now issues `Guard(() => Db.KeyDeleteAsync(new RedisKey[]{ ExecutionData(m.EntryId), MessageIndex(m.MessageId) }), ct)` — one atomic both-key DEL via the inherited `Guard<long>`, drop-on-absent inherent, no new try/catch.
- **D-07 (untouched):** both `KeyExpireAsync(L2ProjectionKeys.MessageIndex(messageId), SlotTtl())` writes (alloc + recovery-retire refresh) remain present and byte-identical — confirmed count == 2 (AC-8).
- **D-08 (untouched):** no new `metrics.*` call site — only the pre-existing `metrics.ResultSent.Add` survives (AC-9). `UseMessageRetry = none` preserved (no bus-retry config touched).
- **Build gate:** `dotnet build SK_P.sln -c Release` AND `-c Debug` both report **0 Warning(s) / 0 Error(s)** (AC-10), including the `BaseApi.Tests` project (confirming the 54-01 test kits already mock the array-DEL + persist overloads).

## Task Commits

Each task was committed atomically:

1. **Task 1: Extract DeleteTerminalAsync, rewire both tails, extend BuildDelete** - `e0c0c7f` (feat)
2. **Task 2: DeleteConsumer both-key atomic DEL** - `7401496` (feat)

**Plan metadata:** (final docs commit — see git log)

_Note: both tasks were `tdd="true"`; this plan is the PRODUCTION shape that PRECEDES the Plan 04 fact inversion (the test kits + mocks already exist from Plan 01), so no separate RED test commit was authored here — the RED/GREEN fact inversion is Plan 04's scope per the phase guardrails._

## Files Created/Modified
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` - Added `DeleteTerminalAsync` (atomic two-key DEL + persist-on-escalate + KeeperDelete handoff); deleted the forward `DeleteSourceTail` local fn and rewired its 4 call sites; replaced the recovery all-clear inline delete with the unified call; extended `BuildDelete(d, messageId)` to stamp `MessageId`. D-07 TTL writes and the REINJECT/`BuildReinject` gate untouched.
- `src/Keeper/Recovery/DeleteConsumer.cs` - `HandleAsync` now issues the both-key array DEL `[ExecutionData(m.EntryId), MessageIndex(m.MessageId)]` via the inherited `Guard`; XML-doc extended to note the both-key A19/GC-03 behavior; no new try/catch.

## Decisions Made
- All locked decisions (D-01, D-02, D-03, D-05, D-06, D-07, D-08, GC-03) followed exactly as specified in the plan and the phase guardrails. No structural deviations.
- Placed `DeleteTerminalAsync` immediately above the `SendResult`/`SendKeeper` send-owner block (one of the plan's two suggested locations).

## Deviations from Plan

**Plan code execution: None — both tasks executed exactly as written.**

### Expected now-red pre-existing facts (NOT a code deviation — by design, fixed in Plan 04)

The production delete path changed shape (scalar `KeyDeleteAsync(RedisKey)` → array `KeyDeleteAsync(RedisKey[])` + a new `KeyPersistAsync` on escalate) AHEAD of the Plan 04 fact inversion. As the phase guardrails explicitly anticipate ("SOME existing delete facts may now FAIL because the production code changed shape ahead of the fact inversion … that is EXPECTED and acceptable for this plan"), the hermetic suite now shows **8 failing facts** (`dotnet test -- --filter-not-trait Category=RealStack` → Failed: 8, Passed: 520, Total: 528). They are all in the delete/recovery-path fact classes:
- `DeleteConsumerFacts` — asserts the retired scalar `KeyDeleteAsync((RedisKey)ExecutionData(...), flags)` overload (`db.Received(1)` on the scalar) — now superseded by the array overload.
- `PipelineEndDeleteFacts` / `PipelineRecoveryFacts` — assert the old single-key forward/recovery delete shape and the `BuildDelete(d)` (no-messageId) escalate; now superseded by the two-key DEL + persist + `BuildDelete(d, messageId)`.

These facts still **compile** (the scalar `IDatabase` overload still exists; the 54-01 kits supply the array + persist mocks), so the **build gate holds 0-warning**. The array-shape + persist + `MessageId` assertions are inverted in **Plan 54-04** — that is where these facts go green again. (The four additional RealStack/live-broker failures seen in the unfiltered run are the same pre-existing live-broker E2E failures noted in Phase 53's close, unrelated to this change.)

**State-update deviation (process, not code):** the plan frontmatter lists `requirements: [GC-01, GC-02, GC-03]`. The BEHAVIOR for all three ships in this plan, but the **proving facts** land in Plan 04, and the existing facts are currently RED by design (above). Consistent with the documented 54-01/54-02 deviations and the project's known GSD scoping-drift caveat, the REQUIREMENTS.md checkboxes are **left open** for the Plan-04/verifier pass rather than marked here — marking them now (with the proving facts red) would misrepresent state. `requirements.mark-complete` was therefore NOT run; STATE/ROADMAP progress recorded normally otherwise.

---

**Total deviations:** 0 code deviations. 1 expected-now-red-facts note + 1 requirements-marking deferral (both per phase guardrails).
**Impact on plan:** None on correctness — the production behavior is wired exactly per the locked decisions and builds 0-warning Release + Debug; the red facts are the intended pre-inversion state.

## Issues Encountered
- A backgrounded `dotnet test --filter "FullyQualifiedName~…"` invocation collided (TestResults log file lock) with a foreground run and emitted a `MTP0001 VSTestTestCaseFilter ignored` warning (the `--filter` substring syntax is VSTest-style and is ignored by the Microsoft.Testing.Platform runner, so the whole suite ran). Resolved by re-running with the MTP-native `-- --filter-class`/`--filter-not-trait` switches after `--`. No impact on the build gate (the authoritative acceptance criterion).

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- The A19 production behavior (both-key terminal DEL on the processor + keeper sides, persist-on-escalate, `KeeperDelete.MessageId` handoff) is fully wired and compiles 0-warning Release + Debug.
- **Plan 54-04** is the next step: invert `PipelineEndDeleteFacts` / `PipelineRecoveryFacts` / `DeleteConsumerFacts` to assert the array-DEL + persist + `MessageId` shape (re-greening the 8 now-red facts) and then mark GC-01/GC-02/GC-03 complete once green.
- D-07 TTL backstop confirmed intact (count == 2); no new metric series; REINJECT gate untouched.

---
*Phase: 54-terminal-index-delete*
*Completed: 2026-06-11*

## Self-Check: PASSED

- FOUND: src/BaseProcessor.Core/Processing/ProcessorPipeline.cs (contains `private async Task DeleteTerminalAsync`, `db.KeyDeleteAsync(new RedisKey[]`, `KeyPersistAsync(L2ProjectionKeys.MessageIndex(messageId))`, `SendKeeper(BuildDelete(d, messageId)`, `MessageId = messageId`; D-07 TTL writes count == 2; `DeleteSourceTail` count == 0; only the pre-existing `metrics.ResultSent.Add` site)
- FOUND: src/Keeper/Recovery/DeleteConsumer.cs (contains `new RedisKey[]`, `L2ProjectionKeys.MessageIndex(m.MessageId)`, still wrapped in `Guard(`)
- FOUND: .planning/phases/54-terminal-index-delete/54-03-SUMMARY.md
- FOUND commit: e0c0c7f (Task 1)
- FOUND commit: 7401496 (Task 2)
- Solution builds 0-warning / 0-error Release AND Debug
