---
status: resolved
phase: 19-orchestrator-console-webapi-bus-wiring-rabbitmq-tier
source: [19-VERIFICATION.md]
started: 2026-05-30T14:28:57Z
updated: 2026-06-01T00:00:00Z
resolved_by: "20 (CorrelationPropagationE2ETests)"
---

## Current Test

[resolved — automated by Phase 20]

## Tests

### 1. Live end-to-end body-carried correlation chain
expected: HTTP 2xx; `docker logs sk-orchestrator` shows a `"Scheduler job start (seam) for {WorkflowId}"` line scoped under a `CorrelationId` equal to the `CorrelationId` on the published `StartOrchestration` message body (the `NewId` minted at the publish boundary) — NOT the HTTP-stage `X-Correlation-Id`. This proves the per-stage handoff across the WebApi → RabbitMQ → Orchestrator boundary.
result: resolved — Phase 20 automated this exact proof as `tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs` (CORR-04 / TEST-RMQ-02): a real POST through the live broker surfaces the body `ICorrelated.CorrelationId` (publish-boundary `NewId`) in Elasticsearch under `attributes.CorrelationId`, equal to the WebApi published-log id and distinct from the HTTP-stage id. GREEN in the Phase-20 close gate (3×265) and the subsequent 335/335 clean-build suite. Manual observation unnecessary.

Steps: Bring up the full compose stack (`docker compose up -d`), seed a Redis L2 root for a workflow (start it via the normal path), then `POST /api/v1/orchestration/start` with a valid `WorkflowIds` array for that workflow. Inspect `docker logs sk-orchestrator` and (optionally) the published message body via the RabbitMQ management UI at http://localhost:15673 (guest/guest). Tear down with `docker compose down` (no `-v`).

Note: This item is substantially the same proof Phase 20 SC#1 will automate (end-to-end correlation surfaced in Elasticsearch under the body `ICorrelated.CorrelationId`). It can be satisfied here by manual observation or deferred to Phase 20's synthetic harness.

## Summary

total: 1
passed: 1
issues: 0
pending: 0
skipped: 0
blocked: 0

## Gaps
