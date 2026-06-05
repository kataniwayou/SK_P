---
phase: 35
slug: fault-intake-correlation
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-05
---

# Phase 35 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Detailed per-task map is filled by the planner from 35-RESEARCH.md §"Validation Architecture".

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit + Microsoft.Testing.Platform (MTP) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests -- --filter-class <FullyQualifiedClass>` (MTP filter syntax) |
| **Full suite command** | `dotnet test tests/BaseApi.Tests` (hermetic; excludes `[Trait("Category","RealStack")]`) |
| **RealStack (SC3) command** | live compose stack up (`docker compose up -d --build keeper processor-sample orchestrator baseapi-service`) THEN the RealStack-trait test |
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
| _planner fills_ | | | INTAKE-03 / KMET-04 | | | unit / RealStack-E2E | | | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Validation Architecture (from 35-RESEARCH.md)

- **SC1 (INTAKE-03 slice):** topology/binding assertion — Keeper binds the `Fault<EntryStepDispatch>` / `Fault<ExecutionResult>` message-type exchanges, NOT any `{queue}_error` queue; recovered work is never double-processed. (TTL'd consolidated DLQ-1 is OUT of scope — Phase 36.)
- **SC2 (KMET-04):** the Keeper-emitted log carries the propagated correlationId + the 5 execution-scope ids — assert scope keys present (incl. the MANDATORY manual CorrelationId scope, since `Fault<T>` is not `ICorrelated`).
- **SC3:** ES log correlated end-to-end — `PollEsForLog` on `resource.attributes.service.name=keeper` + `attributes.CorrelationId`/`attributes.StepId` + `body.text`, against the running Keeper container.

---

## Wave 0 Requirements

- [ ] Regression-guard the shared-helper refactor (D-07): the existing scope tests MUST stay green — notably `ConsoleExecutionScopeFilterTests` (4 cases) + `ExecutionLogScopeKeyTests` (and the 6 other call sites enumerated in 35-RESEARCH.md §3). Run them BEFORE and AFTER the `ExecutionLogScope.BuildState(...)` extraction.

*Existing xUnit infrastructure otherwise covers phase requirements; no new framework install.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| _none expected_ | | | |

*Target: all phase behaviors have automated verification (hermetic unit + RealStack E2E for SC3).*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers the scope-refactor regression guard
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s (hermetic)
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
