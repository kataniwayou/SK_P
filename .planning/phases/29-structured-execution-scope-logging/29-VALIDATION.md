---
phase: 29
slug: structured-execution-scope-logging
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-02
---

# Phase 29 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit + Microsoft.Testing.Platform (MTP) |
| **Config file** | existing test projects (tests/BaseApi.Tests, real-stack E2E project) |
| **Quick run command** | `dotnet test <hermetic test project> -- --filter-class <FQN>` |
| **Full suite command** | `dotnet test` (hermetic) + real-stack E2E + phase-29-close.ps1 (3× GREEN + triple-SHA) |
| **Estimated runtime** | hermetic ~tens of seconds; real-stack E2E minutes |

> Planner/researcher to confirm exact MTP filter syntax (`-- --filter-class`) and the real-stack project name before pinning commands. See 29-RESEARCH.md "Validation Architecture".

---

## Sampling Rate

- **After every task commit:** Run the relevant hermetic `--filter-class` quick command
- **After every plan wave:** Run the full hermetic suite
- **Before `/gsd-verify-work`:** Full hermetic suite GREEN + extended real-stack E2E passes
- **Max feedback latency:** hermetic < 60s

---

## Per-Task Verification Map

> Populated by the planner from the actual task breakdown. Each LOG-0x requirement maps to a hermetic scope-capture test; LOG-06 maps to the extended real-stack E2E + close-gate.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 29-XX-XX | XX | X | LOG-0X | — | N/A | unit/e2e | `dotnet test ... -- --filter-class ...` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] Hermetic scope-capture test scaffolding mirroring `tests/BaseApi.Tests/Console/ConsoleCorrelationFilterTests.cs` (probe-consumer captures scope dictionary) — covers LOG-02, LOG-03, LOG-04, LOG-05
- [ ] Enricher test capturing emitted `LogRecord.Attributes` — covers LOG-04 (`ProcessorId` present when `Id` set, absent/no-exception when null)
- [ ] Real-stack E2E extension point in `SampleRoundTripE2ETests` with a **scope-only** assertion (not template-sourced `WorkflowId` — see RESEARCH L1) — covers LOG-06

*Existing xUnit/MTP + real-stack infrastructure covers the framework; no framework install needed.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| — | — | — | — |

*All phase behaviors have automated verification (hermetic scope-capture + real-stack E2E).*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
