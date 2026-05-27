# Phase 5: Observability + Health Probes - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-27
**Phase:** 05-observability-health-probes
**Areas discussed:** Startup probe gate mechanism, Telemetry sampling + PII safety, Health probe response format, Verification strategy

---

## Out-of-band action (before gray-area discussion)

User flagged `Service:Name` as misleading and requested rename `steps-api` → `sk-api`. Updated 7 forward-looking files in one commit (`64b007e`):
- `src/BaseApi.Service/appsettings.json`
- `.planning/PROJECT.md`
- `.planning/REQUIREMENTS.md` (INFRA-04 line)
- `.planning/ROADMAP.md` (Phase 1 SC#4, Phase 5 SC#1)
- `.planning/research/PITFALLS.md` (code examples)
- `.planning/research/ARCHITECTURE.md` (file tree, code examples, docker-compose example)
- `.planning/research/FEATURES.md` (locked-value description)

Left untouched (historical audit trail — record what was actually shipped at the time):
- `.planning/STATE.md` velocity log (line 83 — describes Plan 01-02 outcome)
- `.planning/phases/01-repository-scaffold/01-02-PLAN.md`, `01-02-SUMMARY.md`, `01-03-PLAN.md`, `01-03-SUMMARY.md`, `01-VERIFICATION.md` — historical record of Phase 1 with the original name

Build verified green Release + Debug 0 warnings 0 errors after rename.

---

## Startup probe gate mechanism

### Question 1: How should the startup probe flip from unhealthy to healthy?

| Option | Description | Selected |
|--------|-------------|----------|
| IHostedService flips bool flag (Recommended) | Register a `StartupCompletionService : IHostedService` that flips a static/singleton `bool IsReady` flag in StartAsync after all earlier IHostedServices have run. Phase 8 migration runner registers BEFORE this service so it runs first; flag flips only after migrations complete. The HealthCheckRegistration reads the flag. Clean DI integration, deterministic order, testable, idiomatic .NET 8. | ✓ |
| IHostApplicationLifetime.ApplicationStarted token | Register a CancellationToken callback on `IHostApplicationLifetime.ApplicationStarted` that flips the flag. Fires once all IHostedServices return from StartAsync. Slightly simpler than option 1 (no new IHostedService class) but Phase 8 migration runner ordering is harder to reason about. | |
| Static bool flag set directly by Phase 8 runner | Phase 5 ships `public static class StartupGate { public static bool IsReady; }`. Phase 8's migration runner directly sets `StartupGate.IsReady = true` after migrations apply. Simplest mechanism. Downside: static mutable state, harder to test, no DI scope, Phase 5 ships an empty contract that depends on Phase 8 to wire correctly. | |

**User's choice:** IHostedService flips bool flag (Recommended)
**Notes:** User accepted the recommended option without comment.

### Question 2: Where does the IsReady flag live, and how does Phase 8's migration runner integrate?

| Option | Description | Selected |
|--------|-------------|----------|
| Singleton IStartupGate + separate MigrationRunner IHostedService (Recommended) | Phase 5 ships: (a) `IStartupGate` interface with `bool IsReady { get; }` + `void MarkReady()`, registered Singleton; (b) `StartupHealthCheck : IHealthCheck` that reads it. Phase 5 leaves IsReady = true by default (no migration runner yet) so the probe is healthy in v1. Phase 8 adds a `MigrationRunner : IHostedService` that calls `_gate.MarkReady()` AFTER `Database.MigrateAsync()` completes, and changes the default to false. DI-friendly, testable, clean Phase 5 ↔ Phase 8 contract. | ✓ |
| Singleton IStartupGate, no separate hosted service in Phase 5 | Ships ONLY the singleton + IHealthCheck. Default IsReady = true in Phase 5 so the probe is always healthy until Phase 8 wires the migration runner. Phase 8 owns both the runner AND switches the default to false. Slightly less ceremony in Phase 5 but Phase 8 has to flip more switches. | |

**User's choice:** Singleton IStartupGate + separate MigrationRunner IHostedService (Recommended)
**Notes:** Locks Phase 5 ↔ Phase 8 contract. Phase 5 ships the gate + immediate `MarkReady()` call after `Build()`; Phase 8 will remove the immediate call and add `MigrationRunner` that flips the gate post-migration.

---

## Telemetry sampling + PII safety

### Question 1: Tracing sampling strategy?

| Option | Description | Selected |
|--------|-------------|----------|
| AlwaysOnSampler (Recommended for v1) | Every request is sampled — 100% of traces export to the Collector. Simplest config, no surprises during dev/staging, easiest to debug. Trade-off: at production volume, traces can become expensive. PROJECT.md doesn't specify load expectations; AlwaysOn is the .NET 8 default and matches the dev-first posture of v1. | ✓ |
| TraceIdRatioBasedSampler with appsettings knob | Sample N% of traces (e.g., 100% by default, configurable to 10% via `OpenTelemetry:TraceSampleRatio`). Adds one config knob; Collector receives fewer traces in prod. | |
| ParentBasedSampler (root=AlwaysOn, parent=respect) | Use AlwaysOn for root spans; for child spans, respect parent's sampling decision (W3C standard). v1 is the only service though, so this is overkill until there's a calling system. | |

**User's choice:** AlwaysOnSampler (Recommended for v1)

### Question 2: Should Npgsql DB spans capture query parameter values?

| Option | Description | Selected |
|--------|-------------|----------|
| Names only, NO values (Recommended) | Span has the SQL statement template (e.g., `SELECT * FROM steps WHERE id = $1`) but NOT the bound parameter values. Safer default — if any DTO field is sensitive (workflow names with PII, schema definitions with secrets, audit-stamped `created_by` usernames), values never reach the Collector. PROJECT.md `Out of Scope: auth/authz` doesn't mean PII isn't a concern — workflow data is the product. | ✓ |
| Values included (parametrized, no raw concatenation) | Span includes both template + bound values. Richer debugging — ops sees exactly what failed query was reading. Risk: every workflow `Name`, `Description`, schema field, or assignment target hits the Collector. | |

**User's choice:** Names only, NO values (Recommended)
**Notes:** T-05-PII threat mitigation. Spans show SQL template only; bound values never leave the process boundary. Listed as a regression-guarded item in Plan 05-02.

---

## Health probe response format

### Question 1: What body format should /health/live, /health/ready, /health/startup return?

| Option | Description | Selected |
|--------|-------------|----------|
| JSON with per-check status + duration (Recommended) | Use `AspNetCore.HealthChecks.UI.Client.UIResponseWriter.WriteHealthCheckUIResponse` to emit JSON with per-check status + duration. Ops can curl any probe and instantly see which sub-check failed. Adds one PackageReference. Aligns with the observability-first posture of Phase 5. | ✓ |
| Plain text default (Healthy / Unhealthy) | Microsoft's default: response body is literally `Healthy` or `Unhealthy`. Simpler — zero new packages. K8s probes work identically. Trade-off: ops can't see WHICH sub-check failed from the body alone. | |
| Mixed: JSON for /ready and /startup, plain for /live | JSON only where multiple sub-checks exist. Slightly less consistent across endpoints. | |

**User's choice:** JSON with per-check status + duration (Recommended)
**Notes:** Adds one new pin (`AspNetCore.HealthChecks.UI.Client 9.0.0`) to align with existing `AspNetCore.HealthChecks.NpgSql 9.0.0`. Uniform JSON format across all 3 probes — `/health/live` returns minimal JSON, `/health/ready` shows startup + npgsql breakdown.

---

## Verification strategy

### Question 1: How should Phase 5 SC#1, #2, #4, #5 be verified?

| Option | Description | Selected |
|--------|-------------|----------|
| In-process listeners via WebApplicationFactory<Program> (Recommended for v1) | Tests use `ActivityListener` (traces), `MeterListener` (metrics), and a custom in-memory ITestLogExporter (logs) registered through `ConfigureTestServices`. NO docker-compose otel-collector. Fast, deterministic. Trade-off: doesn't exercise actual OTLP serialization — only what reaches the listener INSIDE the process. | |
| Real otel-collector container + file exporter | Add `otel-collector` to `compose.yaml` with a file exporter that writes JSON to a host-mounted directory. Tests read JSON, assert exported shape. Higher fidelity — actually proves the OTLP wire format. Slower, more brittle, but truer to prod behavior. | ✓ |
| Hybrid | Use ActivityListener/MeterListener for most assertions; add real-collector smoke test for SC#5 only. Most thorough but Phase 5 ships 2 verification mechanisms. | |

**User's choice:** Real otel-collector container + file exporter
**Notes:** User picked the higher-fidelity option, accepting the slower test cadence in exchange for actually exercising OTLP wire format. Aligns with Phase 3 / Phase 4 philosophy of real backends for fact tests.

### Question 2: Where does the otel-collector live, and how does cleanup work?

| Option | Description | Selected |
|--------|-------------|----------|
| Add to compose.yaml + file exporter under tests/.otel-out/ (Recommended) | Service `otel-collector` (image `otel/opentelemetry-collector-contrib:0.95.0`, gRPC :4317) in existing compose.yaml. Config at `compose/otel-collector-config.yaml`. File exporter writes to `tests/.otel-out/`. Tests truncate file at start of class, OtelCollectorFixture lifts PostgresFixture D-15 pattern. Devs always see Collector running locally (matches prod posture). | ✓ |
| Separate compose.test.yaml for the collector | Cleaner separation but devs without Collector running don't pay container cost; extra command to remember. Prod parity weaker. | |
| Add to compose.yaml conditionally via profile | One file, opt-in via `--profile test`. Discoverability risk: tests fail confusingly if user forgets the profile flag. | |

**User's choice:** Add to compose.yaml + file exporter under tests/.otel-out/ (Recommended)
**Notes:** `tests/.otel-out/` gitignored. `OtelCollectorFixture` is the Phase 3 D-15 / Phase 4 PostgresFixture analog. Plan 05-02 lifts the cleanup discipline (truncate on init, delete on dispose).

---

## Claude's Discretion

- Exact Npgsql.OpenTelemetry options API surface for "names only, no values" — RESEARCH will validate the actual property name against Npgsql.OpenTelemetry 8.0.4 docs (one of `IncludeFormattedMessage`, `EnableSensitiveDataLogging`, `IncludeParameterValues`).
- `OtelCollectorFixture` test directory location — `tests/BaseApi.Tests/Observability/` chosen to mirror SUT subject area; planner may relocate.
- Health endpoint MapHealthChecks placement in Program.cs — functionally equivalent before/after MapControllers; CONTEXT D-08 picks "before MapControllers" for the plumbing-before-business mental model.
- Test endpoint mechanism for TraceExportTests — real controller (when Phase 7 lands) vs Minimal API stub via ConfigureWebHost. Planner picks.

---

## Deferred Ideas

None — discussion stayed within Phase 5 scope.

Cross-phase items surfaced but already owned by their target phase (NOT deferred from Phase 5):
- Migration runner — Phase 8 (D-13)
- `AddBaseApi(...)` / `UseBaseApi(...)` extensions — Phase 7 (D-08)
- Custom `ActivitySource("sk-api")` for feature-specific spans — any future phase (D-15)
- Production Collector backend destination — ops decision, post-v1
- Sampling tuning (`TraceIdRatioBasedSampler` with knob) — deferred until production load observed (D-04)
- Per-path log filtering — deferred until use case requires it (D-09)
- Richer DB telemetry (parameter values per-entity, query plans) — deferred (D-05)
