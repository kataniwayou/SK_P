# Phase 18: BaseConsole.Core Library - Context

**Gathered:** 2026-05-30
**Status:** Ready for planning

<domain>
## Phase Boundary

Build the reusable `BaseConsole.Core` Generic-Host class library — the console-side mirror
of `BaseApi.Core` — and **validate it standalone** before any concrete console inherits it.

In scope:
- Generic-Host composition root (`AddBaseConsole` + `AddBaseConsoleObservability` +
  `AddBaseConsoleMessaging`) that a concrete console wires in a handful of lines (CONSOLE-01, CONSOLE-04).
- Console-flavored OTel: MEL-bridge logs + runtime metrics + MassTransit meter via OTLP;
  NO AspNetCore/HttpClient instrumentation, NO `.WithTracing`/`TracerProvider` (CONSOLE-02).
- Singleton soft-dependency Redis client (`abortConnect=false`), lifted from `BaseApi.Core` (CONSOLE-03).
- `FrameworkReference Microsoft.AspNetCore.App` — stays a library, not the Web SDK (CONSOLE-05).
- Embedded minimal-Kestrel health listener inside an `IHostedService` exposing
  `/health/live|ready|startup` with strict tag discipline (CONSOLE-HEALTH-01..04).
- The two correlation filters wired bus-wide: inbound consume filter (correlationId → AsyncLocal
  accessor + `"CorrelationId"` MEL log scope) and outbound send/publish filter (stamps the
  ambient correlationId onto outgoing `ICorrelated` messages) (CORR-01, CORR-02).

OUT of this phase (downstream):
- The concrete `Orchestrator` console, its consumers, and instance-unique fan-out endpoint → **Phase 19**.
- WebApi publish-only bus join, RabbitMQ compose tier, appsettings for both hosts → **Phase 19**.
- Two-bus fan-out broadcast test, ES correlation E2E proof, synthetic outbound-filter harness send,
  triple-SHA (`rabbitmqctl list_queues`) close gate → **Phase 20**.

This is a library-only phase: no concrete host ships, no real broker is required to validate it.

</domain>

<decisions>
## Implementation Decisions

### ICorrelated stamping mechanism (Area 1 — CORR-02)
- **D-01:** The outbound send/publish filter stamps the **MassTransit envelope**
  (`SendContext.CorrelationId` / `PublishContext.CorrelationId`) from the ambient AsyncLocal
  accessor, gated on `message is ICorrelated`. It does **NOT** mutate the record body, so
  **`ICorrelated` stays get-only** — Phase 17 D-09's deferred-mutability question is resolved by
  *not* needing mutability. Rationale: zero `ICorrelated` implementers exist this milestone; envelope
  stamping is the idiomatic MassTransit mechanism and is exactly what Phase 20's synthetic harness
  asserts. When the first real implementer lands (Processor milestone), it sets its own body fields
  at construction from the ambient id — body mutation never becomes necessary. **Do NOT reopen the
  frozen `Messaging.Contracts` contract to add setters this phase.**

### Standalone validation strategy (Area 2)
- **D-02:** Validate via a **test-only minimal Generic-Host fixture** in `tests/BaseApi.Tests`
  (e.g. `ConsoleTestHostFixture` composing `AddBaseConsole` + `AddBaseConsoleObservability` +
  `AddBaseConsoleMessaging`). Proven THIS phase:
  1. Host boots through the full `AddBaseConsole*` chain.
  2. `/health/live` returns 200 with **both** Redis and RabbitMQ ports dead.
  3. `/health/startup` flips Healthy once the host has initialized.
  4. **No `TracerProvider`** resolvable from the console container (console analog of the deleted `TraceExportTests`).
  5. MassTransit meter registered; AspNetCore/HttpClient instrumentation absent.
  6. Inbound + outbound correlation filters registered; filter behavior exercised via the in-memory
     `AddMassTransitTestHarness` (ships in the core MassTransit package).
  No throwaway sample host ships inside the library itself.
- **D-03:** Phase 18 close gate = existing **dual-SHA (`psql \l` + `redis-cli --scan`) +
  3-consecutive-GREEN**. The triple-SHA `rabbitmqctl list_queues` snapshot is **NOT** introduced
  here — Phase 18 tests use the in-memory harness (no real broker resources to leak). The broker
  leak gate is Phase 20 (TEST-RMQ-05). Deferred-but-not-lost.

### Health probe wiring (Area 3 — CONSOLE-HEALTH-01..04)
- **D-04:** The embedded health-listener port is **appsettings-configurable with a sensible default**
  (e.g. `ConsoleHealth:Port`, default `8081`) so compose / the Orchestrator agree on it and tests can
  override to an ephemeral port. The minimal Kestrel runs as an `IHostedService` **independent of the
  bus**, so `/health/live` answers while the bus is still connecting (Pitfall 5 + embedded-Kestrel gotcha).
- **D-05:** The console's `/health/startup` gate is a **host-initialized latch** — a duplicated
  `IStartupGate` + a console `StartupCompletionService` that calls `MarkReady()` on `StartAsync`
  (mirrors the *Phase 5 default*, before Phase 8 swapped it to migrations). The console has no
  DB/migrations, so the three-way split is: `startup` = host came up; `ready` = MassTransit bus started
  (MT's auto-registered `ready`-tagged check, no hand-rolled latch); `live` = process alive (self-only).
- Tag discipline (carried, not re-decided): `/live` → predicate `Tags.Contains("live")` → only the
  always-Healthy `"self"` check (never Redis/RMQ); `/ready` → `"ready"` (MT bus check); `/startup` →
  `StartupHealthCheck`. The MassTransit bus health check MUST be tagged `"ready"`, never `"live"`.

### Composition surface (Area 4 — CONSOLE-01, CONSOLE-04)
- **D-06:** Mirror the `BaseApi.Core` seam exactly. Public surface = three calls + run:
  - `builder.AddBaseConsoleObservability(cfg)` — a **separate call on `IHostApplicationBuilder`**
    (same engineering reason as the API's D-13: `builder.Logging.AddOpenTelemetry` needs the
    `ILoggingBuilder` surface).
  - `builder.Services.AddBaseConsole(cfg)` — Redis (lifted) + startup gate + embedded health hosted service.
  - `builder.Services.AddBaseConsoleMessaging(cfg, configureConsumers)` — RabbitMQ host + bus-wide
    outbound correlation send/publish filters; the concrete passes a **consumer-registration lambda**
    (the only *code* parameter; base = infra, concrete = consumers — mirrors `AddBaseApi` vs `AddAppFeatures`).
  - then `await host.RunAsync()`.
- **D-07:** `AddBaseConsole` is **non-generic** (no `TDbContext` analog — consoles have no DbContext).
  All configuration — `Service:Name` / `Service:Version`, OTLP endpoint, RabbitMQ host/credentials,
  Redis connection string — flows through **appsettings** (`cfg`), supplied per concrete console;
  nothing host-specific is hardcoded in the base. (Resolves the API's hardcoded `service.name=sk-api`
  problem: the console reads its own resource identity from config, which feeds the Phase 20 ES proof.)

### No BaseConsole.Core → BaseApi.Core dependency (CONSOLE-HEALTH-04 / research Open-Q b)
- **D-08:** `IStartupGate` / `StartupGate` / `StartupHealthCheck` and the Redis registration are
  **duplicated** into `BaseConsole.Core` (~40 LOC + the Redis extension). A `BaseConsole.Core →
  BaseApi.Core` ProjectReference is forbidden — it would drag EF Core + ASP.NET MVC transitively into
  a worker host. Extraction to a shared `Hosting.Abstractions` assembly is deferred until a third host
  type appears.

### Project location (clarified this session)
- **D-09:** The new project goes at **`src/BaseConsole.Core/`**, consistent with the existing three
  source projects (`src/BaseApi.Core`, `src/BaseApi.Service`, `src/Messaging.Contracts`); tests stay in
  `tests/BaseApi.Tests`. The `src/`-vs-`tests/` split is the locked Phase-1 layout (commit `12a6d90`);
  Visual Studio's Solution Explorer shows projects as a flat list (a virtual tree), which does not
  mirror — and does not change — the physical disk paths the `.sln` references. Flattening `src/` is
  explicitly NOT in scope (see Deferred Ideas).

### Claude's Discretion
- Filter registration ordering (inbound consume filter must run before the log scope opens / before
  user consumer code) — standard MassTransit pipeline wiring; planner/executor decides exact placement.
- Exact `ICorrelationAccessor` / AsyncLocal accessor type name and namespace (the research SUMMARY
  names it `ICorrelationAccessor` + `AsyncLocalCorrelationAccessor`; planner confirms whether it lives
  in `Messaging.Contracts` or `BaseConsole.Core` — note it was NOT created in Phase 17, so it is new here).
- Embedded-health-listener class name (research suggests `EmbeddedHealthEndpointService`); default
  port value; whether health config binds to an options record.
- `BaseConsole.Core.csproj` shape (TargetFramework/Nullable inheritance from `Directory.Build.props`,
  `FrameworkReference` + MassTransit/MassTransit.RabbitMQ/StackExchange.Redis/OTel PackageReferences,
  no `Version=` thanks to CPM) — follows the proven Phase 1 csproj-inheritance idiom.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase scope & requirements (authoritative)
- `.planning/ROADMAP.md` §"Phase 18" (lines 48-59) — goal, depends-on (Phase 17), 5 success criteria.
- `.planning/REQUIREMENTS.md` — CONSOLE-01..05, CONSOLE-HEALTH-01..04, CORR-01, CORR-02 (the 11 mapped
  requirements); Out-of-Scope table.
- `.planning/ROADMAP.md` lines 17-23 — cross-phase hard constraints (OTel metrics-only / no `.WithTracing`;
  correlation key casing load-bearing; RabbitMQ soft-on-CRUD posture is the Orchestrator's *inverse*).

### Research (HIGH confidence — Phase 18 flagged "no phase research needed")
- `.planning/research/SUMMARY.md` §"Phase 18" (lines 100-107) — deliverables list; §"Open Questions"
  (b) duplicate-vs-extract → duplicate (D-08); `AddMeter(InstrumentationOptions.MeterName)`.
- `.planning/research/PITFALLS.md` — Pitfall 3 (traces resurrection → metrics-only), Pitfall 5 (live
  probe must never touch broker/Redis), Pitfall 6 (correlation: both filters, scope-key, AsyncLocal flow).
- `.planning/research/STACK.md` — MassTransit 8.5.5 native OTel, `InstrumentationOptions.MeterName`,
  `AddMassTransitTestHarness` ships in core package, `FrameworkReference Microsoft.AspNetCore.App`.
- `.planning/research/ARCHITECTURE.md` — BaseConsole.Core mirrors BaseApi.Core seam; component list.

### Mirror-source code in BaseApi.Core (lift / duplicate / mirror — do NOT ProjectReference)
- `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` — the OTel shape to
  mirror (MEL bridge, `IncludeScopes=true`, `IHostApplicationBuilder` signature, metrics chain). Console
  flavor: drop `AddAspNetCoreInstrumentation`/`AddHttpClientInstrumentation`, keep `AddRuntimeInstrumentation`,
  add `AddMeter(InstrumentationOptions.MeterName)`; still NO `.WithTracing`.
- `src/BaseApi.Core/DependencyInjection/RedisServiceCollectionExtensions.cs` — the `IConnectionMultiplexer`
  Singleton + `abortConnect=false` soft-dep pattern to duplicate (note: currently `internal`).
- `src/BaseApi.Core/Health/IStartupGate.cs` (`IStartupGate` + `StartupGate`) — duplicate.
- `src/BaseApi.Core/Health/StartupHealthCheck.cs` — duplicate (the `"startup"`+`"ready"` tag pattern; for
  the console, the gate's `MarkReady` is host-start, not migrations).
- `src/BaseApi.Core/Health/StartupCompletionService.cs` — the *Phase 5 default* shape (MarkReady on
  StartAsync, no migrations) is the console's model — NOT the Phase 8 migration variant.
- `src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs` — the three-probe tag
  discipline + `MapHealthChecks` predicate wiring to mirror inside the embedded Kestrel hosted service.
- `src/BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs` +
  `BaseApiApplicationBuilderExtensions.cs` — the `AddBaseApi` / `UseBaseApi` composition-root shape D-06 mirrors.

### Contracts already in place (Phase 17 — reference, do not modify)
- `src/Messaging.Contracts/CorrelationKeys.cs` — `CorrelationKeys.LogScope = "CorrelationId"`; the literal
  the inbound filter opens the MEL scope under.
- `src/Messaging.Contracts/ICorrelated.cs` — get-only six-Guid vocabulary; D-01 keeps it get-only.
- `src/Messaging.Contracts/StartOrchestration.cs` / `StopOrchestration.cs` — POCO control records (no
  consumers wired this phase; they are Phase 19's consumer payloads).

### Pin + solution layout
- `Directory.Packages.props` — MassTransit + MassTransit.RabbitMQ 8.5.5 already pinned (Phase 17);
  Phase 18 adds the `PackageReference` entries (no `Version=`). StackExchange.Redis 2.13.1,
  AspNetCore.HealthChecks.UI.Client 9.0.0, OTel 1.15.x already pinned.
- `SK_P.sln` + `src/` — the new `src/BaseConsole.Core` project is added here (D-09).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `ObservabilityServiceCollectionExtensions.AddBaseApiObservability` — near-verbatim source for
  `AddBaseConsoleObservability`; the console flavor is ~two edits (swap AspNetCore/HttpClient
  instrumentation for the MassTransit meter; everything else — MEL bridge, `IncludeScopes`, OTLP,
  resource builder, the `IHostApplicationBuilder` signature — lifts unchanged).
- `RedisServiceCollectionExtensions.AddBaseApiRedis` — the soft-dep Singleton multiplexer pattern
  (`abortConnect=false`, lazy connect, no startup probe, no health check) duplicates cleanly.
- `IStartupGate`/`StartupGate`/`StartupHealthCheck` — already `public sealed`; duplicate as-is. The
  console `StartupCompletionService` is the *simpler* Phase-5-era variant (MarkReady on StartAsync).
- `CorrelationKeys.LogScope` constant already exists — the inbound filter consumes it directly.
- `AddMassTransitTestHarness` (in the core MassTransit 8.5.5 package, already pinned) — the in-memory
  validation vehicle for D-02; no extra NuGet.

### Established Patterns
- Composition root = `Add*` extensions on `IServiceCollection` + a separate observability call on
  `IHostApplicationBuilder` + a `Use*`/`RunAsync` finisher (Phase 7 D-13). D-06 mirrors this.
- Fail-fast config reads via `cfg.Require(...)` / `cfg.RequireConnectionString(...)` (`RequiredConfig.cs`)
  at the composition boundary — reuse for the console's `Service:*`, RabbitMQ, Redis reads.
- Health probes via strict tag predicates; `live` → self-only is a locked invariant (Pitfall 5).
- OTLP exporter + `IncludeScopes=true` is what serializes `"CorrelationId"` into the same ES field
  across services — the cross-service join depends on the console reusing the exact key + IncludeScopes.

### Integration Points
- `src/BaseConsole.Core` is a NEW project added to `SK_P.sln`; it references `Messaging.Contracts`
  (filters/accessor consume `ICorrelated` + `CorrelationKeys`) and MassTransit + MassTransit.RabbitMQ +
  StackExchange.Redis + OTel; it does NOT reference `BaseApi.Core` or `BaseApi.Service` (D-08).
- `tests/BaseApi.Tests` gains the `ConsoleTestHostFixture` + the Phase 18 validation facts; it references
  the new `BaseConsole.Core` project.
- No change to `BaseApi.Service`, `compose.yaml`, or appsettings this phase — those are Phase 19.

</code_context>

<specifics>
## Specific Ideas

- The phase is deliberately the "library exists + boots + probes + filters, proven in isolation" milestone.
  Observable effects: (1) the new assembly exists and is referenced by the test project; (2) a minimal
  test host boots through `AddBaseConsole*`; (3) live=200 with both deps dead, no TracerProvider, MT meter
  present, both filters registered + harness-exercised; (4) v3.3.0 suite stays GREEN.
- The research SUMMARY (line 95) lists the two correlation filters + AsyncLocal accessor as Phase 17
  deliverables — this was superseded in Phase 17 (D-02): the filters + accessor belong HERE (Phase 18,
  CORR-01/CORR-02). They were NOT created in Phase 17. Build them in `BaseConsole.Core` this phase.
- Outbound filter stamps the MT envelope, not the message body (D-01) — the synthetic harness in Phase 20
  asserts the envelope `CorrelationId`, so design the Phase 18 filter test to read the same surface.
- RabbitMQ posture is the Orchestrator's INVERSE of the WebApi's: the console's `/health/ready` SHOULD go
  Unhealthy when the broker drops (the bus is its reason to live) — that's the MT `ready` check default,
  so do nothing special. (The WebApi's soft-on-CRUD `MinimalFailureStatus=Degraded` is a Phase 19 concern.)

</specifics>

<deferred>
## Deferred Ideas

- **Flatten `src/` to a single repo root** (user raised this session) — the `src/`-vs-`tests/` split is the
  locked Phase-1 layout referenced by `SK_P.sln`, `Directory.Build.props`/`Directory.Packages.props`,
  `Dockerfile`, and every `ProjectReference`. Flattening is a cross-cutting refactor / Phase-1 reversal,
  not a Phase 18 concern. NOT adopted. If desired later, raise as a standalone refactor task — but note
  Visual Studio's flat Solution Explorer view is virtual and is not itself a reason to change disk layout.
- **`ICorrelated` settable properties** (body-field stamping) → revisit only when a real `ICorrelated`
  implementer needs it (Processor milestone); D-01 keeps it get-only with envelope stamping.
- **Triple-SHA `rabbitmqctl list_queues` close gate** → Phase 20 (TEST-RMQ-05); Phase 18 uses the
  in-memory harness with no real broker resources, so the dual-SHA gate suffices here (D-03).
- **Extract IStartupGate/Redis to a shared `Hosting.Abstractions` assembly** → only if/when a third host
  type appears; duplicate the ~40 LOC for now (D-08).
- **Two-bus fan-out test, ES correlation E2E proof, synthetic outbound harness send** → Phase 19/20
  (need the concrete Orchestrator consumer + real broker).

</deferred>

---

*Phase: 18-baseconsole-core-library*
*Context gathered: 2026-05-30*
