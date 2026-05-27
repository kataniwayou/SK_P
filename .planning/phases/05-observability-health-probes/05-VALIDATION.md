---
phase: 5
slug: observability-health-probes
status: draft
nyquist_compliant: true
wave_0_complete: false
created: 2026-05-27
last_updated: 2026-05-27
---

# Phase 5 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Populated by gsd-planner from RESEARCH.md `## Validation Architecture` + PATTERNS.md analog set.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 3.2.2 (MTP runner) — pinned in Directory.Packages.props (Phase 1 D-13) |
| **Test project** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (single test assembly; Phase 5 adds files under `tests/BaseApi.Tests/Observability/`) |
| **Real backends** | Phase 2 `postgres:17-alpine` container at `localhost:5433` + Plan 05-01 `otel-collector` container at `localhost:4317` + `localhost:13133` (healthcheck) |
| **Test serialization** | `[CollectionDefinition("Observability", DisableParallelization = true)]` (Plan 05-02 Task 2) — prevents `tests/.otel-out/telemetry.jsonl` interleave |
| **Force-flush mechanism** | `OtelCollectorFixture` registers `ExportProcessorType.Simple` on `OtlpExporterOptions` + calls `ForceFlush(timeoutMs: 5_000)` on MeterProvider/TracerProvider/LoggerProvider before reads |
| **Quick run command** | `dotnet test --filter "FullyQualifiedName~Observability" --no-restore` |
| **Full suite command** | `dotnet test SK_P.sln --no-restore` |
| **Estimated runtime** | ~30-60s cold (Compose containers must be up beforehand — no Testcontainers in Phase 5 per CONTEXT/RESEARCH); ~15s warm |

---

## Sampling Rate

- **After every task commit:** Run quick command for the touched area (`--filter "FullyQualifiedName~LogExportTests"`, etc.)
- **After every plan wave:** Run full suite command
- **Before SUMMARY commit:** Full suite must be green across 3 consecutive runs (Phase 4 cadence)
- **Max feedback latency:** ~60 seconds (cold), ~5 seconds (single fact test, warm)

---

## Per-Task Verification Map

> One row per task in 05-01-PLAN.md (Wave 1 build) + 05-02-PLAN.md (Wave 2 verification).

| Task ID | Plan | Wave | Requirements | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|--------------|------------|-----------------|-----------|-------------------|-------------|--------|
| task-1-cpm-pins | 05-01 | 1 | OBSERV-01, OBSERV-12, HEALTH-04 | — | CPM contract preserved | restore + grep | `dotnet restore SK_P.sln && grep -c 'OpenTelemetry.Instrumentation.Runtime\|Npgsql.OpenTelemetry\|AspNetCore.HealthChecks.UI.Client' Directory.Packages.props` (expect ≥3) | `Directory.Packages.props` | ⬜ pending |
| task-2-csproj-package-refs | 05-01 | 1 | OBSERV-01, OBSERV-12, HEALTH-04 | — | Zero `Version=` attrs on PackageReference | restore + grep | `dotnet restore src/BaseApi.Core/BaseApi.Core.csproj && grep -E 'Version="[^"]*"' src/BaseApi.Core/BaseApi.Core.csproj` (expect 0 hits) | `src/BaseApi.Core/BaseApi.Core.csproj` | ⬜ pending |
| task-3-health-types | 05-01 | 1 | HEALTH-01, HEALTH-02, HEALTH-03 | — | `public sealed` consistency; thread-safe latch | build + grep | `dotnet build src/BaseApi.Core/BaseApi.Core.csproj -c Debug --no-restore && grep -c 'public sealed class\|public interface IStartupGate' src/BaseApi.Core/Health/*.cs` (expect ≥4) | `src/BaseApi.Core/Health/IStartupGate.cs`, `StartupHealthCheck.cs`, `StartupCompletionService.cs` | ⬜ pending |
| task-4-compose-collector-config | 05-01 | 1 | OBSERV-04, OBSERV-12 | T-05-OTLP-EXFIL | Bind-mount target exists pre-up | compose config + ls | `docker compose config --quiet && Test-Path compose/otel-collector-config.yaml && Test-Path tests/.otel-out/.gitkeep` | `compose.yaml`, `compose/otel-collector-config.yaml`, `tests/.otel-out/.gitkeep` | ⬜ pending |
| task-5-program-cs-edit | 05-01 | 1 | OBSERV-01..08, OBSERV-12, HEALTH-01..05 | T-05-PII, T-05-OTLP-EXFIL | Bare `.AddNpgsql()` (Reconciliation 1); MEL bridge (no `WithLogging`); `AddHostedService<StartupCompletionService>` (Reconciliation 2) | build + grep | `dotnet build SK_P.sln -c Release --no-restore && grep -rn 'WithLogging' src/ tests/` (expect 0) `&& grep -n '.AddNpgsql(opts' src/BaseApi.Service/Program.cs` (expect 0) `&& grep -n 'AddHostedService<StartupCompletionService>' src/BaseApi.Service/Program.cs` (expect 1) | `src/BaseApi.Service/Program.cs` | ⬜ pending |
| task-6-gitignore-edit | 05-01 | 1 | (cleanup discipline) | — | `tests/.otel-out/.gitkeep` whitelisted | git check-ignore | `git check-ignore tests/.otel-out/telemetry.jsonl` (expect exit 0) | `.gitignore` | ⬜ pending |
| task-7-build-verification | 05-01 | 1 | (all Phase 5 IDs) | — | 0/0 Release + Debug | full build sweep | `dotnet build SK_P.sln -c Release --no-restore && dotnet build SK_P.sln -c Debug --no-restore` (both 0 errors 0 warnings) | (no new file) | ⬜ pending |
| task-1-collector-up | 05-02 | 0 | (env prep) | — | Both services healthy before tests | docker compose ps | `docker compose ps --format json` (both postgres+otel-collector show Health: healthy) | `.planning/phases/05-observability-health-probes/05-02-psql-BEFORE.snapshot.txt` | ⬜ pending |
| task-2-fixture-and-controller | 05-02 | 0 | (Wave 0) | T-05-OTLP-EXFIL | OTel endpoint pinned to localhost:4317 | build + grep | `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug --no-restore && grep -n 'ExportProcessorType.Simple\|OTEL_EXPORTER_OTLP_ENDPOINT' tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` (expect ≥2 hits) | `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs`, `CollectionDefinitions.cs`, `TestObservabilityController.cs` | ⬜ pending |
| task-3-log-export-tests | 05-02 | 2 | OBSERV-02, OBSERV-05, OBSERV-07 | T-05-LOG-INJECT (delegate) | SC#1 — CorrelationId + service.name + service.version on log; sanitization survives | dotnet test filter | `dotnet test --filter "FullyQualifiedName~LogExportTests" --no-restore` (Passed: 2) | `tests/BaseApi.Tests/Observability/LogExportTests.cs` | ⬜ pending |
| task-4-loglevel-filter-tests | 05-02 | 2 | OBSERV-06 | — | SC#2 — single MEL filter path: Default=Warning suppresses Info in OTLP | dotnet test filter | `dotnet test --filter "FullyQualifiedName~LogLevelFilterTests" --no-restore` (Passed: 2) | `tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` | ⬜ pending |
| task-5-health-endpoints-tests | 05-02 | 2 | HEALTH-01, HEALTH-02, HEALTH-03, HEALTH-05 | T-05-READY-DB-EXPOSE | SC#3 — three probes with correct semantics; SC#4 logs-half — /health/* absent from OTLP logs; ready body shape | dotnet test filter | `dotnet test --filter "FullyQualifiedName~HealthEndpointsTests" --no-restore` (Passed: 7) | `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` | ⬜ pending |
| task-6-metrics-export-tests | 05-02 | 2 | OBSERV-03, OBSERV-08, HEALTH-05 | — | SC#4 metrics-half — app HTTP metric present, /health/* absent; D-16 runtime metric present | dotnet test filter | `dotnet test --filter "FullyQualifiedName~MetricsExportTests" --no-restore` (Passed: 3) | `tests/BaseApi.Tests/Observability/MetricsExportTests.cs` | ⬜ pending |
| task-7-trace-export-tests | 05-02 | 2 | OBSERV-12 | **T-05-PII** | SC#5 — Npgsql child span under ASP.NET Core span; NO db.parameter*; NO bound-value strings | dotnet test filter | `dotnet test --filter "FullyQualifiedName~TraceExportTests" --no-restore` (Passed: 2) | `tests/BaseApi.Tests/Observability/TraceExportTests.cs` | ⬜ pending |
| task-8-final-suite-and-checkpoint | 05-02 | 3 | (all Phase 5 IDs) | T-05-PII, T-05-LOG-INJECT, T-05-READY-DB-EXPOSE, T-05-OTLP-EXFIL | 3 consecutive full-suite runs green; BEFORE/AFTER psql byte-identical; telemetry.jsonl absent post-test | full suite x3 + diff | `for ($i=1; $i -le 3; $i++) { dotnet test SK_P.sln --no-restore }` AND `Compare-Object` on psql snapshots returns empty AND `Test-Path tests/.otel-out/telemetry.jsonl` returns False | `.planning/phases/05-observability-health-probes/05-02-psql-AFTER.snapshot.txt`, `05-02-SUMMARY.md` | ⬜ pending |

*Status legend: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky · 🚧 in progress*

---

## Wave 0 Requirements

Wave 0 = stubs / fixtures / framework install. All satisfied by Plan 05-01 (Tasks 1-7) + Plan 05-02 (Tasks 1-2) before any fact test runs.

- [x] `compose/otel-collector-config.yaml` — Plan 05-01 Task 4 (minimal pipeline OTLP-in -> file+logging-out)
- [x] `compose.yaml` `otel-collector` service block — Plan 05-01 Task 4
- [x] `tests/.otel-out/.gitkeep` — Plan 05-01 Task 4 (Pitfall 5-H defensive: host bind-mount target exists at clone time)
- [x] `.gitignore` `tests/.otel-out/*` + `!tests/.otel-out/.gitkeep` — Plan 05-01 Task 6
- [x] `src/BaseApi.Core/Health/IStartupGate.cs` + `StartupHealthCheck.cs` + `StartupCompletionService.cs` — Plan 05-01 Task 3
- [x] `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` — Plan 05-02 Task 2 (fused WebApplicationFactory<Program> + IAsyncLifetime; ExportProcessorType.Simple; OTLP endpoint pinned to localhost:4317)
- [x] `tests/BaseApi.Tests/Observability/CollectionDefinitions.cs` — Plan 05-02 Task 2 (`[CollectionDefinition("Observability", DisableParallelization = true)]`)
- [x] `tests/BaseApi.Tests/Observability/TestObservabilityController.cs` — Plan 05-02 Task 2 (`/test-obs/ok` + `/test-obs/db-roundtrip`)
- [x] Compose services brought up + healthy + BEFORE psql snapshot captured — Plan 05-02 Task 1

Wave 0 verifies the fixture compiles AND the compose containers are healthy BEFORE Wave 2 fact tests run.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| OTLP exporter does NOT block app shutdown when Collector is unreachable | OBSERV-04 | Hard to assert in xUnit without flaky timeouts; involves graceful-shutdown timing | Stop `otel-collector` container (`docker compose stop otel-collector`), `dotnet run --project src/BaseApi.Service`, issue a request, then Ctrl+C — must exit within OTel's BatchExportProcessor `ExporterTimeout` (~30s) and not hang. Document outcome in 05-02-SUMMARY "Manual Verifications" section. |
| Resource attributes appear in Jaeger/Tempo when ops points Collector at real backend | OBSERV-05 | Out-of-band — requires real observability backend deployed by ops | Document in README how to point `otel-collector` exporters at Jaeger/Tempo (replace `file` + `logging` exporters in `compose/otel-collector-config.yaml`); smoke-test before v1 release. Out of Phase 5 scope. |
| End-to-end correlation under load (~100 RPS) | OBSERV-09..11 + OBSERV-02 | Concurrency-heavy; doesn't fit in xUnit cadence | Run k6/wrk against `localhost:5000/test-obs/ok` for 30s; spot-check 5 random `X-Correlation-Id` values from response headers and confirm each appears in `tests/.otel-out/telemetry.jsonl` as a log attribute. Deferred to v2 load-test pass. |

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify (every task has a build/test/grep gate)
- [x] Wave 0 covers all MISSING references (fixture, collection definition, controller, Collector config, bind-mount target)
- [x] No watch-mode flags (no `dotnet watch`); all commands use `--no-restore` after Task 1 of Plan 05-01
- [x] Feedback latency < 120s (full suite ~60s cold; single class ~5s warm)
- [x] `nyquist_compliant: true` set in frontmatter (every task's automated verify maps to a sampling rate above)

**Approval:** populated by gsd-planner 2026-05-27; awaiting executor sign-off as each task completes.
