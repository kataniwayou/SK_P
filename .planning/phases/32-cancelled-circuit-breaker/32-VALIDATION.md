---
phase: 32
slug: cancelled-circuit-breaker
status: validated-partial
nyquist_compliant: false
hermetic_complete: true
wave_0_complete: true
created: 2026-06-04
updated: 2026-06-05
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
| 32-W0-a | 32-01 | 1 | req-1 | — | breaker fires only at true exhaustion (no premature trip) | hermetic | `dotnet test tests/BaseApi.Tests -- --filter-class "*RetryAttemptNumberingFacts"` | ✓ Processor/RetryAttemptNumberingFacts.cs | ✅ green |
| 32-W0-b | 32-01 | 1 | req-4 | — | fault carries WorkflowId (no halt of wrong/absent workflow) | hermetic (in-mem MT harness) | `... --filter-class "*FaultConsumerBindingFacts"` | ✓ Orchestrator/FaultConsumerBindingFacts.cs | ✅ green |
| 32-T-req1 | 32-04 | 2 | req-1 | T-32-01 | business `ProcessAsync` throw stays Failed, never trips breaker | unit | `... --filter-class "*BreakerTriggerFacts"` | ✓ Processor/BreakerTriggerFacts.cs | ✅ green |
| 32-T-req2 | 32-04 | 2 | req-2 | T-32-02 | marker set effect-first, no TTL (`TTL==-1`); Stop doesn't clear | unit | `... --filter-class "*CancelledMarkerFacts"` | ✓ Processor/CancelledMarkerFacts.cs | ✅ green |
| 32-T-req2k | 32-02 | 1 | req-2 | T-32-07 | marker key shape `skp:cancelled:{id:D}` + sentinel const | unit | `... --filter-class "*CancelledMarkerKeyFacts"` | ✓ Contracts/CancelledMarkerKeyFacts.cs | ✅ green |
| 32-T-req3p | 32-04 | 2 | req-3 | T-32-03 | processor ack-and-discard for cancelled wf only; others unaffected; no rollback | unit | `... --filter-class "*CheckAndDropFacts"` | ✓ Processor/CheckAndDropFacts.cs | ✅ green |
| 32-T-req3o | 32-05 | 2 | req-3 | T-32-03 | orchestrator ack-and-discard cancelled result; dedup counter | unit | `... --filter-class "*ResultCheckAndDropFacts"` | ✓ Orchestrator/ResultCheckAndDropFacts.cs | ✅ green |
| 32-T-req4 | 32-06 | 2 | req-4 | T-32-04 | resolve jobId from L1 + unschedule; absent-L1 + duplicate = idempotent no-op | unit (fake L1 + scheduler) | `... --filter-class "*FaultUnscheduleFacts"` | ✓ Orchestrator/FaultUnscheduleFacts.cs | ✅ green |
| 32-T-req6 | 32-03 | 1 | req-6 | — | **RE-DISPOSITION:** original "`EntryCondition==3` rejected by `IsInEnum`" was the 32-03 removal — **CANCELLED BY USER (D-12 reversed), `PreviousCancelled=3` KEPT**. Surviving half "`StepOutcome.Cancelled==3` kept" is covered by existing `ExecutionResultContractTests`/`StepAdvancementTests`/`DispatchResultSendFacts` (`InlineData(StepOutcome.Cancelled, 3)`). `StepEntryConditionEnumFacts` deliberately deleted. | unit (existing) | `... --filter-class "*StepAdvancementTests"` | ✓ existing (StepEntryConditionEnumFacts removed) | ✅ green (surviving half) / N/A (removal cancelled) |
| 32-T-req6b | 32-07 | 3 | req-6 | — | no live step row has `EntryCondition==3` (data assertion) — now MOOT-but-asserted (3 is a valid inert member); covered live by E2E `SELECT COUNT(*) WHERE EntryCondition=3` | data assertion (DB) | real-stack (in `*CancelledCircuitBreakerE2ETests`) | ✓ authored (RealStack) | ⏳ operator-pending |
| 32-T-req7 | 32-02 | 1 | req-7 | — | dedup counters once per drop; `workflow_cancelled` once per trip; log has 4 ids; no `workflowId` label | unit (MeterListener + log capture) | `... --filter-class "*BreakerMetricsFacts"` | ✓ Orchestrator/BreakerMetricsFacts.cs | ✅ green |
| 32-T-req8u | 32-06 | 2 | req-8 | — | Fault path touches no `flag[H]`; 2 faults == 1 end-state | unit | `... --filter-class "*FaultIdempotencyFacts"` | ✓ Orchestrator/FaultIdempotencyFacts.cs | ✅ green |
| 32-T-req5 | 32-07 | 3 | req-5 | — | no `Cancelled` ExecutionResult to `orchestrator-result`; `_error` still receives msg | real-stack E2E | `... --filter-class "*CancelledCircuitBreakerE2ETests"` | ✓ authored (RealStack) | ⏳ operator-pending |
| 32-T-req8e | 32-07 | 3 | req-8 | — | clear marker + re-Start re-fires the workflow live | real-stack E2E | `... --filter-class "*CancelledCircuitBreakerE2ETests"` | ✓ authored (RealStack) | ⏳ operator-pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky · ⏳ operator-pending (authored, env-gated)*

**Hermetic coverage: 11/11 green** (full suite 467/467 at HEAD). Real-stack req-5/req-6b/req-8e are authored + `Category=RealStack`-tagged → manual-only, pending the operator close gate.

---

## Wave 0 Requirements

The two MassTransit-reality probes MUST land first — they unblock the breaker seam and the fault consumer:

- [x] `RetryAttemptNumberingFacts` — pins the `GetRetryAttempt()` boundary value for an endpoint-level `Immediate(Limit)` policy (Risk R1). **Confirmed `== Limit` (32-01, no escalation).**
- [x] `FaultConsumerBindingFacts` — proves `Fault<EntryStepDispatch>.Message.WorkflowId` round-trips through an in-memory MassTransit harness (Risk R2; D-06 assumption). **Proven (32-01).**
- [x] `BreakerTriggerFacts`, `CancelledMarkerFacts`, `CheckAndDropFacts`, `ResultCheckAndDropFacts`, `FaultUnscheduleFacts`, `BreakerMetricsFacts`, `FaultIdempotencyFacts`, `CancelledMarkerKeyFacts` — hermetic facts for req-1/2/3/4/7/8 all green. (`StepEntryConditionEnumFacts` removed — req-6 removal cancelled by user, D-12 reversed; surviving half covered by existing tests.)
- [x] **Close-gate teardown extension** — `phase-32-close.ps1` scan-cleans the no-TTL `skp:cancelled:*` namespace before the AFTER snapshot (authored in 32-07; net-zero per MEMORY `reference_close_gate_container_rebuild_and_flag_churn`).
- [x] No new framework install — xUnit + the existing real-stack harness cover this surface.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live breaker trip → halt → manual resume round-trip | req-5, req-8 | Requires the full v3.6.0 compose stack up healthy with rebuilt processor/orchestrator/baseapi containers (embedded SourceHash must match); not observable in the dev harness | Operator: `docker compose up -d --build processor-sample orchestrator baseapi-service`; run `*CancelledCircuitBreakerE2ETests`; then `pwsh ./scripts/phase-32-close.ps1` (expect GATE_EXIT=0). Read GATE_*_EXIT from the gate output, not the bg-task wrapper exit. |

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies (11 hermetic green; 3 real-stack authored + manual-only)
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references (R1/R2 probes landed green + close-gate teardown authored)
- [x] No watch-mode flags
- [x] Feedback latency < 240s (hermetic suite ~2m55s)
- [ ] `nyquist_compliant: true` — **held false** pending the operator close gate (req-5/req-6b/req-8e real-stack proof); hermetic surface is complete (`hermetic_complete: true`)

**Approval:** PARTIAL — hermetic coverage complete and green; final compliance flips to `true` on operator `GATE_EXIT=0`.

---

## Validation Audit 2026-06-05

| Metric | Count |
|--------|-------|
| Requirements/tasks audited | 14 |
| COVERED (hermetic, green) | 11 |
| Manual-only / operator-pending (real-stack) | 3 (req-5, req-6b, req-8e) |
| Re-dispositioned (user-cancelled) | 1 (req-6 removal half — D-12 reversed) |
| MISSING (gaps requiring new tests) | 0 |
| Tests generated this audit | 0 (no gaps to fill) |

State A audit: all hermetic facts classes present and green (full suite 467/467 at HEAD). No auditor spawn — zero fillable gaps. Real-stack E2E (`CancelledCircuitBreakerE2ETests`, `Category=RealStack`) authored but environment-gated; remains manual-only until the operator runs `phase-32-close.ps1`.
