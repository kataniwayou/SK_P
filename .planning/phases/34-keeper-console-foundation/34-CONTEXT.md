# Phase 34: Keeper Console Foundation - Context

**Gathered:** 2026-06-05
**Status:** Ready for planning

<domain>
## Phase Boundary

Stand up a runnable, multi-replica `Keeper` console on `BaseConsole.Core` (mirroring `Orchestrator`) that **builds, containerizes, and joins the compose stack as a healthy tier**, with work load-balanced across replicas via a **shared competing-consumer endpoint**.

This phase is the **host shell only** — NO fault-recovery logic, NO L2 probe loop, NO pause/resume, NO real `Fault<T>` intake (those are Phases 35–38). The scope anchor is KEEP-01/02/03; the requirements in REQUIREMENTS.md read as a SPEC and are locked.

</domain>

<decisions>
## Implementation Decisions

### Multi-replica round-robin proof (Fork 1 → 1a)
- **D-01:** Phase 34 ships a **minimal placeholder competing-consumer** — a trivial `IConsumer<T>` bound to a stable shared endpoint (no `InstanceId`/`Temporary`) — purely to materialize the shared queue and make KEEP-02's round-robin behavior **live-verifiable in this phase**. Phase 35 swaps this placeholder for the two real `Fault<EntryStepDispatch>` / `Fault<ExecutionResult>` consumers on the same competing-consumer endpoint pattern.
- **D-02:** The placeholder MUST use the `ResultConsumerDefinition` binding shape — stable queue name via a `ConsumerDefinition<T>.EndpointName`, **plain `AddConsumer<,>()`** with NO `.Endpoint(e => { e.InstanceId; e.Temporary = true; })`. This is the explicit inverse of the Start/Stop per-replica fan-out, so RabbitMQ round-robins one message to one replica.
- **D-03:** Placeholder message type and consumer body are throwaway (a no-op / log-only handler is sufficient). It exists to prove topology, not behavior. Do NOT invest it with recovery semantics.

### Compose multi-replica expression (Fork 2 → 2a)
- **D-04:** The new `keeper` compose tier is defined **WITHOUT `container_name`** (a named container cannot be scaled) and **WITH `deploy.replicas: 2`**, so a plain `docker compose up` brings up 2 healthy Keeper replicas and round-robin is the default reality — not a manual `--scale` step. This is the one deliberate divergence from the `orchestrator`/`processor-sample` tiers (which pin `container_name: sk-X`).
- **D-05:** Otherwise mirror the `orchestrator` compose tier: `build.context: .` + `dockerfile: src/Keeper/Dockerfile`, `restart: unless-stopped`, `depends_on` rabbitmq + redis (both `service_healthy`), the `RabbitMq__*` + `ConnectionStrings__Redis` + `OTEL_EXPORTER_OTLP_ENDPOINT` env block, and a `wget --spider -q http://localhost:8083/health/ready` healthcheck (interval 10s / timeout 3s / retries 5 / start_period 30s). **No `baseapi-service` dependency** (Keeper resolves no identity over the WebApi — unlike processor-sample).

### Readiness gate
- **D-06:** Keeper **keeps the `BaseConsole.Core` default `StartupCompletionService`** — readiness flips on **bus-started**. Do NOT copy the Orchestrator's removal of `StartupCompletionService` (that removal exists only to gate readiness on L1 hydration, which Keeper has none of in this phase).

### Reference firewall & project shape
- **D-07:** `Keeper.csproj` references **`BaseConsole.Core` + `Messaging.Contracts` ONLY** — no `BaseApi.*` (no EF/MVC/Npgsql/Swagger surface), and **no Quartz/Cronos** (Keeper does not schedule). Reference closure is leaner than Orchestrator's: `MassTransit` + `MassTransit.RabbitMQ` + the two ProjectReferences. `OutputType=Exe`, `RootNamespace`/`AssemblyName` = `Keeper`, `appsettings.json` copied to output (`CopyToOutputDirectory=PreserveNewest`, same as Orchestrator since worker SDK doesn't copy it by default).
- **D-08:** `Program.cs` mirrors `Orchestrator/Program.cs` MINUS the Quartz/L1/scheduler/hydration/metrics block and MINUS the `StartupCompletionService` removal: `Host.CreateApplicationBuilder` → `AddBaseConsoleObservability` → `AddBaseConsole` → `Configure<RetryOptions>(GetSection("Retry"))` (DLQ-04 shared policy) → `AddBaseConsoleMessaging(cfg, x => x.AddConsumer<Placeholder, PlaceholderDefinition>())` → `Build` → `RunAsync`.

### Shared `Immediate(N)` policy
- **D-09:** Bind `RetryOptions` from the `"Retry"` appsettings section in Phase 34 already (DLQ-04: "all consumers … use the same `Immediate(N)` policy bound from the shared `RetryOptions`"). The placeholder's `ConsumerDefinition` applies `UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit))` — establishing the pattern the Phase 35/36 consumers inherit.

### Claude's Discretion
- Health port value (8083 follows the 8080/8081/8082 convention — container-internal, NOT published to host).
- Placeholder message/consumer naming and exact no-op body.
- Dockerfile COPY restore-cache layer ordering (mirror Orchestrator's csproj-only restore layer).
- Whether the stable queue name lives as a `KeeperQueues` const in `Messaging.Contracts` (mirroring `OrchestratorQueues`) or local to Keeper — planner's call; the const-in-Contracts pattern is the house precedent.
- appsettings.json key set (Service / RabbitMq / Redis / ConsoleHealth / Retry — mirror Orchestrator).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Requirements (locked)
- `.planning/REQUIREMENTS.md` §"Keeper Console Foundation (KEEP)" — KEEP-01/02/03 verbatim (the SPEC for this phase); §"Give-Up / Dead-Letter (DLQ)" DLQ-04 (shared `Immediate(N)` from `RetryOptions`).
- `.planning/ROADMAP.md` §"Phase 34: Keeper Console Foundation" — goal + 4 success criteria.

### Mirror template (the console to clone)
- `src/Orchestrator/Program.cs` — the thin-shell composition-root pattern Keeper mirrors (minus Quartz/L1/hydration/metrics + minus the `StartupCompletionService` removal).
- `src/Orchestrator/Orchestrator.csproj` — project shape, reference firewall (no `BaseApi.*`), `OutputType=Exe`, appsettings Content copy.
- `src/Orchestrator/Dockerfile` — multi-stage `sdk:8.0` build → `aspnet:8.0-bookworm-slim` runtime (+ `wget` install for the healthcheck), `USER app`, `ASPNETCORE_URLS`/`EXPOSE` on the health port.
- `src/Orchestrator/appsettings.json` — Service/RabbitMq/Redis/ConsoleHealth/Retry key shape.

### Competing-consumer endpoint pattern (D-02 source)
- `src/Orchestrator/Consumers/ResultConsumerDefinition.cs` — the STABLE shared competing-consumer binding (`EndpointName` const, plain `AddConsumer`, `UseMessageRetry(Immediate(Limit))`) — the inverse of the Start/Stop `InstanceId`+`Temporary` fan-out. Keeper's placeholder copies this shape.

### Base library surface
- `src/BaseConsole.Core/` — `AddBaseConsoleObservability`, `AddBaseConsole`, `AddBaseConsoleMessaging`, the embedded health probes (`/live` `/ready` `/startup`), and `StartupCompletionService` (D-06 keeps it).
- `src/Messaging.Contracts/Configuration/` — `RetryOptions` (D-09 binds it); `OrchestratorQueues` (queue-name-const precedent for D-08 discretion).

### Compose integration
- `compose.yaml` §`orchestrator:` (lines ~185–214) and §`processor-sample:` (lines ~227–252) — the tier shape Keeper mirrors; D-04 diverges by dropping `container_name` and adding `deploy.replicas: 2`.
- `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` — existing compose-shape assertions; a new Keeper tier likely needs a corresponding fact.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`BaseConsole.Core` extension trio** (`AddBaseConsoleObservability` / `AddBaseConsole` / `AddBaseConsoleMessaging`): the entire infra spine — Keeper's `Program.cs` is ~6 meaningful lines on top of these.
- **`ResultConsumerDefinition` shape:** drop-in template for the placeholder competing-consumer definition (stable `EndpointName`, `IOptions<RetryOptions>` ctor, `UseMessageRetry(Immediate(Limit))`).
- **`src/Orchestrator/Dockerfile`:** byte-near clone — swap the COPY/restore/publish target list from `Orchestrator` to `Keeper`'s closure (Messaging.Contracts + BaseConsole.Core + Keeper), bump the port to 8083.
- **`RetryOptions` + `"Retry"` config section:** already exists and is bound the same way in Orchestrator (`Program.cs:29`).

### Established Patterns
- **Thin-shell console:** `Host.CreateApplicationBuilder` (NOT `WebApplication`); ASP.NET surface flows transitively via `BaseConsole.Core`'s `FrameworkReference Microsoft.AspNetCore.App` (no FrameworkReference in the concrete csproj).
- **Competing-consumer vs fan-out:** plain `AddConsumer<,>()` + `ConsumerDefinition.EndpointName` = shared round-robin; `.Endpoint(e => { e.InstanceId; e.Temporary = true; })` = per-replica broadcast. Keeper wants the former (D-02).
- **Health port convention:** 8080 baseapi / 8081 orchestrator / 8082 processor → 8083 keeper; `wget --spider` healthcheck (the runtime image ships no curl/wget → Dockerfile installs wget before `USER app`).
- **OTLP env knob:** `OTEL_EXPORTER_OTLP_ENDPOINT: http://otel-collector:4317` in compose (appsettings `OpenTelemetry:Endpoint` is dead config).

### Integration Points
- New SK_P.sln project entry `src/Keeper/Keeper.csproj`.
- New `keeper` tier in `compose.yaml` (with `deploy.replicas: 2`, no `container_name`).
- New `src/Keeper/Dockerfile` referenced by the compose tier.
- Possible new `KeeperQueues` const in `Messaging.Contracts` (mirrors `OrchestratorQueues`).
- Possible new `ComposeYamlFacts` assertion for the keeper tier.

</code_context>

<specifics>
## Specific Ideas

- "Mirror Orchestrator" is the explicit design intent — Keeper is the structural twin of the Orchestrator console, leaner (no scheduling, no hydration, no L1).
- The one deliberate compose divergence (`deploy.replicas: 2`, no `container_name`) is what makes "multi-replica healthy tier" the default `docker compose up` reality rather than a manual `--scale`.
- The placeholder consumer is explicitly throwaway scaffolding to make KEEP-02 verifiable now; it carries no recovery meaning and is replaced wholesale in Phase 35.

</specifics>

<deferred>
## Deferred Ideas

- Real `Fault<EntryStepDispatch>` / `Fault<ExecutionResult>` intake + 6-id/`H` extraction + execution log-scope — **Phase 35** (INTAKE-03, KMET-04). Replaces the Phase-34 placeholder consumer.
- L2 health-probe recovery loop + `keeper-dlq` (DLQ-2) + two-DLQ topology — **Phase 36** (PROBE-01..05, DLQ-01..04).
- `PauseWorkflow`/`ResumeWorkflow` contracts + orchestrator coordination — **Phase 37** (PAUSE-01..05).
- Keeper meter + counters/histograms + real-stack E2E + close gate — **Phase 38** (KMET-01..03, TEST-01..03).

</deferred>

---

*Phase: 34-keeper-console-foundation*
*Context gathered: 2026-06-05*
