---
phase: 05-observability-health-probes
verified: 2026-05-27T10:36:00Z
status: passed
score: 5/5 must-haves verified
overrides_applied: 0
roadmap_success_criteria_verified: 5/5
requirements_verified: 14/14
reconciliations_honored: 4/4
gap_closures_verified: 2/2
live_test_result: "Passed! - Failed: 0, Passed: 47, Skipped: 0, Total: 47, Duration: 18s 703ms"
---

# Phase 5: Observability + Health Probes — Verification Report

**Phase Goal:** Wire OpenTelemetry logs/metrics/traces via the MEL-bridge path and stand up three distinct health probes so the service is observable end-to-end and Kubernetes-style probes can target each endpoint.

**Verified:** 2026-05-27T10:36:00Z
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths (ROADMAP Success Criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | An `ILogger<T>.LogInformation` call during a request appears in the Collector's log export with `service.name=sk-api`, `service.version=3.2.0`, and the request's correlation ID as a log attribute | VERIFIED | `LogExportTests.Test_LogRecord_Has_CorrelationId_And_ServiceResource` (passes live). Program.cs:85-95 wires `builder.Logging.AddOpenTelemetry(o => { o.IncludeScopes = true; o.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("sk-api", "3.2.0")); o.AddOtlpExporter(); })`. Phase 4 `CorrelationIdMiddleware.BeginScope("CorrelationId", id)` flattens through `IncludeScopes=true` into log attribute. |
| 2 | Setting `Logging:LogLevel:Default` to `Warning` suppresses `Information` logs from BOTH the console sink AND the OTLP export — confirming the single MEL filter path (not `WithLogging()`) | VERIFIED | `LogLevelFilterTests.Test_Information_Log_Suppressed_When_Default_Warning` + `Test_Information_Log_Present_When_Default_Information` (both pass live). Grep `WithLogging` in src/ returns 0 hits — MEL-bridge route used exclusively. |
| 3 | `GET /health/live` returns 200 even when Postgres is stopped; `GET /health/ready` returns 503 when Postgres is unreachable and 200 when reachable; `GET /health/startup` returns 503 until the migration runner flips the flag and 200 thereafter | VERIFIED | 5 of 7 HealthEndpointsTests facts: `Test_HealthLive_Always_200_NoDbCheck`, `Test_HealthReady_503_When_Postgres_Unreachable`, `Test_HealthReady_200_When_Postgres_Reachable`, `Test_HealthStartup_503_Before_GateFlipped`, `Test_HealthStartup_200_After_GateFlipped_By_HostedService`. Program.cs:172-186 maps all 3 endpoints with tag predicates + `UIResponseWriter.WriteHealthCheckUIResponse`. |
| 4 | HTTP server metrics for application endpoints appear at the Collector (e.g., `http.server.request.duration`) but requests to `/health/*` do not produce metrics or appear in OTLP logs (filtered out) | VERIFIED | Metrics-half: `MetricsExportTests.Test_HttpServerRequestDuration_Present_For_App_Endpoint` + `Test_HealthPath_Absent_From_HttpServerMetrics` (STRICT `Assert.Empty(healthRoutes)`, line 94). Logs-half: `HealthEndpointsTests.Test_HealthEndpoints_Absent_From_OTLP_Logs`. SC#4 metrics-half gap closure via Collector-side `filter/health_metrics` processor (`compose/otel-collector-config.yaml:37,65`). |
| 5 | A Postgres query issued during a request produces a child span under the ASP.NET Core request span in the trace export (Npgsql instrumentation active) | VERIFIED | `TraceExportTests.Test_NpgsqlChildSpan_Under_AspNetCore_Request_Span` + `Test_NpgsqlChildSpan_DbStatement_Has_NoParameterValues` (3 independent T-05-PII regression assertions). Program.cs:132 chains `.AddNpgsql()` (bare, no callback per Reconciliation 1). |

**Score:** 5/5 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/BaseApi.Core/Health/IStartupGate.cs` | `public interface IStartupGate` + `public sealed class StartupGate` (Volatile.Read / Interlocked.Exchange latch) | VERIFIED | 57 lines, file-scoped namespace, `public sealed`, primary-ctor-friendly, thread-safe latch verified |
| `src/BaseApi.Core/Health/StartupHealthCheck.cs` | `public sealed class StartupHealthCheck(IStartupGate gate) : IHealthCheck` | VERIFIED | 30 lines, primary ctor, tagged for both "startup" + "ready" at registration site |
| `src/BaseApi.Core/Health/StartupCompletionService.cs` | `public sealed class StartupCompletionService(IStartupGate gate) : IHostedService` | VERIFIED | 34 lines, StartAsync flips gate, clean Phase 8 swap-target |
| `src/BaseApi.Service/Program.cs` | OTel + Health composition, additive to Phase 4 pipeline, `public partial class Program { }` marker at bottom | VERIFIED | 195 lines, MEL-bridge logs at lines 85-95, metrics+traces at 104-133, health gate+checks at 143-151, 3 MapHealthChecks at 172-186, marker at line 195 |
| `compose.yaml` | `otel-collector:` service block with image, command, volumes, ports, healthcheck handling | VERIFIED | otel-collector block at lines 24-57; image `otel/opentelemetry-collector-contrib:0.95.0`; ports 4317/4318/13133; `user: "0:0"` for Windows bind-mount writes; wget healthcheck removed (distroless image) — host-side curl probe documented |
| `compose/otel-collector-config.yaml` | OTLP receivers + file/logging exporters + health_check extension + 3 pipelines + filter/health_metrics processor | VERIFIED | 70 lines, all components present + `filter/health_metrics` filterprocessor wired into metrics pipeline (lines 37, 65) — closes SC#4 metrics-half gap |
| `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` | Fused `WebApplicationFactory<Program>` + `IAsyncLifetime` fixture with position-marker file readers, ExportProcessorType.Simple flush, endpoint pinning | VERIFIED | 258 lines, `public class` (intentionally NOT sealed for HealthEndpointsTests nested inheritance — Plan 05-02 Pattern G), 3-ctor strategy (public parameterless + 2 internal overloads) for xUnit IClassFixture compliance |
| `tests/BaseApi.Tests/Observability/OtelEndOfSuiteCleanup.cs` | xUnit v3 `[assembly: AssemblyFixture(...)]` IAsyncLifetime running once at end of suite | VERIFIED | 178 lines, `[assembly: AssemblyFixture(typeof(OtelEndOfSuiteCleanup))]` declaration at line 4, stop→delete→start→post-restart-double-delete sequence |
| `tests/BaseApi.Tests/Observability/{LogExport, LogLevelFilter, HealthEndpoints, MetricsExport, TraceExport}Tests.cs` | 5 fact-test classes covering SC#1-5 + D-16 + T-05-PII | VERIFIED | All 5 files present; live `dotnet test` reports 47 passing (16 Phase 5 facts + 31 Phase 3+4 carry-over) |
| `tests/BaseApi.Tests/Observability/{CollectionDefinitions, TestObservabilityController}.cs` | Wave-0 infrastructure | VERIFIED | DisableParallelization marker + `[Route("test-obs")]` controller with `/ok` (Info log driver) + `/db-roundtrip` (parametrized Npgsql) |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|----|--------|---------|
| `Program.cs` | `IStartupGate` Singleton | `AddSingleton<IStartupGate, StartupGate>()` + `AddCheck<StartupHealthCheck>` | WIRED | Program.cs:143 + 146 |
| `Program.cs` | `StartupCompletionService` IHostedService | `AddHostedService<StartupCompletionService>()` | WIRED | Program.cs:151 — exactly 1 hit; Reconciliation 2 honored (NO inline `app.Services.GetRequiredService<IStartupGate>().MarkReady()` — grep returns 0 hits) |
| `Program.cs` | OTel Collector | `.AddOtlpExporter()` (3 hits — logs + metrics + traces branches) | WIRED | Program.cs:94, 120, 133; env var defaults to `http://localhost:4317` matching compose port mapping |
| Phase 4 `CorrelationIdMiddleware` BeginScope | OTel logs export | `IncludeScopes=true` flattens via MEL bridge | WIRED | Program.cs:88 (`o.IncludeScopes = true`); CorrelationIdMiddleware.cs:79 documents PascalCase key match |
| `compose.yaml` otel-collector | `tests/.otel-out/telemetry.jsonl` | Bind-mount `./tests/.otel-out:/var/otel-out` + file exporter config | WIRED | compose.yaml:42 + compose/otel-collector-config.yaml:46 |
| Test fixture readers | telemetry.jsonl | `ReadAllExportedRecords` with position-marker seek | WIRED | OtelCollectorFixture.cs:190-220 |
| Metrics pipeline | filter/health_metrics processor | `processors: [filter/health_metrics]` | WIRED | compose/otel-collector-config.yaml:65 wires processor into metrics pipeline only (logs + traces pipelines unchanged) |

### Reconciliation Honor Check

| Reconciliation | Expected | Status | Evidence |
|---------------|----------|--------|----------|
| R1 — bare `.AddNpgsql()` (no callback) | 0 hits for `.AddNpgsql(opts` in src/ | HONORED | Grep returns 0 hits in src/; Program.cs:132 uses bare `.AddNpgsql()` with `// SECURITY` comment documenting T-05-PII default-safe rationale |
| R2 — `AddHostedService<StartupCompletionService>` route | Exactly 1 `AddHostedService<StartupCompletionService>` hit in src/; 0 `app.Services.GetRequiredService<IStartupGate>().MarkReady()` hits | HONORED | Grep: `AddHostedService<StartupCompletionService>` returns 1 code hit (Program.cs:151) + 1 doc-comment reference; `app.Services.GetRequiredService<IStartupGate>().MarkReady` returns 0 hits |
| R3 — `public sealed` for Health/ types | `public sealed class` on StartupGate, StartupHealthCheck, StartupCompletionService | HONORED | All 3 files confirmed `public sealed class`; IStartupGate is `public interface`; deviation from CONTEXT D-01/D-02 "internal sealed" wording documented in source XML doc-comments + 05-01-SUMMARY key-decisions |
| R4 — Single fused `OtelCollectorFixture : WebApplicationFactory<Program>, IAsyncLifetime` | Single class composes both contracts | HONORED | OtelCollectorFixture.cs:49 declares `public class OtelCollectorFixture : WebApplicationFactory<Program>, IAsyncLifetime`; intentionally NOT sealed (un-sealing in commit 008793b) to support HealthEndpointsTests nested inheritance (Plan 05-02 Pattern G) |

### Gap Closures Verified

| Gap | Closure Mechanism | Status | Evidence |
|-----|-------------------|--------|----------|
| SC#4 metrics-half (Plan 05-01 SOFT-PASS — metrics-side `Filter` API missing in OTel SDK 1.15.0) | Collector-side `filter/health_metrics` filterprocessor wired into metrics pipeline | CLOSED | compose/otel-collector-config.yaml:37 (processor definition) + line 65 (pipeline wiring) + MetricsExportTests.cs:94 (`Assert.Empty(healthRoutes)` — flipped from SOFT-PASS `Assert.NotEmpty`); commits 2f3ae45 + 1d07463 |
| Telemetry cleanup automation (D-11 discipline — file must not survive past test session) | xUnit v3 `[assembly: AssemblyFixture(typeof(OtelEndOfSuiteCleanup))]` running stop → delete → start → post-restart double-delete | CLOSED | OtelEndOfSuiteCleanup.cs:4 declares assembly fixture; 178-line implementation with best-effort discipline; commit 598c016 (initial) + ca63351 (post-restart guard for external-process residuals) |

### Requirements Coverage

All 14 Phase 5 REQ-IDs declared in PLAN frontmatter cross-referenced against REQUIREMENTS.md (lines 290-306 — all marked Phase 5 / Complete). Every REQ-ID has a corresponding fact test in the 16-fact verification battery.

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|-------------|-------------|--------|----------|
| OBSERV-01 | 05-01, 05-02 | OpenTelemetry 1.15.x stack + Exporter/Instrumentation packages | SATISFIED | Directory.Packages.props pins + LogExportTests verifies resource emission |
| OBSERV-02 | 05-01, 05-02 | Logs wired via `builder.Logging.AddOpenTelemetry(...)` (MEL bridge) | SATISFIED | Program.cs:85; grep `WithLogging` src/ = 0 hits; LogExportTests passes |
| OBSERV-03 | 05-01, 05-02 | HTTP server + client metrics via services-chain | SATISFIED | Program.cs:104-120; MetricsExportTests.Test_HttpServerRequestDuration_Present_For_App_Endpoint |
| OBSERV-04 | 05-01, 05-02 | OTLP exporter targets external Collector | SATISFIED | `.AddOtlpExporter()` × 3 (Program.cs:94, 120, 133); fixture pins endpoint via OtlpExporterOptions.Endpoint + env var |
| OBSERV-05 | 05-01, 05-02 | `service.name` + `service.version` resource attributes | SATISFIED | Two `AddService(serviceName, serviceVersion)` calls (Program.cs:91 + 105); LogExportTests verifies on exported log records |
| OBSERV-06 | 05-01, 05-02 | `Logging:LogLevel` filters BOTH console + OTLP identically | SATISFIED | LogLevelFilterTests (2 facts — present-when-Information + absent-when-Warning) |
| OBSERV-07 | 05-01, 05-02 | OTel logger options: `IncludeFormattedMessage=true`, `IncludeScopes=true`, `ParseStateValues=true` | SATISFIED | Program.cs:87-89 all three set |
| OBSERV-08 | 05-01, 05-02 | Health endpoints excluded from metrics | SATISFIED | Traces filter (Program.cs:123-124) + metrics-side Collector filterprocessor (compose/otel-collector-config.yaml:37) + MetricsExportTests STRICT `Assert.Empty(healthRoutes)` |
| OBSERV-12 | 05-01, 05-02 | OTel tracing with `AddAspNetCoreInstrumentation`/`AddHttpClientInstrumentation`/`AddNpgsql` | SATISFIED | Program.cs:121-133; TraceExportTests verifies child span chain + T-05-PII |
| HEALTH-01 | 05-01, 05-02 | `/health/startup` returns Healthy after DI + migrations | SATISFIED | Program.cs:182 + StartupHealthCheck.cs; HealthEndpointsTests startup probe facts (2) |
| HEALTH-02 | 05-01, 05-02 | `/health/live` returns Healthy independent of dependencies | SATISFIED | Program.cs:172 with "live"-tag-only predicate + Test_HealthLive_Always_200_NoDbCheck |
| HEALTH-03 | 05-01, 05-02 | `/health/ready` reflects Postgres reachability AND startup gate | SATISFIED | Program.cs:177 + ready-tag StartupHealthCheck + NpgSql; HealthEndpointsTests ready facts (2 — reachable + unreachable) |
| HEALTH-04 | 05-01, 05-02 | `AspNetCore.HealthChecks.NpgSql` registered for Postgres reachability | SATISFIED | Program.cs:147 `.AddNpgSql(cfg.GetConnectionString("Postgres")!, tags: new[] { "ready" })` |
| HEALTH-05 | 05-01, 05-02 | Health endpoints excluded from logging + metrics | SATISFIED | Logs: HealthEndpointsTests.Test_HealthEndpoints_Absent_From_OTLP_Logs (with HealthFilterEnabledFixture); Metrics: MetricsExportTests.Test_HealthPath_Absent_From_HttpServerMetrics |

**Orphaned requirements check:** REQUIREMENTS.md traceability table maps exactly 14 IDs to Phase 5 (OBSERV-01..08, OBSERV-12, HEALTH-01..05). All 14 declared in BOTH PLAN frontmatters. Zero orphaned requirements.

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Solution builds + 47 tests pass live | `dotnet test SK_P.sln --no-restore` | `Passed! - Failed: 0, Passed: 47, Skipped: 0, Total: 47, Duration: 18s 703ms` | PASS |
| otel-collector container running | `docker compose ps otel-collector` | `Up 23 seconds` (just restarted by end-of-suite cleanup) | PASS |
| Postgres container healthy | `docker compose ps postgres` | `Up 13 hours (healthy)` | PASS |
| BEFORE/AFTER psql snapshots byte-identical | `diff 05-02-psql-BEFORE.snapshot.txt 05-02-psql-AFTER.snapshot.txt` | Exit 0 (no diff output) | PASS |
| Reconciliation 1 grep guard | grep `.AddNpgsql(opts` src/ | 0 hits | PASS |
| Reconciliation 2 inline-MarkReady guard | grep `app.Services.GetRequiredService<IStartupGate>().MarkReady` src/ | 0 hits | PASS |
| Pitfall 8 grep guard (no WithLogging) | grep `WithLogging` src/ | 0 hits | PASS |
| 3 MapHealthChecks registrations | grep `MapHealthChecks` Program.cs | 3 hits (lines 172, 177, 182) | PASS |
| UIResponseWriter on every probe | grep `UIResponseWriter.WriteHealthCheckUIResponse` Program.cs | 3 code hits (lines 175, 180, 185) | PASS |
| `public partial class Program` marker preserved | grep `public partial class Program` Program.cs | 1 hit (line 195) | PASS |
| Assembly fixture declared | grep `[assembly: AssemblyFixture` tests/ | 1 hit (OtelEndOfSuiteCleanup.cs:4) | PASS |
| Filterprocessor wired into metrics pipeline | grep `filter/health_metrics` compose/ | 2 hits (definition line 37 + pipeline ref line 65) | PASS |
| SC#4 metrics-half STRICT closure | grep `Assert.Empty(healthRoutes)` tests/ | 1 hit (MetricsExportTests.cs:94) | PASS |

### Anti-Patterns Scanned

The phase-modified files were scanned for TODO/FIXME/placeholder/stub patterns:

| File | Pattern Type | Findings | Severity |
|------|-------------|----------|----------|
| `src/BaseApi.Service/Program.cs` | Documented deviations | 1 long `// DEVIATION (Rule 1)` comment explaining metrics-side filter API mismatch — this is intentional, documented + closed via Collector-side filter | Info (intentional, documented closure) |
| `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs:117` | Bare `catch { _startPosition = 0; }` | Confirmed (REVIEW IN-02) — narrow IOException recommended; not blocking | Info |
| `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs:162` | Hard-coded 1s pre-init delay | Confirmed (REVIEW IN-04) — magic number for batching cadence | Info |
| `src/BaseApi.Service/Program.cs:42,43,147` | Null-forgiving `cfg["..."]!` | Confirmed (REVIEW IN-03) — could mask missing config; not blocking | Info |
| Anti-patterns aggregate from `05-REVIEW.md` | 0 critical / 3 warning / 6 info | All findings are fixture-lifecycle robustness and test-determinism concerns; NO security or correctness defects in production code paths | Info (per REVIEW.md status) |

**Stub classification:** None. No code path returns hardcoded empty for the user-visible surface. Every "hardcoded empty" pattern is in test fixtures or initial state overwritten by real data flow (e.g., `_startPosition = 0` is a sentinel updated to real file length on InitializeAsync). REVIEW.md confirms "no security or correctness defects in production code paths."

### Data-Flow Trace (Level 4)

| Artifact | Data Source | Produces Real Data | Status |
|----------|-------------|--------------------|--------|
| `/health/live` endpoint | `"self"` AddCheck → always Healthy | Yes (200 with `"status":"Healthy"` JSON) | FLOWING |
| `/health/ready` endpoint | `StartupHealthCheck` (IStartupGate.IsReady) + `AddNpgSql` (real Postgres connection) | Yes (200/503 by composite gate + DB reachability) | FLOWING |
| `/health/startup` endpoint | `StartupHealthCheck` (IStartupGate.IsReady) | Yes (StartupCompletionService.StartAsync flips on host start; live tests confirm 503 before + 200 after) | FLOWING |
| OTLP log records | MEL → AddOpenTelemetry → AddOtlpExporter → otel-collector → file exporter → telemetry.jsonl | Yes (LogExportTests reads real log records with CorrelationId/service.name/service.version attributes) | FLOWING |
| OTLP metric records | OTel meter providers → AddOtlpExporter → otel-collector → filterprocessor → file exporter | Yes (MetricsExportTests reads http.server.request.duration + process.runtime.dotnet.*/dotnet.*) | FLOWING |
| OTLP trace records | OTel tracer provider → AddAspNetCoreInstrumentation + AddNpgsql → AddOtlpExporter → file exporter | Yes (TraceExportTests reads child-span chain with parentSpanId match + db.statement attribute) | FLOWING |

All artifacts pass Level 4 — data flows end-to-end from emission to file exporter to test assertion.

### Human Verification Required

None required by automated criteria. All 5 ROADMAP SCs verified by passing fact tests + live build; reconciliations and gap closures all programmatically confirmed.

Phase 8 forward-compat path (clean MigrationRunner substitution) is documented in source XML doc-comments and has a regression net (`HealthNoStartupCompletionFixture` negative-path test) — but this is a forward-looking contract, not a current Phase 5 success criterion. No human verification needed for Phase 5 close.

### Notes from 05-REVIEW.md (acknowledgement only — do NOT block phase)

The 05-REVIEW.md (status: issues_found, 0 critical / 3 warning / 6 info) surfaces robustness concerns about test fixtures (env-var restoration on construction throw, file-rotation `_startPosition` invalidation, separate ResourceBuilder on logs vs metrics+traces branches). None are correctness defects; the REVIEW explicitly concludes "no security or correctness defects in production code paths." Phase 5 goal achievement is not blocked by any REVIEW finding. WR-03 (shared ResourceBuilder) is a forward-looking refactor opportunity that becomes relevant when Phase 7's `AddBaseApi` extension consolidates the OTel composition.

## Gaps Summary

No gaps blocking the phase goal. All 5 ROADMAP success criteria verified by passing fact tests against the real otel-collector + real Postgres backends. All 14 Phase 5 REQ-IDs mapped to implementation and verifying tests. All 4 reconciliations honored. Both previously-documented gap closures (SC#4 metrics-half via Collector filterprocessor; telemetry.jsonl cleanup via xUnit v3 assembly fixture) verified present in code.

The 47/47 live test result, byte-identical psql `\l` snapshots, and all reconciliation grep guards returning expected counts together constitute strong end-to-end evidence that Phase 5 delivers its stated goal.

---

*Verified: 2026-05-27T10:36:00Z*
*Verifier: Claude (gsd-verifier)*
*Phase 5 status: PASSED — ready to proceed to Phase 6*
