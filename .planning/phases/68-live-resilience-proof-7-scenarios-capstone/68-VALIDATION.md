---
phase: 68
slug: live-resilience-proof-7-scenarios-capstone
status: draft
nyquist_compliant: true
wave_0_complete: false
created: 2026-06-15
---

# Phase 68 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
>
> **This is an empirical live-proof phase.** Per the locked CONTEXT scope discipline there is
> NO new product code and NO new recovery logic — so the validation is the **7 live harness
> runs**, each scored PASS/FAIL by the Phase 66 analyzer from Prometheus + Elasticsearch.
> Hermetic/unit coverage of the *deliverables* is N/A: the wrapper is thin ops glue, and the
> scoring it consumes is already covered hermetically by Phase 66's `PassFailEngineFacts.cs`.
> The "test framework" for the capstone proof is the live sweep itself.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | The 7-scenario live sweep (`scripts/phase-68-*.ps1`) driving the Phase 66 analyzer (`AnalyzerE2ETests.cs`, xunit.v3 / MTP) once per run |
| **Config file** | none — the harness shells `dotnet test ... -- --filter-method` (MTP-native filter; VSTest `--filter` is silently ignored — [[mtp-filter-syntax]]) |
| **Quick run command** | `pwsh -File scripts/phase-67-harness.ps1 -ScenarioId TEST-03` (one scenario, ~10-12 min) |
| **Full suite command** | `pwsh -File scripts/phase-68-*.ps1` (all 7, ~70-85 min) |
| **Estimated runtime** | ~70-85 min for a clean all-PASS sweep (plan for a ~1.5 h uninterrupted window) |

---

## Sampling Rate

- **Per scenario (the unit of proof):** one 5-min window + ~120s analyzer drain + bring-up/teardown ≈ 10-12 min, scored once.
- **Per sweep (the milestone gate):** all 7 in numeric order; wrapper exit 0 iff 7/7 PASS.
- **Re-run granularity:** single-scenario re-invocation of the bare harness, on an INFRA-ABORT exit code (10-70) only — never on a verdict FAIL (exit 1) (D-04).
- **Max feedback latency:** ~12 min (one scenario).

---

## Per-Task Verification Map

> Authoring tasks (the wrapper + the 5 data rows + the cosmetic fixture fix) are verified by
> static checks (AST parse / grep / build). The capstone PROOF is verified by the live sweep.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 68-01-01 | 01 | 1 | TEST-03..07 | — | N/A (ops glue, no product code) | static | `pwsh -NoProfile -Command "$null = [scriptblock]::Create((Get-Content -Raw scripts/phase-67-harness.ps1))"` (AST parse clean) + grep all 5 new rows present | ❌ W0 | ⬜ pending |
| 68-01-02 | 01 | 1 | TEST-01..07 | — | N/A | static | `pwsh -NoProfile -Command "$null=[scriptblock]::Create((Get-Content -Raw scripts/phase-68-*.ps1))"` (wrapper AST parse clean) + grep loops all 7 ids | ❌ W0 | ⬜ pending |
| 68-02-01 | 02 | 2 | TEST-01..07 | — | N/A | **live (empirical)** | `pwsh -File scripts/phase-68-*.ps1` → roll-up shows 7/7 PASS; exit 0 | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

### What the analyzer measures (binding, per run)
- **Zero-missing (completeness):** every STARTED run (distinct correlationId with ≥1 `Step_*` log) reaches the full 9-label set incl. both sinks `Step_F1`+`Step_F2`. `Missing = Started − Complete; >0 ⇒ FAIL` (`PassFailEngine.cs`). **ES-primary, counter-reset-immune** — the binding arbiter precisely because every fault row resets a tier's Prom counters mid-window.
- **Effect-once (dedupe):** ANY duplicate `(correlationId, StepLabel)` in the ES `Step_*` hits ⇒ FAIL, fail-closed. Message-level redelivery is *reported, not failed* only because the recovery machinery (processor slot-array recovery pass) prevents a redelivery from emitting a second `Step_*` log.

---

## Wave 0 Requirements

- [ ] `scripts/phase-68-*.ps1` — the sweep wrapper (NEW; the only authored control flow)
- [ ] 5 rows in the `$Scenarios` table in `scripts/phase-67-harness.ps1` (TEST-03..07)
- [ ] (recommended, cosmetic) rename `Analyze_HappyPath_Window_Yields_Pass` → verdict-neutral name, sync the harness `--filter-method` literal, and fix the stale IN-04 comment ("fault = FAIL expected" is now wrong for the capstone)
- [ ] roll-up summary artifact (JSON + console/.md table)

*No new C# test files, no framework install — Phase 66's `PassFailEngineFacts.cs` already covers the scoring hermetically; the live proof is the validation.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| The 7-scenario live sweep produces 7/7 PASS | TEST-01..07 | Requires the full live docker stack + ~1.5 h wall-clock + real fault injection; cannot run in a hermetic CI unit pass. It IS the phase deliverable, and it is **fully automated** (no human verification step — the analyzer renders the verdict from Prom+ES). | `pwsh -File scripts/phase-68-*.ps1`; inspect the roll-up (`analyzer-reports/phase-68-summary.json` + console table) for 7/7 PASS; on any FAIL, investigate per D-01b (TTL-vs-dwell artifact vs real defect) — do NOT retry a verdict FAIL; re-run only on a distinct INFRA-ABORT exit (D-04). |

---

## Validation Sign-Off

- [x] All tasks have automated verify (static for authoring; live-empirical for the proof) or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references (wrapper + rows + fixture fix + summary)
- [x] No watch-mode flags
- [x] Feedback latency < ~12 min per scenario
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
