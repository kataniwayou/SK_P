# Phase 66: Prometheus + ES Analyzer & PASS/FAIL Engine - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-14
**Phase:** 66-prometheus-es-analyzer-pass-fail-engine
**Areas discussed:** Artifact form & invocation; Completeness & MISSING/DUPLICATE model; Effect-once vs redelivery rule; Prometheus role + report/verdict output

---

## Artifact form & invocation

| Option | Description | Selected |
|--------|-------------|----------|
| C# RealStack E2E fixture | Reuse ElasticsearchTestClient + PrometheusTestClient, `dotnet test --filter`, matches Phase 65 seeder | ✓ |
| PowerShell script | scripts/phase-66-analyze.ps1; re-implements ES/Prom knowledge | |
| Python script | New runtime; re-derives field-shape constants | |

**User's choice:** C# RealStack E2E fixture.
**Notes:** Strong precedent — Phase 65 seeder (D-01/D-02), existing observability clients, anti-desync discipline.

| Option | Description | Selected |
|--------|-------------|----------|
| Test exit code + written report file | xUnit-assert pass bar (red on FAIL) AND always write report (write-then-assert) | ✓ |
| Report file only (no assert) | Fixture always passes; verdict only in report JSON | |

**User's choice:** Test exit code + written report file.

---

## Completeness & MISSING/DUPLICATE model

| Option | Description | Selected |
|--------|-------------|----------|
| Processor Step_<label> log | Single Phase-64 log per (corrId,label) = completed-effect signal; complete = all 9 labels | ✓ |
| Orchestrator result-consumed log | Use orchestrator step-advance log instead | |
| Both (processor AND orchestrator) | Require both signals | |

**User's choice:** Processor Step_<label> log.

| Option | Description | Selected |
|--------|-------------|----------|
| Orchestrator per-fire correlationId set | Distinct per-fire correlationIds from existing dispatch telemetry; sees zero-log fires | ✓ |
| Distinct correlationIds at Step_A in ES | One field, but blind to fires whose entry step never logged | |
| Window/cadence math (~10) | Independent but can't attribute which correlationId is missing | |

**User's choice:** Orchestrator per-fire correlationId set.
**Notes:** Flagged research item #1 — exact orchestrator log/field; no new product log may be added (v8 scope discipline).

| Option | Description | Selected |
|--------|-------------|----------|
| Post-window settle/drain period | Bounded drain after window, then snapshot; every fire judged | ✓ |
| Exclude boundary triggers | Drop last N triggers from MISSING accounting | |
| Claude's discretion (settle value) | Lock settle-then-analyze, defer exact duration | |

**User's choice:** Post-window settle/drain period (exact duration = Claude's discretion via research).

---

## Effect-once vs redelivery rule

| Option | Description | Selected |
|--------|-------------|----------|
| Reconcile vs dedupe counters | Dedupe-corroborated duplicate = reported redelivery; unaccounted = FAIL | ✓ |
| Any ES duplicate = hard FAIL | Treat the log as the effect; strict | |

**User's choice:** Reconcile vs dedupe counters.
**Notes:** Research item #2 — processor dedup-gate position vs the Step_* log; if log is post-dedup, rule collapses to "any duplicate = FAIL".

| Option | Description | Selected |
|--------|-------------|----------|
| Fail-closed | Unexplained/ambiguous duplicate or imbalance defaults to FAIL | ✓ |
| Fail-open (report-only) | Ambiguous cases reported, don't fail | |

**User's choice:** Fail-closed.

---

## Prometheus role + report/verdict output

| Option | Description | Selected |
|--------|-------------|----------|
| ES-primary + Prom reconciliation gate | ES primary arbiter; unreconciled Prom imbalance = FAIL, accounted = reported | ✓ |
| ES-only verdict, Prom corroborating | PASS/FAIL purely from ES; Prom never independently fails | |
| Co-equal hard thresholds | Both apply fixed thresholds; brittle across 7 scenarios | |

**User's choice:** ES-primary + Prom reconciliation gate.

| Option | Description | Selected |
|--------|-------------|----------|
| JSON (machine) + human-readable summary | Structured JSON harness parses + markdown/console trace; per-test by scenario id | ✓ |
| JSON only | Single artifact; humans read raw JSON | |
| Console/text only | stdout + exit code; no structured artifact | |

**User's choice:** JSON (machine) + human-readable summary.

## Claude's Discretion

- ES multi-hit aggregation mechanism (Research item #3 — extend ElasticsearchTestClient).
- Settle/drain duration + poll intervals.
- Report file path/dir, JSON schema, scenario-id parameterization.
- Non-`completed` outcome surfacing; multi-replica attribution (informational).

## Deferred Ideas

None — discussion stayed within phase scope.
</content>
