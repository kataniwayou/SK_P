---
phase: 05-observability-health-probes
plan: 02
subsystem: observability
tags: [opentelemetry, otlp, mel-bridge, healthchecks, npgsql-tracing, otel-collector, xunit-v3, webapplicationfactory, testcontainers-style-fixture, postgres-fixture, filterprocessor, assembly-fixture]

# Dependency graph
requires:
  - phase: 05-observability-health-probes
    provides: Plan 05-01 wiring (MEL-bridge logs, services-chain metrics+traces, Health/ types, otel-collector compose service, /health/* tag discipline). Plan 05-02 is the Wave-2 verification battery that asserts Plan 05-01's wiring actually produces the telemetry shapes the ROADMAP SC#1-5 + D-16 + T-05-PII / T-05-LOG-INJECT / T-05-OTLP-EXFIL / T-05-READY-DB-EXPOSE demand.
  - phase: 04-cross-cutting-middleware-error-handling
    provides: CorrelationIdMiddleware BeginScope("CorrelationId", id) + ASCII-printable sanitization on X-Correlation-Id header — verified delegated through MEL → OTLP path
  - phase: 03-ef-core-persistence-base
    provides: PostgresFixture (per-class throwaway DB stepsdb_test_mw_{guid}) + TestErrorDbContext shape — reused for SC#5 Npgsql child-span fact
  - phase: 02-postgres-docker-compose
    provides: postgres:17-alpine on localhost:5433 — BEFORE/AFTER \l byte-identical snapshots verify zero test-DB leaks
provides:
  - OtelCollectorFixture — fused WebApplicationFactory<Program> + IAsyncLifetime + ExportProcessorType.Simple flush + OTLP endpoint pinning (localhost:4317) — public class (NOT sealed) to support HealthEndpointsTests nested inheritance
  - OtelEndOfSuiteCleanup — xUnit v3 [assembly: AssemblyFixture] with IAsyncLifetime that runs ONCE at end of the test assembly to stop otel-collector, delete telemetry.jsonl + rotation siblings, restart the collector, double-delete guard against external-process residuals
  - ObservabilityCollection — [CollectionDefinition("Observability", DisableParallelization = true)] preventing telemetry.jsonl interleave
  - TestObservabilityController — [Route("test-obs")] with /ok (LogInformation driver) + /db-roundtrip (parametrized Npgsql query for T-05-PII regression)
  - 5 fact-test classes — LogExportTests (2), LogLevelFilterTests (2), HealthEndpointsTests (7), MetricsExportTests (3), TraceExportTests (2) = 16 facts, all GREEN
  - Position-marker file-handle strategy — fixture records telemetry.jsonl byte offset at InitializeAsync, ReadAllExportedRecords seeks past that offset (replaces truncate/delete which orphans the file-exporter's open inode on Windows + Docker Desktop)
  - Env-var-in-ctor pattern for connection-string overrides — Environment.SetEnvironmentVariable in fixture ctor BEFORE base ctor runs, capture+restore prior value on Dispose (replaces ConfigureAppConfiguration which arrives AFTER Program.cs has captured the connection string by value into AddNpgSql)
  - Collector-side filterprocessor (filter/health_metrics) — drops http.server.request.duration data points whose http.route starts with /health/ BEFORE file write; closes Plan 05-01 SC#4 metrics-half gap
affects:
  - 06 (validation + mapping base) — no observability changes expected; CorrelationId propagation chain stable
  - 07 (composition root refactor) — AddBaseApi/UseBaseApi extensions will absorb the Program.cs OTel + Health wiring; OtelCollectorFixture's WebApplicationFactory pattern continues to work as-is
  - 08 (entity build-out + migrations) — MigrationRunner replaces StartupCompletionService via 1-line AddHostedService swap; Plan 05-02 HealthNoStartupCompletionFixture's removal-predicate (matches typeof(StartupCompletionService)) is the regression net for "did the swap break Health/" — if Phase 8 forgets to register MigrationRunner, /health/startup stays 503 and this fixture's negative-path test catches it

# Tech tracking
tech-stack:
  added:
    - xUnit v3 [assembly: AssemblyFixture(typeof(...))] attribute — once-per-assembly fixture for end-of-suite cleanup (telemetry.jsonl deletion via Docker compose orchestration)
    - System.Diagnostics.Process — shells out to `docker compose stop|start otel-collector` from inside the test runtime (15s timeout via CancellationToken, best-effort with stderr logging)
    - otel-collector-contrib filterprocessor (`filter/health_metrics`) — OTTL `IsMatch(attributes["http.route"], "^/health/.*")` predicate drops data points BEFORE file export
  patterns:
    - "Pattern A (Wave-0 fused fixture): OtelCollectorFixture composes WebApplicationFactory<Program> + IAsyncLifetime + ConfigureTestServices override of OtlpExporterOptions (ExportProcessorType.Simple + Endpoint=http://localhost:4317). Multiple public + internal constructors satisfy IClassFixture activation while still supporting LogLevelFilterTests / HealthEndpointsTests / TraceExportTests direct `new` with optional connectionString + logLevelDefaultOverride args."
    - "Pattern B (position-marker file-handle strategy): the otel-collector v0.95.0 file exporter holds an exclusive write handle on telemetry.jsonl for the container's lifetime — truncate-on-init MAY succeed on Linux but delete-on-init orphans the inode under Windows + Docker Desktop. Workaround: record File.Length AT InitializeAsync (_startPosition) and ReadAllExportedRecords seeks past that offset so each test class only sees records written during its own lifetime."
    - "Pattern C (env-var-in-ctor for connection-string override): WebApplicationFactory<Program> builds the host BEFORE ConfigureWebHost callbacks run — by then Program.cs has already called services.AddNpgSql(cfg.GetConnectionString(\"Postgres\")!, ...) which captures the connection string BY VALUE into the registered IHealthCheck. ConfigureAppConfiguration overrides arrive too late. Working pattern: Environment.SetEnvironmentVariable(\"ConnectionStrings__Postgres\", ...) IN the fixture's ctor body (runs BEFORE the base WebApplicationFactory<Program> ctor), capture-and-restore the prior value on DisposeAsync to avoid leaking process-wide state across fixtures."
    - "Pattern D (MEL category filter override for SC#4 logs-half): appsettings.Development.json raises Microsoft.AspNetCore back to Information (dev ergonomics), so WebApplicationFactory<Program>'s default ASPNETCORE_ENVIRONMENT=Development surfaces /health/* request-start/finish logs. Working pattern: ConfigureAppConfiguration + AddInMemoryCollection sets Logging:LogLevel:Microsoft.AspNetCore + Microsoft.AspNetCore.Hosting.Diagnostics + Microsoft.AspNetCore.Routing all to Warning. This is the working sibling of Pattern C — IConfiguration overrides DO work for log filters (MEL reads IConfiguration on every log event), unlike AddNpgSql which captures by value at registration time."
    - "Pattern E (xUnit v3 [assembly: AssemblyFixture] for end-of-suite cleanup): the Plan 05-01 D-11 cleanup discipline (telemetry.jsonl absent post-test) cannot be honored per-class — the otel-collector container holds the write handle. Pattern: a sealed IAsyncLifetime class registered via `[assembly: AssemblyFixture(typeof(...))]`. DisposeAsync runs ONCE after all tests in the assembly: docker compose stop otel-collector → delete telemetry.jsonl + rotation siblings → docker compose start otel-collector → 750ms delay → defensive double-delete against external-process residuals (MCP agents / instrumented IDEs producing OTLP traffic to the same localhost:4317 endpoint)."
    - "Pattern F (Collector-side filterprocessor for SDK API gaps): when an OTel SDK doesn't expose the filter you need (here: OTel .NET 1.15.0's parameterless MeterProviderBuilder.AddAspNetCoreInstrumentation), filter at the Collector instead. otel-collector-contrib's filterprocessor with OTTL `IsMatch(attributes[\"http.route\"], \"^/health/.*\")` drops data points before file export. SDK still emits, Collector applies ops-policy filtering — the idiomatic OTel layered architecture."
    - "Pattern G (specialized fixture subclassing for per-test config variants): HealthEndpointsTests defines 4 nested subclasses (HealthDeadPostgresFixture, HealthLiveLocalhostFixture, HealthFilterEnabledFixture, HealthNoStartupCompletionFixture) inheriting from OtelCollectorFixture to apply per-test config overrides. Required un-sealing of the base fixture (commit 008793b)."

key-files:
  created:
    - tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs — fused fixture (~260 lines): WebApplicationFactory<Program> + IAsyncLifetime + position-marker file readers + FlushAsync + 3 ctors (public parameterless for IClassFixture activation + 2 internal overloads for direct `new` with cstring/logLevel overrides). `public class` (NOT sealed) to support nested inheritance from HealthEndpointsTests.
    - tests/BaseApi.Tests/Observability/CollectionDefinitions.cs — [CollectionDefinition("Observability", DisableParallelization = true)] marker so all 5 fact classes run serially (single shared telemetry.jsonl).
    - tests/BaseApi.Tests/Observability/TestObservabilityController.cs — [Route("test-obs")] with /ok (LogInformation "test-obs ok ran") and /db-roundtrip (parametrized SELECT). Discovered via fixture's AddApplicationPart(typeof(OtelCollectorFixture).Assembly).
    - tests/BaseApi.Tests/Observability/LogExportTests.cs — 2 facts: Test_LogRecord_Has_CorrelationId_And_ServiceResource + Test_LogRecord_CorrelationId_Survives_Sanitization (T-05-LOG-INJECT delegate to Phase 4).
    - tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs — 2 facts: suppressed-when-Warning + present-when-Information; uses `new OtelCollectorFixture(null, "Warning")` then `await using`.
    - tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs — 7 facts covering 3 probes × 2 outcomes + ready-body shape (T-05-READY-DB-EXPOSE) + log-filter (SC#4 logs-half). 4 nested fixture subclasses (DeadPostgres / LiveLocalhost / FilterEnabled / NoStartupCompletion) applying per-test config overrides.
    - tests/BaseApi.Tests/Observability/MetricsExportTests.cs — 3 facts: app endpoint emits http.server.request.duration + /health/* ABSENT from that instrument (STRICT empty — Plan 05-02 fix-forward closed the gap) + runtime metric (process.runtime.dotnet.* OR dotnet.*) exported (D-16).
    - tests/BaseApi.Tests/Observability/TraceExportTests.cs — 2 facts: Npgsql child span has parentSpanId matching ASP.NET Core request span (SC#5) + db.statement has $1 placeholder + NO db.parameter* attribute keys + bound Guid value does NOT leak into any span attribute string-value (T-05-PII — 3 independent regression assertions).
    - tests/BaseApi.Tests/Observability/OtelEndOfSuiteCleanup.cs — xUnit v3 assembly fixture (158 lines): IAsyncLifetime.DisposeAsync runs once at end of test assembly, shells out to `docker compose stop|delete|start otel-collector`, post-restart double-delete guard for external-process residuals. Best-effort with 15s timeouts and stderr logging; never throws.
    - .planning/phases/05-observability-health-probes/05-02-psql-BEFORE.snapshot.txt — psql \l output captured Task 1 (BEFORE container traffic).
    - .planning/phases/05-observability-health-probes/05-02-psql-AFTER.snapshot.txt — psql \l output captured Task 8 (AFTER all 3 dotnet test runs). diff exit 0 vs BEFORE — byte-identical, zero leaked DBs.
  modified:
    - compose/otel-collector-config.yaml — added filterprocessor (filter/health_metrics) that drops http.server.request.duration data points whose http.route starts with /health/; wired into metrics pipeline (logs + traces pipelines unchanged). Closes Plan 05-01 Deviation #2 (SC#4 metrics-half gap).
    - compose.yaml — Plan 05-02 fix-forward to Plan 05-01: dropped wget-based otel-collector healthcheck (image lacks wget; non-functional); exposed :13133 to host. Plan 05-02 fix-forward to Plan 05-01: added `user: "0:0"` to otel-collector for Windows bind-mount writes (Docker Desktop UID mapping needs root to write into the host-mounted dir).

key-decisions:
  - "Position-marker file-handle strategy over truncate/delete (Wave-0 fixture Pattern B) — the otel-collector v0.95.0 file exporter holds an exclusive write handle on telemetry.jsonl; truncate may succeed but delete orphans the inode the exporter is writing to. Position marker records File.Length at InitializeAsync; ReadAllExportedRecords seeks past that offset so each test only observes its own records."
  - "Env-var-in-ctor pattern for ConnectionStrings:Postgres overrides (Pattern C — supersedes the original planner-checker WARNING #1 fix). ConfigureAppConfiguration is too late because Program.cs calls services.AddNpgSql(cfg.GetConnectionString(\"Postgres\")!, ...) at host-build time, capturing the connection string by value into the registered IHealthCheck. Environment.SetEnvironmentVariable in the fixture ctor (BEFORE base ctor) DOES propagate via the env-var configuration source. Capture+restore prior value on Dispose to prevent leakage across fixtures."
  - "MEL category filter override pattern for SC#4 logs-half (Pattern D). WebApplicationFactory<Program> defaults to ASPNETCORE_ENVIRONMENT=Development which loads appsettings.Development.json — that file raises Microsoft.AspNetCore back to Information so /health/* request-start logs DO reach OTLP under the default test environment. HealthFilterEnabledFixture overrides via ConfigureAppConfiguration + AddInMemoryCollection setting 3 categories down to Warning, replicating production behavior."
  - "[assembly: AssemblyFixture] for end-of-suite cleanup (Pattern E). The xUnit v3 attribute runs the fixture's DisposeAsync ONCE after all assembly tests complete; shells out to docker compose stop|delete|start otel-collector for D-11 cleanup discipline. Best-effort with 15s timeouts; never throws (cleanup failure must not mask a successful test run). Post-restart double-delete guard with 750ms delay covers external-process residuals (MCP agents etc.)."
  - "Collector-side filterprocessor over SDK-side filtering (Pattern F). Closes Plan 05-01 Deviation #2 (SC#4 metrics-half) — the parameterless MeterProviderBuilder.AddAspNetCoreInstrumentation in OTel 1.15.0 has no Filter knob, and the AspNetCore HTTP server metric instrument is shared across ALL routes (AddView at instrument level would drop legitimate app metrics too). Collector-side filtering with OTTL IsMatch is the idiomatic layered-OTel architecture."
  - "Un-sealing OtelCollectorFixture to support HealthEndpointsTests nested subclasses (commit 008793b). The 4 specialized fixtures (DeadPostgres / LiveLocalhost / FilterEnabled / NoStartupCompletion) require inheritance + ConfigureWebHost override. Sealing would have forced an awkward composition pattern; un-sealing matches the documented Pattern G."
  - "Three-ctor IClassFixture activation strategy (Pattern A details). xUnit's IClassFixture<T> requires T to have a single public parameterless ctor; ctors with default parameters do NOT satisfy parameter resolution (raises 'unresolved constructor arguments'). Solution: public parameterless `OtelCollectorFixture()` for IClassFixture activation + two internal overloads (`OtelCollectorFixture(string?)`, `OtelCollectorFixture(string?, string?)`) for direct `new` use by LogLevelFilterTests / TraceExportTests + nested subclasses."

patterns-established:
  - "Pattern A: Fused fixture (WebApplicationFactory<Program> + IAsyncLifetime + Configure*Services exporter overrides + multiple ctor overloads)"
  - "Pattern B: Position-marker file-handle strategy (record byte offset at InitializeAsync; seek past on read; never truncate or delete during test lifetime against a container-owned file)"
  - "Pattern C: Env-var-in-ctor for connection-string overrides (capture-and-restore prior value on Dispose; runs BEFORE base ctor builds the host)"
  - "Pattern D: MEL category filter override (ConfigureAppConfiguration + AddInMemoryCollection setting Logging:LogLevel:Microsoft.AspNetCore + .Hosting.Diagnostics + .Routing to Warning replicates production behavior under test ASPNETCORE_ENVIRONMENT=Development)"
  - "Pattern E: xUnit v3 [assembly: AssemblyFixture] for end-of-suite cleanup (single IAsyncLifetime running once after all assembly tests; shells out to docker compose with best-effort stderr-logged 15s timeouts)"
  - "Pattern F: Collector-side filterprocessor closes SDK-API gaps (idiomatic layered OTel — SDK emits all, Collector applies ops-policy filtering with OTTL)"
  - "Pattern G: Specialized fixture subclassing for per-test config variants (un-seal base fixture; nested private sealed classes override ConfigureWebHost; chain through specific ctors)"

requirements-completed: [OBSERV-01, OBSERV-02, OBSERV-03, OBSERV-04, OBSERV-05, OBSERV-06, OBSERV-07, OBSERV-08, OBSERV-12, HEALTH-01, HEALTH-02, HEALTH-03, HEALTH-04, HEALTH-05]

# Metrics
duration: ~60min (continuation block: ~17min)
completed: 2026-05-27
---

# Phase 5 Plan 02: Observability + Health Probes — Verification Battery Summary

**16 fact tests across 5 classes (LogExport 2 + LogLevelFilter 2 + HealthEndpoints 7 + MetricsExport 3 + TraceExport 2) verify Plan 05-01's OTel + Health wiring against the real otel-collector + Phase 2 Postgres, with 3 consecutive `dotnet test SK_P.sln` runs reporting 47/47 passes, BEFORE/AFTER `psql \l` byte-identical (Phase 3 D-15), and `tests/.otel-out/telemetry.jsonl` automatically deleted post-suite via an xUnit v3 [assembly: AssemblyFixture]. Plan 05-01 Deviation #2 (SC#4 metrics-half gap) closed via Collector-side filter/health_metrics processor; the test assertion flipped from SOFT-PASS to STRICT empty.**

## Performance

- **Duration:** ~60 min total wall-clock (initial executor session ~43 min + continuation block ~17 min for Gap 1 + Gap 2 closures and SUMMARY commit)
- **Started:** 2026-05-27T09:08Z (continuation block: 2026-05-27T10:09Z)
- **Completed:** 2026-05-27T10:20Z
- **Tasks:** 8 (Task 1 docker-compose bring-up; Task 2 Wave-0 infrastructure; Tasks 3-7 5 fact-test classes; Task 8 3x consecutive verify + checkpoint); continuation block added 2 commits closing 2 gaps + 2 supporting commits (un-sealing, post-restart guard)
- **Files modified:** 13 total (5 created + 8 modified + 2 psql snapshots) — see frontmatter `key-files` for the structured list

## Accomplishments

### Goals achieved

**14 Phase 5 REQ-IDs runtime-verified** (not just code-edited — actual exported telemetry matches each requirement's invariant):

| REQ-ID | Fact verifying | File |
|--------|----------------|------|
| OBSERV-01 | `service.name=sk-api` + `service.version=3.2.0` on every log record | `LogExportTests.Test_LogRecord_Has_CorrelationId_And_ServiceResource` |
| OBSERV-02 | MEL-bridge logs flow to OTLP | `LogExportTests.Test_LogRecord_Has_CorrelationId_And_ServiceResource` |
| OBSERV-03 | OTel metrics + traces wired via services-chain | `MetricsExportTests` + `TraceExportTests` (any fact) |
| OBSERV-04 | OTLP exporter endpoint pinned to localhost:4317 (Collector) | All Phase 5 facts (file exporter would not see records if exfilled elsewhere) |
| OBSERV-05 | `service.name` + `service.version` on log records | `LogExportTests.Test_LogRecord_Has_CorrelationId_And_ServiceResource` |
| OBSERV-06 | MEL filter single source of truth (Default=Warning suppresses both sinks) | `LogLevelFilterTests` (2 facts) |
| OBSERV-07 | CorrelationId log attribute from `BeginScope("CorrelationId", id)` | `LogExportTests` (both facts) |
| OBSERV-08 | /health/* excluded from traces (Plan 05-01) + from metrics (Plan 05-02 Collector filter) | `MetricsExportTests.Test_HealthPath_Absent_From_HttpServerMetrics` (STRICT) + `HealthEndpointsTests.Test_HealthEndpoints_Absent_From_OTLP_Logs` |
| OBSERV-12 | Npgsql child span under ASP.NET Core request span | `TraceExportTests.Test_NpgsqlChildSpan_Under_AspNetCore_Request_Span` |
| HEALTH-01 | `/health/live` always 200 (no DB check — Pitfall 15) | `HealthEndpointsTests.Test_HealthLive_Always_200_NoDbCheck` |
| HEALTH-02 | `/health/ready` 503 when Postgres unreachable, 200 when reachable | `HealthEndpointsTests.Test_HealthReady_503_When_Postgres_Unreachable` + `Test_HealthReady_200_When_Postgres_Reachable` |
| HEALTH-03 | `/health/startup` 503 before gate flipped, 200 after | `HealthEndpointsTests.Test_HealthStartup_503_Before_GateFlipped` + `Test_HealthStartup_200_After_GateFlipped_By_HostedService` |
| HEALTH-04 | UIResponseWriter JSON body shape | `HealthEndpointsTests` (every fact reads response body) |
| HEALTH-05 | /health/* tag-discipline + path filtering across signals | `HealthEndpointsTests` + `MetricsExportTests` (combined coverage) |

**5 ROADMAP Phase 5 Success Criteria all GREEN:**

| SC# | Description | Verifying facts | Status |
|-----|-------------|-----------------|--------|
| SC#1 | Log record has CorrelationId + service.name + service.version | LogExportTests (2) | GREEN |
| SC#2 | Default=Warning suppresses Information from both sinks | LogLevelFilterTests (2) | GREEN |
| SC#3 | 3 probes return correct codes (live/ready/startup × healthy/unhealthy) | HealthEndpointsTests (5 of 7) | GREEN |
| SC#4 | App metrics present + /health/* absent (BOTH halves now STRICT) | MetricsExportTests.Test_HealthPath_Absent_From_HttpServerMetrics (metrics-half) + HealthEndpointsTests.Test_HealthEndpoints_Absent_From_OTLP_Logs (logs-half) | GREEN — both halves closed |
| SC#5 | Npgsql child span under ASP.NET Core request span | TraceExportTests (2) | GREEN |

**Threat register dispositions verified:**

| Threat ID | Disposition | Verifying fact / evidence |
|-----------|-------------|---------------------------|
| T-05-PII | mitigate (verified) | `TraceExportTests.Test_NpgsqlChildSpan_DbStatement_Has_NoParameterValues` — 3 independent regression assertions: db.statement has $1 placeholder, NO db.parameter* keys, distinctive bound Guid does NOT leak into any span attribute |
| T-05-LOG-INJECT | mitigate (delegated to Phase 4) | `LogExportTests.Test_LogRecord_CorrelationId_Survives_Sanitization` — Phase 4 CorrelationIdMiddleware sanitization preserved through MEL → OTLP bridge; 32-hex pattern enforced |
| T-05-OTLP-EXFIL | mitigate (verified) | Fixture pins endpoint to localhost:4317 (`OtlpExporterOptions.Endpoint` + env var); any test producing telemetry destined elsewhere would not appear in `tests/.otel-out/telemetry.jsonl` — implicit cross-test verification |
| T-05-READY-DB-EXPOSE | accept (verify shape) | `HealthEndpointsTests.Test_HealthReady_Body_Has_Per_Check_Status_But_No_Sensitive_Fields` — body has `entries.npgsql.status` + `entries.startup.status`, ABSENT: Password=, postgres;Username, `at Npgsql.` stack-trace markers |
| T-05-LOG-FORGE | accept (no test) | Acknowledged per project-level auth-out-of-scope decision; no regression test in Phase 5 |

### Three consecutive `dotnet test SK_P.sln --no-restore` runs (continuation block, after Gap 1 + Gap 2 closures)

| Run | Passed | Failed | Skipped | Duration | telemetry.jsonl post-run |
|-----|--------|--------|---------|----------|--------------------------|
| 1   | 47     | 0      | 0       | 17.67s   | ABSENT (cleanup honored) |
| 2   | 47     | 0      | 0       | 17.71s   | ABSENT (cleanup honored) |
| 3   | 47     | 0      | 0       | 18.09s   | ABSENT (cleanup honored) |

Pass count identical across all three runs. otel-collector container in `running` state post-suite (subsequent test runs work without manual intervention).

### psql \l BEFORE / AFTER

```
$ diff .planning/phases/05-observability-health-probes/05-02-psql-BEFORE.snapshot.txt .planning/phases/05-observability-health-probes/05-02-psql-AFTER.snapshot.txt
$ echo $?
0
```

Byte-identical — Phase 3 D-15 cleanup discipline honored (zero leaked test DBs across 16 facts × 3 runs).

## Task Commits

Initial executor session (Tasks 1-7 + checkpoint):

1. **Task 1 (Wave 0) — docker compose up + BEFORE snapshot** — (no commit; runtime action). BEFORE snapshot file written.
1.5. **Plan 05-01 fix-forward — drop wget healthcheck + expose :13133** — `ea8f745` (fix(05-01))
1.6. **Plan 05-01 fix-forward — Windows bind-mount user override** — `3c99c4d` (fix(05-01))
2. **Task 2 (Wave 0) — OtelCollectorFixture + CollectionDefinitions + TestObservabilityController** — `36aaee9` (feat(05-02))
2-fix. **Windows file-handle reality (truncate replaced with position-marker; xUnit ctor rule)** — `4f45c28` (fix(05-02))
3. **Task 3 — LogExportTests (SC#1 + T-05-LOG-INJECT)** — `f6551b5` (feat(05-02))
4. **Task 4 — LogLevelFilterTests (SC#2)** — `ba30309` (feat(05-02))
5. **Task 5 — HealthEndpointsTests (SC#3 + SC#4 logs-half + T-05-READY-DB-EXPOSE)** — `736102b` (feat(05-02))
6. **Task 6 — MetricsExportTests (SC#4 metrics-half initial SOFT-PASS + D-16)** — `1a84070` (feat(05-02))
7. **Task 7 — TraceExportTests (SC#5 + T-05-PII)** — `46256a5` (feat(05-02))

Continuation block (Gap 1 + Gap 2 closures):

8. **Un-sealing OtelCollectorFixture for nested subclasses** — `008793b` (fix(05-02)) — surfaced as uncommitted working-tree mod from prior executor's Task 5 work
9. **GAP 1 fix — Collector-side filterprocessor closes SC#4 metrics-half** — `2f3ae45` (fix(05-01)) — `compose/otel-collector-config.yaml` adds `filter/health_metrics` processor + wires into metrics pipeline
10. **GAP 1 test flip — Assert.NotEmpty → Assert.Empty (STRICT)** — `1d07463` (test(05-02)) — `MetricsExportTests.cs` `Test_HealthPath_Absent_From_HttpServerMetrics`
11. **GAP 2 — xUnit v3 [assembly: AssemblyFixture] for end-of-suite telemetry.jsonl cleanup** — `598c016` (test(05-02))
12. **GAP 2 fix — post-restart double-delete guard against external-process residuals** — `ca63351` (fix(05-02))

**Plan metadata commit (final docs commit):** to follow this SUMMARY.

## Files Created/Modified

### Created (10)

- `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` — fused fixture (~260 lines); see Pattern A + Pattern B + 3-ctor strategy
- `tests/BaseApi.Tests/Observability/CollectionDefinitions.cs` — DisableParallelization marker
- `tests/BaseApi.Tests/Observability/TestObservabilityController.cs` — /test-obs/ok + /test-obs/db-roundtrip
- `tests/BaseApi.Tests/Observability/LogExportTests.cs` — 2 facts (SC#1 + T-05-LOG-INJECT delegate)
- `tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` — 2 facts (SC#2)
- `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — 7 facts (SC#3 + SC#4 logs-half + T-05-READY-DB-EXPOSE) + 4 nested fixture subclasses
- `tests/BaseApi.Tests/Observability/MetricsExportTests.cs` — 3 facts (SC#4 metrics-half STRICT + D-16); Plan 05-02 fix-forward flipped the assertion
- `tests/BaseApi.Tests/Observability/TraceExportTests.cs` — 2 facts (SC#5 + T-05-PII × 3 independent assertions)
- `tests/BaseApi.Tests/Observability/OtelEndOfSuiteCleanup.cs` — xUnit v3 [assembly: AssemblyFixture] end-of-suite cleanup
- `.planning/phases/05-observability-health-probes/05-02-psql-BEFORE.snapshot.txt` + `05-02-psql-AFTER.snapshot.txt` — D-15 byte-identical proof

### Modified (3)

- `compose/otel-collector-config.yaml` — added `filter/health_metrics` filterprocessor entry + wired into the metrics pipeline (closes SC#4 metrics-half gap)
- `compose.yaml` — Plan 05-01 fix-forward: dropped non-functional wget healthcheck; exposed :13133 to host; `user: "0:0"` for Windows bind-mount writes
- `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` — Plan 05-02 fix-forward (commit 008793b): un-sealed the class for HealthEndpointsTests nested inheritance

## Decisions Made

See `key-decisions` in frontmatter for the structured list. Most load-bearing summary:

1. **Position-marker file-handle strategy (Pattern B)** — chosen over truncate/delete after the file-exporter's exclusive write handle reality surfaced on Windows + Docker Desktop. Records `File.Length` at InitializeAsync; ReadAllExportedRecords seeks past that offset.
2. **Env-var-in-ctor for ConnectionStrings:Postgres overrides (Pattern C)** — supersedes the original planner-checker WARNING #1 fix (ConfigureAppConfiguration arrives AFTER Program.cs has captured the connection string by value into AddNpgSql).
3. **MEL category filter override pattern (Pattern D)** — required because WebApplicationFactory<Program> defaults to ASPNETCORE_ENVIRONMENT=Development which raises Microsoft.AspNetCore back to Information; the SC#4 logs-half assertion specifically tests the production-mode invariant.
4. **xUnit v3 [assembly: AssemblyFixture] for end-of-suite cleanup (Pattern E)** — closes Plan 05-02 D-11 cleanup discipline; cannot be honored per-class because the otel-collector container holds the exclusive write handle.
5. **Collector-side filterprocessor over SDK-side filtering (Pattern F)** — closes Plan 05-01 Deviation #2 (SC#4 metrics-half) via OTTL `IsMatch(attributes["http.route"], "^/health/.*")`. Idiomatic OTel layered architecture; SDK emits all, Collector filters by ops-policy.
6. **Un-sealing OtelCollectorFixture (Pattern G)** — HealthEndpointsTests needs 4 nested subclasses overriding ConfigureWebHost; sealing would have forced an awkward composition pattern.
7. **Three-ctor IClassFixture strategy (Pattern A details)** — public parameterless ctor for IClassFixture activation + internal overloads for direct `new` use; default parameters do NOT satisfy IClassFixture parameter resolution.

## Deviations from Plan

### Auto-fixed Issues — Wave-0 fixture and HealthEndpointsTests work (initial executor session)

**1. [Rule 1 — Bug] otel-collector healthcheck wget unavailable in image**
- **Found during:** Task 1 (docker compose up)
- **Issue:** Plan 05-01 compose.yaml had `healthcheck: test: ["CMD", "wget", ...]` against `otel/opentelemetry-collector-contrib:0.95.0` — the image lacks `wget` binary, healthcheck reports unhealthy.
- **Fix:** Dropped the healthcheck entry; exposed :13133 to host so the Collector's HTTP health_check extension can be probed from outside.
- **Files modified:** `compose.yaml`
- **Committed in:** `ea8f745` (fix(05-01))

**2. [Rule 1 — Bug] Windows + Docker Desktop bind-mount permission denial**
- **Found during:** Task 1 (docker compose up — collector failed to write to `/var/otel-out`)
- **Issue:** Default container user lacks write permission on Windows Docker Desktop bind-mounted directories.
- **Fix:** Added `user: "0:0"` (root) to the otel-collector compose service.
- **Files modified:** `compose.yaml`
- **Committed in:** `3c99c4d` (fix(05-01))

**3. [Rule 1 — Bug] Fixture's truncate-on-init / delete-on-dispose orphans the file-exporter inode**
- **Found during:** Task 2 (initial fixture run — empty reads from telemetry.jsonl across subsequent tests)
- **Issue:** The file exporter holds an exclusive write handle for the container's lifetime. Truncate MAY work, but delete-on-dispose removes the directory entry while the exporter writes to the now-orphaned inode — subsequent fixture instances see an "empty" file forever.
- **Fix:** Position-marker strategy (Pattern B) — record `File.Length` at InitializeAsync; never delete during fixture lifetime; ReadAllExportedRecords seeks past `_startPosition` so each test class only observes records written during its own lifetime. xUnit v3 IClassFixture single-public-ctor rule also required restructuring constructors.
- **Files modified:** `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs`
- **Committed in:** `4f45c28` (fix(05-02))

**4. [Rule 1 — Bug] xUnit v3 IClassFixture ctor rule violation**
- **Found during:** Task 2 (initial fixture build — IClassFixture activation failed)
- **Issue:** Original single-public-ctor-with-default-args (`OtelCollectorFixture(string? cs = null, string? ll = null)`) raised xUnit "had one or more unresolved constructor arguments". IClassFixture requires exactly ONE public parameterless ctor; default parameters do NOT satisfy the parameter resolver.
- **Fix:** Three-ctor strategy — public `OtelCollectorFixture()` + internal `OtelCollectorFixture(string?)` + internal `OtelCollectorFixture(string?, string?)` (chained through `: this(...)`). Tests using direct `new` (LogLevelFilterTests / TraceExportTests + nested subclasses) call the internal overloads.
- **Files modified:** `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs`
- **Committed in:** `4f45c28` (fix(05-02), folded with deviation #3)

**5. [Rule 1 — Bug] ConfigureAppConfiguration override of ConnectionStrings:Postgres ineffective**
- **Found during:** Task 5 (HealthEndpointsTests / `Test_HealthReady_503_When_Postgres_Unreachable` returning 200 instead of 503)
- **Issue:** Planner-checker WARNING #1 prescribed ConfigureAppConfiguration + AddInMemoryCollection to override the connection string. Empirical reality: WebApplicationFactory<Program> calls `builder.Build()` BEFORE ConfigureAppConfiguration callbacks run, and Program.cs has already executed `services.AddNpgSql(cfg.GetConnectionString("Postgres")!, ...)` which captured the connection string BY VALUE into the registered IHealthCheck. ConfigureAppConfiguration overrides the IConfiguration object too late to influence the captured value.
- **Fix:** Env-var-in-ctor pattern (Pattern C) — `Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", ...)` in the nested fixture's constructor body (runs BEFORE the base WebApplicationFactory<Program> ctor → BEFORE Program.cs executes). Capture-and-restore prior value on DisposeAsync to prevent leakage across fixtures. Applied to HealthDeadPostgresFixture + HealthLiveLocalhostFixture (the two cases needing connection-string overrides).
- **Files modified:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs`
- **Committed in:** `736102b` (feat(05-02) Task 5)

**6. [Rule 1 — Bug] ASPNETCORE_ENVIRONMENT=Development raises Microsoft.AspNetCore to Information**
- **Found during:** Task 5 (HealthEndpointsTests / `Test_HealthEndpoints_Absent_From_OTLP_Logs` finding /health/* paths in log records)
- **Issue:** Plan 05-01 documented Pattern D-09 — the coarse `Microsoft.AspNetCore: Warning` setting from appsettings.json drops request-start/finish logs for /health/*. But `WebApplicationFactory<Program>` defaults to ASPNETCORE_ENVIRONMENT=Development which loads `appsettings.Development.json` (where Microsoft.AspNetCore is raised back to Information). The test was failing because the dev override defeats the production-mode invariant the test asserts.
- **Fix:** HealthFilterEnabledFixture (Pattern D) — ConfigureAppConfiguration + AddInMemoryCollection setting 3 categories down to Warning (Microsoft.AspNetCore + Microsoft.AspNetCore.Hosting.Diagnostics + Microsoft.AspNetCore.Routing). IConfiguration overrides DO work for log filters because MEL reads IConfiguration on every log event (unlike AddNpgSql which captures by value at registration).
- **Files modified:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs`
- **Committed in:** `736102b` (feat(05-02) Task 5)

**7. [Rule 3 — Blocking] Race condition between collector batching and test position marker**
- **Found during:** Task 5 (HealthEndpointsTests / `Test_HealthEndpoints_Absent_From_OTLP_Logs` flake — prior tests' batched records sometimes arrived AFTER the current test's InitializeAsync position marker)
- **Issue:** The Collector batches gRPC receives + file writes. Records produced by test N-1 may flush to telemetry.jsonl DURING test N's window, ending up past N's `_startPosition` and polluting the assertion.
- **Fix:** Added `await Task.Delay(TimeSpan.FromSeconds(1), ct)` BEFORE the test's InitializeAsync to let the Collector drain prior tests' buffered records first.
- **Files modified:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs`
- **Committed in:** `736102b` (feat(05-02) Task 5)

### Auto-fixed Issues — Continuation block (Gap 1 + Gap 2 closures)

**8. [Rule 1 — Bug] OtelCollectorFixture un-sealing left uncommitted by prior executor**
- **Found during:** Continuation block git status check
- **Issue:** Prior executor's Task 5 (HealthEndpointsTests) required un-sealing OtelCollectorFixture to support 4 nested inherited fixtures. The change was made but NOT committed separately; surfaced as a working-tree modification in the continuation block's initial git status.
- **Fix:** Committed as a fix-forward to the Wave-0 fixture commit (36aaee9).
- **Files modified:** `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs`
- **Committed in:** `008793b` (fix(05-02))

**9. [Rule 1 — Bug] SC#4 metrics-half gap — STRICT empty assertion was SOFT-PASS placeholder (Gap 1)**
- **Found during:** Continuation block (per user directive "close everything now")
- **Issue:** Plan 05-01 Deviation #2 deferred /health/* metrics filtering because OpenTelemetry.Instrumentation.AspNetCore 1.15.0's MeterProviderBuilder.AddAspNetCoreInstrumentation is parameterless (no Filter callback). In .NET 8, AspNetCore HTTP server metrics come from the built-in `Microsoft.AspNetCore.Hosting` Meter whose `http.server.request.duration` instrument has no per-tag drop knob; `.DisableHttpMetrics()` is .NET 9+ only. AddView at instrument-level would drop the entire instrument (legitimate app endpoint metrics included). The existing test asserted `Assert.NotEmpty(healthRoutes)` as a documented SOFT-PASS marking the gap.
- **Fix:** Two-commit Gap 1 closure. Commit `2f3ae45` (fix(05-01)) adds `processors.filter/health_metrics` to compose/otel-collector-config.yaml — otel-collector-contrib's filterprocessor drops data points whose `metric.name == "http.server.request.duration"` AND `IsMatch(attributes["http.route"], "^/health/.*")`, wired into the metrics pipeline. Commit `1d07463` (test(05-02)) flips the assertion to `Assert.Empty(healthRoutes)`. SDK still emits, Collector drops before file export — idiomatic OTel layered architecture. OBSERV-08 / HEALTH-05 now FULLY satisfied for metrics (previously partial — backend-only).
- **Files modified:** `compose/otel-collector-config.yaml` + `tests/BaseApi.Tests/Observability/MetricsExportTests.cs`
- **Committed in:** `2f3ae45` (fix(05-01)) + `1d07463` (test(05-02))

**10. [Rule 1 — Bug] tests/.otel-out/telemetry.jsonl persisted post-suite (Gap 2)**
- **Found during:** Continuation block (per user directive "close everything now")
- **Issue:** The otel-collector container's file exporter holds an exclusive write handle on `tests/.otel-out/telemetry.jsonl` for the container's lifetime. The per-test OtelCollectorFixture.DisposeAsync cannot delete the file from inside the test process without orphaning the inode (Pattern B / position-marker strategy was chosen over deletion specifically because of this). D-11 cleanup discipline mandates the file does NOT survive past the test session.
- **Fix:** Added `tests/BaseApi.Tests/Observability/OtelEndOfSuiteCleanup.cs` — an xUnit v3 [assembly: AssemblyFixture(typeof(...))] with IAsyncLifetime. DisposeAsync runs ONCE at end of the assembly: (1) `docker compose stop otel-collector` releases the file handle; (2) deletes telemetry.jsonl + any rotation siblings (`telemetry*.jsonl`); (3) `docker compose start otel-collector` restarts for subsequent test runs. Best-effort with 15s timeouts and stderr logging; never throws.
- **Files modified:** `tests/BaseApi.Tests/Observability/OtelEndOfSuiteCleanup.cs` (NEW)
- **Committed in:** `598c016` (test(05-02))

**11. [Rule 1 — Bug] External-process OTLP traffic re-populates telemetry.jsonl post-restart (Gap 2 follow-up)**
- **Found during:** 3-run verification after Gap 2 initial commit (Run 2 left telemetry.jsonl as 13KB despite the assembly fixture running)
- **Issue:** After `docker compose start otel-collector`, the just-started Collector takes a brief moment to accept connections. External processes on the same host with an OTLP client pointed at localhost:4317 (in this development environment: MCP.Terminal agents producing dotnet runtime metrics) may flush a small batch into a freshly-created telemetry.jsonl during the window between our `start` command and the test process exit. Race surfaces non-deterministically (Run 1/3 clean, Run 2 has residual bytes).
- **Fix:** Post-restart double-delete guard — added `await Task.Delay(TimeSpan.FromMilliseconds(750))` after the restart, then defensive `File.Delete(TelemetryFile)` with IOException swallow (covers the rare case where the Collector reopened the handle in the race window — next test session's end-of-suite cleanup will clean it up).
- **Files modified:** `tests/BaseApi.Tests/Observability/OtelEndOfSuiteCleanup.cs`
- **Committed in:** `ca63351` (fix(05-02))

---

**Total deviations:** 11 auto-fixed across the plan execution.

Initial executor session: 7 (3 Rule 1 production-config bugs to fix-forward Plan 05-01; 4 Rule 1 test-environment bugs surfaced during Wave-0 fixture and HealthEndpointsTests work).

Continuation block: 4 (1 uncommitted prior-executor change surfaced via git status; 2-commit Gap 1 closure for SC#4 metrics-half; 2-commit Gap 2 closure for telemetry.jsonl cleanup + race-condition guard).

All 11 deviations were Rule 1 (bug) auto-fixes; no Rule 4 (architectural) checkpoints were required. The semantic intent of Plan 05-02 (verification battery proves Plan 05-01's wiring delivers ROADMAP SC#1-5 + D-16 + the threat-register dispositions) is fully delivered, and the two prior gaps Plan 05-02 had documented (SC#4 metrics-half SOFT-PASS, telemetry.jsonl deferred-cleanup) are both now CLOSED via Collector-side filterprocessor + xUnit v3 [assembly: AssemblyFixture].

## Verification Evidence

### Build sweep
```text
dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug --no-restore → Build succeeded. 0 Warning(s). 0 Error(s).
```

### 3 consecutive dotnet test SK_P.sln --no-restore runs (continuation block, after Gap 1 + Gap 2 closures)

| Run | Result | telemetry.jsonl post-run |
|-----|--------|--------------------------|
| 1   | Passed! - Failed: 0, Passed: 47, Skipped: 0, Total: 47, Duration: 17s 672ms | ABSENT |
| 2   | Passed! - Failed: 0, Passed: 47, Skipped: 0, Total: 47, Duration: 17s 706ms | ABSENT |
| 3   | Passed! - Failed: 0, Passed: 47, Skipped: 0, Total: 47, Duration: 18s 093ms | ABSENT |

### psql BEFORE/AFTER snapshots
- `.planning/phases/05-observability-health-probes/05-02-psql-BEFORE.snapshot.txt` — captured at Task 1 (Wave 0 docker compose up)
- `.planning/phases/05-observability-health-probes/05-02-psql-AFTER.snapshot.txt` — captured at Task 8 (continuation block, after 3-run verify)
- `diff` exit 0 — byte-identical. Zero leaked test DBs across 16 facts × 3 consecutive runs.

### .gitignore behavior
- `git check-ignore tests/.otel-out/telemetry.jsonl` → exit 0 (ignored) ✓
- `git check-ignore tests/.otel-out/.gitkeep` → exit 1 (NOT ignored — whitelisted) ✓
- Post-suite filesystem: `.gitkeep` present, `telemetry.jsonl` absent ✓

### GREEN/RED grid (Task 8 — all GREEN)

| Item | Status | Evidence |
|------|--------|----------|
| SC#1 Log has CorrelationId + service.name + service.version | GREEN | LogExportTests (2 facts pass) |
| SC#2 Information suppressed when Default=Warning (both sinks) | GREEN | LogLevelFilterTests (2 facts pass) |
| SC#3 /health/live always 200 (no DB check) | GREEN | HealthEndpointsTests.Test_HealthLive_Always_200_NoDbCheck |
| SC#3 /health/ready 503/200 toggle | GREEN | HealthEndpointsTests.Test_HealthReady_503/200 (2 facts) |
| SC#3 /health/startup 503/200 toggle | GREEN | HealthEndpointsTests.Test_HealthStartup_503/200 (2 facts) |
| SC#4 metrics-half — /health/* absent from http.server.request.duration | GREEN (STRICT — Collector filterprocessor) | MetricsExportTests.Test_HealthPath_Absent_From_HttpServerMetrics |
| SC#4 logs-half — /health/* requests not in OTLP log stream | GREEN | HealthEndpointsTests.Test_HealthEndpoints_Absent_From_OTLP_Logs |
| SC#5 Npgsql child span under ASP.NET Core span | GREEN | TraceExportTests.Test_NpgsqlChildSpan_Under_AspNetCore_Request_Span |
| D-16 process.runtime.dotnet.* metric exported | GREEN | MetricsExportTests.Test_RuntimeMetric_ProcessRuntimeDotnet_Exported (accepts dotnet.* OR process.runtime.dotnet.*) |
| T-05-PII no db.parameter* / no bound-value strings in span attrs | GREEN | TraceExportTests.Test_NpgsqlChildSpan_DbStatement_Has_NoParameterValues (3 independent assertions) |
| T-05-LOG-INJECT correlation ID sanitization MEL → OTLP survives | GREEN | LogExportTests.Test_LogRecord_CorrelationId_Survives_Sanitization |
| T-05-READY-DB-EXPOSE ready body has status but no secrets/stack | GREEN | HealthEndpointsTests.Test_HealthReady_Body_Has_Per_Check_Status_But_No_Sensitive_Fields |
| 3 consecutive dotnet test runs exit 0 | GREEN | 3 × 47/47 above |
| psql \l BEFORE/AFTER byte-identical | GREEN | diff exit 0 |
| tests/.otel-out/telemetry.jsonl absent post-test | GREEN | Verified on all 3 runs (Run 1/2/3 ABSENT post-run) |

## Self-Check: PASSED

### File existence
- `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` — FOUND
- `tests/BaseApi.Tests/Observability/CollectionDefinitions.cs` — FOUND
- `tests/BaseApi.Tests/Observability/TestObservabilityController.cs` — FOUND
- `tests/BaseApi.Tests/Observability/LogExportTests.cs` — FOUND
- `tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` — FOUND
- `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — FOUND
- `tests/BaseApi.Tests/Observability/MetricsExportTests.cs` — FOUND
- `tests/BaseApi.Tests/Observability/TraceExportTests.cs` — FOUND
- `tests/BaseApi.Tests/Observability/OtelEndOfSuiteCleanup.cs` — FOUND
- `.planning/phases/05-observability-health-probes/05-02-psql-BEFORE.snapshot.txt` — FOUND
- `.planning/phases/05-observability-health-probes/05-02-psql-AFTER.snapshot.txt` — FOUND
- `compose/otel-collector-config.yaml` (filter/health_metrics block present) — FOUND

### Commits exist (verified via `git log --oneline`)
- `ea8f745` — FOUND (fix(05-01) wget healthcheck drop + :13133 expose)
- `3c99c4d` — FOUND (fix(05-01) Windows bind-mount root user)
- `36aaee9` — FOUND (feat(05-02) Wave-0 fixture + collection + controller)
- `4f45c28` — FOUND (fix(05-02) position-marker + xUnit ctor rule)
- `f6551b5` — FOUND (feat(05-02) LogExportTests)
- `ba30309` — FOUND (feat(05-02) LogLevelFilterTests)
- `736102b` — FOUND (feat(05-02) HealthEndpointsTests)
- `1a84070` — FOUND (feat(05-02) MetricsExportTests)
- `46256a5` — FOUND (feat(05-02) TraceExportTests)
- `008793b` — FOUND (fix(05-02) un-seal fixture for nested inheritance)
- `2f3ae45` — FOUND (fix(05-01) Collector filterprocessor — Gap 1 closure)
- `1d07463` — FOUND (test(05-02) assertion flip SOFT-PASS → STRICT — Gap 1 closure)
- `598c016` — FOUND (test(05-02) [assembly: AssemblyFixture] cleanup — Gap 2 closure)
- `ca63351` — FOUND (fix(05-02) post-restart double-delete guard — Gap 2 race fix)

## Known Stubs

None — Phase 5 ships fully-wired observability + health surfaces, and the only deferred-by-design element at Plan 05-01 close (metrics-side /health filter) has been CLOSED in this plan via the Collector-side filterprocessor. No stubs prevent any plan goal.

## Threat Flags

None new. The plan's existing T-05-PII / T-05-LOG-INJECT / T-05-OTLP-EXFIL / T-05-READY-DB-EXPOSE / T-05-LOG-FORGE register all carry forward unchanged — code shape did not introduce new trust boundaries beyond what `<threat_model>` already enumerated. All five dispositions verified (4 mitigate-verified + 1 accept-noted) — see threat-register table in Goals achieved section above.

## Issues Encountered

None beyond the 11 auto-fixed deviations above. The deviations split into:
- **3 Plan 05-01 production fix-forwards** (`ea8f745`, `3c99c4d`, `2f3ae45`) — compose.yaml + otel-collector-config.yaml needed surgical edits to ship the verification battery against the real Collector. Each carries the `fix(05-01)` prefix per Phase 3 D-18 convention for cross-plan fix-forwards.
- **5 test-environment surprises** surfaced during Wave-0 fixture and HealthEndpointsTests work — position-marker reality, xUnit ctor rule, env-var-vs-ConfigureAppConfiguration timing, ASPNETCORE_ENVIRONMENT=Development overriding LogLevel, batched-write race condition.
- **3 continuation-block closures** — uncommitted un-sealing (`008793b`), SC#4 metrics-half (`2f3ae45` + `1d07463`), and telemetry.jsonl cleanup (`598c016` + `ca63351`).

All deviations were Rule 1 (bug) auto-fixes; no Rule 4 (architectural) checkpoints were required.

## User Setup Required

None — no external service configuration required for Plan 05-02. The Wave-0 task (docker compose up postgres otel-collector) is the only manual precondition; subsequent test runs are fully self-cleaning via OtelEndOfSuiteCleanup. otel-collector container stays running post-suite (Pattern E restart step) so consecutive `dotnet test` invocations work without intervention.

## Next Phase Readiness

**Ready for Phase 6 (Validation + Mapping Base).** Hand-off notes:

- **No observability changes expected in Phase 6.** OTel + Health wiring in Program.cs is stable; Phase 6 only adds BaseDtoValidator + FluentValidation registration + Mapperly mapper interface — all orthogonal to the observability layer.
- **CorrelationId propagation chain stable.** Phase 4 CorrelationIdMiddleware → MEL `BeginScope("CorrelationId", id)` → OTel LoggerProvider `IncludeScopes=true` → OTLP log attribute `CorrelationId`. Verified end-to-end by LogExportTests.
- **OtelCollectorFixture is the pattern future Phase 7+ tests should follow.** Position-marker file-handle strategy (Pattern B), three-ctor IClassFixture activation strategy (Pattern A), env-var-in-ctor pattern (Pattern C) — these will all carry forward when Phase 8's integration tests need to observe telemetry while running real entity CRUD against Testcontainers.PostgreSql.
- **xUnit v3 [assembly: AssemblyFixture] is the right pattern for once-per-assembly setup/teardown.** Phase 8 integration tests should consider an analogous assembly fixture for Testcontainers.PostgreSql container lifecycle (one container per test assembly, not per class) if measured cold-start cost is significant.

**Phase 6 will:**
- Add Mapperly + FluentValidation csproj refs (already pinned in Directory.Packages.props)
- Create BaseDtoValidator<T>, IEntityMapper<,,,>, MP-codes-as-errors csproj setup
- Add MapperlyTests / BaseDtoValidatorTests fact classes
- NO Program.cs OTel/Health edits expected

**Phase 8 forward-looking (well beyond this plan):**
- MigrationRunner replaces StartupCompletionService via 1-line `AddHostedService<MigrationRunner>` swap. `HealthNoStartupCompletionFixture` in Plan 05-02 already provides the negative-path regression net: if Phase 8 forgets to register MigrationRunner, `/health/startup` stays 503 and this fixture's negative-path test catches the regression. The IStartupGate Singleton + interface contract are stable.

---

*Phase: 05-observability-health-probes*
*Plan: 02 (Wave 2 — verification battery)*
*Completed: 2026-05-27*
