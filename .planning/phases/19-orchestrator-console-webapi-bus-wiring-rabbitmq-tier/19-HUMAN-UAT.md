---
status: partial
phase: 19-orchestrator-console-webapi-bus-wiring-rabbitmq-tier
source: [19-VERIFICATION.md]
started: 2026-05-30T14:28:57Z
updated: 2026-05-30T14:28:57Z
---

## Current Test

[awaiting human testing]

## Tests

### 1. Live end-to-end body-carried correlation chain
expected: HTTP 2xx; `docker logs sk-orchestrator` shows a `"Scheduler job start (seam) for {WorkflowId}"` line scoped under a `CorrelationId` equal to the `CorrelationId` on the published `StartOrchestration` message body (the `NewId` minted at the publish boundary) — NOT the HTTP-stage `X-Correlation-Id`. This proves the per-stage handoff across the WebApi → RabbitMQ → Orchestrator boundary.
result: [pending]

Steps: Bring up the full compose stack (`docker compose up -d`), seed a Redis L2 root for a workflow (start it via the normal path), then `POST /api/v1/orchestration/start` with a valid `WorkflowIds` array for that workflow. Inspect `docker logs sk-orchestrator` and (optionally) the published message body via the RabbitMQ management UI at http://localhost:15673 (guest/guest). Tear down with `docker compose down` (no `-v`).

Note: This item is substantially the same proof Phase 20 SC#1 will automate (end-to-end correlation surfaced in Elasticsearch under the body `ICorrelated.CorrelationId`). It can be satisfied here by manual observation or deferred to Phase 20's synthetic harness.

## Summary

total: 1
passed: 0
issues: 0
pending: 1
skipped: 0
blocked: 0

## Gaps
