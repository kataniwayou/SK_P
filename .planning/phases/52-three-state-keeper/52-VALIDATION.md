---
phase: 52
slug: three-state-keeper
status: validated
nyquist_compliant: true
wave_0_complete: true
created: 2026-06-11
validated: 2026-06-11
---

# Phase 52 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from 52-RESEARCH.md §Validation Architecture.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (project-pinned) + NSubstitute + MassTransit.Testing `ITestHarness` |
| **Config file** | none (xUnit auto-discovery); facts under `tests/BaseApi.Tests/Keeper/` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Keeper"` |
| **Full suite command** | `dotnet test` (must be 0-warning) |
| **Estimated runtime** | ~30–60 seconds (keeper-scoped quick run) |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Keeper"`
- **After every plan wave:** Run `dotnet test` (Debug)
- **Before `/gsd-verify-work`:** Full suite green at Release + Debug, **0 warnings**
- **Max feedback latency:** ~60 seconds (quick keeper-scoped run)

---

## Per-Task Verification Map

| Requirement | Behavior | Test Type | Automated Command | File Exists | Status |
|-------------|----------|-----------|-------------------|-------------|--------|
| KEEP-01 | REINJECT present → sends `EntryStepDispatch` w/ `Payload` to `queue:{proc}` | unit (consumer + CapturingSendProvider) | `dotnet test --filter "FullyQualifiedName~ReinjectConsumerFacts"` | ✅ `Reinject_present_sends_EntryStepDispatch_with_Payload` | ✅ green |
| KEEP-01 | REINJECT absent/empty → silent drop + counter, NO send, NO throw (D-06/D-07) | unit | `dotnet test --filter "FullyQualifiedName~ReinjectConsumerFacts"` | ✅ `Reinject_absent_drops_no_throw_no_send_and_increments_counter` | ✅ green |
| KEEP-02 | INJECT → write `L2[entryId]` → send `StepCompleted` → delete `L2[deleteEntryId]` **in order** | unit (FakeRedis / `Received()` ordering) | `dotnet test --filter "FullyQualifiedName~InjectConsumerFacts"` | ✅ `Inject_writes_sends_completed_deletes_source_in_order` | ✅ green |
| KEEP-03 | DELETE deletes key; absent → no-op | unit | `dotnet test --filter "FullyQualifiedName~DeleteConsumerFacts"` | ✅ `Delete_deletes_execution_data_key` + `Delete_absent_key_no_throws` | ✅ green |
| KEEP-04 | Gate-closed → endpoint stopped → message NOT consumed (accumulates); gate-open → consumed/drained | integration (`ITestHarness` Stop/Start + Consumed assertions) | `dotnet test --filter "FullyQualifiedName~KeeperPauseAccumulate"` | ✅ `Started_endpoint_consumes_Stopped_endpoint_accumulates` | ✅ green |
| KEEP-04 | `BitHealthLoop` drives Stop on unhealthy edge, Start on healthy edge | unit (fake endpoint handle) | `dotnet test --filter "FullyQualifiedName~BitHealthLoop"` | ✅ `Healthy_To_Unhealthy_Edge_Stops_Recovery_Endpoint` + `Same_State_Ticks_No_Stop_Start` | ✅ green |
| KEEP-05 | Dlq1 mode: exhausted op → routes to `skp-dlq-1` (ConsolidatedFault) | integration (`ITestHarness`) | `dotnet test --filter "FullyQualifiedName~RecoveryDeadLetter"` | ✅ `InfraFault_reinject_faults_and_routes_to_dead_letter` | ✅ green |
| KEEP-05 | SustainedOutage mode: exhausted op → requeue/hold, NO dead-letter | integration (`ITestHarness`) | `dotnet test --filter "FullyQualifiedName~SustainedOutage"` | ✅ `SustainedOutage_holds_and_redelivers_no_dead_letter` | ✅ green |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*
*Verified 2026-06-11: `dotnet test --filter-namespace "*Keeper*"` → 32/32 green (whole-solution build 0/0).*

---

## Wave 0 Requirements

- [x] `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs` — KEEP-02 write→send→delete ordering
- [x] Extend `ReinjectConsumerFacts.cs` — absent-path now DROP + counter (KEEP-01 / D-06 / D-07); old "throws" assertion deleted
- [x] New pause/accumulate integration fact (`KeeperPauseAccumulateFacts`) — KEEP-04 (endpoint Stop → no consume → Start → drain)
- [x] Extend `BitHealthLoopTests.cs` with a fake endpoint handle — KEEP-04 driver (Stop on unhealthy edge, Start on healthy)
- [x] New SustainedOutage fact (`SustainedOutageFacts`) — KEEP-05 hold/requeue mode (asserts NO `ConsolidatedFault`, message redelivered)
- [x] Adapt `RecoveryDeadLetterFacts.cs` — repurposed data-gone→dead-letter to infra-fault op-exhaustion→dead-letter (Dlq1), since data-gone is now a drop (D-06)
- [x] (optional) counter coverage — `keeper_reinject_dropped` increment asserted inside the absent-drop fact (MeterListener)

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live `skp-dlq-1` broker queue + TTL, real partitioner serialization | KEEP-05 | Hermetic in-memory harness proves routing/fault/drop **shape**, not broker-literal queues | Deferred to Phase 54 TEST-01 (RealStack live triple-SHA E2E) |

---

## Validation Sign-Off

- [x] All tasks have automated verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 60s
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** ✅ NYQUIST-COMPLIANT — 2026-06-11

---

## Validation Audit 2026-06-11

| Metric | Count |
|--------|-------|
| Requirements mapped | 8 (KEEP-01..05) |
| COVERED (green) | 8 |
| PARTIAL | 0 |
| MISSING | 0 |
| Manual-only (deferred) | 1 (live `skp-dlq-1` broker → Phase 54 TEST-01) |

State A audit. All 8 requirement rows transitioned ⬜ pending → ✅ green; every
Wave-0 test file now exists and runs. Evidence: `dotnet test --filter-namespace
"*Keeper*"` → 32/32 green; `dotnet build SK_P.sln -c Debug` → 0 warnings / 0 errors.
No gaps found — no auditor spawn or test generation needed. The single Manual-Only
item (broker-literal queue/TTL) is unchanged: hermetic harness proves routing/fault
shape; live-queue proof remains the operator-gated Phase 54 deliverable.
