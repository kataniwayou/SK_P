---
phase: 19
slug: orchestrator-console-webapi-bus-wiring-rabbitmq-tier
status: draft
nyquist_compliant: true
wave_0_complete: false
created: 2026-05-30
---

# Phase 19 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (.NET 8) + MassTransit.TestHarness (`AddMassTransitTestHarness`, in-memory) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (existing) |
| **Quick run command** | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Orchestrator|FullyQualifiedName~Console|FullyQualifiedName~Messaging" --nologo` |
| **Full suite command** | `dotnet test SK_P.sln --nologo` |
| **Estimated runtime** | ~90–180 seconds (full suite; quick subset ~20–40s) |

---

## Sampling Rate

- **After every task commit:** Run the quick run command (Orchestrator/Console/Messaging subset).
- **After every plan wave:** Run the full suite command.
- **Before `/gsd-verify-work`:** Full suite must be green 3× consecutively (milestone cadence).
- **Max feedback latency:** 180 seconds.

---

## Per-Task Verification Map

> Filled by the planner against the final task breakdown. Rows below are the validation
> anchors derived from the 14 phase requirements + the shipped-code reconciliation (D-01).
> The planner MUST map each task to one of these requirement anchors and supply the concrete
> `dotnet test --filter` command + file-exists check.

| Anchor | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command (planner finalizes filter) | Status |
|--------|-------------|------------|-----------------|-----------|----------------------------------------------|--------|
| Slim `ICorrelated` `{ Guid CorrelationId }` init-set; Start/Stop implement it | MSG-CONTRACTS-02/03 (amended), ORCH-CON-03 | — | Contract compiles; control records carry body CorrelationId | unit | `dotnet test ...--filter ~Messaging` | ⬜ pending |
| Inbound consume filter reads BODY (`message is ICorrelated`) not envelope | CORR-01 (amended) | T-19 corr-integrity | Logged scope value = body CorrelationId | unit (harness) | `dotnet test ...--filter ~Console.*Correlation` | ⬜ pending |
| Orchestrator binds instance-unique temporary/auto-delete fan-out endpoint | ORCH-CON-01/02 | T-19 fan-out-trap | Each instance gets own queue (fan-out, not LB) | unit (config assertion) | `dotnet test ...--filter ~Orchestrator` | ⬜ pending |
| Orchestrator reads L2 root per WorkflowId; opens correlated scope; logs to seam | ORCH-CON-03/04 | — | No Redis writes; no Quartz; logs at scheduler-seam | unit (harness + fake Redis) | `dotnet test ...--filter ~Orchestrator` | ⬜ pending |
| Business failure (WorkflowId absent from L2) → catch + log + ack (not thrown) | MSG-ACK-01 | T-19 ack-business | Consume completes; no dead-letter | unit (harness) | `dotnet test ...--filter ~Orchestrator.*Ack` | ⬜ pending |
| Infra fault → throw → bounded retry (Ignore<business>) → `_error` | MSG-ACK-02/03 | T-19 ack-infra | Retries then dead-letters; business excluded | unit (harness) | `dotnet test ...--filter ~Orchestrator.*Retry` | ⬜ pending |
| `ConsumerDefinition` per consumer as config seam | MSG-ACK-04 | — | Definitions present; retry/endpoint configured there | unit | `dotnet test ...--filter ~Orchestrator` | ⬜ pending |
| WebApi publish-only bus join; references Messaging.Contracts only | MSG-WEBAPI-01 | T-19 dep-firewall | No BaseConsole.Core ref from BaseApi.* | unit (dep firewall) | `dotnet test ...--filter ~Composition|~Firewall` | ⬜ pending |
| Start publishes StartOrchestration{ids}; Stop publishes StopOrchestration{ids}; body CorrelationId minted | MSG-WEBAPI-02 | — | Published msg observed in harness with body CorrelationId | unit (harness) | `dotnet test ...--filter ~Orchestration.*Publish` | ⬜ pending |
| Broker unreachable → Start/Stop 5xx + RFC 7807; CRUD surface unaffected | MSG-WEBAPI-03 | T-19 broker-down | 5xx ProblemDetails on Start/Stop; CRUD 2xx | manual + unit | see Manual-Only (real-broker-down = Phase 20) | ⬜ pending |
| WebApi bus health = Degraded (MinimalFailureStatus); CRUD /health/ready stays 200 | MSG-WEBAPI-04 | T-19 broker-down | /health/ready 200 with broker down | unit | `dotnet test ...--filter ~Health` | ⬜ pending |
| RabbitMQ compose service healthy; depends_on service_healthy; appsettings both hosts | INFRA-RMQ-02/03 | — | compose has rabbitmq + healthcheck; appsettings RabbitMq: section | manual (compose) + grep | see Manual-Only | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] Rewrite `tests/BaseApi.Tests/Console/ConsoleCorrelationFilterTests.cs` — its `ProbeMessage` carries all 6 Guids (won't compile after the `ICorrelated` slim) and it asserts the **envelope** CorrelationId (semantically wrong after the body re-point). MUST be rewritten to a slim `{ Guid CorrelationId }` probe asserting the **body**-sourced scope value. (RESEARCH HIGH-confidence finding.)
- [ ] New Orchestrator consumer test scaffolding (fixture + fake/in-memory Redis L2 root) for the ack-split + fan-out-config assertions, using `AddMassTransitTestHarness`.
- [ ] WebApi publish-side harness fixture (publish-only bus under `AddMassTransitTestHarness`) for MSG-WEBAPI-02 publish assertions.

*Real broker (`rabbitmq:4.1.8-management-alpine`) is NOT required for Wave 0 — all Phase 19 assertions use the in-memory harness; the real-broker two-bus fan-out + ES correlation + broker-down CRUD tests are Phase 20 (TEST-RMQ-01..05).*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| RabbitMQ compose service comes up healthy | INFRA-RMQ-02 | Requires Docker daemon + real broker container | `docker compose up -d rabbitmq` → `docker inspect --format '{{.State.Health.Status}}' sk-rabbitmq` = `healthy` |
| Orchestrator container runs against the live broker | ORCH-CON-01, INFRA-RMQ-02 | Requires full compose stack | `docker compose up -d` → orchestrator `/health/ready` 200 once bus started |
| End-to-end body-CorrelationId surfaces in Elasticsearch | CORR-04 (deferred to Phase 20) | Needs real broker + ES + running orchestrator | Phase 20 TEST-RMQ-02 |
| Two-bus fan-out broadcast (both instances receive) | (deferred to Phase 20) | Needs two live bus instances | Phase 20 TEST-RMQ-01 |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references (esp. the ConsoleCorrelationFilterTests rewrite)
- [ ] No watch-mode flags
- [ ] Feedback latency < 180s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** planner-finalized 2026-05-30 — each Phase 19 task maps to an anchor with an automated dotnet command; Wave 0 (ConsoleCorrelationFilterTests rewrite + Orchestrator ack harness + WebApi publish harness) is covered by Plan 01 Task 2, Plan 02 Task 3, Plan 03 Task 3 respectively.
