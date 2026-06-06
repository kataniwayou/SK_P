---
status: partial
phase: 37-orchestrator-pause-resume-coordination
source: [37-VERIFICATION.md]
started: 2026-06-06T00:00:00Z
updated: 2026-06-06T00:00:00Z
---

## Current Test

[awaiting human testing — live stack required, routed to the Phase-39 close gate]

## Tests

### 1. Live Keeper→Orchestrator pause/resume bus round-trip
expected: On an L2 (Redis) outage that produces a Fault<EntryStepDispatch>/Fault<ExecutionResult>, Keeper publishes PauseWorkflow → the orchestrator's Quartz job enters Paused state (GetTriggerState(TriggerKey(jobId)) == Paused). On L2 recovery Keeper publishes ResumeWorkflow → the orchestrator deletes the stale job and reschedules a fresh from-now trigger off the L1 cron → Normal trigger with a future StartAt. No duplicate fires.
why_human: Requires a live RabbitMQ + Redis + rebuilt Keeper+Orchestrator containers (embedded SourceHash must match). Cannot be asserted programmatically without the running stack. Phase-39 close-gate signal per the Phase-35/36 precedent.
result: [pending]

### 2. GaveUp path: workflow stays paused after Keeper parks to keeper-dlq, publishes no Resume
expected: With Redis kept down past Keeper MaxAttempts, the original Fault<T> envelope lands in keeper-dlq (depth = 1), NO ResumeWorkflow is published, and the orchestrator's workflow trigger remains Paused (GetTriggerState(TriggerKey(jobId)) == Paused) — the workflow is stranded until an operator intervenes. keeper_dlq_pushed counter increments (Phase-39 metric).
why_human: Same live-stack dependency. The hermetic test GaveUp_PublishesPause_ButNoResume proves the publish behavior; the real-orchestrator Quartz end-state and the keeper-dlq queue depth require the live environment.
result: [pending]

## Summary

total: 2
passed: 0
issues: 0
pending: 2
skipped: 0
blocked: 0

## Gaps
