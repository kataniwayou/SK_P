# Phase 19 — Deferred Items

## Plan 19-03 (WebApi publish join)

### Orchestration HTTP integration facts now require a live RabbitMQ broker (~20 tests)

**Discovered during:** Plan 19-03 Task 3 full-suite run (the MTP `--filter` is ignored — MTP0001
warning — so `dotnet test --filter` runs the WHOLE suite; this surfaced the downstream impact).

**What happens:** After Task 2, `OrchestrationService.StartAsync`/`StopAsync` publish
`StartOrchestration`/`StopOrchestration` to RabbitMQ. With **no RabbitMQ broker** in the
environment (RabbitMQ is added to `compose.yaml` in **Plan 19-04**, not this plan), the publish
throws `RabbitMqConnectionException: Broker unreachable: guest@rabbitmq:5672/`. The
`FallbackExceptionHandler` correctly maps this to **HTTP 500** — exactly the MSG-WEBAPI-03 contract
(broker is a hard dependency for the Start/Stop path).

The pre-existing orchestration HTTP integration facts (e.g.
`StartOrchestrationFacts.Start_Returns204_*`, `StopOrchestrationFacts`, `HappyPathE2EFacts`,
`IdempotencyFacts`, `StartLoopFacts`, `StartCleanupFacts`, `StopGateFacts`, `StopScanFacts`,
and the other Start/Stop happy-path facts) assert a **204** after a successful Start/Stop. They now
return **500** because the broker is unreachable. This is the INTENDED behavior change of this
plan, not a regression bug.

**Why deferred (out of scope for 19-03):**
- The plan explicitly states: "This plan does NOT touch compose.yaml or any Dockerfile (Plan 04)."
  RabbitMQ is brought into the compose stack in **Plan 19-04** (INFRA-RMQ-02/03).
- The plan's verification is scoped to: (a) `dotnet build src/BaseApi.Service ... -c Release` exits 0,
  and (b) the new `OrchestrationServicePublish` subset exits 0 — both GREEN.
- The plan defers the end-to-end real-broker HTTP assertions to **Phase 20 (TEST-RMQ-01..05)**.

**What Plan 19-04 / Phase 20 must do:**
1. Add the RabbitMQ tier to `compose.yaml` (Plan 19-04).
2. Bring the broker up for the integration suite (a `RabbitMqFixture` or the compose stack), OR
   update the orchestration HTTP happy-path facts to provision a broker before Start/Stop.
3. Phase 20 adds the real-broker-down HTTP contract test (TEST-RMQ-03): Start/Stop → 5xx while CRUD
   stays 2xx.

**Health is NOT affected:** `HealthEndpointsTests` passes 9/9 with the broker down — the bus health
check is capped at `Degraded` (MSG-WEBAPI-04), so `/health/ready` stays 200. Verified in 19-03.
