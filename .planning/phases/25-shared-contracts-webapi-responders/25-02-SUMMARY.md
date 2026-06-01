---
phase: 25-shared-contracts-webapi-responders
plan: 02
subsystem: api
tags: [webapi-responders, masstransit-request-response, dependency-firewall, dual-response, degraded-health-cap, di-hooks]

# Dependency graph
requires:
  - phase: 25-shared-contracts-webapi-responders
    plan: 01
    provides: six request/response records + ProcessorQueues constants + public ProcessorProjection in Messaging.Contracts
  - phase: 19-orchestrator-console-webapi-bus-wiring
    provides: publish-only AddBaseApiMessaging join with the MinimalFailureStatus=Degraded health cap + Messaging.Contracts-only firewall
provides:
  - AddBaseApiMessaging gains two optional null-default MassTransit-typed hooks (configureConsumers + configureEndpoints); no-hook call is byte-equivalent publish-only
  - GetProcessorBySourceHashConsumer (RPC-01) dual-response identity responder on ProcessorQueues.IdentityQuery
  - GetSchemaDefinitionConsumer (RPC-02) dual-response schema-definition responder on ProcessorQueues.SchemaQuery
  - AddBaseApiResponderMessaging (BaseApi.Service Composition extension) wires both consumers on explicit ReceiveEndpoints
  - BaseApiCoreFirewallTests reflection guard asserting BaseApi.Core references neither BaseApi.Service nor BaseConsole.Core
affects: [26-baseprocessor-core, 27-execution-round-trip]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Two optional MassTransit-typed seams on a Core DI extension (configureConsumers on IBusRegistrationConfigurator + configureEndpoints on IBusRegistrationContext/IRabbitMqBusFactoryConfigurator) so the Service binds concrete consumers without the Core library naming them — firewall held"
    - "Dual-response query responder: try { read; RespondAsync<TFound> } catch (NotFoundException) { RespondAsync<TNotFound> }, no correlation filters, no ConsumerDefinition"
    - "In-memory MassTransit harness round-trip via harness.GetRequestClient<T>() + GetResponse<TFound,TNotFound>() over a REAL service on a seeded EF-InMemory AppDbContext"

key-files:
  created:
    - src/BaseApi.Service/Features/Processor/Responders/GetProcessorBySourceHashConsumer.cs
    - src/BaseApi.Service/Features/Schema/Responders/GetSchemaDefinitionConsumer.cs
    - src/BaseApi.Service/Composition/ResponderMessaging.cs
    - tests/BaseApi.Tests/Messaging/BaseApiCoreFirewallTests.cs
    - tests/BaseApi.Tests/Messaging/ProcessorResponderTests.cs
    - tests/BaseApi.Tests/Messaging/SchemaResponderTests.cs
  modified:
    - src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs
    - src/BaseApi.Service/Program.cs

key-decisions:
  - "Responder wiring extracted to a BaseApi.Service-owned Composition extension (AddBaseApiResponderMessaging) instead of inlined in Program.cs — the inline two-lambda call pushed Program.cs over the SC#3 <=10 body-line cap (ProgramMinimalityFacts). The plan explicitly permitted this extraction; the exact consumer/endpoint bindings are preserved."
  - "Test namespace is BaseApi.Tests.MessagingResponders, NOT BaseApi.Tests.Messaging — a Messaging-suffixed namespace shadows the top-level Messaging namespace for sibling files that reference Messaging.Contracts.* unqualified (broke StopConsumerLifecycleTests at compile)."
  - "Responder tests construct the REAL ProcessorService/SchemaService (parameterless Mapperly mapper + new Repository<T>(db) + seeded EF-InMemory AppDbContext; read path ignores the validators, so NSubstitute passing-stubs suffice) — the plan's preferred lowest-effort option. No read-seam interface was introduced."
  - "harness.GetRequestClient<T>() is the MassTransit 8.5.5 accessor (compiled + ran GREEN) — the AddRequestClient<T>() DI fallback was not needed."

patterns-established:
  - "Optional-hook extension of a Core DI bus join keeps the dependency firewall by typing the seams in MassTransit interfaces only"
  - "Dual found/not-found bus responder backed by a NotFoundException-on-miss read"

requirements-completed: [RPC-01, RPC-02, RPC-03]

# Metrics
duration: ~32min
completed: 2026-06-01
---

# Phase 25 Plan 02: WebApi Bus Responders Summary

**Extended the WebApi's Phase-19 publish-only MassTransit join into a request/response responder host — two optional Core hooks, two dual-response consumers (`GetProcessorBySourceHash` RPC-01 + `GetSchemaDefinition` RPC-02) bound on explicit `ProcessorQueues.*` endpoints — while preserving the Core->Service/Console dependency firewall, the publish-only default path, and the Degraded bus-health cap (RPC-03). Full suite GREEN 345/345.**

## Performance

- **Duration:** ~32 min (17:16 -> 17:48 UTC)
- **Tasks:** 3
- **Files:** 8 (6 created, 2 modified)
- **Full real-stack suite:** 345/345 GREEN (one ~3.5-min pass after the Task-3 fix)

## Accomplishments
- `AddBaseApiMessaging` now takes two optional null-default hooks — `configureConsumers` (`Action<IBusRegistrationConfigurator>`) and `configureEndpoints` (`Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>`). With no hooks the join is byte-equivalent to the Phase-19 publish-only path. The `MinimalFailureStatus = Degraded` block is byte-identical; no `ConfigureEndpoints(context)` auto-naming was introduced (D-06).
- `GetProcessorBySourceHashConsumer` (RPC-01) answers a hit with `ProcessorIdentityFound { Id, InputSchemaId?, OutputSchemaId?, ConfigSchemaId? }` and a miss (the backing `ProcessorService.GetBySourceHashAsync` throws `NotFoundException`) with `ProcessorIdentityNotFound`.
- `GetSchemaDefinitionConsumer` (RPC-02) answers a hit with `SchemaDefinitionFound { Definition }` and a miss with `SchemaDefinitionNotFound`.
- Both responders are bound on explicit `ReceiveEndpoint`s keyed off `ProcessorQueues.IdentityQuery` / `ProcessorQueues.SchemaQuery` via the Service-owned `AddBaseApiResponderMessaging` extension.
- `BaseApiCoreFirewallTests` (reflection, no host boot) proves `BaseApi.Core` references neither `BaseApi.Service` nor `BaseConsole.Core` after the hook addition (T-25-02-04 mitigation).
- Two harness round-trip test classes prove found AND not-found for both queries over an in-memory bus + a real service on a seeded EF-InMemory DbContext (no RabbitMQ broker).

## Task Commits

1. **Task 1: two optional hooks + Core firewall guard** — `2bf83ff` (feat)
2. **Task 2: dual-response consumers + harness round-trips** — `a32c762` (feat)
3. **Task 3: Program.cs responder wiring (extracted extension)** — `37f549f` (feat)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Test namespace shadowed the top-level `Messaging` namespace**
- **Found during:** Task 1 (first build after creating `BaseApiCoreFirewallTests`)
- **Issue:** The plan placed the new tests under `tests/BaseApi.Tests/Messaging/`. Using the matching namespace `BaseApi.Tests.Messaging` made C# resolve every sibling file's unqualified `Messaging.Contracts.Projections.StepProjection` to `BaseApi.Tests.Messaging.Contracts...` first — breaking `StopConsumerLifecycleTests.cs` at compile (CS0234/CS1503).
- **Fix:** Named the new test classes `namespace BaseApi.Tests.MessagingResponders` (files still live in the `Messaging/` folder). No unrelated files touched.
- **Files modified:** all three new test files.
- **Commits:** `2bf83ff` (firewall test), `a32c762` (responder tests).

**2. [Rule 1 - Bug] Inline responder wiring broke the Program.cs `<=10` body-line cap (SC#3)**
- **Found during:** Task 3 (full-suite run after inlining the two-lambda `AddBaseApiMessaging` call in `Program.cs`)
- **Issue:** `ProgramMinimalityFacts.ProgramCs_BodyLines_LessThan_OrEqualTo_Ten` failed — the inline call added ~16 non-trivial body lines, exceeding the locked SC#3 invariant. (The `ReceiveTransport faulted: guest@rabbitmq:5672` retry noise in the full-run log was a pre-existing red herring from real-broker integration tests booting the WebApi against the compose-internal `rabbitmq` hostname, not a new failure.)
- **Fix:** Extracted the consumer + endpoint lambdas into a thin `BaseApi.Service`-owned `Composition/ResponderMessaging.AddBaseApiResponderMessaging` extension (mirrors `AppFeatures`), called as a single line from `Program.cs`. The plan explicitly permitted this. Exact bindings preserved.
- **Files modified:** `src/BaseApi.Service/Program.cs`, `src/BaseApi.Service/Composition/ResponderMessaging.cs` (new).
- **Commit:** `37f549f`.

## Issues Encountered
- The MTP test runner ignored `--filter` (VSTest property; MTP0001 warning), so the per-task verification commands ran the full suite; targeted runs used the native `-- --filter-class` flag instead. The single full-suite failure was diagnosed by decoding the UTF-16 MTP log and extracting the `failed BaseApi.Tests...` token (see Deviation 2).

## Threat Surface
No new external HTTP surface — the responders are bus-only (T-25-02 register: T-25-02-01/02/03/05 accepted as bus-internal single-key reads; T-25-02-04 mitigation actively tested). The firewall (`BaseApiCoreFirewallTests`) and the Degraded-cap broker-down guards (`Health_Ready/Live_Returns_200_When_Broker_Dead`) are GREEN. No threat flags raised.

## Known Stubs
None. Both consumers are fully wired to the real `ProcessorService`/`SchemaService` read paths; the dual-response branches carry real projected data (found) or the echoed request key (not-found).

## User Setup Required
None — no external service configuration. The responder endpoints are declared on the existing RabbitMQ broker at WebApi boot (production), and exercised in-memory in tests.

## Next Phase Readiness
- Phase 26 (`BaseProcessor.Core`) now has a live WebApi server side to query: it holds the `IRequestClient`s for `GetProcessorBySourceHash` (identity-by-SourceHash) and `GetSchemaDefinition(schemaId)`, targeting `exchange:processor-identity-query` / `exchange:schema-definition-query`.

## Self-Check: PASSED

- All 6 created files present on disk; both modified files updated.
- All 3 task commits present in git history (`2bf83ff`, `a32c762`, `37f549f`).
- Zero deletions introduced by the task commits (`git diff --diff-filter=D 2bf83ff~1 HEAD` empty).
- Firewall intact: `BaseApi.Core.csproj` has 0 forbidden ProjectReferences; full suite 345/345 GREEN.

---
*Phase: 25-shared-contracts-webapi-responders*
*Completed: 2026-06-01*
