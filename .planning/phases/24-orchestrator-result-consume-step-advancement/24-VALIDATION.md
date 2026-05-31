---
phase: 24
slug: orchestrator-result-consume-step-advancement
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-05-31
---

# Phase 24 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (v3 — `TestContext.Current.CancellationToken`) + MassTransit.Testing (`AddMassTransitTestHarness`/`ITestHarness`) + NSubstitute + `FakeTimeProvider` |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (single test project; orchestrator tests under `tests/BaseApi.Tests/Orchestrator/`) |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Orchestrator"` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Estimated runtime** | ~30–90 seconds (in-memory bus + Redis-mux stubs; no live broker/Redis) |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Orchestrator"` (add `--filter "FullyQualifiedName~Orchestration"` for the WebApi first-win facts).
- **After every plan wave:** Run `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` (full suite).
- **Before `/gsd-verify-work`:** Full suite must be green.
- **Max feedback latency:** ~90 seconds.

---

## Per-Task Verification Map

> Task IDs are provisional (assigned during planning). Requirement → test-behavior mapping is locked from 24-RESEARCH.md § Validation Architecture.

| Req ID | Behavior | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|--------|----------|------------|-----------------|-----------|-------------------|-------------|--------|
| ORCH-RESULT-01 | `ExecutionResult` round-trips all fields; NO payload field; `StepOutcome` ints 0–3 match `StepEntryCondition.Previous*` | — | N/A | unit | `dotnet test --filter ExecutionResultContract` | ❌ W0 | ⬜ pending |
| ORCH-RESULT-02 | Result consumed exactly once on shared `queue:orchestrator-result` (competing, NOT fan-out) | — | N/A | harness | `dotnet test --filter ResultConsume` | ❌ W0 | ⬜ pending |
| ORCH-ADVANCE-01 | Full outcome×entry-condition match table; `Never(5)` never selected; no L2/Redis read on result path | — | N/A | unit | `dotnet test --filter StepAdvancement` | ❌ W0 | ⬜ pending |
| ORCH-ADVANCE-02 | Continuation dispatch field-copy: ids from result, `stepId/processorId/payload` from L1 projection; one dispatch per selected next step | — | N/A | harness (`CapturingDispatchConsumer`) | `dotnet test --filter ContinuationDispatch` | ❌ W0 | ⬜ pending |
| ORCH-RESULT-ACK-01 | Unknown `(wf,step)` / no-match / corrupt-projection = clean ack (no throw, no `_error`); infra fault propagates; redelivered result MAY re-dispatch | V5 Input Validation | Corrupt/malformed result = business ack-skip, never a crash (mirror `WorkflowLifecycle.IsBusiness`) | unit + stub | `dotnet test --filter ResultAck` | ❌ W0 (reuse `OrchestratorTestStubs`) | ⬜ pending |
| ORCH-GATE-01 | Gate-closed Start/Stop/result message is redelivered (not acked away) and processed after `MarkReady` | DoS (redelivery storm) | Finite scheduled-redelivery set exhausts to `_error` (no infinite storm) | harness | `dotnet test --filter GateClosedRedeliver` | ❌ W0 | ⬜ pending |
| ORCH-START-RELOAD-01 | Start already-in-L1 re-hydrates + reschedules (no existence skip); stop→start revives a live job | — | N/A | unit | extend `StartConsumerLifecycleTests` | ✅ extend | ⬜ pending |
| ORCH-STOP-DRAIN-01 | Stop deletes Quartz job but KEEPS L1; a late result for the stopped workflow still dispatches its matching next steps | — | N/A | unit + harness | extend `StopConsumerLifecycleTests` | ✅ extend | ⬜ pending |
| WEBAPI-SUPPRESS-01 | 2nd Start for existing `workflowId` does NOT overwrite root or republish; 2nd Stop for absent `workflowId` is a no-op | — | N/A | unit | extend `StartOrchestrationFacts`/`StopOrchestrationFacts` (reconcile current 422/overwrite facts) | ✅ extend | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Orchestrator/ExecutionResultContractTests.cs` — ORCH-RESULT-01 (serialize→deserialize round-trip; assert no output/payload field; assert int values 0–3)
- [ ] `tests/BaseApi.Tests/Orchestrator/StepAdvancementTests.cs` — ORCH-ADVANCE-01 (full match table; pure, no harness)
- [ ] `tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs` — ORCH-RESULT-02 / ORCH-ADVANCE-02 (reuse `CapturingDispatchConsumer` + `AddMassTransitTestHarness`)
- [ ] `tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs` — ORCH-RESULT-ACK-01 (reuse `OrchestratorTestStubs`; unknown/no-match/corrupt = ack; injected infra fault propagates)
- [ ] `tests/BaseApi.Tests/Orchestrator/GateClosedRedeliverTests.cs` — ORCH-GATE-01 (harness; assert message survives a closed→open gate transition)
- [ ] No framework install needed — all test deps present (xUnit + MassTransit.Testing + NSubstitute + FakeTimeProvider).
- [ ] Reconcile/update existing facts asserting Phase 22/23 behavior: `StartOrchestrationFacts`, `StopOrchestrationFacts`, `StopScanFacts`, `OrchestrationServicePublishTests`, `StartConsumerLifecycleTests`, `StopConsumerLifecycleTests` (the conditionless + first-win redesign changes their expectations).

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Redelivery interval sizing comfortably outlasts real hydration | ORCH-GATE-01 | Interval values (`5s/15s/30s/60s`) are ASSUMED (A1) — hydration duration not benchmarked; the automated test asserts the redeliver-after-MarkReady behavior, not the wall-clock fit | After integration, observe orchestrator startup logs against a populated L2; confirm a result arriving during hydration redelivers and lands after `MarkReady` without hitting `_error` |

*All other phase behaviors have automated verification.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 90s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
