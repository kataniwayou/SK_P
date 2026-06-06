---
phase: 39-keeper-observability-real-stack-e2e-close-gate
plan: 01
subsystem: infra
tags: [opentelemetry, metrics, prometheus, keeper, imeterfactory, histogram, instrumentadvice, diagnosticsource]

# Dependency graph
requires:
  - phase: 34-keeper-console-foundation
    provides: runnable Keeper console + thin-shell Program.cs (AddBaseConsoleObservability metrics-only MeterProvider, the AddSingleton<L2ProbeRecovery> insertion anchor)
  - phase: 30-processor-metrics-service-instance-labels
    provides: the OrchestratorMetrics IMeterFactory house pattern (const-to-AddMeter symmetry) copied here
provides:
  - "Keeper.Observability.KeeperMetrics — the code-defined \"Keeper\" IMeterFactory meter holding all 8 Phase-39 instruments (6 Counter<long> + keeper_in_flight UpDownCounter<long> + keeper_recovery_duration Histogram<double>)"
  - "KeeperMetricTags — interned closed-enum tag keys/values (fault_type/outcome/reason/ProcessorId) + FaultTags(...) DRY helper for Plan 02's two consumers"
  - "Program.cs const-to-AddMeter symmetry: AddSingleton<KeeperMetrics>() + ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(KeeperMetrics.MeterName))"
  - "LOCKED bucket route for Plans 02/03: Route A (InstrumentAdvice) — DiagnosticSource resolves to 10.0.0; histogram unit \"s\""
affects: [39-02-keeper-metric-instrumentation, 39-03-prometheus-scrape, keeper-observability]

# Tech tracking
tech-stack:
  added: []   # no new package; System.Diagnostics.Metrics (DiagnosticSource 10.0.0) is transitive via OTel 1.15.3
  patterns:
    - "Keeper meter mirrors the OrchestratorMetrics IMeterFactory house shape (sealed class, ctor meterFactory.Create, no static Meter — D-01 test-isolation invariant)"
    - "Histogram custom buckets via InstrumentAdvice<double>{HistogramBucketBoundaries=...} co-located on the instrument (Route A), not split into an OTel AddView"
    - "Interned closed-enum metric labels (KeeperMetricTags) decoupled from C# enum member names — copied from EntryStepDispatchConsumer.OutcomeLabel switch shape"

key-files:
  created:
    - src/Keeper/Observability/KeeperMetrics.cs
    - tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs
  modified:
    - src/Keeper/Program.cs

key-decisions:
  - "Wave-0 gate resolved: transitive System.Diagnostics.DiagnosticSource = 10.0.0 (>= 9.0.0) -> Route A (InstrumentAdvice) LOCKED for Plans 02/03; no OTel AddView in Program.cs"
  - "Histogram unit = \"s\" (second-scale) -> Plan 03 predicts the Prometheus suffix keeper_recovery_duration_seconds_* (collector add_metric_suffixes)"
  - "All 8 instrument names are snake_case with NO _total/_seconds/_count embedded (collector appends suffixes)"
  - "Cardinality (KMET-02/T-39-01): ProcessorId-only bounded label; deliberately NO workflowId/correlationId key — grep + MeterListener test-asserted"

patterns-established:
  - "KeeperMetricTags interned label vocabulary: fault_type{dispatch,result}, outcome{recovered,gave_up}, reason{probe_exhausted}, ProcessorId (\"D\" guid) — the closed enum Plan 02 consumers emit"

requirements-completed: [KMET-01, KMET-03]

# Metrics
duration: 6min
completed: 2026-06-06
---

# Phase 39 Plan 01: Keeper Meter Definition Summary

**Code-defined `Keeper` IMeterFactory meter (`KeeperMetrics`) with all 8 Phase-39 instruments — 6 counters, the `keeper_in_flight` UpDownCounter, and the repo's first histogram `keeper_recovery_duration` with `{1,5,10,30,60,120}`s buckets via `InstrumentAdvice<double>` (Route A, DiagnosticSource 10.0.0) — DI-registered in Program.cs with the const-to-AddMeter symmetry.**

## Performance

- **Duration:** ~6 min
- **Started:** 2026-06-06T10:26Z
- **Completed:** 2026-06-06T10:32Z
- **Tasks:** 2 (Task 1 verification-only Wave-0 gate; Task 2 TDD)
- **Files modified:** 3 (2 created, 1 modified)

## Accomplishments
- **Wave-0 gate run + LOCKED:** `dotnet list src/Keeper/Keeper.csproj package --include-transitive` resolves `System.Diagnostics.DiagnosticSource` to **10.0.0** (>= 9.0.0) -> **Route A (advice)** chosen. The histogram's custom buckets ride on `InstrumentAdvice<double>` co-located on the instrument; no OTel `AddView` is needed in `Program.cs`. This shape is fixed for Plans 02/03.
- **`KeeperMetrics.cs` authored** (namespace `Keeper.Observability`): `public const string MeterName = "Keeper"`, IMeterFactory ctor (no `static Meter`), exactly 6 `Counter<long>` (FaultConsumed/Recovered/DlqPushed/WorkflowPaused/WorkflowResumed/L2ProbeFailed) + 1 `UpDownCounter<long>` (InFlight=`keeper_in_flight`) + 1 `Histogram<double>` (RecoveryDuration=`keeper_recovery_duration`, unit `"s"`, advice buckets).
- **`KeeperMetricTags` interned helper** with the closed-enum tag keys/values + a `FaultTags(faultType, procId)` DRY builder, so Plan 02's two consumers don't re-spell the tag arrays. ProcessorId-only cardinality.
- **`Program.cs` const-to-AddMeter symmetry** wired alongside `AddSingleton<L2ProbeRecovery>()`, mirroring `Orchestrator/Program.cs:72-73`.

## Task Commits

1. **Task 2 (TDD RED): KeeperMetrics hermetic guard** - `aba2079` (test)
2. **Task 2 (TDD GREEN): author KeeperMetrics + Program.cs registration** - `c09e33b` (feat)

_Task 1 (Wave-0 DiagnosticSource version check) is verification-only — no source file, no commit; its result is recorded in this SUMMARY. No REFACTOR commit needed (implementation clean as authored)._

**Plan metadata:** _(this SUMMARY + STATE/ROADMAP commit)_

## Files Created/Modified
- `src/Keeper/Observability/KeeperMetrics.cs` (NEW) - the `Keeper` IMeterFactory meter + 8 instruments + `KeeperMetricTags` interned labels/`FaultTags` helper
- `src/Keeper/Program.cs` (MODIFY) - `AddSingleton<KeeperMetrics>()` + `ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(KeeperMetrics.MeterName))` + 2 usings (`Keeper.Observability`, `OpenTelemetry.Metrics`)
- `tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs` (NEW) - 6 hermetic facts: MeterName const, 8 instruments non-null, exact snake_case names + no-suffix, second-scale advice buckets via `Instrument.Advice`, ProcessorId-only cardinality (MeterListener), interned-label literals

## Decisions Made
- **Bucket route = A (advice), LOCKED.** The single authoritative `dotnet list ... --include-transitive` check returned DiagnosticSource 10.0.0, so `InstrumentAdvice<double>{HistogramBucketBoundaries=new double[]{1,5,10,30,60,120}}` compiles on net8.0 and keeps the bucket config co-located with the instrument (no `Program.cs` `AddView`). Plans 02/03 build against this fixed shape.
- **Histogram unit = `"s"`.** Plan 03 should expect the Prometheus series `keeper_recovery_duration_seconds_bucket/_sum/_count` (the collector's `add_metric_suffixes` appends `_seconds`/`_count`).
- **Test seam for buckets:** read `RecoveryDuration.Advice?.HistogramBucketBoundaries` straight off the live instrument (the `Instrument.Advice` property is exposed in DiagnosticSource >= 9.0.0) — no MeterProvider build needed to assert the boundaries.

## Deviations from Plan

None - plan executed exactly as written. (The `static Meter` and `workflowId` string matches in the acceptance-criteria greps appear ONLY inside XML doc-comment prose explaining what is deliberately absent — there is no `static Meter` field and no `workflowId`-keyed tag in code; the intent of both criteria is satisfied and the MeterListener cardinality test proves it at runtime.)

## Issues Encountered
- `ConfigureOpenTelemetryMeterProvider` needed `using OpenTelemetry.Metrics;` in `Program.cs` (same extension namespace the Orchestrator imports) — added alongside `using Keeper.Observability;`. Caught at first Release build; resolved before commit (folded into the GREEN commit, not a runtime deviation).

## Verification
- **Wave-0:** `dotnet list src/Keeper/Keeper.csproj package --include-transitive | grep DiagnosticSource` -> `System.Diagnostics.DiagnosticSource 10.0.0`.
- **Build:** `dotnet build src/Keeper/Keeper.csproj -c Release` -> **0 Warning / 0 Error** (TreatWarningsAsErrors).
- **Hermetic test:** `BaseApi.Tests.exe --filter-class BaseApi.Tests.Keeper.KeeperMetricsFacts` -> **6/6 passed, 0 failed** (323ms). RED proven first (CS0234 — `Keeper.Observability` absent) at `aba2079`, GREEN at `c09e33b`.
- **Grep gates:** no embedded `_total`/`_seconds` in instrument-name string literals; bucket array `{ 1, 5, 10, 30, 60, 120 }` present in `KeeperMetrics.cs`; `AddSingleton<KeeperMetrics>()` + `AddMeter(KeeperMetrics.MeterName)` present in `Program.cs`.
- **No file deletions** (`git diff --diff-filter=D HEAD~2 HEAD` empty); pre-existing untracked items (`.claude/`, `27-PATTERNS.md`, `psql-*.txt`, `launchSettings.json`) left untouched.

## Known Stubs
None — `KeeperMetrics` is a complete instrument definition. The instruments are not yet *incremented* anywhere; that wiring is Plan 02's explicit scope (the consumers + probe loop), not a stub.

## TDD Gate Compliance
- RED gate: `aba2079` (`test(39-01)`) — failing compile before implementation. ✓
- GREEN gate: `c09e33b` (`feat(39-01)`) — implementation makes the suite pass. ✓
- REFACTOR gate: none needed (no cleanup required). ✓

## Next Phase Readiness
- Plan 02 (Wave 2) can thread `KeeperMetrics` into the two fault consumers + `L2ProbeRecovery` using the locked instruments + `KeeperMetricTags`/`FaultTags` helper. The histogram bucket route (advice) and unit (`"s"`) are fixed.
- Plan 03 (Prometheus scrape) should assert the suffixed series: `keeper_*_total` (counters), `keeper_in_flight` (gauge), `keeper_recovery_duration_seconds_{bucket,sum,count}` with the `{1,5,10,30,60,120}` boundaries.

## Self-Check: PASSED
- FOUND: src/Keeper/Observability/KeeperMetrics.cs
- FOUND: tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs
- FOUND: .planning/phases/39-keeper-observability-real-stack-e2e-close-gate/39-01-SUMMARY.md
- FOUND commit: aba2079 (test RED)
- FOUND commit: c09e33b (feat GREEN)

---
*Phase: 39-keeper-observability-real-stack-e2e-close-gate*
*Completed: 2026-06-06*
