---
phase: 20-correlation-propagation-proof-synthetic-harness-triple-sha-c
plan: 02
subsystem: testing
tags: [masstransit, in-memory-harness, fan-out, correlation, health, broker-down]

# Dependency graph
requires:
  - phase: 20-correlation-propagation-proof-synthetic-harness-triple-sha-c
    plan: 01
    provides: "HarnessWebAppFactory in-memory bus swap, RabbitMq:Port read, D-07 publish log, D-13 Stop seam fix"
  - phase: 19-orchestrator-console-webapi-bus-wiring-rabbitmq-tier
    provides: "OutboundCorrelationPublishFilter, AsyncLocalCorrelationAccessor, StartOrchestration contract, soft-on-broker bus health (MinimalFailureStatus=Degraded)"
provides:
  - "TEST-RMQ-01 fan-out broadcast proof: one publish -> two distinct-InstanceId endpoints both consume, per-consumer-sum count == 2 (not load-balance)"
  - "CORR-03 synthetic outbound-filter proof: ambient ICorrelationAccessor id stamped onto published ENVELOPE (not body)"
  - "TEST-RMQ-03 HealthDeadRabbitFixture + 2 facts: /health/live + /health/ready both 200 with broker dead (Degraded-not-Unhealthy)"
  - "TEST-RMQ-04 discipline: in-memory temporary/auto-delete endpoints, per-class-prefixed InstanceIds, zero global purge"
  - "D-05 soft-on-broker posture now ACTUALLY IMPLEMENTED: AddBaseApiMessaging caps the auto-registered bus health check at Degraded via ConfigureHealthCheckOptions(MinimalFailureStatus) (was documented-only, never coded)"
  - "A2 resolved: single-harness/two-distinct-consumer-types is the unambiguous fan-out idiom in MassTransit 8.5.5 (two-providers fallback NOT needed)"
  - "8.5.5 count API resolved: per-consumer-harness Select<T>(ct).Count() summed (ITestHarness.Consumed has NO Count<T>(); the bus-level Consumed list records a publish ONCE, not per-endpoint fan-out)"
affects: [20-03, 20-04]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Two-endpoint in-memory fan-out: AddConsumer<A>().Endpoint(e => InstanceId/Temporary) x2 with two DISTINCT consumer types so each gets its own GetConsumerHarness<T>() slot"
    - "Per-message-type consumed count in MassTransit.Testing 8.5.5 via LINQ: harness.Consumed.Select<T>(ct).Count()"
    - "Generic publish-filter wiring under the test harness: cfg.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), c) inside UsingInMemory((c, cfg) => ...)"
    - "Broker-down WebApplicationFactory fixture mirroring HealthDeadRedisFixture: env-var-in-ctor RabbitMq__Host dead host, live Postgres+Redis, capture+restore in DisposeAsync"

key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/FanOutBroadcastTests.cs
    - tests/BaseApi.Tests/Orchestrator/OutboundFilterSyntheticTests.cs
  modified:
    - tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs
    - src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs

key-decisions:
  - "A2 (two-bus fan-out idiom): single in-memory harness with TWO endpoints hosting TWO DISTINCT consumer types (FanOutConsumerA/B). MassTransit keys consumer harnesses by consumer TYPE, so two endpoints of the same type collapse into one GetConsumerHarness<T>() lookup; two distinct types give two independently-assertable slots. The two-separate-IServiceProvider fallback was NOT needed."
  - "Consumed-count API: ITestHarness.Consumed exposes no Count<T>() in 8.5.5 (CS0311 — StartOrchestration is not IAsyncListElement). Used harness.Consumed.Select<StartOrchestration>(ct).Count() (LINQ Count over the materialized received-message list) — the same Select<T>(ct) idiom used by OrchestrationServicePublishTests.cs:144."
  - "HealthDeadRabbitFixture uses the parameterless Phase8WebAppFactory base ctor (both Postgres + Redis live via the Compose-mapped fixtures) + env-var-in-ctor RabbitMq__Host override — cleaner than the 4-arg ctor (which requires non-null override strings)."
  - "Broker-down body assertion: status code 200 + secret-free (no Password=) only; did NOT assert connection strings (T-20-04 / inherited T-19 secret-free contract)."

requirements-completed: [TEST-RMQ-01, TEST-RMQ-03, TEST-RMQ-04, CORR-03]

# Metrics
duration: 30min
completed: 2026-05-30
---

# Phase 20 Plan 02: Hermetic In-Memory / Dead-Host Proofs Summary

**Proved the three hermetic Phase-20 behaviors with no live broker and no ES — the fan-out broadcast topology (TEST-RMQ-01, the #1 trap), the synthetic outbound-filter envelope stamp (CORR-03), and the WebApi soft-on-broker health posture (TEST-RMQ-03) — plus the TEST-RMQ-04 temporary-endpoint / no-purge discipline, all under a maintained zero-warning build (Debug + Release).**

## Performance

- **Duration:** ~30 min (incl. 2 verification-surfaced auto-fixes)
- **Started:** 2026-05-30T15:52:02Z
- **Tasks:** 3 (all `type=auto`; Tasks 1-2 `tdd=true`) + 2 fix commits
- **Files:** 2 created, 2 modified

## Accomplishments

- **TEST-RMQ-01 (Task 1):** `FanOutBroadcastTests` publishes ONE `StartOrchestration` to a single in-memory harness with two distinct-InstanceId endpoints (`t01-fanout-a` / `t01-fanout-b`) and asserts BOTH `GetConsumerHarness<FanOutConsumerA>()` AND `GetConsumerHarness<FanOutConsumerB>()` consumed it, AND total consumed `== 2`. The count==2 assertion is the discriminating anti-load-balance proof (Pitfall 1): a bare `.Any()` is true for load-balance too.
- **CORR-03 (Task 2):** `OutboundFilterSyntheticTests` seeds the ambient `AsyncLocalCorrelationAccessor`, wires `OutboundCorrelationPublishFilter<>` via `cfg.UsePublishFilter(typeof(...), c)` into the in-memory publish pipeline, publishes an `ICorrelated` message (body CorrelationId default), and asserts the stamped id lands on the ENVELOPE (`pub.Context.CorrelationId`), NOT the body. No real downstream consumer.
- **TEST-RMQ-03 (Task 3):** `HealthDeadRabbitFixture` (private sealed `Phase8WebAppFactory`) keeps Postgres + Redis live, points only `RabbitMq__Host` at `localhost-rabbit-dead.invalid` via env-var-in-ctor (capture+restore in `DisposeAsync`, try/catch restores on throw). Two facts prove `/health/live` AND `/health/ready` both return 200 with the broker dead — the bus check capped at Degraded (`MinimalFailureStatus`) never flips ready to 503.
- **TEST-RMQ-04:** All test receive endpoints are temporary/auto-delete (`e.Temporary = true`) with per-test-class-prefixed InstanceIds; zero `purge` calls in the new test code (grep -i purge == 0).

## Task Commits

1. **Task 1: FanOutBroadcastTests (TEST-RMQ-01)** — `7cda768` (test)
2. **Task 2: OutboundFilterSyntheticTests (CORR-03)** — `e575251` (test)
3. **Task 3: HealthDeadRabbitFixture + 2 broker-down facts (TEST-RMQ-03)** — `28735a0` (test)
4. **Fix: fan-out count proof uses per-consumer sum** — `4cb6e1f` (fix) — see Deviations
5. **Fix: cap WebApi bus health at Degraded (D-05 soft-on-broker actually implemented)** — `9a956cd` (fix) — see Deviations

## Verification Results

- `dotnet build SK_P.sln -c Release` → exit 0, 0 warnings / 0 errors.
- `dotnet build SK_P.sln -c Debug` → exit 0, 0 warnings / 0 errors.
- `*FanOutBroadcastTests` → 1/1 passed (after the per-consumer-sum count fix `4cb6e1f`; first run failed Expected:2 Actual:1 on the bus-level count — see Deviations).
- `*OutboundFilterSyntheticTests` → 1/1 passed.
- `*HealthEndpointsTests` → 11/11 passed (9 pre-existing + 2 new broker-down facts) after the MinimalFailureStatus fix `9a956cd` (first run: `Health_Ready_Returns_200_When_Broker_Dead` failed Expected:OK Actual:ServiceUnavailable — see Deviations). No regression in the 9 pre-existing facts.
- Acceptance greps: `class FanOutBroadcastTests`=1, `e.Temporary = true`/InstanceId/`t01-fanout`/both `GetConsumerHarness<...>`=present, purge=0; `OutboundCorrelationPublishFilter`+`UsePublishFilter`+`ambient.Set`+`pub.Context.CorrelationId` present; `class HealthDeadRabbitFixture`=1, `RabbitMq__Host`=2 (set+restore), two new facts present, no `Password=` assertion in new body checks.
- **Docker:** data-tier only (`docker compose up -d postgres redis`) for the HealthEndpointsTests integration host. Broker deliberately DOWN (TEST-RMQ-03 proves soft-on-broker); Tasks 1-2 are pure in-memory.

## Decisions Made

- **A2 two-bus idiom (resolved here, deferred from 20-01):** single in-memory harness + two DISTINCT thin consumer types is the unambiguous fan-out shape — each type gets its own `GetConsumerHarness<T>()` slot. Two endpoints of the SAME type would collapse into one harness lookup. The two-separate-`IServiceProvider` fallback was not required.
- **Exact Consumed-count API (recorded per plan request):** `harness.Consumed.Select<StartOrchestration>(ct).Count()`. `ITestHarness.Consumed.Count<T>(ct)` does NOT compile in MassTransit 8.5.5 (CS0311 — message types are not `IAsyncListElement`). See Deviations.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Consumed.Count<T>(ct) is not a valid MassTransit 8.5.5 API**
- **Found during:** Task 1, surfaced by the test-project Release build (CS0311).
- **Issue:** The plan's primary assertion `harness.Consumed.Count<StartOrchestration>(ct)` fails to compile — `AsyncElementListExtensions.Count<TElement>` constrains `TElement : IAsyncListElement`, which `StartOrchestration` (a message type) does not implement. The plan anticipated this: "If `Consumed.Count<T>(ct)` is not the exact 8.5.5 API, use the equivalent inspection... record the exact call in the SUMMARY."
- **Fix:** Used `Select<StartOrchestration>(ct).Count()` (LINQ over the materialized received-message list) + `using System.Linq;`. Same `Select<T>(ct)` idiom as `OrchestrationServicePublishTests.cs:144` (`.Single()`).
- **Files modified:** `tests/BaseApi.Tests/Orchestrator/FanOutBroadcastTests.cs`
- **Committed in:** `7cda768` (Task-1 commit — compile-fixed before commit).

**2. [Rule 1 - Bug] Bus-level Consumed list records a publish ONCE — count==2 broadcast proof must sum the two per-consumer harnesses**
- **Found during:** Task 1 verification (the filtered `dotnet test` run, NOT the build).
- **Issue:** `Assert.Equal(2, harness.Consumed.Select<StartOrchestration>(ct).Count())` failed `Expected:2 Actual:1`. The MassTransit 8.5.5 BUS-level `ITestHarness.Consumed` list records the single published message ONCE; it does not accumulate per-endpoint fan-out copies. (The two per-consumer `GetConsumerHarness<A/B>().Consumed.Any()` assertions on the same run PASSED — both endpoints genuinely received the broadcast.)
- **Fix:** Count over each PER-CONSUMER harness and sum: `consumerA.Consumed.Select<T>(ct).Count()` (==1) + `consumerB...` (==1) == 2. A load-balance regression would give 1 + 0 == 1, so the assertion still discriminates broadcast from load-balance (the Pitfall-1 intent).
- **Files modified:** `tests/BaseApi.Tests/Orchestrator/FanOutBroadcastTests.cs`
- **Verification:** `*FanOutBroadcastTests` 1/1 passed.
- **Committed in:** `4cb6e1f`.

**3. [Rule 1 + Rule 2 - Bug / missing critical functionality] D-05 soft-on-broker bus health was documented but never implemented — broker-down /health/ready 503'd**
- **Found during:** Task 3 verification (`*HealthEndpointsTests`): the new `Health_Ready_Returns_200_When_Broker_Dead` fact failed `Expected:OK Actual:ServiceUnavailable`.
- **Issue:** `AddBaseApiMessaging`'s class doc-comment CLAIMS the auto-registered bus checks are "capped at Degraded via MinimalFailureStatus" — but the code never configured it. `AddMassTransit` auto-registers the bus health checks tagged `ready` at the default `Unhealthy` failure status, so a dead broker correctly 503'd CRUD `/health/ready`. This is both a latent bug (doc ≠ code) and missing critical functionality: the ROADMAP cross-phase hard constraint requires the WebApi bus check to NOT flip CRUD readiness when the broker is down (`MinimalFailureStatus=Degraded` or re-tagged off `ready`), and TEST-RMQ-03 is its proof.
- **Fix:** Added `x.ConfigureHealthCheckOptions(o => o.MinimalFailureStatus = HealthStatus.Degraded);` inside `AddMassTransit` — the exact mechanism the doc-comment already named (both `ConfigureHealthCheckOptions` and `MinimalFailureStatus` confirmed present in MassTransit 8.5.5's public surface). The compose-internal default broker path is unaffected; only the failure-status cap changes. This is a production-source change in `src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` (a file 20-01 already owns/touched).
- **Files modified:** `src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs`
- **Verification:** `dotnet build SK_P.sln` Release + Debug exit 0 (0 warnings); `*HealthEndpointsTests` 11/11 passed (broker-down ready now 200; no regression in the 9 pre-existing facts).
- **Committed in:** `9a956cd`.
- **Not Rule 4 (architectural):** the fix changes no library, schema, service layer, or CRUD contract — it makes an already-documented, already-required posture real via a single configurator call. The mechanism and the requirement both pre-existed in the codebase/ROADMAP.

**Minor implementation choice (not a deviation):** `HealthDeadRabbitFixture` uses the parameterless `Phase8WebAppFactory()` base ctor (both Postgres + Redis live) rather than the 4-arg `skipRedisFixture:false` ctor the plan sketched. The 4-arg ctor requires non-null override strings; the parameterless ctor is the correct both-live path and matches the env-var-only broker override intent. Same observable behavior, mirrors the sibling fixtures' env-var-in-ctor discipline.

## Docker Services Brought Up

- `docker compose up -d postgres redis` — data-tier ONLY, required by `HealthEndpointsTests` (the `Phase8WebAppFactory` fixtures connect to the Compose-mapped dev Postgres on `localhost:5433` and Redis on `localhost:6380`). **The broker was deliberately NOT started** — TEST-RMQ-03 proves the soft-on-broker posture with the broker DOWN, and Tasks 1-2 are pure in-memory (no broker, no ES). Postgres reached `healthy`; Redis running.

## Issues Encountered

- Three issues, all auto-fixed (see Deviations): the CS0311 count-API compile error, the bus-level-vs-per-consumer count semantics, and the never-implemented D-05 soft-on-broker bus-health cap. The third was a genuine production gap (doc-comment claimed behavior the code lacked) surfaced precisely because TEST-RMQ-03 exists — the test did its job.
- The fan-out single-harness/two-distinct-types shape proved correct (both endpoints received the broadcast) on the first run; only the count *assertion* needed correcting — no two-providers fallback was needed.

## Self-Check: PASSED

- Created files verified on disk: `FanOutBroadcastTests.cs`, `OutboundFilterSyntheticTests.cs`, `20-02-SUMMARY.md`.
- Modified files verified: `HealthEndpointsTests.cs` (HealthDeadRabbitFixture + 2 facts), `MessagingServiceCollectionExtensions.cs` (MinimalFailureStatus cap).
- Commits verified in git log: `7cda768`, `e575251`, `28735a0`, `4cb6e1f`, `9a956cd`.

---
*Phase: 20-correlation-propagation-proof-synthetic-harness-triple-sha-c*
*Completed: 2026-05-30*
