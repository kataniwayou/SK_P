---
phase: 56
slug: typed-base-config-seam
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-12
---

# Phase 56 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (.NET hermetic test suite) |
| **Config file** | none — existing test project |
| **Quick run command** | `dotnet test --filter "FullyQualifiedName~SampleProcessorFacts" --nologo` |
| **Full suite command** | `dotnet test --nologo` |
| **Estimated runtime** | ~60 seconds |

---

## Sampling Rate

- **After every task commit:** Run quick run command (affected facts)
- **After every plan wave:** Run full suite command
- **Before `/gsd-verify-work`:** Full suite must be green + `dotnet build -c Release` 0-warning
- **Max feedback latency:** 90 seconds

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 56-01-01 | 01 | 1 | CFG-01 | — | typed config deserialized and passed to author seam | unit | `dotnet test --nologo` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

> Planner refines this map: every plan task with observable behavior (deser-success, deser-failure→StepFailed, empty→null config, sample round-trip, 0-warning build) must map to an automated command.

---

## Wave 0 Requirements

- [ ] New fact: deser-failure (malformed payload) through a real `BaseProcessor<TConfig>` → exactly one `StepFailed` (Req 4a — not covered today)
- [ ] Updated `SampleProcessorFacts` — reflection signature + payload object shape `{"value":"StepA1"}` (D-10)

*Identified during research; planner converts to concrete Wave 0 tasks.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| — | — | — | All phase behaviors have automated verification. |

*All phase behaviors have automated verification.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 90s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
