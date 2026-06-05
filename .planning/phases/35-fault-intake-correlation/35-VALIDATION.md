---
phase: 35
slug: fault-intake-correlation
status: verified
nyquist_compliant: true
wave_0_complete: true
created: 2026-06-05
---

# Phase 35 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Per-task map filled by /gsd-validate-phase audit (State A) on 2026-06-05 — every requirement-slice has an authored automated test; hermetic lanes verified GREEN this audit, SC3 RealStack authored + operator-gated.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit + Microsoft.Testing.Platform (MTP) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests -- --filter-class <FullyQualifiedClass>` (MTP filter syntax) |
| **Full suite command** | `dotnet test tests/BaseApi.Tests` (hermetic; excludes `[Trait("Category","RealStack")]`) |
| **RealStack (SC3) command** | live compose stack up (`docker compose up -d --build keeper processor-sample orchestrator baseapi-service`) THEN `dotnet test tests/BaseApi.Tests -- --filter-class "*KeeperFaultIntakeE2ETests"` |
| **Estimated runtime** | hermetic ~tens of seconds; RealStack E2E ~minutes (live poll windows) |

---

## Sampling Rate

- **After every task commit:** Run the quick (class-filtered) hermetic test for the touched unit
- **After every plan wave:** Run the full hermetic suite
- **Before `/gsd-verify-work`:** Full hermetic suite green + the RealStack SC3 proof green against a freshly-rebuilt stack
- **Max feedback latency:** hermetic < ~60s

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 01-T0/T1 | 35-01 | 0 (D-07 guard) | INTAKE-03 (shared helper) | T-35-01/02 | `ExecutionLogScope.BuildState` is byte-identical SoT — 5 fixed keys, no CorrelationId, skip-rules preserved | unit (regression) | `dotnet test tests/BaseApi.Tests -- --filter-class "*ConsoleExecutionScopeFilterTests"` | ✅ `tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs` | ✅ green (4/4, this audit) |
| 01-T0 | 35-01 | 0 (D-07 guard) | INTAKE-03 (key-string contract) | T-35-02 | scope key strings == param names | unit | `dotnet test tests/BaseApi.Tests -- --filter-class "*ExecutionLogScopeKeyTests"` | ✅ `tests/BaseApi.Tests/Contracts/ExecutionLogScopeKeyTests.cs` | ✅ green (1/1, per 35-01 Wave-0) |
| 01-T1 | 35-01 | 0 (D-07 guard) | INTAKE-03 (cross-console no-drift) | T-35-02 | 5 other scope call-sites unchanged | unit | `--filter-class "*EntryStepDispatchScopeTests"` / `*EntryStepDispatchRuntimeScopeTests` / `*WorkflowFireJobScopeTests` / `*ProcessorIdEnricherTests` | ✅ (5 enumerated classes) | ✅ green (7/7, per 35-01 Wave-0 before+after) |
| 02-T1 | 35-02 | 1 | KMET-04 (SC2) | T-35-04/05/06/07 | captured scope carries propagated CorrelationId + 5 exec ids; skip-rule mirror; no StackTrace; null-safe Exceptions | unit (hermetic, TDD) | `dotnet test tests/BaseApi.Tests -- --filter-class "*KeeperFaultConsumerScopeTests"` | ✅ `tests/BaseApi.Tests/Keeper/KeeperFaultConsumerScopeTests.cs` | ✅ green (3/3, this audit) |
| 02-T2 | 35-02 | 1 | INTAKE-03 (binding shape) | T-35-04 | `Fault<EntryStepDispatch>` delivers exactly-once to the real fault consumer on `keeper-fault-recovery` | unit (hermetic) | `dotnet test tests/BaseApi.Tests -- --filter-class "*KeeperRoundRobinTests"` | ✅ `tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs` | ✅ green (1/1, this audit) |
| 03-T1 | 35-03 | 2 | INTAKE-03 + KMET-04 (SC3) | T-35-08/09/10 | running Keeper container emits ES log correlated by `service.name=keeper` + propagated `CorrelationId` + `StepId` + `body.text ~ "keeper fault intake"`; net-zero teardown; no DLQ-1/TTL topology | RealStack E2E | (RealStack) `dotnet test tests/BaseApi.Tests -- --filter-class "*KeeperFaultIntakeE2ETests"` against rebuilt stack | ✅ `tests/BaseApi.Tests/Orchestrator/KeeperFaultIntakeE2ETests.cs` | ⏸ authored — live run **operator-gated** (see Manual-Only) |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky · ⏸ authored/operator-gated*

---

## Validation Architecture (from 35-RESEARCH.md)

- **SC1 (INTAKE-03 slice):** topology/binding assertion — Keeper binds the `Fault<EntryStepDispatch>` / `Fault<ExecutionResult>` message-type exchanges, NOT any `{queue}_error` queue; recovered work is never double-processed. (TTL'd consolidated DLQ-1 is OUT of scope — Phase 36.) Hermetically proven by `KeeperRoundRobinTests` (exactly-once delivery to the real fault consumer).
- **SC2 (KMET-04):** the Keeper-emitted log carries the propagated correlationId + the 5 execution-scope ids — scope keys present (incl. the MANDATORY manual CorrelationId scope, since `Fault<T>` is not `ICorrelated`). Hermetically proven by `KeeperFaultConsumerScopeTests` (3/3).
- **SC3:** ES log correlated end-to-end — `PollEsForLog` on `resource.attributes.service.name=keeper` + `attributes.CorrelationId`/`attributes.StepId` + `body.text`, against the running Keeper container. Authored as `KeeperFaultIntakeE2ETests`; LIVE run operator-gated.

---

## Wave 0 Requirements

- [x] Regression-guard the shared-helper refactor (D-07): the existing scope tests stayed green — `ConsoleExecutionScopeFilterTests` (4/4) + `ExecutionLogScopeKeyTests` (1/1) + the 5 other enumerated call sites (`EntryStepDispatchScopeTests` 2/2, `EntryStepDispatchRuntimeScopeTests` 1/1, `WorkflowFireJobScopeTests` 1/1, `ProcessorIdEnricherTests` 2/2). Run BEFORE and AFTER the `ExecutionLogScope.BuildState(...)` extraction (35-01-SUMMARY); `ConsoleExecutionScopeFilterTests` re-confirmed 4/4 GREEN at this validation audit.

*Existing xUnit infrastructure otherwise covers phase requirements; no new framework install.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| SC3 live correlated ES log (running Keeper container) | INTAKE-03 / KMET-04 (end-to-end) | The automated test (`KeeperFaultIntakeE2ETests`) EXISTS and is authored, but its live run requires the full compose stack (Keeper **rebuilt** — Pitfall 5) and a live ES poll window — it cannot run in the hermetic CI lane. Operator-gated per the Phase-31..34 precedent; the Phase-38 live close gate is the authoritative signal. | 1) `docker compose up -d --build keeper processor-sample orchestrator baseapi-service`; 2) wait all four Healthy; 3) `dotnet test tests/BaseApi.Tests -- --filter-class "*KeeperFaultIntakeE2ETests"`; 4) expect GREEN — `PollEsForLog` hit with `service.name=keeper` + `attributes.CorrelationId == tripped dCorr` + `attributes.StepId == stepId` + `body.text ~ "keeper fault intake"`; 5) net-zero: no leftover `skp:*` after teardown. Triage: stale Keeper (skipped `--build`) or `OTEL_EXPORTER_OTLP_ENDPOINT` not wired. |

*All phase behaviors have an authored automated test (hermetic unit + the RealStack E2E for SC3). SC3's automated test is operator-gated for its LIVE execution only.*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers the scope-refactor regression guard (re-confirmed 4/4 green this audit)
- [x] No watch-mode flags
- [x] Feedback latency < 60s (hermetic)
- [x] `nyquist_compliant: true` set in frontmatter (every requirement-slice has an authored automated test; SC3 live run operator-gated)

**Approval:** verified 2026-06-05

---

## Validation Audit 2026-06-05

| Metric | Count |
|--------|-------|
| Gaps found (MISSING) | 0 |
| Resolved (tests generated) | 0 |
| Escalated | 0 |
| Hermetic classes re-confirmed GREEN this audit | 3 (ConsoleExecutionScopeFilterTests 4/4, KeeperFaultConsumerScopeTests 3/3, KeeperRoundRobinTests 1/1) |
| Manual-only (RealStack / operator-gated) | 1 (SC3 KeeperFaultIntakeE2ETests live run) |

State A audit: every requirement (INTAKE-03, KMET-04) and the D-07 regression guard already had authored automated tests — no MISSING gaps, so no `gsd-nyquist-auditor` test-generation was required. The hermetic lanes were re-run live at audit time and confirmed GREEN. The single non-hermetic item (SC3 live ES correlation) is an authored RealStack test whose live execution is operator-gated and tracked by the Phase-38 close gate.
