---
phase: 47-dlq-consolidation-at-least-once-semantics
verified: 2026-06-09T00:00:00Z
status: gaps_found
score: 2/3 must-haves verified
overrides_applied: 0
gaps:
  - truth: "The existing RecoveryDeadLetterFacts.DataGone_reinject_faults_and_routes_to_dead_letter fact proves (hermetically) that the REINJECT-data-gone case terminates deterministically at skp-dlq-1"
    status: partial
    reason: "WR-01 (from 47-REVIEW.md, independently confirmed): EmptyMux() stubs StringGetAsync but ReinjectConsumer.HandleAsync gates the data-gone terminal on StringLengthAsync (line 33 of ReinjectConsumer.cs). NSubstitute returns the Task<long> default (0L) for the unstubbed StringLengthAsync, so == 0 is true and RecoveryDataGoneException fires — but by mock default, not by an explicit absent-key expression. The StringGetAsync stub is dead code: deleting it would not change the test outcome. The test passes for the wrong mechanical reason."
    artifacts:
      - path: "tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs"
        issue: "EmptyMux() at line 38-45 stubs StringGetAsync but production code (ReinjectConsumer.cs:33) calls StringLengthAsync. The data-gone condition fires accidentally via the NSubstitute Task<long> default (0L), not via an explicit stub expressing the absent-key state."
    missing:
      - "Stub the method the consumer actually calls: db.StringLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(0L); in EmptyMux(). This makes the data-gone condition explicit rather than inherited from mock default, and the StringGetAsync stub can be removed or left (it is harmless)."
---

# Phase 47: DLQ Consolidation & At-Least-Once Semantics — Verification Report

**Phase Goal:** Every terminal give-up across the processor and the Keeper (a send exception with its retry loop exhausted; a Keeper L2 op exhausted) routes to a single consolidated `_DLQ1`, and the whole execution path is at-least-once with no dedup/idempotency key — duplicate effects are tolerated downstream by construction.
**Verified:** 2026-06-09
**Status:** gaps_found
**Re-verification:** No — initial verification

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
| SC-1 | A single consolidated `_DLQ1` receives every terminal send/L2 give-up from the processor and the Keeper — no separate `keeper-dlq`, wired once, proven structurally | VERIFIED | `ProcessorSendExhaustion_RoutesToDlq1` (KeeperDlqConsolidationTests) + `No_v4_give_up_path_references_keeper_dlq` (AtLeastOnceStructuralFacts) + cited `Dlq1_Consolidated` / `Keeper_SendFault_RetriesToDlq1` (Phase 36). All confirmed present in source. |
| SC-2 | Execution path carries no dedup/idempotency key; a redelivered message reproduces its effect without collapse | VERIFIED | `No_dedup_machinery_on_execution_path` (reflection guard over Orchestrator + BaseProcessor.Core assemblies). `Duplicate_StepCompleted_reproduces_effect_no_collapse` (ONE dispatcher, Assert.Equal(2, Calls.Count)). `Duplicate_Reinject_reproduces_effect_no_collapse` (ONE consumer, Assert.Equal(2, send.Sent.Count)). All confirmed in source. |
| SC-3 | The REINJECT-data-gone case terminates deterministically at `_DLQ1` for operator triage rather than looping — proven by test | PARTIAL | `DataGone_reinject_faults_and_routes_to_dead_letter` exists, carries [Trait("Phase","47")], and asserts RecoveryDataGoneException + ConsolidatedFault. However: the `EmptyMux()` helper stubs `StringGetAsync` while production code (`ReinjectConsumer.cs:33`) gates the data-gone terminal on `StringLengthAsync`. The test passes via NSubstitute's Task<long> default (0L) for the unstubbed method — an accidental pass, not an explicit proof of the absent-key condition. The proof of SC-3 is therefore structurally present but mechanically unsound. |

**Score:** 2/3 truths fully verified (SC-3 partial — accidental-pass on data-gone gate)

---

## Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` | Processor send-exhaustion -> skp-dlq-1 sibling [Fact] containing `ConsolidatedErrorTransportFilter.Dlq1` | VERIFIED | File exists. `ProcessorSendExhaustion_RoutesToDlq1` fact present at line 225. `BuildProcessorHarness` variant present. `[Trait("Phase","47")]` on the new fact. No `"skp-dlq-1"` literal in the new fact body — uses `ConsolidatedErrorTransportFilter.Dlq1` const. `ProcessorSendExhaustedConsumer` throwaway consumer present. |
| `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs` | Reflection no-dedup guard (FACT A) + source-scan no-keeper-dlq guard (FACT B) | VERIFIED | File exists (new namespace `BaseApi.Tests.Resilience`). FACT A: reflects over `typeof(StepDispatcher).Assembly` + `typeof(ProcessorPipeline).Assembly`, asserts no `MessageIdentity` type and no `MessageIdentity` property/field. FACT B: resolves repo root via `[CallerFilePath]`, asserts both scoped dirs exist before scanning, excludes `KeeperRecoveryHandler.cs` by exact filename, scans for both `KeeperQueues.DeadLetter` and `"keeper-dlq"`. Both facts carry `[Trait("Phase","47")]`. |
| `tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs` | StepCompleted duplicate-delivery no-collapse fact (R3) containing `Assert.Equal(2` | VERIFIED | File exists. `Duplicate_StepCompleted_reproduces_effect_no_collapse` fact present at line 244. ONE `RecordingDispatcher`, ONE `StepCompletedConsumer`, `consumer.Consume(...)` called twice with the SAME `msg` instance. `Assert.Equal(2, dispatcher.Calls.Count)` at line 281. Both calls assert same `nextStepId`/`nextProcessorId`/`entryId`. `[Trait("Phase","47")]` present. |
| `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs` | EntryStepDispatch/KeeperReinject duplicate-delivery fact (R3) + Phase-47 trait on DataGone fact (R2) | PARTIAL | File exists. `Duplicate_Reinject_reproduces_effect_no_collapse` present: ONE `ReinjectConsumer` + `CapturingSendProvider`, same `msg` Consumed twice, `Assert.Equal(2, send.Sent.Count)` at line 169. `[Trait("Phase","47")]` on both new and DataGone facts. HOWEVER: `EmptyMux()` at lines 38-45 stubs `StringGetAsync` while production gates on `StringLengthAsync` — data-gone test passes accidentally (WR-01). |
| `.planning/phases/47-dlq-consolidation-at-least-once-semantics/47-DLQ-AUDIT.md` | Traceability ledger with RESIL-02 and RESIL-03 rows, all three SCs, no unproven row | VERIFIED | File exists. 8-row table present. RESIL-02 covered by 5 rows (SC-1: 4 rows, SC-3: 1 row). RESIL-03 covered by 3 rows (SC-2: 3 rows). All three SCs covered. Every proving-test method name matches a real fact confirmed in source. No row contains "unproven"/"TODO"/"TBD". Runner-correct verify commands present. |
| `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` | Dated at-least-once guarantee amendment (A16) citing 47-DLQ-AUDIT.md + KeeperReinject.Payload note | VERIFIED | A16 amendment line present at line 5 (after A15). Explicitly names the at-least-once/no-dedup guarantee. Cross-references lines 5/105/112. Cites `47-DLQ-AUDIT.md`. Records `KeeperReinject` carries `Payload : string`. Additive only (1 insertion, 0 deletions confirmed in summary). |

---

## Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `AtLeastOnceStructuralFacts.No_v4_give_up_path_references_keeper_dlq` | `src/BaseProcessor.Core/Processing/` + `src/Keeper/Recovery/` | `Directory.EnumerateFiles` excluding `KeeperRecoveryHandler.cs` | WIRED | Both directory assertions present (T-47-01 guard). Exclusion `Path.GetFileName(f) != "KeeperRecoveryHandler.cs"` present at line 99. Scans for both `KeeperQueues.DeadLetter` and `"keeper-dlq"`. |
| `AtLeastOnceStructuralFacts.No_dedup_machinery_on_execution_path` | Orchestrator + BaseProcessor.Core assemblies | `typeof(StepDispatcher).Assembly` / `typeof(ProcessorPipeline).Assembly` | WIRED | Both `typeof(...)` anchors present at lines 25-28. `Assert.DoesNotContain` for type name at line 54. `GetProperties`/`GetFields` member sweep at lines 61-62. |
| `Duplicate_StepCompleted_reproduces_effect_no_collapse` | `StepCompletedConsumer` + `RecordingDispatcher` | double-Consume of SAME message into ONE dispatcher | WIRED | One `RecordingDispatcher` created at line 272. Two `consumer.Consume(...)` calls at lines 277-278 with same `msg`. `Assert.Equal(2, dispatcher.Calls.Count)` at line 281. |
| `DataGone_reinject_faults_and_routes_to_dead_letter` | `ReinjectConsumer` data-gone gate | `EmptyMux()` -> `StringLengthAsync == 0` | PARTIAL (WR-01) | `EmptyMux()` stubs `StringGetAsync` (dead). Production gates on `StringLengthAsync`. Test passes via NSubstitute `Task<long>` default (0L). Functionally green but mechanically unsound proof. |
| `47-DLQ-AUDIT.md` rows | Green Phase-47 facts (and cited Phase-36/46 facts) | file:method citation per row | WIRED | All 8 method names confirmed present in `tests/BaseApi.Tests/`. Phase-47 filter covers 6/6 tagged facts. Two cited-existing Phase-36 facts separately confirmed in KeeperDlqConsolidationTests. |
| Design-doc A16 amendment | `47-DLQ-AUDIT.md` | cross-reference citation | WIRED | "47-DLQ-AUDIT.md" appears in A16 amendment line (design doc line 5). |

---

## Data-Flow Trace (Level 4)

Not applicable — this phase produces no data-rendering components. All deliverables are test facts and documentation. Level 4 data-flow tracing is N/A.

---

## Behavioral Spot-Checks

These checks verify whether the Phase-47 tests are discoverable and structurally present (build verification is doc-only for spot checks; actual test execution is attested by summary evidence only).

| Behavior | Evidence | Status |
|----------|----------|--------|
| 6 facts tagged `[Trait("Phase","47")]` | Confirmed in source: `ProcessorSendExhaustion_RoutesToDlq1`, `No_dedup_machinery_on_execution_path`, `No_v4_give_up_path_references_keeper_dlq`, `DataGone_reinject_faults_and_routes_to_dead_letter`, `Duplicate_StepCompleted_reproduces_effect_no_collapse`, `Duplicate_Reinject_reproduces_effect_no_collapse` | PASS |
| `EmptyMux()` stubs the correct Redis method | FAIL — stubs `StringGetAsync`; production uses `StringLengthAsync` | FAIL |
| `Duplicate_Reinject` uses ONE consumer + double-Consume | Confirmed: `new ReinjectConsumer(...)` once, `consumer.Consume(ContextFor(msg, ct))` twice at lines 164-165 | PASS |
| `Duplicate_StepCompleted` uses ONE dispatcher + double-Consume | Confirmed: `new RecordingDispatcher()` once at line 272, `consumer.Consume(...)` twice at lines 277-278 | PASS |
| `No_v4_give_up_path_references_keeper_dlq` excludes exactly `KeeperRecoveryHandler.cs` | Confirmed: `Path.GetFileName(f) != "KeeperRecoveryHandler.cs"` at line 99; no other exclusions | PASS |
| Build 0/0 | Attested in 47-01-SUMMARY, 47-02-SUMMARY, 47-03-SUMMARY | PASS (summary evidence) |

---

## Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|-------------|-------------|--------|----------|
| RESIL-02 | 47-01-PLAN, 47-02-PLAN, 47-03-PLAN | Processor and Keeper terminal give-ups route to a single consolidated `_DLQ1` | SATISFIED (with caveat) | `ProcessorSendExhaustion_RoutesToDlq1` (processor give-up -> skp-dlq-1), `No_v4_give_up_path_references_keeper_dlq` (structural no-keeper-dlq guard), cited `Dlq1_Consolidated`/`Keeper_SendFault_RetriesToDlq1` (generic exhaustion). Data-gone terminal (R2) is present but the test proof is mechanically unsound (WR-01 accidental-pass). |
| RESIL-03 | 47-01-PLAN, 47-02-PLAN, 47-03-PLAN | Execution path is at-least-once with no dedup/idempotency key; duplicate effects tolerated | SATISFIED | `No_dedup_machinery_on_execution_path` (reflection guard — no MessageIdentity type/member on execution-path assemblies), `Duplicate_StepCompleted_reproduces_effect_no_collapse` (Count==2, no collapse), `Duplicate_Reinject_reproduces_effect_no_collapse` (Count==2, no collapse). |

**REQUIREMENTS.md traceability check:** RESIL-02 and RESIL-03 are both mapped to Phase 47 (`RESIL-02 | Phase 47 | Pending`, `RESIL-03 | Phase 47 | Pending`). Both are covered by facts in this phase. No orphaned requirements.

---

## Anti-Patterns Found

| File | Location | Pattern | Severity | Impact |
|------|----------|---------|----------|--------|
| `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs` | Lines 38-45 (`EmptyMux()`) | Stubs `StringGetAsync` but production gates on `StringLengthAsync` — the explicit stub is dead code; the data-gone condition fires via NSubstitute's Task<long> default (0L) | Blocker (SC-3 proof integrity) | `DataGone_reinject_faults_and_routes_to_dead_letter` passes for the wrong mechanical reason. The proof of SC-3 (REINJECT-data-gone terminates at _DLQ1) is not trustworthy as a regression guard: if production were refactored to gate on a non-zero-default method, the test would silently stop proving the invariant. |
| `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` | Line 207 (`Dlq_TopologyArgs`) | `Assert.Equal(604_800_000, (int)TimeSpan.FromDays(7).TotalMilliseconds)` asserts a property of TimeSpan, not the SUT; can never fail for a production reason | Warning | Tautology assertion — not a Phase 47 deliverable concern, pre-existing (Phase 36). Flagged for completeness per WR finding IN-01. |
| `tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs` | Lines 283-290 | ExecutionId excluded from no-collapse assertions without asserting it actually differs per call | Info | Minor proof gap per IN-02 — not a false pass; Count==2 is still the primary no-collapse assertion. |

---

## Human Verification Required

None. All verification is programmatic (reflection, source scan, harness assertions). The phase's scope is hermetic-only; live/real-stack proof is deferred to Phase 49 (TEST-01..03) by design and is out of scope for this phase.

---

## Gaps Summary

**One gap blocking full SC-3 proof integrity — WR-01 (confirmed independently):**

`RecoveryDeadLetterFacts.EmptyMux()` stubs `db.StringGetAsync(...)` to return `RedisValue.Null`, but `ReinjectConsumer.HandleAsync` (line 33 of `ReinjectConsumer.cs`) gates the data-gone terminal on `StringLengthAsync`, not `StringGetAsync`. The `StringGetAsync` stub is never called by the production path and is dead code. The test passes because NSubstitute returns `0L` (the C# default for `Task<long>`) for the unstubbed `StringLengthAsync` call, which satisfies the `== 0` predicate and causes `RecoveryDataGoneException` to be thrown — the correct behavior, but for the wrong reason.

**Why this matters for SC-3:** The success criterion requires that the REINJECT-data-gone case "terminates deterministically at `_DLQ1`". The test asserts this — and the assertion is true — but the proof is mechanically unsound. If `ReinjectConsumer` were ever refactored to use a different Redis method whose NSubstitute default is non-zero (or truthy), the test would silently stop proving the invariant. The `PresentMux()` helper in the same file (line 124-131) correctly stubs `StringLengthAsync -> 7L`, which makes the `EmptyMux` omission stand out as an oversight.

**Fix (one-line change):** In `EmptyMux()`, replace the `StringGetAsync` stub with:
```csharp
db.StringLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(0L);
```
The `StringGetAsync` line can be removed or left (it is harmless). This makes the data-gone condition explicit and the test a genuine proof of SC-3.

**WR-02 (noted, not a gap):** `Duplicate_Reinject_reproduces_effect_no_collapse` proves no-collapse on the happy single-attempt path. The no-dedup claim is real (there genuinely is no dedup key), but the docstring over-claims the redelivery-under-retry seam. This is a claim/proof scope mismatch, not a false pass, and does not block the phase goal. Tightening the docstring is a recommended improvement but is not required to close the phase.

---

_Verified: 2026-06-09_
_Verifier: Claude (gsd-verifier)_
