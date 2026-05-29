---
phase: 14
slug: validation-gates-dfs-schema-edge-payload-config-schema
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-05-29
---

# Phase 14 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (.NET) — see existing `*Facts` test projects |
| **Config file** | none — existing test projects cover the suite |
| **Quick run command** | `dotnet test --filter "FullyQualifiedName~Orchestration"` |
| **Full suite command** | `dotnet test` |
| **Estimated runtime** | ~per existing suite |

---

## Sampling Rate

- **After every task commit:** Run quick filtered orchestration test run
- **After every plan wave:** Run `dotnet test`
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** keep under existing suite runtime

---

## Per-Task Verification Map

> Populated by the planner from RESEARCH.md §Validation Architecture. Each L1-VALIDATE
> requirement maps to at least one observable behavior with an automated xUnit fact.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| TBD | — | — | L1-VALIDATE-01..10 | — | gate-order 422 + offending ids; null-passes; per-Start single-parse; SSRF non-regression; L1 cleanup on failure | unit/integration | `dotnet test` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

> Behaviors to sample (from RESEARCH.md §Validation Architecture):
- [ ] Cycle gate: true-cycle → 422 with `stepChain`; diamond/fan-in DAG → passes (no false-positive, per D-14)
- [ ] Missing-step gate: forced snapshot with dangling `NextStepId` → 422 `(parentStepId, missingChildId)`; null/empty NextStepIds → terminal/pass
- [ ] Schema-edge gate: mismatch → 422 `(parentStepId, childStepId)`; null-on-either-side → pass
- [ ] Payload↔ConfigSchema gate: bad payload → 422 `assignmentId` + flattened errors; null ConfigSchemaId → pass; per-Start cache parses each schema once
- [ ] Gate-order short-circuit: multi-failure workflow asserts FIRST gate fires (existence(404) → cycle → schema-edge → payload)
- [ ] L1 cleanup (`snapshot.Dispose()`/`IsDisposed`) runs on the validation-failure path
- [ ] Regression guards stay GREEN: Phase 8 SSRF `<500ms`; Assignment-PUT/POST "valid JSON only"; existence 404 (`StartOrchestrationFacts`)

*Planner refines into concrete test files + per-task rows.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| — | — | — | — |

*All phase behaviors have automated verification (xUnit facts over in-memory snapshots + integration tests).*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < full-suite runtime
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
