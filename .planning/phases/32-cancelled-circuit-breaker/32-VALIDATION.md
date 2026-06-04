---
phase: 32
slug: cancelled-circuit-breaker
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-04
---

# Phase 32 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from `32-RESEARCH.md` §"Validation Architecture". Task IDs below are
> requirement-anchored; the planner binds them to final `{32-PP-TT}` IDs.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (`tests/BaseApi.Tests`), Microsoft.Testing.Platform (MTP) runner; real-stack E2E in the same project (compose-backed) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` — MTP, so filters use `-- --filter-class "*Name"` (NOT VSTest `--filter`) |
| **Quick run command** | `dotnet test tests/BaseApi.Tests -- --filter-class "*<FactsClass>"` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests -- --filter-not-trait "Category=RealStack"` (hermetic) |
| **Phase gate** | `pwsh -NoProfile -File ./scripts/phase-32-close.ps1` — 3×GREEN + triple-SHA (psql \l + redis --scan + rabbitmqctl) BEFORE==AFTER |
| **Estimated runtime** | hermetic ~4 min full suite; single facts class ~seconds |

---

## Sampling Rate

- **After every task commit:** Run the matching hermetic facts class — `dotnet test tests/BaseApi.Tests -- --filter-class "*<Facts>"`
- **After every plan wave:** Run the full hermetic suite — `dotnet test tests/BaseApi.Tests -- --filter-not-trait "Category=RealStack"`
- **Before `/gsd-verify-work` / phase close:** `phase-32-close.ps1` 3×GREEN + triple-SHA held
- **Max feedback latency:** ~240 seconds (full hermetic suite)

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 32-W0-a | W0 | 0 | req-1 | — | breaker fires only at true exhaustion (no premature trip) | hermetic | `dotnet test tests/BaseApi.Tests -- --filter-class "*RetryAttemptNumberingFacts"` | ❌ W0 (Risk R1) | ⬜ pending |
| 32-W0-b | W0 | 0 | req-4 | — | fault carries WorkflowId (no halt of wrong/absent workflow) | hermetic (in-mem MT harness) | `... --filter-class "*FaultConsumerBindingFacts"` | ❌ W0 (Risk R2) | ⬜ pending |
| 32-T-req1 | TBD | 1 | req-1 | T-32-01 | business `ProcessAsync` throw stays Failed, never trips breaker | unit | `... --filter-class "*BreakerTriggerFacts"` | ❌ W0 | ⬜ pending |
| 32-T-req2 | TBD | 1 | req-2 | T-32-02 | marker set effect-first, no TTL (`TTL==-1`); Stop doesn't clear | unit | `... --filter-class "*CancelledMarkerFacts"` | ❌ W0 | ⬜ pending |
| 32-T-req3 | TBD | 1 | req-3 | T-32-03 | ack-and-discard for cancelled wf only; others unaffected; no rollback | unit | `... --filter-class "*CheckAndDropFacts"` | ❌ W0 | ⬜ pending |
| 32-T-req4 | TBD | 1 | req-4 | T-32-04 | resolve jobId from L1 + unschedule; absent-L1 + duplicate = idempotent no-op | unit (fake L1 + scheduler) | `... --filter-class "*FaultUnscheduleFacts"` | ❌ W0 | ⬜ pending |
| 32-T-req6 | TBD | 1 | req-6 | — | `EntryCondition==3` rejected by `IsInEnum`; `StepOutcome.Cancelled==3` kept | unit | `... --filter-class "*StepEntryConditionEnumFacts"` | ❌ W0 | ⬜ pending |
| 32-T-req6b | TBD | 1 | req-6 | — | no live step row has `EntryCondition==3` | data assertion (DB) | real-stack / migration check | ❌ W0 | ⬜ pending |
| 32-T-req7 | TBD | 1 | req-7 | — | dedup counters once per drop; `workflow_cancelled` once per trip; log has 4 ids; no `workflowId` label | unit (MeterListener + log capture) | `... --filter-class "*BreakerMetricsFacts"` | ❌ W0 | ⬜ pending |
| 32-T-req8u | TBD | 1 | req-8 | — | Fault path touches no `flag[H]`; 2 faults == 1 end-state | unit | `... --filter-class "*FaultIdempotencyFacts"` | ❌ W0 | ⬜ pending |
| 32-T-req5 | TBD | N | req-5 | — | no `Cancelled` ExecutionResult to `orchestrator-result`; `_error` still receives msg | real-stack E2E | `... --filter-class "*CancelledCircuitBreakerE2ETests"` | ❌ W-N | ⬜ pending |
| 32-T-req8e | TBD | N | req-8 | — | clear marker + re-Start re-fires the workflow live | real-stack E2E | `... --filter-class "*CancelledCircuitBreakerE2ETests"` | ❌ W-N | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

The two MassTransit-reality probes MUST land first — they unblock the breaker seam and the fault consumer:

- [ ] `RetryAttemptNumberingFacts` — pins the `GetRetryAttempt()` boundary value for an endpoint-level `Immediate(Limit)` policy (Risk R1). If it is NOT `== Limit`, escalate before building the breaker — do not silently change the SPEC's `== Limit`.
- [ ] `FaultConsumerBindingFacts` — proves `Fault<EntryStepDispatch>.Message.WorkflowId` round-trips through an in-memory MassTransit harness (Risk R2; D-06 assumption).
- [ ] `BreakerTriggerFacts`, `CancelledMarkerFacts`, `CheckAndDropFacts`, `FaultUnscheduleFacts`, `StepEntryConditionEnumFacts`, `BreakerMetricsFacts`, `FaultIdempotencyFacts` — hermetic stubs for req-1/2/3/4/6/7/8.
- [ ] **Close-gate teardown extension** — `phase-32-close.ps1` net-zero teardown MUST scan-clean the new `skp:cancelled:*` namespace. The marker has NO TTL (D-07), so it will not self-expire; without explicit deletion the redis triple-SHA drifts every gate run (the exact failure mode in MEMORY `reference_close_gate_container_rebuild_and_flag_churn`).
- [ ] No new framework install — xUnit + the existing real-stack harness already cover this surface.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live breaker trip → halt → manual resume round-trip | req-5, req-8 | Requires the full v3.6.0 compose stack up healthy with rebuilt processor/orchestrator/baseapi containers (embedded SourceHash must match); not observable in the dev harness | Operator: `docker compose up -d --build processor-sample orchestrator baseapi-service`; run `*CancelledCircuitBreakerE2ETests`; then `pwsh ./scripts/phase-32-close.ps1` (expect GATE_EXIT=0). Read GATE_*_EXIT from the gate output, not the bg-task wrapper exit. |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references (incl. the two R1/R2 probes + close-gate teardown)
- [ ] No watch-mode flags
- [ ] Feedback latency < 240s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
