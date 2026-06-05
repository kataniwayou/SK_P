---
status: partial
phase: 33-fault-recovery-spike-de-risk
source: [33-VERIFICATION.md]
started: "2026-06-05T09:18:56Z"
updated: "2026-06-05T09:18:56Z"
---

## Current Test

[awaiting human testing — operator gate, full v3.7.0 compose stack required]

## Tests

### 1. Live FaultRecoverySpikeE2ETests run (against rebuilt v3.7.0 compose stack)
expected: Both Fault<EntryStepDispatch> + Fault<ExecutionResult> captured (>= 1 each); Fault<StartOrchestration>/Fault<StopOrchestration> produce ZERO captures over the 8s settle window; captured tuple's 6 ids non-empty + H matches a-priori ComputeH(...); a new skp:data:* (dispatch) / advance effect (result) appears at the correct origin endpoint; CountEsHitsAsync == 1 (not 2) over the 8s+ settle window. Commands: `docker compose up -d --build processor-sample orchestrator baseapi-service` then `dotnet test tests/BaseApi.Tests -- --filter-class "*FaultRecoverySpikeE2ETests"`. If the result-trip Pitfall-1 window proves fragile, switch TripResultFaultAsync to the committed D-06 PublishSyntheticResultFaultAsync fallback and record which path was used.
result: [pending]

### 2. Phase-33 close gate (net-zero triple-SHA)
expected: GATE_EXIT=0 — both build configs zero-warning, 3x consecutive GREEN full RealStack run, redis/psql/rabbitmq triple-SHA BEFORE==AFTER (net-zero). Read GATE_*_EXIT from the gate output, NOT the bg-task wrapper exit. Command: `pwsh -NoProfile -File ./scripts/phase-33-close.ps1`. Failure triage in 33-02-SUMMARY.md Pending-Verification (drained L2KeysToCleanup; rebuilt container SourceHash; prior-phase stale/flaky ES).
result: [pending]

## Summary

total: 2
passed: 0
issues: 0
pending: 2
skipped: 0
blocked: 0

## Gaps
