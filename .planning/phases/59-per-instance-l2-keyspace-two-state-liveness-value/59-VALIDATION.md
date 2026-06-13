---
phase: 59
slug: per-instance-l2-keyspace-two-state-liveness-value
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-13
---

# Phase 59 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | {xUnit / NUnit / MSTest — confirm from existing L2ProjectionKeys tests} |
| **Config file** | {path or "none — existing test project"} |
| **Quick run command** | `{dotnet test --filter on the new hermetic tests}` |
| **Full suite command** | `{dotnet test of the contracts test project}` |
| **Estimated runtime** | ~{N} seconds |

---

## Sampling Rate

- **After every task commit:** Run `{quick run command}`
- **After every plan wave:** Run `{full suite command}`
- **Before `/gsd-verify-work`:** Full suite must be green + 0-warning Release/Debug build
- **Max feedback latency:** {N} seconds

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| {N}-01-01 | 01 | 1 | KEY-01/02 | — | golden key-string pinned | unit | `{command}` | ❌ W0 | ⬜ pending |
| {N}-01-02 | 01 | 1 | KEY-04/STATE-01/02 | — | factory: any Fail⇒Unhealthy, null⇒Success | unit | `{command}` | ❌ W0 | ⬜ pending |
| {N}-01-03 | 01 | 1 | STATE-01 | — | shape test: no inputDefinition/outputDefinition keys | unit | `{command}` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky · Planner fills final task IDs/commands.*

---

## Wave 0 Requirements

- [ ] Hermetic golden test for the new `PerInstance`/`InstanceIndex` key builders (mirror existing `L2ProjectionKeysTests`)
- [ ] Serialization/shape test asserting `inputDefinition`/`outputDefinition` ABSENCE on the new value record
- [ ] Factory invariant tests (Fail⇒Unhealthy, null⇒Success) for the smart-constructor
- [ ] instanceId resolver tests (mirror existing `ResolveInstanceIdFacts` if present)

*All validation is hermetic (no real Redis/stack) per RESEARCH §Validation Architecture.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| — | — | — | — |

*All phase behaviors have automated (hermetic) verification — contract-surface phase, no runtime integration.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < {N}s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** {pending / approved YYYY-MM-DD}
