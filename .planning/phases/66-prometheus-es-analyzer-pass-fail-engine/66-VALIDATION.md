---
phase: 66
slug: prometheus-es-analyzer-pass-fail-engine
status: complete
nyquist_compliant: true
wave_0_complete: true
created: 2026-06-14
validated: 2026-06-14
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
| **Quick run command** | `dotnet test tests/BaseApi.Tests -- --filter-class "BaseApi.Tests.Observability.Analysis.PassFailEngineFacts"` (hermetic unit facts — no stack) |
| **Full suite command** | `dotnet test tests/BaseApi.Tests -- --filter-not-trait "Category=RealStack"` (hermetic) |
| **Live gate command** | `dotnet test tests/BaseApi.Tests -- --filter-class "BaseApi.Tests.Observability.AnalyzerE2ETests"` (RealStack — needs Phase 65/67 seeded fan-out window) |
| **Estimated runtime** | hermetic facts sub-second; RealStack analyzer bounded by drain (~45–60s) |

---

## Sampling Rate

- **After every task commit:** Run the quick (hermetic `PassFailEngine` + `SearchAllHits`) facts — sub-second, no stack.
- **After every plan wave:** Run the full hermetic suite (`Category!=RealStack`).
- **Before `/gsd-verify-work`:** One RealStack `Analyzer` happy-path run green against the live stack.
- **Max feedback latency:** < 2s for the hermetic sample; phase gate is the single live run.

---

## Per-Task Verification Map

> **xUnit v3 / MTP filter note:** under this project's xUnit.v3 + Microsoft.Testing.Platform runner the legacy VSTest `--filter "FullyQualifiedName~..."` form is **silently ignored** (runs the full ~633-test suite). Use `--filter-class`/`--filter-method`/`--filter-namespace` passed after `--`. The commands below are corrected to the working MTP form (drafted commands used the legacy form — see 66-01-SUMMARY Issues).

| Req ID | Behavior | Test Type | Automated Command | File Exists | Status |
|--------|----------|-----------|-------------------|-------------|--------|
| OBS-01 | per-correlationId trace aggregates all 9 labels → COMPLETE | unit (synthetic hits) | `dotnet test tests/BaseApi.Tests -- --filter-method "*PassFailEngineFacts.Complete*"` | ✅ | ✅ green |
| OBS-02 | missing label → MISSING | unit | `dotnet test tests/BaseApi.Tests -- --filter-method "*PassFailEngineFacts.Missing*"` | ✅ | ✅ green |
| OBS-02 | duplicate label → FAIL (fail-closed) | unit | `dotnet test tests/BaseApi.Tests -- --filter-method "*PassFailEngineFacts.Duplicate*"` | ✅ | ✅ green |
| OBS-03 | Prom reconciliation: balanced→pass, unaccounted delta→FAIL | unit (synthetic `PromCounterSnapshot`) | `dotnet test tests/BaseApi.Tests -- --filter-method "*PassFailEngineFacts.Reconcile*"` | ✅ | ✅ green |
| OBS-04 | report JSON written BEFORE assert; verdict matches engine | integration + RealStack | `dotnet test tests/BaseApi.Tests -- --filter-class "BaseApi.Tests.Observability.AnalyzerE2ETests"` | ✅ | ⚠️ live-gate (deferred — see Manual-Only) |
| item #3 | `SearchAllHits` returns N hits, groups by correlationId correctly | unit vs captured ES `_search` JSON fixture | `dotnet test tests/BaseApi.Tests -- --filter-class "BaseApi.Tests.Observability.Helpers.ElasticsearchTestClientFacts"` | ✅ | ✅ green |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky/live-gate*

**Audited run (2026-06-14):** `dotnet test … --filter-class PassFailEngineFacts --filter-class ElasticsearchTestClientFacts` → **Passed! Failed: 0, Passed: 10, Total: 10, 1.47s**. All 5 hermetic decision-branch rows confirmed green by direct execution (not just SUMMARY hearsay).

### Decision-branch signals (each branch provably exercised hermetically)

- **COMPLETE:** run with all 9 labels → `report.CompleteRuns == 1`.
- **MISSING:** run missing `Step_F2` → `report.Missing >= 1`, `Verdict.Fail`, missing label in `report.MissingDetail`.
- **DUPLICATE/fail-closed:** run with two `Step_C` hits → `Verdict.Fail` reason "unaccountable duplicate"; `report.Duplicates` non-empty (proves item #2 fail-closed wiring — dedupe counters are dormant, so any duplicate fails).
- **RECONCILE-FAIL:** `PromCounterSnapshot{dispatch_sent=10, complete=10, result_sent_completed short by 1}` → `Verdict.Fail` reason "unreconciled"; `report.Reconciliation == Unreconciled`.
- **RECONCILE-PASS:** balanced snapshot + all-complete runs → `report.Verdict == Pass`.
- **Window/drain (RealStack only):** TEST-01 happy-path live run → `Verdict.Pass`, `Missing == 0`.

---

## Wave 0 Requirements

- [x] `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` — covers OBS-01/02/03 decision branches with synthetic inputs. **(6 facts, green)**
- [x] `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClientFacts.cs` — covers `SearchAllHits` grouping against a captured `_search` JSON fixture. **(4 facts, green)**
- [x] ES `_mapping` Wave-0 probe — `@timestamp` confirmed `type:date` (A2, baked into `EsIndexNames.WindowTimestampFieldPath`); `attributes.Sum` unmapped at probe → read defensively (A1). Recorded in 66-03-SUMMARY + const doc-comment.
- [x] Confirm Prom counter windowing strategy vs Phase 65/67 reset/restart behavior (A3) — `scripts/phase-65-reset.ps1` = FLUSHALL+heal, **no container restart** → counters lifetime-cumulative → windowed before/after delta. Locked into `BuildSnapshot`.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| MISSING-run *attribution* (which correlationId fired-but-vanished) | OBS-02 | Per-fire correlationId is NOT observable from existing telemetry (research item #1 — fallback to `orchestrator_dispatch_sent_total` + cadence). The *count* of missing runs is detectable; the *identity* is not. | Documented as an accepted limitation; the analyzer reports the count and the unrecoverable-identity caveat. |
| OBS-04 live PASS/FAIL verdict against real telemetry (`AnalyzerE2ETests`) | OBS-04 | RealStack fixture: a green verdict requires the full compose stack WITH the Phase 65/67 fan-out workflow seeded + firing (~10 runs over a window). A bare live run with no seeded window yields `triggerCount=0` (vacuous, not a meaningful gate). Structure, wiring, write-then-assert ordering, and path-traversal guard are all hermetically verified by inspection (66-VERIFICATION truths 6,15,16). | Deferred to the Phase 67/68 fault-injection harness, which is the fixture's intended entrypoint. Live gate command in the infra table. |

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references (no MISSING gaps — all hermetic tests exist & green)
- [x] No watch-mode flags
- [x] Feedback latency < 2s (hermetic sample ran in 1.47s)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** ✅ validated 2026-06-14 — 5/6 requirement rows automated & green by direct execution; OBS-04 is a documented live gate (Phase 67 entrypoint), not a coverage gap.

---

## Validation Audit 2026-06-14
| Metric | Count |
|--------|-------|
| Requirement rows | 6 |
| COVERED (automated, green) | 5 |
| Live-gate (deferred to Phase 67) | 1 (OBS-04) |
| Gaps found (MISSING) | 0 |
| Resolved | 0 (no test generation needed) |
| Escalated | 0 |

**Method:** State A audit. Existing VALIDATION.md was a planning-time draft (all rows `⬜ pending`, files `❌ W0`, `nyquist_compliant: false`). All 9 planned artifacts confirmed on disk; the 2 hermetic fact classes (10 facts) executed live → 10/10 green in 1.47s. No `gsd-nyquist-auditor` spawn required — no MISSING gaps. Statuses, frontmatter, filter-command syntax (legacy VSTest → MTP), Wave-0 checkboxes, and Manual-Only updated to reflect verified reality.
