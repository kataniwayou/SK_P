---
phase: 47-dlq-consolidation-at-least-once-semantics
verified: 2026-06-09T10:55:00Z
status: passed
score: 3/3 must-haves verified
overrides_applied: 0
re_verification:
  previous_status: gaps_found
  previous_score: 2/3
  gaps_closed:
    - "SC-3: EmptyMux() now explicitly stubs StringLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(0L) — the production gate ReinjectConsumer.HandleAsync calls. Fix commit: ca7177b."
  gaps_remaining: []
  regressions: []
---

# Phase 47: DLQ Consolidation & At-Least-Once Semantics — Verification Report

**Phase Goal:** Every terminal give-up across the processor and the Keeper (a send exception with its retry loop exhausted; a Keeper L2 op exhausted) routes to a single consolidated `_DLQ1`, and the whole execution path is at-least-once with no dedup/idempotency key — duplicate effects are tolerated downstream by construction.
**Verified:** 2026-06-09T10:55:00Z
**Status:** passed
**Re-verification:** Yes — after gap closure (commit ca7177b closes SC-3 WR-01)

---

## Context: Verification-Only Phase

This phase produces no new production source. Deliverables are:

1. New xUnit `[Fact]` tests under `tests/BaseApi.Tests/` proving structural invariants against existing v4 production code.
2. A human-readable audit ledger (`47-DLQ-AUDIT.md`) and an additive design-doc amendment (A16).

Verification therefore checks that the proving tests actually exist, are structurally sound, and genuinely establish the must-haves — not that new production features were added.

---

## Goal Achievement

### Observable Truths (derived from phase success criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| SC-1 | A single consolidated `_DLQ1` receives every terminal send/L2 give-up from the processor and the Keeper — no separate `keeper-dlq`, wired once, proven structurally | VERIFIED | `ProcessorSendExhaustion_RoutesToDlq1` (KeeperDlqConsolidationTests) + `No_v4_give_up_path_references_keeper_dlq` (AtLeastOnceStructuralFacts) + cited `Dlq1_Consolidated` / `Keeper_SendFault_RetriesToDlq1` (Phase 36). All confirmed present in source. No regression. |
| SC-2 | Execution path carries no dedup/idempotency key; a redelivered message reproduces its effect without collapse | VERIFIED | `No_dedup_machinery_on_execution_path` (reflection guard over Orchestrator + BaseProcessor.Core assemblies). `Duplicate_StepCompleted_reproduces_effect_no_collapse` (ONE dispatcher, Assert.Equal(2, Calls.Count)). `Duplicate_Reinject_reproduces_effect_no_collapse` (ONE consumer, Assert.Equal(2, send.Sent.Count)). All confirmed in source. No regression. |
| SC-3 | The REINJECT-data-gone case terminates deterministically at `_DLQ1` for operator triage rather than looping — proven by test | VERIFIED | `DataGone_reinject_faults_and_routes_to_dead_letter` exists, carries [Trait("Phase","47")], asserts RecoveryDataGoneException + ConsolidatedFault. **Gap WR-01 is CLOSED (commit ca7177b):** `EmptyMux()` now explicitly stubs `db.StringLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(0L)` (line 45 of RecoveryDeadLetterFacts.cs), which is the exact gate `ReinjectConsumer.HandleAsync` calls at line 33 (`await Db.StringLengthAsync(...) == 0`). The data-gone condition now fires by explicit stub expression, not by NSubstitute mock default. The `StringGetAsync` stub is retained for internal consistency. `PresentMux()` symmetrically stubs `StringLengthAsync -> 7L`. The proof is mechanically sound. |

**Score:** 3/3 truths verified

---

## Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` | Processor send-exhaustion -> skp-dlq-1 sibling [Fact] containing `ConsolidatedErrorTransportFilter.Dlq1` | VERIFIED | File exists. `ProcessorSendExhaustion_RoutesToDlq1` fact present at line 225. `BuildProcessorHarness` variant present. `[Trait("Phase","47")]` on the new fact. Uses `ConsolidatedErrorTransportFilter.Dlq1` const. `ProcessorSendExhaustedConsumer` throwaway consumer present. No regression. |
| `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs` | Reflection no-dedup guard (FACT A) + source-scan no-keeper-dlq guard (FACT B) | VERIFIED | File exists (namespace `BaseApi.Tests.Resilience`). FACT A: reflects over `typeof(StepDispatcher).Assembly` + `typeof(ProcessorPipeline).Assembly`, asserts no `MessageIdentity` type and no `MessageIdentity` property/field. FACT B: resolves repo root via `[CallerFilePath]`, asserts both scoped dirs exist before scanning, excludes `KeeperRecoveryHandler.cs` by exact filename, scans for both `KeeperQueues.DeadLetter` and `"keeper-dlq"`. Both facts carry `[Trait("Phase","47")]`. No regression. |
| `tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs` | StepCompleted duplicate-delivery no-collapse fact (R3) containing `Assert.Equal(2` | VERIFIED | File exists. `Duplicate_StepCompleted_reproduces_effect_no_collapse` fact present at line 244. ONE `RecordingDispatcher`, ONE `StepCompletedConsumer`, `consumer.Consume(...)` called twice with the SAME `msg` instance. `Assert.Equal(2, dispatcher.Calls.Count)` at line 281. Both calls assert same `nextStepId`/`nextProcessorId`/`entryId`. `[Trait("Phase","47")]` present. No regression. |
| `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs` | EntryStepDispatch/KeeperReinject duplicate-delivery fact (R3) + Phase-47 trait on DataGone fact (R2) + sound EmptyMux() data-gone gate | VERIFIED | File exists. `Duplicate_Reinject_reproduces_effect_no_collapse` present: ONE `ReinjectConsumer` + `CapturingSendProvider`, same `msg` Consumed twice, `Assert.Equal(2, send.Sent.Count)` at line 174. `[Trait("Phase","47")]` on both facts. **WR-01 CLOSED:** `EmptyMux()` now stubs `StringLengthAsync -> 0L` at line 45 (commit ca7177b). The data-gone gate is explicitly exercised. `PresentMux()` symmetrically stubs `StringLengthAsync -> 7L`. Both helpers are mechanically consistent with production. |
| `.planning/phases/47-dlq-consolidation-at-least-once-semantics/47-DLQ-AUDIT.md` | Traceability ledger with RESIL-02 and RESIL-03 rows, all three SCs, no unproven row | VERIFIED | File exists. 8-row table present. RESIL-02 covered by 5 rows (SC-1: 4 rows, SC-3: 1 row). RESIL-03 covered by 3 rows (SC-2: 3 rows). All three SCs covered. Every proving-test method name matches a real fact confirmed in source. No row contains "unproven"/"TODO"/"TBD". Runner-correct verify commands present. No regression. |
| `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` | Dated at-least-once guarantee amendment (A16) citing 47-DLQ-AUDIT.md + KeeperReinject.Payload note | VERIFIED | A16 amendment line present (after A15). Explicitly names the at-least-once/no-dedup guarantee. Cites `47-DLQ-AUDIT.md`. Records `KeeperReinject` carries `Payload : string`. Additive only. No regression. |

---

## Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `AtLeastOnceStructuralFacts.No_v4_give_up_path_references_keeper_dlq` | `src/BaseProcessor.Core/Processing/` + `src/Keeper/Recovery/` | `Directory.EnumerateFiles` excluding `KeeperRecoveryHandler.cs` | WIRED | Both directory assertions present (T-47-01 guard). Exclusion `Path.GetFileName(f) != "KeeperRecoveryHandler.cs"` at line 99. Scans for both `KeeperQueues.DeadLetter` and `"keeper-dlq"`. |
| `AtLeastOnceStructuralFacts.No_dedup_machinery_on_execution_path` | Orchestrator + BaseProcessor.Core assemblies | `typeof(StepDispatcher).Assembly` / `typeof(ProcessorPipeline).Assembly` | WIRED | Both `typeof(...)` anchors at lines 25-28. `Assert.DoesNotContain` for type name at line 54. `GetProperties`/`GetFields` member sweep at lines 61-62. |
| `Duplicate_StepCompleted_reproduces_effect_no_collapse` | `StepCompletedConsumer` + `RecordingDispatcher` | double-Consume of SAME message into ONE dispatcher | WIRED | One `RecordingDispatcher` at line 272. Two `consumer.Consume(...)` calls at lines 277-278 with same `msg`. `Assert.Equal(2, dispatcher.Calls.Count)` at line 281. |
| `DataGone_reinject_faults_and_routes_to_dead_letter` | `ReinjectConsumer` data-gone gate | `EmptyMux()` -> `StringLengthAsync == 0` | WIRED (gap closed) | `EmptyMux()` now explicitly stubs `db.StringLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(0L)` (commit ca7177b, line 45). Production gate at `ReinjectConsumer.cs:33` calls `await Db.StringLengthAsync(L2ProjectionKeys.ExecutionData(m.EntryId)) == 0` — the stub now directly targets this call. Proof is mechanically sound. |
| `47-DLQ-AUDIT.md` rows | Green Phase-47 facts (and cited Phase-36/46 facts) | file:method citation per row | WIRED | All 8 method names confirmed present in `tests/BaseApi.Tests/`. Phase-47 filter covers 6/6 tagged facts. Two cited-existing Phase-36 facts separately confirmed in KeeperDlqConsolidationTests. |
| Design-doc A16 amendment | `47-DLQ-AUDIT.md` | cross-reference citation | WIRED | "47-DLQ-AUDIT.md" appears in A16 amendment line. |

---

## Data-Flow Trace (Level 4)

Not applicable — this phase produces no data-rendering components. All deliverables are test facts and documentation. Level 4 data-flow tracing is N/A.

---

## Behavioral Spot-Checks

| Behavior | Evidence | Status |
|----------|----------|--------|
| 6 facts tagged `[Trait("Phase","47")]` | Confirmed in source: `ProcessorSendExhaustion_RoutesToDlq1`, `No_dedup_machinery_on_execution_path`, `No_v4_give_up_path_references_keeper_dlq`, `DataGone_reinject_faults_and_routes_to_dead_letter`, `Duplicate_StepCompleted_reproduces_effect_no_collapse`, `Duplicate_Reinject_reproduces_effect_no_collapse` | PASS |
| `EmptyMux()` stubs the correct Redis method | `StringLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(0L)` at line 45. `ReinjectConsumer.cs:33` gates on `StringLengthAsync`. Gap WR-01 closed by commit ca7177b. | PASS |
| `PresentMux()` / `EmptyMux()` are symmetric | Both stub `StringLengthAsync`: `EmptyMux` -> `0L`, `PresentMux` -> `7L`. Symmetric and consistent. | PASS |
| `Duplicate_Reinject` uses ONE consumer + double-Consume | `new ReinjectConsumer(...)` once, `consumer.Consume(ContextFor(msg, ct))` twice at lines 169-170 | PASS |
| `Duplicate_StepCompleted` uses ONE dispatcher + double-Consume | `new RecordingDispatcher()` once at line 272, `consumer.Consume(...)` twice at lines 277-278 | PASS |
| `No_v4_give_up_path_references_keeper_dlq` excludes exactly `KeeperRecoveryHandler.cs` | `Path.GetFileName(f) != "KeeperRecoveryHandler.cs"` at line 99; no other exclusions | PASS |
| Build 0/0 | Attested in 47-01-SUMMARY, 47-02-SUMMARY, 47-03-SUMMARY; ca7177b adds 1 file, 0 new types, 0 API changes | PASS (summary evidence) |

---

## Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|-------------|-------------|--------|----------|
| RESIL-02 | 47-01-PLAN, 47-02-PLAN, 47-03-PLAN | Processor and Keeper terminal give-ups route to a single consolidated `_DLQ1` | SATISFIED | `ProcessorSendExhaustion_RoutesToDlq1` (processor give-up -> skp-dlq-1), `No_v4_give_up_path_references_keeper_dlq` (structural no-keeper-dlq guard), cited `Dlq1_Consolidated`/`Keeper_SendFault_RetriesToDlq1` (generic exhaustion), `DataGone_reinject_faults_and_routes_to_dead_letter` (data-gone terminal -> skp-dlq-1, proof now mechanically sound after ca7177b). |
| RESIL-03 | 47-01-PLAN, 47-02-PLAN, 47-03-PLAN | Execution path is at-least-once with no dedup/idempotency key; duplicate effects tolerated | SATISFIED | `No_dedup_machinery_on_execution_path` (reflection guard — no MessageIdentity type/member on execution-path assemblies), `Duplicate_StepCompleted_reproduces_effect_no_collapse` (Count==2, no collapse), `Duplicate_Reinject_reproduces_effect_no_collapse` (Count==2, no collapse). |

**REQUIREMENTS.md traceability check:** RESIL-02 and RESIL-03 are both mapped to Phase 47 (`RESIL-02 | Phase 47 | Pending`, `RESIL-03 | Phase 47 | Pending`). Both are fully covered by facts in this phase. No orphaned requirements.

---

## Anti-Patterns Found

| File | Location | Pattern | Severity | Impact |
|------|----------|---------|----------|--------|
| `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` | Line 207 (`Dlq_TopologyArgs`) | `Assert.Equal(604_800_000, (int)TimeSpan.FromDays(7).TotalMilliseconds)` asserts a property of TimeSpan, not the SUT; can never fail for a production reason | Warning | Tautology assertion — not a Phase 47 deliverable concern, pre-existing (Phase 36). Flagged for completeness per WR finding IN-01. Not a blocker. |
| `tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs` | Lines 283-290 | ExecutionId excluded from no-collapse assertions without asserting it actually differs per call | Info | Minor proof gap per IN-02 — not a false pass; Count==2 is still the primary no-collapse assertion. Not a blocker. |

No blockers remain after ca7177b.

---

## Human Verification Required

None. All verification is programmatic (reflection, source scan, harness assertions). The phase's scope is hermetic-only; live/real-stack proof is deferred to Phase 49 (TEST-01..03) by design and is out of scope for this phase.

---

## Gap Closure Summary

**One gap (WR-01) was found in initial verification (status: gaps_found, score: 2/3) and is now closed.**

**WR-01 — Root cause:** `RecoveryDeadLetterFacts.EmptyMux()` stubbed `db.StringGetAsync(...)` to return `RedisValue.Null`, but `ReinjectConsumer.HandleAsync` (line 33 of `ReinjectConsumer.cs`) gates the data-gone terminal on `StringLengthAsync`, not `StringGetAsync`. The test passed because NSubstitute returned `0L` (the C# default for `Task<long>`) for the unstubbed `StringLengthAsync` call — an accidental pass.

**Fix (commit ca7177b):** Added `db.StringLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(0L);` to `EmptyMux()` (line 45 of RecoveryDeadLetterFacts.cs). The `StringGetAsync` stub was retained for internal consistency. The docstring on `EmptyMux()` was updated to explicitly document the rationale. The fix is a net-additive change (6 insertions, 1 deletion) with no production source changes.

**Verification of the fix:** The explicit `StringLengthAsync -> 0L` stub directly targets the production gate at `ReinjectConsumer.cs:33` (`await Db.StringLengthAsync(L2ProjectionKeys.ExecutionData(m.EntryId)) == 0`). The `PresentMux()` helper in the same file has always stubbed `StringLengthAsync -> 7L` symmetrically. The absent-key proof is now by explicit expression, not mock default. SC-3 is fully verified.

---

_Verified: 2026-06-09T10:55:00Z_
_Verifier: Claude (gsd-verifier)_
_Re-verification: gap WR-01 closed by commit ca7177b_
