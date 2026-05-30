# Phase 20: Correlation Propagation Proof + Synthetic Harness + Triple-SHA Closeout - Context

**Gathered:** 2026-05-30
**Status:** Ready for planning

<domain>
## Phase Boundary

The **proof + closeout** phase for milestone v3.4.0 — it adds NO new runtime capability. It proves what Phases 17–19 built works end-to-end and runs the milestone close gate.

In scope (7 requirements):
- **CORR-03** — outbound correlation filter exercised by a synthetic test-harness downstream send (no real downstream consumer).
- **CORR-04 / TEST-RMQ-02** — per-stage correlation proven end-to-end: a real HTTP Start (with an `X-Correlation-Id` scoping the HTTP stage) → the body `ICorrelated.CorrelationId` minted fresh at publish → the Orchestrator's correlated log line surfaces in Elasticsearch under the `"CorrelationId"` attribute carrying that body Guid (clean per-stage handoff, NOT one value across all hops).
- **TEST-RMQ-01** — fan-out broadcast test: two in-process bus instances (two `InstanceId`s) both receive a single published Start (broadcast, not load-balanced).
- **TEST-RMQ-03** — broker-down test: CRUD `/health/ready` + `/health/live` both stay 200 with RabbitMQ unreachable (`HealthDeadRabbitFixture` mirroring `HealthDeadRedisFixture`).
- **TEST-RMQ-04** — test receive endpoints are temporary/auto-delete, per-test-class-prefixed; NO global queue purge in teardown (the `FLUSHDB`-ban analog).
- **TEST-RMQ-05** — phase-close gate extended to a triple-SHA snapshot (`psql \l` + `redis-cli --scan` + `rabbitmqctl list_queues name`) asserting BEFORE=AFTER, alongside 3-consecutive-GREEN.

Also folded in (Phase-19 tech debt on the close-gate critical path / cheap correctness): the `baseapi-service` wget-healthcheck fix and the IN-02 Stop-consumer mislog fix (see D-12/D-13).

OUT of milestone (FUTURE / Processor v3.5.x+): Quartz scheduler, the stage-3 per-job-trigger `Guid CorrelationId`, derived `IExecutionCorrelated`, `Send` to `queue:processorId`, request/response, concrete `JobTrigger`/`ExecutionResult`. The dual-write outbox (code-review WR-02) and the published-set-vs-completed-set refinement (WR-01) are deferred design concerns, not closeout scope.

</domain>

<decisions>
## Implementation Decisions

### Test transport strategy & Phase-19 reconciliation (Area 1 — Fork A) ⭐ LOAD-BEARING
- **D-01:** The **in-memory MassTransit harness (`AddMassTransitTestHarness`) is the DEFAULT test transport.** Swap the `WebApplicationFactory` / `Phase8WebAppFactory` `UsingRabbitMq` registration for the in-memory harness so `/start|/stop` publish succeeds instantly → the **16 Phase-19 orchestration HTTP facts go green again (204)** and the published `StartOrchestration`/`StopOrchestration` contract is assertable in-process. Keeps the suite hermetic (no live-broker coupling) and kills the ~1m40s publish-timeout hangs. This is the same in-memory pattern Phase 19's `OrchestrationServicePublishTests` already use, applied to the HTTP integration path.
- **D-02:** **Only TEST-RMQ-02 (the ES correlation proof) uses the real Docker stack** — it is the sole genuinely cross-process assertion (WebApi → real RabbitMQ → Orchestrator container → ES) and cannot be faked in-memory.
- **D-03:** **TEST-RMQ-03 (broker-down) uses the real RabbitMQ transport pointed at a dead/unreachable endpoint** (a `HealthDeadRabbitFixture` mirroring the existing `HealthDeadRedisFixture` pattern), NOT in-memory — the subject under test is unreachable-broker behavior (CRUD `/health/ready` + `/health/live` stay 200, bus check Degraded per Phase 19 D-05).
- **D-04:** **TEST-RMQ-01 (fan-out) + CORR-03 (outbound-filter synthetic send) stay in-memory** two-bus harness — their requirements specify "in-process bus instances" / "synthetic … no real downstream consumer."

### ES correlation-proof mechanics (Area 2 — Fork A)
- **D-05:** Reuse the existing ES-query E2E pattern (`tests/BaseApi.Tests/Observability/OrchestrationLogsE2ETests.cs` + `Helpers/ElasticsearchTestClient.cs`) — poll ES for the orchestrator log doc by `CorrelationId`, tolerate indexing latency. Do not invent new ES plumbing.
- **D-06:** **Hybrid topology** — the real **Orchestrator container** consumes from real RabbitMQ and logs via otel-collector → ES; the HTTP Start is driven by an **in-process WebApi (`WebApplicationFactory`)** that — *as the single exception to D-01's in-memory default* — points at the real host endpoints (`localhost:5673` broker, host Redis, host ES). Both share the same broker/Redis instances (host-port vs compose-DNS views; MassTransit's message-type exchanges are publisher-location-agnostic). Lighter than also containerizing the WebApi.
- **D-07:** **Full-chain assertion.** Add a small structured **publish-side log on `OrchestrationService`** (e.g. `"Published StartOrchestration CorrelationId={guid}"`). TEST-RMQ-02 reads BOTH the WebApi-published doc and the Orchestrator seam doc from ES and asserts: `webapi.published.CorrelationId == orchestrator.seam.CorrelationId` **AND** both `!=` the HTTP `X-Correlation-Id` sent on the request — proving the exact body Guid propagated across the per-stage handoff (not the HTTP id).
- **D-08:** Seed the workflow's L2 root in real Redis before the proof request (via the normal start path or a direct projection write) so the Orchestrator reaches the scheduler-job-start seam (the success path), not the MSG-ACK-01 business-ack absent path.

### Triple-SHA close gate (Area 3 — Fork A)
- **D-09:** RabbitMQ snapshot = `docker exec sk-rabbitmq rabbitmqctl list_queues name`, sorted → SHA-256, asserted BEFORE=AFTER alongside `psql \l` + `redis-cli --scan`. Because test endpoints are temporary/auto-delete + per-class-prefixed (TEST-RMQ-04), a clean run leaks zero queues; the Orchestrator's own `orchestrator-{InstanceId}` queue is temporary/auto-delete and present in both BEFORE and AFTER when the stack is up (stable, not a leak). Assert net-zero change.
- **D-10:** Same 3-consecutive-GREEN cadence; the suite now includes the Phase 20 tests, and **TEST-RMQ-02 requires the full Docker stack (running Orchestrator container + ES) up during the gate** — which the existing stack-up gate already provides.
- **D-11:** Copy `scripts/phase-17-close.ps1` (+ a bash counterpart, latest is `scripts/phase-16-close.sh`) → `phase-20-close.{ps1,sh}`, add the rabbitmq SHA, **and fix the three deferred-at-v3.3.0 close-gate smells** (the `-1` fact-count fallback, the `compose ps --format json` assumption, the PS1/SH service-list divergence) — this is the milestone-closing gate, the right moment to make it clean.

### Phase-19 tech-debt fold-in (Area 4 — Fork A)
- **D-12:** **Fold in the `baseapi-service` wget healthcheck fix** (necessary close-gate prerequisite). Install `wget` in the root `Dockerfile` runtime stage **before `USER app`** (mirrors the Phase 19 `e4fcf67` orchestrator fix), so `sk-baseapi-service` reports healthy when the full stack comes up for the close gate. `aspnet:8.0-bookworm-slim` ships neither wget nor curl.
- **D-13:** **Fold in the IN-02 fix** (cheap correctness): `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` logs the *Start* seam string `"Scheduler job start"` for a Stop message (copy-paste); correct the Stop log message AND update the carried-over assertion in `tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs` (line ~217) that locks in the wrong string. Keeps the seam-log vocabulary clean so it doesn't muddy the TEST-RMQ-02 ES proof.

### Claude's Discretion
- Exact in-memory-harness wiring inside the factory (how `UsingRabbitMq` is replaced/removed via `RemoveMassTransit`/reconfigure), fixture class names, and whether the harness factory is a `Phase8WebAppFactory` variant or a sibling.
- `HealthDeadRabbitFixture` placement/shape (mirror the `HealthDeadRedisFixture`/`HealthDeadPostgresFixture` private-sealed-`Phase8WebAppFactory` pattern in `HealthEndpointsTests.cs`).
- Exact ES query shape / poll timeout for the two-doc correlation assertion (reuse `ElasticsearchTestClient`).
- Exact structured-log message text + log level for the WebApi publish-side log (D-07).
- Two-bus in-memory harness construction for TEST-RMQ-01 (two `InstanceId` endpoints, single publish, both consume) and the CORR-03 synthetic outbound send shape.
- Close-gate script internals beyond the triple-SHA + smell fixes; placement of `phase-20-close.*`.
- Test placement (`tests/BaseApi.Tests/Orchestrator/` per Phase 19 discretion vs a dedicated project) for the heavy two-bus + ES tests.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase scope & requirements (authoritative — read AMENDED text)
- `.planning/ROADMAP.md` §"Phase 20" — goal, depends-on (Phase 19), 5 success criteria (SC#1 AMENDED 2026-05-30 per Phase 19 D-01: ES proof asserts the body-minted Guid, not the HTTP X-Correlation-Id).
- `.planning/REQUIREMENTS.md` — CORR-03, CORR-04 (AMENDED), TEST-RMQ-01..05 (the 7 mapped requirements); Out-of-Scope table (no durable per-replica queues, no global purge, fan-out-not-LB).
- `.planning/ROADMAP.md` lines 17-23 — cross-phase hard constraints (per-stage correlation AMENDED; fan-out-not-LB proven at 2 replicas here; no global purge / triple-SHA = this phase).

### Prior-phase context (locked decisions this phase proves/builds on)
- `.planning/phases/19-orchestrator-console-webapi-bus-wiring-rabbitmq-tier/19-CONTEXT.md` — **D-01 correlation model** (body-carried, per-stage handoff, slim `ICorrelated`), D-05 (WebApi bus health Degraded-not-Unhealthy), D-06 (instance-unique temporary/auto-delete endpoint), D-09 (rabbitmq compose tier, host ports 5673/15673, guest/guest).
- `.planning/phases/19-orchestrator-console-webapi-bus-wiring-rabbitmq-tier/19-VERIFICATION.md` — the single human-UAT item (live cross-process correlation proof) that **TEST-RMQ-02 absorbs/automates**.
- `.planning/phases/19-orchestrator-console-webapi-bus-wiring-rabbitmq-tier/19-REVIEW.md` — IN-02 (Stop mislog, D-13), WR-01/WR-02 (deferred design concerns).
- `.planning/phases/18-baseconsole-core-library/18-CONTEXT.md` — `BusReadyHealthCheck` (Orchestrator hard-on-broker), the outbound filter exercised by CORR-03's harness.

### Test infrastructure to reuse/mirror
- `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — `HealthDeadRedisFixture` / `HealthDeadPostgresFixture` (private sealed `Phase8WebAppFactory` variants with dead-port config) — the pattern for `HealthDeadRabbitFixture` (TEST-RMQ-03 / D-03).
- `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` — the integration test host; `ConfigureAppConfiguration` + `AddInMemoryCollection` override point; this is where the in-memory harness swap (D-01) lands.
- `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` + `OrchestrationLogsE2ETests.cs` / `SchemasLogsE2ETests.cs` — the ES-query E2E pattern (D-05).
- `tests/BaseApi.Tests/Orchestration/OrchestrationServicePublishTests.cs` — existing in-memory publish-harness pattern (precedent for D-01).
- `tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs` — carries the IN-02 wrong-string assertion (~line 217) to fix (D-13).

### Source files touched
- `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` — add the publish-side correlation log (D-07).
- `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` — fix the Stop seam-log message (D-13, IN-02).
- `Dockerfile` (root) — install wget in the aspnet runtime stage before `USER app` (D-12).
- `compose.yaml` — baseapi-service healthcheck (line ~230) already uses `wget --spider`; D-12 makes it executable (no compose change needed beyond confirming).

### Close gate
- `scripts/phase-17-close.ps1` (latest) + `scripts/phase-16-close.sh` (latest bash) — the dual-SHA gate to extend to triple-SHA and de-smell (D-11); copy → `scripts/phase-20-close.{ps1,sh}`.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `Phase8WebAppFactory` + its `ConfigureAppConfiguration`/`AddInMemoryCollection` override is the seam for the in-memory-harness swap (D-01) and the `HealthDeadRabbitFixture` (D-03).
- `HealthDeadRedisFixture`/`HealthDeadPostgresFixture` (in `HealthEndpointsTests.cs`) are the exact mirror for the broker-down fixture.
- `ElasticsearchTestClient` + the `*LogsE2ETests` give the ES poll-and-assert pattern (D-05/D-07) — no new ES plumbing.
- `OrchestrationServicePublishTests` already proves the in-memory publish-harness pattern (D-01 precedent).
- `scripts/phase-1{6,7}-close.{ps1,sh}` are the dual-SHA gate to extend (D-11).

### Established Patterns
- Dual-SHA close gate (psql \l + redis-cli --scan, BEFORE=AFTER, 3× GREEN) — extend to triple by adding `rabbitmqctl list_queues name`.
- Temporary/auto-delete per-class test queues + no-global-purge = the bus analog of the established `FLUSHDB`-ban / `stepsdb_test_*`-leak discipline.
- Soft-vs-hard health posture per host (WebApi soft-on-broker, Orchestrator hard-on-broker) — TEST-RMQ-03 asserts the WebApi soft side.

### Integration Points
- `tests/BaseApi.Tests` gains the Phase 20 tests (fan-out, ES E2E, broker-down, outbound-filter synthetic send) + the in-memory-harness swap on the integration host.
- Real Docker stack (postgres + redis + rabbitmq + otel-collector + elasticsearch + prometheus + orchestrator + baseapi-service) must come up healthy for TEST-RMQ-02 and the close gate — gated on the D-12 wget fix.

</code_context>

<specifics>
## Specific Ideas

- The guiding principle: **in-memory by default, real stack only where a real broker/ES is the actual subject under test.** TEST-RMQ-02 is the one true cross-process proof (it automates the Phase 19 human-UAT item); everything else is hermetic and fast.
- TEST-RMQ-02's full-chain assertion (D-07) deliberately proves BOTH equality (webapi-published Guid == orchestrator-logged Guid) AND distinctness from the HTTP id — the two halves of "per-stage handoff, not one value across all hops."
- This is the milestone-closing phase, so the close gate is made clean (smells fixed, D-11) and the full stack is made genuinely healthy (wget fix, D-12) rather than carrying known cruft into the v3.4.0 ship.

</specifics>

<deferred>
## Deferred Ideas

- **WR-02 — Start dual-write atomicity (outbox).** `OrchestrationService.StartAsync` commits L2 writes then publishes; a broker-unreachable publish leaves L2 projected but no message sent (eventually consistent only on client retry). A transactional outbox is a real design decision → FUTURE / Processor milestone, not closeout scope.
- **WR-01 — published `WorkflowIds` = input set, not per-stage completed set.** Faithful today; forward-fragile only once per-workflow skips exist → revisit when execution-stage skips are introduced.
- **Stop writing vestigial `WorkflowRootProjection.correlationId`** (unread under D-01) — optional later cleanup, left written for zero churn (carried from Phase 19).
- Code-review IN-01/IN-03/IN-04/IN-05 (dead `WorkflowRootNotFoundException` seam, discarded-deserialize poison behavior, service-version literal drift, the wget apt layer) — informational, no action this phase.

None of these are losses — all tracked here and/or in `.planning/REQUIREMENTS.md` Future/Out-of-Scope.

</deferred>

---

*Phase: 20-correlation-propagation-proof-synthetic-harness-triple-sha-c*
*Context gathered: 2026-05-30*
