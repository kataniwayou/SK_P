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
    provides: "OutboundCorrelationPublishFilter, AsyncLocalCorrelationAccessor, StartOrchestration contract, AddBaseApiMessaging soft-on-broker bus health (MinimalFailureStatus=Degraded, already implemented in 19-03)"
provides:
  - "TEST-RMQ-01 fan-out broadcast proof: one publish -> two distinct-InstanceId endpoints both consume, per-consumer-sum count == 2 (not load-balance)"
  - "CORR-03 synthetic outbound-filter proof: ambient ICorrelationAccessor id stamped onto published ENVELOPE (not body)"
  - "TEST-RMQ-03 HealthDeadRabbitFixture + 2 facts: /health/live + /health/ready both 200 with broker dead (Degraded-not-Unhealthy)"
  - "TEST-RMQ-04 discipline: in-memory temporary/auto-delete endpoints, per-class-prefixed InstanceIds, zero global purge"
  - "A2 resolved: single-harness/two-distinct-consumer-types is the unambiguous fan-out idiom in MassTransit 8.5.5 (two-providers fallback NOT needed)"
  - "8.5.5 count API resolved: per-consumer-harness Select<T>(ct).Count() summed (ITestHarness.Consumed has NO Count<T>(); reading the bus-level Consumed list raw count is FLAKY — races the 2nd endpoint delivery)"
affects: [20-03, 20-04]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Two-endpoint in-memory fan-out: AddConsumer<A>().Endpoint(e => InstanceId/Temporary) x2 with two DISTINCT consumer types so each gets its own GetConsumerHarness<T>() slot"
    - "Broadcast count proof in MassTransit.Testing 8.5.5: SUM the two PER-CONSUMER-harness Select<T>(ct).Count() (each awaited to settlement via .Any()); the bus-level harness.Consumed raw count races the second delivery"
    - "Generic publish-filter wiring under the test harness: cfg.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), c) inside UsingInMemory((c, cfg) => ...)"
    - "Broker-down WebApplicationFactory fixture mirroring HealthDeadRedisFixture: env-var-in-ctor RabbitMq__Host dead host, live Postgres+Redis, capture+restore in DisposeAsync"

key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/FanOutBroadcastTests.cs
    - tests/BaseApi.Tests/Orchestrator/OutboundFilterSyntheticTests.cs
  modified:
    - tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs

key-decisions:
  - "A2 (two-bus fan-out idiom): single in-memory harness with TWO endpoints hosting TWO DISTINCT consumer types (FanOutConsumerA/B). MassTransit keys consumer harnesses by consumer TYPE, so two endpoints of the same type collapse into one GetConsumerHarness<T>() lookup; two distinct types give two independently-assertable slots. The two-separate-IServiceProvider fallback was NOT needed."
  - "Consumed-count API: ITestHarness.Consumed exposes no Count<T>() in 8.5.5 (CS0311 — StartOrchestration is not IAsyncListElement). Counting the BUS-level Consumed list raw is FLAKY (races the 2nd endpoint's delivery -> intermittent Actual:1). Correct proof: SUM the two PER-CONSUMER harness Select<T>(ct).Count() (each already awaited via .Any())."
  - "HealthDeadRabbitFixture uses the parameterless Phase8WebAppFactory base ctor (both Postgres + Redis live via the Compose-mapped fixtures) + env-var-in-ctor RabbitMq__Host override — cleaner than the 4-arg ctor (which requires non-null override strings)."
  - "Broker-down body assertion: status code 200 + secret-free (no Password=) only; did NOT assert connection strings (T-20-04 / inherited T-19 secret-free contract)."
  - "No production change needed for TEST-RMQ-03: the D-05 MinimalFailureStatus=Degraded cap was already implemented in AddBaseApiMessaging by Plan 19-03 (verified on disk) — the two broker-down facts passed against the existing soft-on-broker posture."

requirements-completed: [TEST-RMQ-01, TEST-RMQ-03, TEST-RMQ-04, CORR-03]

# Metrics
duration: 30min
completed: 2026-05-30
---

# Phase 20 Plan 02: Hermetic In-Memory / Dead-Host Proofs Summary

**Proved the three hermetic Phase-20 behaviors with no live broker and no ES — the fan-out broadcast topology (TEST-RMQ-01, the #1 trap), the synthetic outbound-filter envelope stamp (CORR-03), and the WebApi soft-on-broker health posture (TEST-RMQ-03) — plus the TEST-RMQ-04 temporary-endpoint / no-purge discipline, all under a maintained zero-warning build (Debug + Release).**

## Performance

- **Duration:** ~30 min (incl. one verification-surfaced auto-fix)
- **Started:** 2026-05-30T15:52:02Z
- **Tasks:** 3 (all `type=auto`; Tasks 1-2 `tdd=true`) + 1 follow-up fix commit
- **Files:** 2 created, 1 modified

## Accomplishments

- **TEST-RMQ-01 (Task 1):** `FanOutBroadcastTests` publishes ONE `StartOrchestration` to a single in-memory harness with two distinct-InstanceId endpoints (`t01-fanout-a` / `t01-fanout-b`) and asserts BOTH `GetConsumerHarness<FanOutConsumerA>()` AND `GetConsumerHarness<FanOutConsumerB>()` consumed it, AND the per-consumer-sum total `== 2`. The count==2 assertion is the discriminating anti-load-balance proof (Pitfall 1): a bare `.Any()` is true for load-balance too; a load-balance regression gives 1 + 0 == 1.
- **CORR-03 (Task 2):** `OutboundFilterSyntheticTests` seeds the ambient `AsyncLocalCorrelationAccessor`, wires `OutboundCorrelationPublishFilter<>` via `cfg.UsePublishFilter(typeof(...), c)` into the in-memory publish pipeline, publishes an `ICorrelated` message (body CorrelationId default), and asserts the stamped id lands on the ENVELOPE (`pub.Context.CorrelationId`), NOT the body. No real downstream consumer.
- **TEST-RMQ-03 (Task 3):** `HealthDeadRabbitFixture` (private sealed `Phase8WebAppFactory`) keeps Postgres + Redis live, points only `RabbitMq__Host` at `localhost-rabbit-dead.invalid` via env-var-in-ctor (capture+restore in `DisposeAsync`, try/catch restores on throw). Two facts prove `/health/live` AND `/health/ready` both return 200 with the broker dead — the bus check is capped at Degraded by the `MinimalFailureStatus` already wired into `AddBaseApiMessaging` (Plan 19-03), so it never flips ready to 503.
- **TEST-RMQ-04:** All test receive endpoints are temporary/auto-delete (`e.Temporary = true`) with per-test-class-prefixed InstanceIds (`t01-fanout-*`); zero `purge` calls in the new test code (grep -i purge == 0).

## Task Commits

1. **Task 1: FanOutBroadcastTests (TEST-RMQ-01)** — `7cda768` (test)
2. **Task 2: OutboundFilterSyntheticTests (CORR-03)** — `e575251` (test)
3. **Task 3: HealthDeadRabbitFixture + 2 broker-down facts (TEST-RMQ-03)** — `28735a0` (test)
4. **Fix: fan-out count proof uses per-consumer sum (bus-level count was flaky)** — `5e7e9bc` (fix) — see Deviations

## Verification Results

- `dotnet build SK_P.sln -c Release` → exit 0, 0 warnings / 0 errors.
- `dotnet build SK_P.sln -c Debug` → exit 0, 0 warnings / 0 errors.
- `*FanOutBroadcastTests` → GREEN, **5× consecutive** after the per-consumer-sum fix `5e7e9bc` (the prior bus-level count was flaky — Expected:2 Actual:1 intermittently; see Deviations).
- `*OutboundFilterSyntheticTests` → 1/1 passed.
- `*HealthEndpointsTests` → 11/11 passed (9 pre-existing + 2 new broker-down facts) with NO production change — the D-05 `MinimalFailureStatus=Degraded` cap was already present in `AddBaseApiMessaging` (Plan 19-03). No regression in the 9 pre-existing facts.
- Acceptance greps: `class FanOutBroadcastTests`=1, `e.Temporary = true`/InstanceId/`t01-fanout`/both `GetConsumerHarness<...>` present, purge=0; `OutboundCorrelationPublishFilter`+`UsePublishFilter`+`ambient.Set`+`pub.Context.CorrelationId` present; `class HealthDeadRabbitFixture`=1, `RabbitMq__Host`=2 (set+restore), two new facts present, no `Password=` leak assertion drift.
- **Docker:** data-tier only (`docker compose up -d postgres redis`) for the HealthEndpointsTests integration host. Broker deliberately DOWN (TEST-RMQ-03 proves soft-on-broker); Tasks 1-2 are pure in-memory.

## Decisions Made

- **A2 two-bus idiom (resolved here, deferred from 20-01):** single in-memory harness + two DISTINCT thin consumer types is the unambiguous fan-out shape — each type gets its own `GetConsumerHarness<T>()` slot. Two endpoints of the SAME type would collapse into one harness lookup. The two-separate-`IServiceProvider` fallback was not required.
- **Exact broadcast-count API (recorded per plan request):** sum the two PER-CONSUMER harness `Select<StartOrchestration>(ct).Count()` (each already awaited to settlement via `.Any()`). `ITestHarness.Consumed.Count<T>(ct)` does NOT compile in 8.5.5 (CS0311 — message types are not `IAsyncListElement`), AND reading the bus-level `Consumed.Select<T>(ct).Count()` raw is flaky (it races the second endpoint's delivery). See Deviations.
- **TEST-RMQ-03 needed no production change:** `AddBaseApiMessaging` already caps the auto-registered bus check at Degraded via `ConfigureHealthCheckOptions(o => o.MinimalFailureStatus = HealthStatus.Degraded)` (implemented by Plan 19-03; verified on disk). The two new facts passed against the existing posture.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Consumed.Count<T>(ct) is not a valid MassTransit 8.5.5 API**
- **Found during:** Task 1, surfaced by the test-project Release build (CS0311).
- **Issue:** The plan's primary assertion `harness.Consumed.Count<StartOrchestration>(ct)` fails to compile — `AsyncElementListExtensions.Count<TElement>` constrains `TElement : IAsyncListElement`, which `StartOrchestration` (a message type) does not implement. The plan anticipated this: "If `Consumed.Count<T>(ct)` is not the exact 8.5.5 API, use the equivalent inspection... record the exact call in the SUMMARY."
- **Fix (initial):** Used `Select<StartOrchestration>(ct).Count()` (LINQ over the materialized received-message list) + `using System.Linq;`. This compiled and was committed with Task 1 — but the COUNT it read (the bus-level list) turned out flaky; see Deviation 2.
- **Files modified:** `tests/BaseApi.Tests/Orchestrator/FanOutBroadcastTests.cs`
- **Committed in:** `7cda768` (Task-1 commit — compile-fixed before commit).

**2. [Rule 1 - Bug] Bus-level Consumed raw count is FLAKY for the broadcast proof — must sum the two per-consumer harnesses**
- **Found during:** Task 1 verification — surfaced by the filtered `dotnet test` run (NOT the build), and confirmed flaky by a 5× repeat (1 failure observed: `Expected:2 Actual:1`).
- **Issue:** `Assert.Equal(2, harness.Consumed.Select<StartOrchestration>(ct).Count())` reads the BUS-level `ITestHarness.Consumed` list, which is settled-after-await only via the two per-consumer `.Any()` calls above it; its raw count races the second endpoint's delivery and intermittently returns 1. (The two per-consumer `GetConsumerHarness<A/B>().Consumed.Any()` assertions always PASSED — both endpoints genuinely received the broadcast.)
- **Fix:** Count over each PER-CONSUMER harness (each already awaited to settlement via `.Any()`) and sum: `consumerA.Consumed.Select<T>(ct).Count()` (==1) + `consumerB...` (==1) == 2. A load-balance regression would give 1 + 0 == 1, preserving the Pitfall-1 broadcast/load-balance discrimination. Fault check also moved to the two awaited per-consumer lists.
- **Files modified:** `tests/BaseApi.Tests/Orchestrator/FanOutBroadcastTests.cs`
- **Verification:** `*FanOutBroadcastTests` GREEN 5× consecutive after the fix.
- **Committed in:** `5e7e9bc`.

**Not a deviation — TEST-RMQ-03 production posture already in place:** the plan and ROADMAP both call for the WebApi bus check to stay Degraded (not 503) when the broker is down. On first run I suspected this was unimplemented, but inspection confirmed `AddBaseApiMessaging` already wires `ConfigureHealthCheckOptions(o => o.MinimalFailureStatus = HealthStatus.Degraded)` (Plan 19-03 / MSG-WEBAPI-04). No production change was made or needed; the two broker-down facts passed against the existing code.

**Minor implementation choice (not a deviation):** `HealthDeadRabbitFixture` uses the parameterless `Phase8WebAppFactory()` base ctor (both Postgres + Redis live) rather than the 4-arg `skipRedisFixture:false` ctor the plan sketched. The 4-arg ctor requires non-null override strings; the parameterless ctor is the correct both-live path and matches the env-var-only broker override intent. Same observable behavior, mirrors the sibling fixtures' env-var-in-ctor discipline.

## Docker Services Brought Up

- `docker compose up -d postgres redis` — data-tier ONLY, required by `HealthEndpointsTests` (the `Phase8WebAppFactory` fixtures connect to the Compose-mapped dev Postgres on `localhost:5433` and Redis on `localhost:6380`). **The broker was deliberately NOT started** — TEST-RMQ-03 proves the soft-on-broker posture with the broker DOWN, and Tasks 1-2 are pure in-memory (no broker, no ES). Postgres reached `healthy`; Redis running.

## Issues Encountered

- Two auto-fixed issues (see Deviations): the CS0311 count-API compile error and the flaky bus-level broadcast count. The flakiness only surfaced on a repeated run — a single run can pass by luck — so the 5× repeat was the load-bearing verification.
- The fan-out single-harness/two-distinct-types shape proved correct (both endpoints received the broadcast) from the first run; only the count *assertion source* needed correcting — no two-providers fallback was needed.

## Self-Check: PASSED

- Created files verified on disk: `FanOutBroadcastTests.cs`, `OutboundFilterSyntheticTests.cs`, `20-02-SUMMARY.md`.
- Modified file verified: `HealthEndpointsTests.cs` (HealthDeadRabbitFixture + 2 facts).
- Commits verified in git log: `7cda768`, `e575251`, `28735a0`, `5e7e9bc`.

---
*Phase: 20-correlation-propagation-proof-synthetic-harness-triple-sha-c*
*Completed: 2026-05-30*
