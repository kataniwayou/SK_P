---
status: passed
phase: 33-fault-recovery-spike-de-risk
source: [33-VERIFICATION.md]
started: "2026-06-05T09:18:56Z"
updated: "2026-06-05T10:30:00Z"
---

## Current Test

[all items passed — operator gate satisfied live against the rebuilt v3.7.0 compose stack]

## Tests

### 1. Live FaultRecoverySpikeE2ETests run (against rebuilt v3.7.0 compose stack)
expected: Both Fault<EntryStepDispatch> + Fault<ExecutionResult> captured (>= 1 each); Fault<StartOrchestration>/Fault<StopOrchestration> produce ZERO captures; captured tuple's 6 ids non-empty + H matches a-priori ComputeH(...); a new skp:data:* / advance effect appears at the correct origin endpoint; CountEsHitsAsync == 1 (not 2) over the 8s+ settle window.
result: PASSED (2026-06-05). `dotnet test -- --filter-class "*FaultRecoverySpikeE2ETests"` GREEN (1/1) against the rebuilt stack (processor-sample rebuilt; SourceHash matched). Two trip recipes were corrected first (commit c2d6ea6, test-only, no src/ changes): (a) the dispatch trip now poisons the dedup-gate `flag[dispatch.H]` GET — Redis SET overwrites any type with a string (no WRONGTYPE), only the GET on a list trips; (b) the RESULT trip used the **D-06 synthetic fallback** (`PublishSyntheticResultFaultAsync`) because the spike does not start orchestration, so the orchestrator owns no L1 entry and ResultConsumer takes its L1-miss business-ack before/at the `flag[m.H]` gate — the live result trip is unwinnable here. Dispatch trip + collapse proven live; result type bind + double-`.Message` unwrap + re-inject-by-type proven via synthetic; PROBE-06 `flag[H]` collapse proven live on the dispatch hop. Result-trip path used: **D-06 synthetic fallback**.

### 2. Phase-33 close gate (net-zero triple-SHA)
expected: GATE_EXIT=0 — both build configs zero-warning, 3x consecutive GREEN full RealStack run, redis/psql/rabbitmq triple-SHA BEFORE==AFTER (net-zero).
result: PASSED (2026-06-05). `pwsh -NoProfile -File ./scripts/phase-33-close.ps1` → **GATE_EXIT=0**. 453 facts GREEN ×3 (Run 1/2/3 Exit=0, ~6 min each); Release+Debug builds 0-warning; triple-SHA BEFORE==AFTER HELD: psql `\l`=`34ac23852ac61a2fa704e7a1a30b7f85821490b0c4d0924c28b3c0dd530ca911`, redis `--scan`=`4c4187d8db6de714c337930288ef4fcf9bfa2266864a73607880320fa3e09420`, rabbitmqctl `list_queues`=`e77637a22ea726c2c9dd5a8463b999347e8809a24ab0d55291eac6fc8f089b06`. Net-zero held.

## Summary

total: 2
passed: 2
issues: 0
pending: 0
skipped: 0
blocked: 0

## Gaps
