# Phase 19: Orchestrator Console + WebApi Bus Wiring + RabbitMQ Tier - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-30
**Phase:** 19-orchestrator-console-webapi-bus-wiring-rabbitmq-tier
**Areas discussed:** Correlation model (Fork 1), Publish-side wiring (Fork 2), WebApi bus-health soft-dep (Fork 3), InstanceId + endpoint shape (Fork 4), Orchestrator containerization (Fork 5)

---

## Fork 1 — Correlation model (carrier + contract shape) — iterated 4×

This fork went through four refinements as the user clarified intent. Final model = body-carried, slim `ICorrelated`, per-stage handoff.

| Option (in order proposed) | Description | Selected |
|--------|-------------|----------|
| 1. L2-root carries X-CorrelationId | HTTP X-Correlation-Id stored in L2 root; orchestrator reads it back per WorkflowId and logs under it. Original ROADMAP/ORCH-CON-03/CORR-04 wording. | |
| 2. Per-stage handoff, envelope-carried | HTTP id scopes HTTP stage (not persisted); fresh **envelope** CorrelationId minted at publish; 3rd fresh Guid at future job trigger. | superseded |
| 3. Fat ICorrelated (all 6) on control msgs | Start/Stop implement the full 6-id ICorrelated; unused ids = Guid.Empty; body-carried. | rejected by user ("I don't like … WorkflowId = Empty") |
| 4. Slim ICorrelated + derived per category | ICorrelated = `{ CorrelationId }` only; operational msgs (Start/Stop) implement just it; execution msgs implement a derived `IExecutionCorrelated : ICorrelated` (5 more ids) defined later where real; body-carried; per-stage handoff. | ✓ FINAL |

**User's reasoning (verbatim, condensed):**
- (→opt 2) "http request use the x-correlationId up to the stage that publish to orchestrator. not storing x correlation in redis at all. the publish generate … correlationId to the orchestrator. orchestrator use the correlationId up to scheduler job, this one generate new correlationId. this is consistency behavior."
- (→opt 3 then 4) "ICorrelated interface is perfect to messages between orchestrator to processor vice versa … the message between webapi to orchestrator … are operational, meaning part of the Id's will be guid.empty … still we want consistency."
- (→opt 4 final) "another approach … ICorrelated only contains CorrelationId, and inherit and create dedicated messages for each (I don't like … Guid[] WorkflowIds payload retained, ICorrelated.WorkflowId = Empty)."
- Approval: "go ahead, defer the derived interface."

**Carrier question (envelope vs body):** Resolved to **body** — user explicitly wants their own `ICorrelated` contract to carry correlation, not the MassTransit envelope slot. The envelope `CorrelationId` slot is left unused. `CorrelatedBy<Guid>` (MT's auto-bridge) rejected — would drag MassTransit into the pure-POCO `Messaging.Contracts` (Phase 17 D-01).

**Notes / amendments (all 2026-05-30, Phase 19 D-01):** This model amends **shipped** Phase 17 (ICorrelated 6-id get-only → slim 1-id init-set, D-09; Start/Stop non-implementers → implementers, D-10; MSG-CONTRACTS-02/03) and **shipped** Phase 18 (inbound filter envelope→body, D-01/CORR-01), plus ROADMAP goal + Phase 19 SC#2 + Phase 20 SC#1 + correlation constraint, and REQUIREMENTS ORCH-CON-03/CORR-04/TEST-RMQ-02. Code reconciliation is Phase 19 implementation work. The `"CorrelationId"` log-scope KEY stays the shared join (Phase 17 D-11); VALUE is per-stage. Derived `IExecutionCorrelated` + stage-3 per-job Guid deferred to Processor milestone. Captured as CONTEXT D-01/D-01a/D-02.

---

## Fork 2 — Publish-side wiring

| Option | Description | Selected |
|--------|-------------|----------|
| Publish-only, body mint, AddBaseApiMessaging in BaseApi.Core | WebApi joins publish-only; no correlation filter; sets the body `ICorrelated.CorrelationId` at construction (NewId.NextGuid); registration extension in BaseApi.Core for symmetry with AddBaseConsoleMessaging. | ✓ |
| Inline registration in Program.cs (keep BaseApi.Core MassTransit-free) | Same publish-only behavior but wired inline in BaseApi.Service/Program.cs; BaseApi.Core stays free of MassTransit. | |

**User's choice:** Publish-only + body mint + AddBaseApiMessaging in BaseApi.Core. Resolved under the final body-carried model — Start/Stop now implement slim ICorrelated (Fork-1 opt 4), so the publisher sets the body field at construction: `Publish(new StartOrchestration(ids) { CorrelationId = NewId.NextGuid() })`. The envelope is NOT set (body-only model). User: "check if fork 2 still unclear then amend them now" → confirmed clear, recommendation adopted.
**Notes:** CONTEXT D-02/D-03/D-04. (Earlier intermediate framing used an envelope mint; superseded when Fork 1 settled on body-carried.)

---

## Fork 3 — WebApi bus-health soft-dependency (MSG-WEBAPI-04)

| Option | Description | Selected |
|--------|-------------|----------|
| MinimalFailureStatus = Degraded | Bus check stays visible in /health payload as Degraded on broker-down but never flips CRUD /health/ready to 503. | ✓ |
| Strip the `ready` tag | Remove the bus check from the ready aggregate entirely — quieter but loses the Degraded signal. | |

**User's choice:** Degraded (recommendation accepted, not contested).
**Notes:** Mirrors Redis soft posture; inverse of the Orchestrator (hard-on-broker). CONTEXT D-05.

---

## Fork 4 — InstanceId + receive-endpoint shape (ORCH-CON-02)

| Option | Description | Selected |
|--------|-------------|----------|
| Config key + GUID fallback, one shared endpoint | `Orchestrator:InstanceId` with generated-GUID fallback; one temporary/auto-delete endpoint `orchestrator-{InstanceId}` hosting both Start + Stop consumers. | ✓ |
| Two separate endpoints | Separate instance-unique endpoints per consumer. | |

**User's choice:** One shared endpoint + GUID fallback (recommendation accepted, not contested).
**Notes:** Temporary/auto-delete so replicas don't orphan durable queues on rescale. CONTEXT D-06.

---

## Fork 5 — Orchestrator containerization this phase (INFRA-RMQ-02/03)

| Option | Description | Selected |
|--------|-------------|----------|
| Add Dockerfile + orchestrator compose service now | Runnable orchestrator container this phase; /health/ready hard-on-broker; depends_on rabbitmq service_healthy. | ✓ |
| Defer container to Phase 20 | Library + wiring only this phase; containerize alongside the Phase 20 ES E2E proof. | |

**User's choice:** Containerize now (recommendation accepted, not contested).
**Notes:** Phase 19 headline = "first runnable Orchestrator." RabbitMQ = rabbitmq:4.1.8-management-alpine, ping healthcheck, ports 5673/15673, guest/guest. CONTEXT D-09.

---

## Claude's Discretion

- WorkflowRootProjection.correlationId becomes vestigial under D-01 (left written this milestone).
- Business-exception type name/location (WorkflowRootNotFoundException).
- RedisProjectionKeys duplicate-vs-hoist for the orchestrator read path.
- Orchestrator read-L2 helper shape; test location (BaseApi.Tests/Orchestrator vs separate project).
- Orchestrator.csproj/Program.cs thin-shell shape; NewId.NextGuid vs Guid.NewGuid for the mint.

## Deferred Ideas

- Stage-3 per-job-trigger Guid CorrelationId (bus-world, orchestrator↔processor) → Processor milestone.
- Stop writing the vestigial L2-root correlationId → optional later cleanup.
- Hoist RedisProjectionKeys to Messaging.Contracts → planner's call.
- Prefetch/concurrency tuning → Out-of-Scope; ConsumerDefinition is the future seam.
- AddBaseApiMessaging request/response support → FUT-REQRESP-01.
