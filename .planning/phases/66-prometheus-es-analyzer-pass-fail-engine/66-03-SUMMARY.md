---
phase: 66-prometheus-es-analyzer-pass-fail-engine
plan: 03
subsystem: testing
tags: [elasticsearch, prometheus, otel, observability, realstack, analyzer, obs-04]

# Dependency graph
requires:
  - phase: 66-01
    provides: "PassFailEngine.Analyze + RunTrace.FromLabels + PromCounterSnapshot + AnalyzerReport (pure analyzer core)"
  - phase: 66-02
    provides: "ElasticsearchTestClient.SearchAllHits + EsIndexNames.StepLabelFieldPath/SumFieldPath/CorrelationIdFieldPath"
provides:
  - "AnalyzerE2ETests — RealStack OBS-04 fixture: gather ES Step_* hits + windowed-delta Prom counters -> PassFailEngine -> write JSON report -> assert verdict"
  - "EsIndexNames.WindowTimestampFieldPath — Wave-0-verified @timestamp <date> const for the analyzer window range filter"
  - "analyzer-reports/{scenarioId}.json + .txt — the per-scenario self-verifying report artifact (written before assert)"
affects: [67, 68, fault-injection-harness, OBS-01, OBS-02, OBS-03, OBS-04]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Windowed-delta Prom read: before/after CounterSet snapshots subtracted (counters are lifetime cumulative — FLUSHALL+heal, no restart)"
    - "Write-then-assert: serialize+write report to disk BEFORE the xUnit verdict assert so the artifact exists on a red run"
    - "Poll-to-stable ES drain: SearchAllHits repeated until hit count unchanged across two polls (no in-flight run scored MISSING)"
    - "Scenario-id whitelist (^[A-Za-z0-9_-]+$) validated before composing the report path (path-traversal guard)"

key-files:
  created:
    - tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs
  modified:
    - tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs

key-decisions:
  - "Wave-0 probes run against the LIVE ES (curl _mapping/field): @timestamp confirmed type:date (A2 baked into WindowTimestampFieldPath); attributes.Sum unmapped at probe (no Step_* docs yet) -> read defensively (A1)"
  - "Prom windowing strategy = before/after windowed DELTA (snapshot subtraction). scripts/phase-65-reset.ps1 confirmed FLUSHALL+heal with NO container restart -> counters lifetime cumulative (A3 / Pitfall 3)"
  - "Trigger denominator = orchestrator_dispatch_sent_total windowed delta (rounded). No per-fire correlationId log exists (item #1): missing COUNT is detectable, missing IDENTITY is not — accepted limitation, surfaced by the engine"
  - "Dormant dedupe counters (orchestrator_result_deduped_total, processor_dispatch_deduped_total) queried; an EMPTY vector maps to null (absent) and feeds no reconciliation arithmetic"
  - "Read-only factory: OMITS the L2KeysToCleanup/parent-index/composite-key net-zero sweep (the seeder owns those writes); the only write is the JSON report file"

patterns-established:
  - "RealStack analyzer shell: gather real ES+Prom telemetry -> pure engine -> write-then-assert; exit code + JSON report are the Phase 67/68 harness contract"
  - "Wave-0 probe-then-bake: empirically confirm an ES field via _mapping/field, bake the verified value into an EsIndexNames const with the probe command recorded in the doc-comment"

requirements-completed: [OBS-01, OBS-02, OBS-03, OBS-04]

# Metrics
duration: 9min
completed: 2026-06-14
---

# Phase 66 Plan 03: AnalyzerE2ETests RealStack Pass/Fail Fixture Summary

**The RealStack OBS-04 fixture that gathers REAL telemetry — `Step_*` ES hits grouped into per-run `RunTrace`s by `attributes.CorrelationId` + the live Prometheus counter set read as WINDOWED DELTAS — feeds the pure `PassFailEngine`, writes the JSON report to disk, and THEN asserts the verdict (a FAIL ⇒ non-zero `dotnet test` exit); plus a Wave-0-verified `@timestamp` window-range const.**

## Performance

- **Duration:** ~9 min
- **Started:** 2026-06-14T13:15:36Z
- **Completed:** 2026-06-14T13:24Z
- **Tasks:** 2
- **Files:** 2 (1 created, 1 modified)

## Accomplishments

### Task 1 (Wave 0) — probe-then-bake the window timestamp field + confirm Prom windowing
- **A2 (window timestamp):** probed the LIVE ES — `GET http://localhost:9200/logs-generic.otel-default/_mapping/field/@timestamp` returned `{"@timestamp":{...,"mapping":{"@timestamp":{"type":"date","ignore_malformed":false}}}}`. `@timestamp` is present and a `date` type. Baked into `EsIndexNames.WindowTimestampFieldPath = "@timestamp"` with the probe command + observed result recorded in the doc-comment (consume the truth, not the prediction).
- **A1 (Sum type):** probed `_mapping/field/attributes.Sum` → EMPTY mapping (no `Step_*` sum docs had been indexed in the live data stream at probe time, so the dynamic field is not yet present). The ECS dynamic template maps numeric attributes to `long` (cf. `attributes.ContentLength` → `long`), so `attributes.Sum` will surface numeric once Step_* docs land. The fixture reads it DEFENSIVELY (`TryGetInt32` then `GetString`+parse) regardless — Sum is informational, never a completeness gate.
- **A3 (Prom windowing):** inspected `scripts/phase-65-reset.ps1`. It does **FLUSHALL + heal-wait + psql DELETE only — NO `docker compose down`, NO container restart** (explicit: "Stack stays UP"). Counters are therefore process-lifetime **cumulative** → the fixture (Task 2) windows them via before/after **snapshot-delta**. (Phase 67 caveat documented: a crashed+restarted tier mid-window resets its counters, breaking delta continuity — which is why ES-primary completeness is the binding arbiter and Prom reconciliation is corroborating only.)

### Task 2 — AnalyzerE2ETests RealStack fixture (OBS-04)
- `[Trait("Category","RealStack")]` + `[Collection("Observability")]` — excluded by the hermetic filter, serialized against shared ES/Prom backends.
- **OBS-01:** `SearchAllHits` over a window-bounded `Step_*`-family `_search` (static raw-string template, `size:2000`, `exists` StepLabel filter + `@timestamp` range, sort asc; ALL field paths from `EsIndexNames` consts — no `.keyword`); grouped by `attributes.CorrelationId` into `RunTrace.FromLabels` (duplicates retained).
- **OBS-02:** completeness + fail-closed duplicate scoring delegated to the Plan-01 engine.
- **OBS-03:** before/after `CounterSet` snapshots → windowed deltas for the 5 live counters; the 2 dormant dedupe counters queried and mapped to `null` (absent) when the vector is empty; non-completed outcomes (failed/cancelled/processing) read per-outcome as deltas.
- **OBS-04 / write-then-assert:** `File.WriteAllTextAsync(reportPath, json)` (+ a `.txt` HumanSummary) runs BEFORE `Assert.True(report.Verdict == Verdict.Pass)` — the artifact exists even on a red run, and the persisted report + exit code reflect the SAME report object.
- **Security V5 / T-66-07:** `scenarioId` validated against `^[A-Za-z0-9_-]+$` BEFORE composing the report path; path composed only under a fixed `analyzer-reports` dir.
- **D-04 / item #1:** `triggerCount` derived from the `orchestrator_dispatch_sent_total` windowed delta (rounded); the missing-IDENTITY limitation (no per-fire correlationId log) is carried by the engine's `MissingDetail`.

## Task Commits

1. **Task 1 (Wave 0): bake WindowTimestampFieldPath const** — `0514d91` (feat)
2. **Task 2: AnalyzerE2ETests RealStack fixture (OBS-04)** — `e6ea260` (feat)

## Files Created/Modified

- `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` — added `WindowTimestampFieldPath = "@timestamp"` (Wave-0-verified `<date>`) with the probe command + mapping result + Sum-type-A1 note in the doc-comment.
- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` — the RealStack OBS-04 fixture: window-bounded `Step_*` ES read → per-run traces, windowed-delta Prom counter set, engine call, write-then-assert, path-traversal guard.

## Decisions Made

- **Wave-0 probes run live, not predicted** — `@timestamp` type confirmed by an actual `_mapping/field` curl against the running ES; the const value is the empirically-verified field, not a guess.
- **Windowed delta over raw cumulative** — confirmed from the reset script that no service restart occurs, so the only meaningful Prom reconciliation is a before/after subtraction. The `CounterSet` record holds raw reads; `BuildSnapshot` subtracts.
- **Dormant ≠ zero** — `SumOrNullAsync` distinguishes an EMPTY vector (dormant dedupe counter, no series → `null`/absent) from a present-but-zero LIVE counter (`SumOrZeroAsync` → `0`), so the engine's "feeds no arithmetic" contract for dormant counters holds.
- **Read-only factory** — the analyzer writes no Redis state, so the net-zero L2 sweep from `MetricsRoundTripE2ETests` is intentionally omitted; only the report file is written (under the local test output dir).

## Deviations from Plan

None — plan executed exactly as written. The `.keyword` acceptance grep returns matches only in doc-comment warning text (`<c>.keyword</c>`); no query body or field path uses a `.keyword` sub-field (verified — the only occurrence is the documentation cautioning against the Phase 11 trap).

## Verification

- `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug` → **Build succeeded** (0 warnings) after each task.
- `EsIndexNames.cs` carries `WindowTimestampFieldPath` (grep confirmed).
- `AnalyzerE2ETests.cs`: `WriteAllTextAsync` (lines 130-131) precedes `Assert.True(report.Verdict == Verdict.Pass` (line 134) — write-then-assert ordering held by line order; regex literal `^[A-Za-z0-9_-]+$` present (line 70) and validated before path compose; `dispatch_sent_total` read + `DispatchSentDelta` rounded to triggerCount; both dormant dedupe counters queried via `SumOrNullAsync`; no `.keyword` in any query.
- **Live gate (NOT run this plan):** `dotnet test --filter "Category=RealStack&FullyQualifiedName~Analyzer"` is the Phase 67/68 harness entrypoint. AnalyzerE2ETests is a RealStack fixture that requires the full compose stack WITH the fan-out workflow seeded + firing (~10 runs over a window) to reach a green verdict — that seeding/firing is the Phase 65/67 harness's job, not this plan's. Build + structure are verified hermetically; the live ES `@timestamp`/Sum probes ran successfully against the up stack. A bare live run with no seeded fan-out window would correctly produce `triggerCount=0` and is not a meaningful PASS gate here.

## Threat Surface

No new security-relevant surface beyond the plan's `<threat_model>`:
- **T-66-07** (Tampering, report path): `scenarioId` whitelisted against `^[A-Za-z0-9_-]+$` before path composition; fixed `analyzer-reports` dir — mitigation present.
- **T-66-08** (Tampering, query body): ES body is a STATIC raw-string template with only validated window timestamps interpolated + `EsIndexNames` const field paths; PromQL goes through `PrometheusTestClient` (`Uri.EscapeDataString`). No hand-rolled concat over untrusted input.
- **T-66-09** (false-RED / DoS): defensive JSON parsing throughout — hits missing `_source`/`attributes`/`CorrelationId`/`StepLabel` are skipped (never thrown); `SearchAllHits` returns `[]` on non-success; Sum read tolerantly. A genuinely unreachable Prom backend fails LOUD via `QueryPrometheus`'s `Assert.Fail` (a real RED, not a false one).
- **T-66-11** (Repudiation): write-then-assert — the same `report` object is serialized to disk and then asserted, so artifact and exit code always agree.

## Next Phase Readiness

- The OBS-04 self-verifying fixture is ready for the Phase 67/68 fault-injection harness to invoke per scenario (parameterizing scenarioId + window timing), reading the exit code + JSON report.
- Phase 66 (Prometheus/ES analyzer pass/fail engine) is complete: 66-01 (engine core) + 66-02 (SearchAllHits) + 66-03 (RealStack fixture). OBS-01..04 satisfied.
- No blockers.

---
*Phase: 66-prometheus-es-analyzer-pass-fail-engine*
*Completed: 2026-06-14*

## Self-Check: PASSED
- Files: AnalyzerE2ETests.cs, EsIndexNames.cs, 66-03-SUMMARY.md — all FOUND.
- Commits: 0514d91 (feat, Task 1), e6ea260 (feat, Task 2) — all FOUND.
