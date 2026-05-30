---
phase: 18-baseconsole-core-library
plan: 02
subsystem: infra
tags: [dotnet8, masstransit, rabbitmq, opentelemetry, correlation, asynclocal, messaging-filters, console]

# Dependency graph
requires:
  - phase: 18-01
    provides: "ICorrelationAccessor + AsyncLocalCorrelationAccessor, public RequiredConfig (cfg.Require), BaseConsole.Core csproj with MassTransit/RabbitMQ + console-flavored OTel package refs"
  - phase: 17-messaging-contracts
    provides: "Messaging.Contracts leaf assembly — CorrelationKeys.LogScope (\"CorrelationId\") + get-only ICorrelated vocabulary"
provides:
  - "AddBaseConsoleObservability(IHostApplicationBuilder, IConfiguration) — console-flavored OTel: MEL-bridge logs (IncludeScopes) + MassTransit meter + runtime + OTLP, no traces, no AspNetCore/HttpClient instrumentation (CONSOLE-02)"
  - "InboundCorrelationConsumeFilter : IFilter<ConsumeContext> — AsyncLocal accessor.Set + CorrelationKeys.LogScope MEL scope (CORR-01)"
  - "OutboundCorrelationSendFilter<T> : IFilter<SendContext<T>> + OutboundCorrelationPublishFilter<T> : IFilter<PublishContext<T>> — envelope CorrelationId stamp gated on ICorrelated + Guid.TryParse, body untouched (CORR-02 / D-01)"
  - "AddBaseConsoleMessaging(IServiceCollection, IConfiguration, Action<IBusRegistrationConfigurator>) — AddMassTransit + UsingRabbitMq bus skeleton, accessor Singleton, all three filters bus-wide, default bus health tags preserved (CONSOLE-04, feeds CONSOLE-HEALTH-03)"
affects: [18-03 AddBaseConsole composition root + health listener, 19 Orchestrator consumers + WebApi bus wiring, 20 correlation propagation proof + synthetic harness]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Console OTel = metrics-only: AddMeter(InstrumentationOptions.MeterName) + runtime + OTLP, never a tracer provider (Pitfall 1)"
    - "MassTransit bus-wide correlation pipeline: non-generic IFilter<ConsumeContext> inbound + open-generic IFilter<SendContext<T>>/IFilter<PublishContext<T>> outbound, all registered via Use*Filter(typeof(...), ctx)"
    - "Envelope-only correlation stamp (D-01): write context.CorrelationId, never the record body; ICorrelated stays get-only so Messaging.Contracts is never reopened for setters"
    - "configureConsumers lambda is the sole code seam in AddBaseConsoleMessaging (D-06 — base supplies infra, concrete supplies consumers)"
    - "Auto-registered MassTransit bus health check left at default tags [\"ready\",\"masstransit\"] — the tag-override hook is never called (custom tags would REPLACE defaults)"

key-files:
  created:
    - src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs
    - src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs
    - src/BaseConsole.Core/Messaging/OutboundCorrelationSendFilter.cs
    - src/BaseConsole.Core/Messaging/OutboundCorrelationPublishFilter.cs
    - src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs
  modified: []

key-decisions:
  - "InstrumentationOptions resolves from the MassTransit.Monitoring namespace (NOT the bare MassTransit namespace as the plan's <interfaces> note claimed); MeterName == \"MassTransit\" confirmed via package-reference probe"
  - "c.Host(string host, Action<IRabbitMqHostConfigurator>) with h.Username/h.Password is the canonical RabbitMQ host overload in MassTransit.RabbitMQ 8.5.5 (build-confirmed)"
  - "Doc-comment prose rephrased to avoid the literal forbidden tokens (.WithTracing, AddSource, ConfigureHealthCheckOptions) so the plan's strict grep verification gate passes against the working tree — no code symbol ever referenced them (Plan 01 precedent)"

requirements-completed: [CONSOLE-02, CONSOLE-04, CORR-01, CORR-02]

# Metrics
duration: 4min
completed: 2026-05-30
---

# Phase 18 Plan 02: Console OTel + Correlation Filters + Bus Skeleton Summary

**Console-flavored metrics-only OpenTelemetry composition on `IHostApplicationBuilder`, the three bus-wide MassTransit correlation filters (inbound consume opens the `"CorrelationId"` MEL scope + populates the AsyncLocal accessor; both outbound send/publish stamp the envelope `CorrelationId` gated on `ICorrelated` + `Guid.TryParse` with the record body untouched), and the `AddBaseConsoleMessaging` RabbitMQ bus skeleton that registers them all bus-wide plus the accessor Singleton — warning-clean in Release and Debug.**

## Performance

- **Duration:** 4 min
- **Started:** 2026-05-30T08:44:46Z
- **Completed:** 2026-05-30T08:48:58Z
- **Tasks:** 3
- **Files modified:** 5 (all created under src/BaseConsole.Core/)

## Accomplishments
- Landed `AddBaseConsoleObservability` (CONSOLE-02): MEL-bridge logs with `IncludeScopes=true` (load-bearing for the correlation scope) + OTLP, plus metrics = `AddMeter(InstrumentationOptions.MeterName)` + `AddRuntimeInstrumentation` + OTLP. No tracer provider, no AspNetCore/HttpClient instrumentation (vs the API base library analog).
- Landed the three correlation filters: `InboundCorrelationConsumeFilter` (CORR-01 — `accessor.Set` + `CorrelationKeys.LogScope` MEL scope with a `Guid.NewGuid()` fallback) and the open-generic `OutboundCorrelationSendFilter<T>`/`OutboundCorrelationPublishFilter<T>` (CORR-02 / D-01 — envelope `context.CorrelationId` stamp gated on `ICorrelated` + `Guid.TryParse`, body never mutated).
- Landed `AddBaseConsoleMessaging` (CONSOLE-04): `AddMassTransit` + `UsingRabbitMq` with host/credentials read via `cfg.Require` fail-fast, the `ICorrelationAccessor` Singleton, all three filters wired bus-wide (`UseConsumeFilter`/`UseSendFilter`/`UsePublishFilter`), `ConfigureEndpoints`, and the consumer-registration lambda seam (empty this phase). The auto-registered bus health check is left at default tags `["ready","masstransit"]` for CONSOLE-HEALTH-03.
- Release + Debug builds both warning-clean; all plan `<verification>` grep gates green.

## Task Commits

Each task was committed atomically:

1. **Task 1: AddBaseConsoleObservability — metrics-only OTel** - `963fa47` (feat)
2. **Task 2: The three correlation filters (inbound consume, outbound send, outbound publish)** - `53b8aa5` (feat)
3. **Task 3: AddBaseConsoleMessaging — bus skeleton + bus-wide filter registration** - `aed4433` (feat)

**Plan metadata:** _(docs commit appended after STATE/ROADMAP updates)_

## Files Created/Modified
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` - `AddBaseConsoleObservability(IHostApplicationBuilder, IConfiguration)`: MEL-bridge logs (IncludeScopes) + OTLP, metrics with MassTransit meter + runtime + OTLP, no traces
- `src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs` - `IFilter<ConsumeContext>` opening the `CorrelationKeys.LogScope` MEL scope + populating the AsyncLocal accessor
- `src/BaseConsole.Core/Messaging/OutboundCorrelationSendFilter.cs` - `IFilter<SendContext<T>>` envelope stamp gated on `ICorrelated` + `Guid.TryParse`
- `src/BaseConsole.Core/Messaging/OutboundCorrelationPublishFilter.cs` - `IFilter<PublishContext<T>>` identical envelope stamp
- `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` - `AddBaseConsoleMessaging`: bus skeleton + accessor Singleton + bus-wide filter registration

## Decisions Made
- `InstrumentationOptions` is in the `MassTransit.Monitoring` namespace, not the bare `MassTransit` namespace the plan's `<interfaces>` note asserted. Confirmed `MeterName == "MassTransit"` via an isolated package-reference probe. The using was corrected to `MassTransit.Monitoring`.
- The RabbitMQ host overload `c.Host(rabbitHost, h => { h.Username(...); h.Password(...); })` (host string + `IRabbitMqHostConfigurator`) is the canonical and build-confirmed shape in MassTransit.RabbitMQ 8.5.5.
- Doc-comment prose was rephrased to avoid the literal forbidden tokens so the plan's strict grep gate passes (Plan 01 precedent) — no code symbol ever referenced them.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] `InstrumentationOptions` namespace correction**
- **Found during:** Task 1
- **Issue:** The plan's `<interfaces>` note and the action body's usings stated `InstrumentationOptions` lives in the bare `MassTransit` namespace. With `using MassTransit;` the build failed: `CS0103: The name 'InstrumentationOptions' does not exist in the current context`.
- **Fix:** Probed MassTransit 8.5.5 in an isolated package-reference project — the type resolves from `MassTransit.Monitoring` and `MeterName == "MassTransit"`. Replaced the `using MassTransit;` with `using MassTransit.Monitoring;`. No change to the called member or its value.
- **Files modified:** `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs`
- **Verification:** `dotnet build ... -c Release` exits 0, zero warnings; `AddMeter(InstrumentationOptions.MeterName)` literal preserved per acceptance criteria.
- **Committed in:** `963fa47`

**2. [Rule 3 - Blocking] Rephrased doc-comment prose to satisfy the strict grep verification gate**
- **Found during:** Tasks 1 and 3
- **Issue:** The plan's acceptance criteria assert the observability file does NOT contain `.WithTracing` / `AddSource` and the messaging file does NOT contain `ConfigureHealthCheckOptions`. My first-pass explanatory XML-doc referenced these tokens in prose (e.g. "no `<c>.WithTracing</c>` / `<c>AddSource</c>` here", "`<c>ConfigureHealthCheckOptions</c>` is deliberately NOT called"), which would trip a literal grep gate even though no code symbol referenced them.
- **Fix:** Rephrased the offending comments to convey the same intent without the literal tokens ("no trace pipeline and no activity-source registration", "the bus-check tag-override hook is deliberately NOT called").
- **Files modified:** `BaseConsoleObservabilityExtensions.cs`, `MessagingServiceCollectionExtensions.cs`
- **Verification:** Grep over `src/BaseConsole.Core/` for all forbidden tokens returns "No matches found"; both builds remain warning-clean.
- **Committed in:** `963fa47` / `aed4433` (folded into the respective task commits)

---

**Total deviations:** 2 auto-fixed (both blocking — a real namespace mismatch + verification-gate compliance)
**Impact on plan:** One real one-line `using` correction (Deviation 1) + cosmetic comment edits (Deviation 2). No behavioral or structural change beyond the corrected namespace; no scope creep.

## Issues Encountered
The `InstrumentationOptions` namespace mismatch (Deviation 1) was the only real issue — surfaced immediately on the first Release build and resolved by package probe. All subsequent builds (Release + Debug) passed with zero warnings on the first attempt.

## User Setup Required
None — this plan registers types and DI extensions only. No live RabbitMQ connection is established at build time (the bus is wired but not started until the Plan 03 composition root / Plan 04 harness exercises it).

## Next Phase Readiness
- The bus skeleton + correlation machinery now exist. Plan 03 (`AddBaseConsole` composition root + embedded health listener) can call `AddBaseConsoleObservability` and `AddBaseConsoleMessaging`, and surface the auto-registered bus `ready` check inside the inner Kestrel listener (CONSOLE-HEALTH-03).
- Plan 04's in-memory harness exercises the three filters (inbound scope + accessor population; outbound envelope stamp) and asserts no tracer provider is resolvable (Pitfall 1).
- No blockers.

## Threat Surface Scan
No new security-relevant surface beyond the plan's `<threat_model>`. The mitigations for T-18-04 (inbound id as opaque scope value, `Guid.NewGuid()` fallback), T-18-05 (`cfg.Require` credential reads, key-not-value in exceptions), and T-18-07 (no tracer provider / `AddSource`) are all implemented as specified.

## Self-Check: PASSED
- All 5 created files verified present on disk (`git ls-files src/BaseConsole.Core/` shows the 5 new files among the library set).
- All 3 task commits verified in `git log`: `963fa47`, `53b8aa5`, `aed4433`.

---
*Phase: 18-baseconsole-core-library*
*Completed: 2026-05-30*
