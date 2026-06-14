---
phase: 66
slug: prometheus-es-analyzer-pass-fail-engine
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-14
---

# Phase 66 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> The analyzer is itself a verification artifact — the validation question is: **how do we prove each decision branch of the analyzer's correctness logic is actually exercised**, so a passing analyzer is trustworthy, not vacuously green? Source: 66-RESEARCH.md § Validation Architecture.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v2 (`TestContext.Current.CancellationToken` in use) |
| **Config file** | none separate — standard `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~PassFailEngine"` (hermetic unit facts — no stack) |
| **Full suite command** | `dotnet test tests/BaseApi.Tests --filter "Category!=RealStack"` (hermetic) |
| **Live gate command** | `dotnet test tests/BaseApi.Tests --filter "Category=RealStack&FullyQualifiedName~Analyzer"` |
| **Estimated runtime** | hermetic facts sub-second; RealStack analyzer bounded by drain (~45–60s) |

---

## Sampling Rate

- **After every task commit:** Run the quick (hermetic `PassFailEngine` + `SearchAllHits`) facts — sub-second, no stack.
- **After every plan wave:** Run the full hermetic suite (`Category!=RealStack`).
- **Before `/gsd-verify-work`:** One RealStack `Analyzer` happy-path run green against the live stack.
- **Max feedback latency:** < 2s for the hermetic sample; phase gate is the single live run.

---

## Per-Task Verification Map

| Req ID | Behavior | Test Type | Automated Command | File Exists | Status |
|--------|----------|-----------|-------------------|-------------|--------|
| OBS-01 | per-correlationId trace aggregates all 9 labels → COMPLETE | unit (synthetic hits) | `--filter "FullyQualifiedName~PassFailEngine.Complete"` | ❌ W0 | ⬜ pending |
| OBS-02 | missing label → MISSING | unit | `--filter "FullyQualifiedName~PassFailEngine.Missing"` | ❌ W0 | ⬜ pending |
| OBS-02 | duplicate label → FAIL (fail-closed) | unit | `--filter "FullyQualifiedName~PassFailEngine.Duplicate"` | ❌ W0 | ⬜ pending |
| OBS-03 | Prom reconciliation: balanced→pass, unaccounted delta→FAIL | unit (synthetic `PromCounterSnapshot`) | `--filter "FullyQualifiedName~PassFailEngine.Reconcile"` | ❌ W0 | ⬜ pending |
| OBS-04 | report JSON written BEFORE assert; verdict matches engine | integration + RealStack | `--filter "FullyQualifiedName~Analyzer"` | ❌ W0 | ⬜ pending |
| item #3 | `SearchAllHits` returns N hits, groups by correlationId correctly | unit vs captured ES `_search` JSON fixture | `--filter "FullyQualifiedName~ElasticsearchTestClient.SearchAllHits"` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

### Decision-branch signals (each branch provably exercised hermetically)

- **COMPLETE:** run with all 9 labels → `report.CompleteRuns == 1`.
- **MISSING:** run missing `Step_F2` → `report.Missing >= 1`, `Verdict.Fail`, missing label in `report.MissingDetail`.
- **DUPLICATE/fail-closed:** run with two `Step_C` hits → `Verdict.Fail` reason "unaccountable duplicate"; `report.Duplicates` non-empty (proves item #2 fail-closed wiring — dedupe counters are dormant, so any duplicate fails).
- **RECONCILE-FAIL:** `PromCounterSnapshot{dispatch_sent=10, complete=10, result_sent_completed short by 1}` → `Verdict.Fail` reason "unreconciled"; `report.Reconciliation == Unreconciled`.
- **RECONCILE-PASS:** balanced snapshot + all-complete runs → `report.Verdict == Pass`.
- **Window/drain (RealStack only):** TEST-01 happy-path live run → `Verdict.Pass`, `Missing == 0`.

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` — covers OBS-01/02/03 decision branches with synthetic inputs.
- [ ] `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClientFacts.cs` (or extend existing) — covers `SearchAllHits` grouping against a captured `_search` JSON fixture.
- [ ] ES `_mapping` Wave-0 probe — confirm the window timestamp field (A2) and `Sum` attribute type (A1); bake result into the query template + an `EsIndexNames` const.
- [ ] Confirm Prom counter windowing strategy vs Phase 65/67 reset/restart behavior (A3) before locking reconciliation arithmetic.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| MISSING-run *attribution* (which correlationId fired-but-vanished) | OBS-02 | Per-fire correlationId is NOT observable from existing telemetry (research item #1 — fallback to `orchestrator_dispatch_sent_total` + cadence). The *count* of missing runs is detectable; the *identity* is not. | Documented as an accepted limitation; the analyzer reports the count and the unrecoverable-identity caveat. |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 2s (hermetic)
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
