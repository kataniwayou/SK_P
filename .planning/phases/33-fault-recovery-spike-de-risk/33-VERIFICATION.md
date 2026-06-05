---
phase: 33-fault-recovery-spike-de-risk
verified: 2026-06-05T12:45:00Z
status: human_needed
score: 6/8 must-haves verified (2 operator-gated)
overrides_applied: 0
human_verification:
  - test: "Run the live FaultRecoverySpikeE2ETests against rebuilt v3.7.0 compose stack"
    expected: "Both Fault<EntryStepDispatch> + Fault<ExecutionResult> captured (>= 1 each); Fault<StartOrchestration>/Fault<StopOrchestration> produce ZERO captures; captured H matches a-priori ComputeH; new skp:data:* / advance effect appears at the correct origin endpoint; CountEsHitsAsync == 1 (not 2) over 8s+ settle window"
    why_human: "Requires full v3.7.0 compose stack with REBUILT processor-sample/orchestrator/baseapi-service containers (embedded SourceHash must match host build). Not runnable in a non-interactive executor. Per the Phase-31/31.1/32/32.1 precedent this is an operator gate."
  - test: "Run scripts/phase-33-close.ps1 against live stack after the spike test passes"
    expected: "GATE_EXIT=0: both build configs zero-warning, 3x consecutive GREEN full RealStack run, redis/psql/rabbitmq triple-SHA BEFORE==AFTER (net-zero). Read GATE_*_EXIT from gate output, NOT the bg-task wrapper exit."
    why_human: "Requires the full live infra stack. The script is authored and ParseFile-clean but the triple-SHA comparison cannot be exercised without the running containers."
---

# Phase 33: Fault-Recovery Spike De-Risk — Verification Report

**Phase Goal:** De-risk the v3.7.0 Keeper milestone's load-bearing fault-recovery contract (bind Fault<EntryStepDispatch>/Fault<ExecutionResult> -> double-.Message unwrap of the 6-id+H tuple -> verbatim re-inject-by-type -> Phase-31 flag[H] duplicate-collapse, plus the negative command-fault proof) with a standing RealStack regression guard, an authored net-zero close gate, and a recorded D-10 _error retention decision.
**Verified:** 2026-06-05T12:45:00Z
**Status:** human_needed
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Spike test file exists, compiles clean (Release 0/0), RealStack-tagged, excluded from hermetic suite (zero regression, 447/0) | VERIFIED | File at `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs` (819 lines). `[Trait("Category","RealStack")]` confirmed at line 55. Commits b21d3a6/9d9bfd4/a56e655 each show 0/0 build claims; hermetic 447/0 confirmed in Task 3 commit message. |
| 2 | File binds IConsumer<Fault<EntryStepDispatch>> AND IConsumer<Fault<ExecutionResult>> on a short-lived in-test IBusControl (INTAKE-01 bind half, authored) | VERIFIED | `FaultDispatchProbe : IConsumer<Fault<EntryStepDispatch>>` (line 301) and `FaultResultProbe : IConsumer<Fault<ExecutionResult>>` (line 316) confirmed. Short-lived bus Start/try/finally Stop bracket at lines 165-268. |
| 3 | Each fault-probe body unwraps inner message via context.Message.Message (double .Message) and captures all 6 IExecutionCorrelated ids + H (INTAKE-02) | VERIFIED | 4 occurrences of `context.Message.Message` found (2 per probe). Captured tuple fields m.H, m.CorrelationId, m.WorkflowId, m.StepId, m.ProcessorId, m.EntryId, m.ExecutionId all confirmed present. |
| 4 | Test computes dispatch H and resultH a-priori via MessageIdentity, arms WRONGTYPE poison, re-injects extracted Fault<T>.Message verbatim by type via GetSendEndpoint+Send, registers every poison/data/flag key in L2KeysToCleanup (INTAKE-04 + PROBE-06 + net-zero, authored) | VERIFIED | `ArmWrongTypePoisonAsync` + `ListRightPushAsync` present. HashBlob/HashManifest/ComputeH all confirmed. `GetSendEndpoint(new Uri($"queue:{dispatchInner.ProcessorId:D}"))` and `GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"))` confirmed. 6 `L2KeysToCleanup.Add` calls found (>= 3 required). D-06 comment and `PublishSyntheticResultFaultAsync` stub present. `PrewriteFlagPendingAsync` called once (exactly once before two sends). `CountEsHitsAsync == 1` assertion confirmed. Negative proof via `Fault<StartOrchestration>` + `Fault<StopOrchestration>` Publish confirmed. Net-zero teardown: `orchestration/stop` POST + `ScanKeys` registration loop present. |
| 5 | scripts/phase-33-close.ps1 exists, ParseFile-clean (exit 0), BOM-free, no FLUSHDB, no skp:cancelled scan-clean | VERIFIED | File at `scripts/phase-33-close.ps1` (340 lines). First 3 bytes: 0x23 0x20 0x50 (not BOM). FLUSHDB count: 0. `skp:cancelled` count: 0. ParseFile returns 0 errors. |
| 6 | Close gate carries correct grep profile: redis-cli --scan == 12, v3.7.0 == 6, v3.6.0/v3.6.1 == 0, header "Phase 33 close gate", single phase-32.1-close provenance line | VERIFIED | `redis-cli --scan`: 12. `v3.7.0`: 6. `v3.6.0 or v3.6.1`: 0. Header line 1: "# Phase 33 close gate — v3.7.0 (triple-SHA)". Phase 32.1/phase-32.1-close references: 1 (the single provenance comment). Phase-32.1-close.ps1 still present in git ls-files (ADD, not rename). |
| 7 | Against the rebuilt v3.7.0 compose stack: live FaultRecoverySpikeE2ETests proves both Fault<T> types captured, 6-id+H unwrapped, re-inject-by-type produces effect exactly once, duplicate collapses (CountEsHits==1), close gate holds triple-SHA BEFORE==AFTER | NEEDS HUMAN | Operator gate — full live compose stack with REBUILT containers required. Runbook documented in 33-02-SUMMARY.md Pending-Verification section. |
| 8 | D-10 {procId}_error retention decision recorded (TTL'd forensic, source-agnostic DLQ-1 in Phase 36) in SUMMARY; no _error topology change in this phase (D-11: no metric work) | VERIFIED | D-10 decision recorded verbatim in 33-02-SUMMARY.md lines 82-84. No `src/` changes in any of the 4 task commits (D-03 held). |

**Score:** 6/8 truths fully verified (2 operator-gated, classified as human_needed per established spike precedent)

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs` | RealStack spike: dual Fault<T> capture + WRONGTYPE dispatch/result trips + verbatim re-inject + duplicate-collapse + negative command-fault proof | VERIFIED | 819 lines, contains all required patterns. |
| `scripts/phase-33-close.ps1` | v3.7.0 triple-SHA net-zero close gate (clone of phase-32.1-close.ps1, relabeled 32.1->33) | VERIFIED | 340 lines, ParseFile-clean, BOM-free, all acceptance greps pass. |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| FaultDispatchProbe / FaultResultProbe | context.Message.Message | double .Message unwrap into 6-id + H capture tuple | WIRED | 4 occurrences of `context.Message.Message` confirmed (2 per probe class, lines 305 and 321). |
| FaultRecoverySpikeE2ETests re-inject (dispatch) | queue:{dispatchInner.ProcessorId:D} | bus.GetSendEndpoint(...).Send(dispatchInner) | WIRED | `GetSendEndpoint(new Uri($"queue:{dispatchInner.ProcessorId:D}"))` at line 204. |
| FaultRecoverySpikeE2ETests re-inject (result) | queue:{OrchestratorQueues.Result} | bus.GetSendEndpoint(...).Send(resultInner) | WIRED | `GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"))` at line 232. |
| FaultRecoverySpikeE2ETests poison + teardown | factory.L2KeysToCleanup | every armed poison/data/flag key registered | WIRED | 6 `L2KeysToCleanup.Add` calls confirmed (dispatch poison key, result poison key, wfId + wfId:stepId scaffold keys, plus scan-registered run keys). |
| phase-33-close.ps1 | live RealStack stack (redis/psql/rabbitmq) | 3x GREEN full-suite + unfiltered triple-SHA BEFORE==AFTER | AUTHORED / OPERATOR-GATED | Script authored and ParseFile-clean. Live execution requires full compose stack — operator gate per Phase-31/32 precedent. |

---

### Data-Flow Trace (Level 4)

Not applicable — `FaultRecoverySpikeE2ETests.cs` is a RealStack test artifact (not a production component rendering dynamic data). The data-flow is the live test itself, which is operator-gated. The close-gate script is a tooling artifact with no data rendering. Level 4 skipped for both artifacts.

---

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Solution compiles Release 0/0 | `dotnet build SK_P.sln -c Release` | Claimed 0/0 in all three Plan 01 task commits (b21d3a6, 9d9bfd4, a56e655) | SKIP — requires dotnet SDK in verifier environment; commit messages and SUMMARY self-check confirm |
| Hermetic suite 447/0 with RealStack test excluded | `dotnet test -- --filter-not-trait "Category=RealStack"` | Claimed 447/0, 0 regression in Task 3 commit message | SKIP — requires test runner; SUMMARY self-check confirms |
| close-gate ParseFile exit 0 | `pwsh -NoProfile -Command "[Parser]::ParseFile(...)"` | 0 parse errors | PASS — verified programmatically above |
| RealStack spike test GREEN | `dotnet test -- --filter-class "*FaultRecoverySpikeE2ETests"` | Requires live stack | SKIP (operator gate) |

---

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| INTAKE-01 | 33-01, 33-02 | Keeper recovers off Fault<T> events — IConsumer<Fault<EntryStepDispatch>> + IConsumer<Fault<ExecutionResult>>; Fault<StartOrchestration>/Fault<StopOrchestration> NOT consumed | AUTHORED — live gate pending | Dual probe classes present and wired. Negative D-09 proof authored (Publish Fault<Start/StopOrchestration>, assert ZERO captures). LIVE proof is operator-gated. |
| INTAKE-02 | 33-01, 33-02 | Extract original message + full 6-id IExecutionCorrelated tuple + H from Fault<T>.Message | AUTHORED — live gate pending | double `.Message` unwrap present in both probes. All 6 ids + H confirmed in capture tuple. LIVE proof is operator-gated. |
| INTAKE-04 | 33-01, 33-02 | Re-inject original message to origin endpoint by type — queue:{processorId:D} for dispatch, queue:orchestrator-result for result | AUTHORED — live gate pending | Both `GetSendEndpoint` + `Send` re-inject paths confirmed. Verbatim extracted instance forwarded. LIVE proof is operator-gated. |
| PROBE-06 | 33-01, 33-02 | Keeper performs no flag[H] dedup of its own — re-injection idempotency rides receiver's Phase-31 gate; duplicate re-inject collapses at processor or orchestrator | AUTHORED — live gate pending | One `PrewriteFlagPendingAsync` seed, Send/PollForFlagAck/Send pattern, `CountEsHitsAsync == 1` assertion all confirmed. LIVE proof is operator-gated. |

All 4 requirement IDs declared in the PLAN frontmatter are accounted for. REQUIREMENTS.md traceability table maps all 4 to Phase 33 (status "Authored (33-01) — live gate 33-02"). No orphaned requirements found for Phase 33 in REQUIREMENTS.md.

---

### Anti-Patterns Found

| File | Pattern | Severity | Assessment |
|------|---------|----------|------------|
| `FaultRecoverySpikeE2ETests.cs` | `PublishSyntheticResultFaultAsync` method body uses `await bus.Publish(synthetic, ct)` without an Immediate(0) consuming endpoint registered in the in-test bus | Info | Intentional D-06 fallback stub — currently UNUSED (marked with xmldoc). The live `TripResultFaultAsync` path is primary. No impact on authored behavior. |
| `FaultRecoverySpikeE2ETests.cs` | `PollForNewKeyAsync` helper is defined but not called in the final test body | Info | Inherited from the clone source rig; kept for parity. No behavioral impact on the spike's actual test logic. Not a stub — the test body uses `ScanKeys` + `PollForCaptureAsync` patterns instead. |

No blockers. No `TODO`/`FIXME`/placeholder comments in load-bearing code paths. No `return null` / empty implementation stubs that affect the actual test flow. No `Cancelled`/`WorkflowCancelled`/`CancelledMarkerValue` references (reverted symbols grep == 0). No `src/` modifications in any of the 4 task commits (D-03 held).

---

### Human Verification Required

#### 1. Live FaultRecoverySpikeE2ETests Run

**Test:** Rebuild the three containers, then run the spike test against the live v3.7.0 compose stack.

```
docker compose up -d --build processor-sample orchestrator baseapi-service
dotnet test tests/BaseApi.Tests -- --filter-class "*FaultRecoverySpikeE2ETests"
```

**Expected:**
- Both `Fault<EntryStepDispatch>` and `Fault<ExecutionResult>` captured (capture count >= 1 each) — INTAKE-01 bind.
- Published `Fault<StartOrchestration>` / `Fault<StopOrchestration>` produce ZERO captures over the 8s settle window — INTAKE-01 negative (D-09).
- Captured tuple's 6 ids all non-empty + H matches a-priori ComputeH(...) — INTAKE-02.
- A new `skp:data:*` (dispatch) / advance effect (result) appears for the re-injected identity at the correct origin endpoint — INTAKE-04.
- `CountEsHitsAsync == 1` (NOT 2) over the 8s+ settle window — PROBE-06 collapse.

**Pitfall 1 note:** If the live result-trip Pitfall-1 window proves fragile (captures a `Fault<EntryStepDispatch>` when expecting `Fault<ExecutionResult>`), switch `TripResultFaultAsync` to the committed D-06 synthetic-fallback helper (`PublishSyntheticResultFaultAsync`). The dispatch trip carries the novel risk independently; record which path was used.

**Why human:** Requires the full v3.7.0 compose stack with REBUILT processor-sample/orchestrator/baseapi-service containers (embedded SourceHash must match host build — Pitfall 5). Not runnable in a non-interactive executor. Per the Phase-31/31.1/32/32.1 precedent this is an operator gate.

#### 2. Phase-33 Close Gate

**Test:** After the spike test passes, run the close gate.

```
pwsh -NoProfile -File ./scripts/phase-33-close.ps1
```

**Expected:** `GATE_EXIT=0` — both build configs zero-warning, 3x consecutive GREEN full RealStack run + redis/psql/rabbitmq triple-SHA BEFORE==AFTER (net-zero). Read `GATE_*_EXIT` from the gate output, NOT the bg-task wrapper exit (MEMORY note).

**Failure triage (per 33-02-SUMMARY.md Pending-Verification):**
- Redis SHA mismatch: check spike's `L2KeysToCleanup` drained — every armed WRONGTYPE poison + run-minted `skp:data:*`/`skp:flag:*` registered; settle-drain waits <= 330s.
- Stale container SourceHash: rebuild `processor-sample orchestrator baseapi-service` and re-run.
- Prior-phase stale/flaky ES assertion: triage per close-gate cadence notes; RED is usually a prior-phase stale ES assertion / flaky race, not a current-phase bug.

**Why human:** Requires the full live infra stack. Triple-SHA BEFORE==AFTER comparison cannot be exercised without running containers.

---

### Gaps Summary

No gaps. All autonomously-verifiable must-haves are verified. The two operator-gated must-haves (live spike execution + close gate triple-SHA) are correctly classified as `human_needed` per the established Phase-31/31.1/32/32.1 de-risk spike precedent. The authored artifact (FaultRecoverySpikeE2ETests.cs, 819 lines) is substantive, wired, and contains all required patterns. The close gate (phase-33-close.ps1, 340 lines) is ParseFile-clean, BOM-free, and passes all acceptance greps. D-03 (no src/ changes) held across all 4 task commits. D-10 _error retention decision is recorded in 33-02-SUMMARY.md.

---

_Verified: 2026-06-05T12:45:00Z_
_Verifier: Claude (gsd-verifier)_
