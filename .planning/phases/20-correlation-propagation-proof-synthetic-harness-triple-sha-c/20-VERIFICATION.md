---
phase: 20
slug: correlation-propagation-proof-synthetic-harness-triple-sha-c
status: passed
score: 7/7 must-haves verified
verified: 2026-06-01
verification_type: reconstructed
reconstruction_note: "Phase 20 shipped without a VERIFICATION.md at the time (the triple-SHA close gate + VALIDATION.md substituted). This artifact was reconstructed on 2026-06-01 during the v3.4.0 milestone audit from the four 20-0x-SUMMARY.md files, 20-VALIDATION.md (nyquist_compliant=true), the live CorrelationPropagationE2ETests evidence, and the Phase-20 close gate (3x265 GREEN + triple-SHA BEFORE==AFTER). No code was changed; this records verification of work that was already complete and independently proven."
requirements_total: 7
requirements_met: 7
---

# Phase 20: Correlation-Propagation Proof + Synthetic Harness + Triple-SHA Closeout — Verification Report

**Phase Goal:** Prove the body-carried correlation chain end-to-end across the live WebApi → RabbitMQ → Orchestrator boundary (closing the Phase 19 human-deferred item), stand up the in-memory MassTransit test harness + a synthetic outbound-filter proof, prove fan-out broadcast and broker-down health resilience, and close the milestone segment with a triple-SHA (`psql \l` + `redis-cli --scan` + `rabbitmqctl list_queues`) BEFORE==AFTER gate over 3 consecutive GREEN runs.

**Verified:** 2026-06-01 (reconstructed during milestone audit — see frontmatter note) · **Status:** passed (7/7)

## Goal Achievement

### Observable Truths

| # | Requirement | Truth | Status | Evidence |
|---|-------------|-------|--------|----------|
| 1 | TEST-RMQ-01 | Fan-out broadcast: one publish → two distinct-InstanceId endpoints both consume (per-consumer-sum count == 2, not load-balanced). | VERIFIED | `tests/BaseApi.Tests/Orchestrator/FanOutBroadcastTests.cs` (20-02): two endpoints hosting two distinct consumer types, summed per-consumer-harness `Select<T>(ct).Count()` == 2. |
| 2 | CORR-03 | Synthetic outbound-filter proof: the ambient `ICorrelationAccessor` id is stamped onto the published **envelope** by `OutboundCorrelationPublishFilter`. | VERIFIED | `tests/BaseApi.Tests/Orchestrator/OutboundFilterSyntheticTests.cs` (20-02): publish-filter wired under the in-memory harness; asserts envelope-stamped correlation id. |
| 3 | CORR-04 / TEST-RMQ-02 | Real-stack correlation propagation: HTTP POST → live RabbitMQ → orchestrator seam log carries the body `ICorrelated.CorrelationId` (the publish-boundary `NewId`), surfaced in Elasticsearch under `attributes.CorrelationId`, equal to the WebApi published-log id and distinct from the HTTP-stage `X-Correlation-Id`. | VERIFIED | `tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs` (20-03, hardened 20-04): real POST → real broker @5673 → ES term query on `attributes.CorrelationId`; net-zero L2 teardown (deletes its 3 `skp:` keys). This is the automated form of the Phase-19 human-deferred item. |
| 4 | TEST-RMQ-03 | Broker-down health: `/health/live` + `/health/ready` both return 200 with RabbitMQ dead (Degraded, not Unhealthy) — CRUD unaffected. | VERIFIED | `HealthDeadRabbitFixture` + 2 facts in `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` (20-02): dead `RabbitMq__Host`, live Postgres+Redis, both probes 200. |
| 5 | TEST-RMQ-04 | Test discipline: in-memory temporary/auto-delete endpoints, per-class-prefixed InstanceIds, zero global purge. | VERIFIED | 20-02 + 20-04: per-class InstanceId prefixes; `CorrelationPropagationE2ETests` restored to net-zero L2 (TEST-REDIS-04 invariant); no global purge anywhere. |
| 6 | TEST-RMQ-05 | Triple-SHA close gate: `psql \l` + `redis-cli --scan` + `rabbitmqctl list_queues name` BEFORE==AFTER, alongside the 3-consecutive-GREEN cadence. | VERIFIED | 20-04 close gate ran at **exit 0**: zero-warning build (Release+Debug), **3× consecutive 265-GREEN**, triple-SHA BEFORE==AFTER held on all three resources. (Also re-held in the phase-21 and phase-22 close gates.) |
| 7 | D-01 harness | The in-memory MassTransit harness (`HarnessWebAppFactory`) is wired into the 13 publishing orchestration HTTP fact classes (built in 20-01, completed/wired in 20-04 via a complete namespace-based service-descriptor strip before `AddMassTransitTestHarness`). | VERIFIED | `tests/BaseApi.Tests/Composition/HarnessWebAppFactory.cs` (20-01, completed 20-04): RemoveMassTransit() is absent in MassTransit 8.5.5 (CS1061) → manual namespace-based strip + health-check dedup; wired into the 13 publishing fact classes. |

**Score:** 7/7 verified.

## Requirements Coverage

| Requirement | Status | Source Plan | Evidence |
|-------------|--------|-------------|----------|
| CORR-03 | satisfied | 20-02 | OutboundFilterSyntheticTests (envelope-stamp proof) |
| CORR-04 | satisfied | 20-03 | CorrelationPropagationE2ETests (real-stack) |
| TEST-RMQ-01 | satisfied | 20-02 | FanOutBroadcastTests |
| TEST-RMQ-02 | satisfied | 20-03 | CorrelationPropagationE2ETests (ES term query) |
| TEST-RMQ-03 | satisfied | 20-02 | HealthDeadRabbitFixture + 2 facts |
| TEST-RMQ-04 | satisfied | 20-02/20-04 | temporary/auto-delete discipline, net-zero L2 |
| TEST-RMQ-05 | satisfied | 20-04 | triple-SHA close gate, exit 0, 3×265 GREEN |

## Gaps Summary

None. All 7 requirements satisfied; the milestone-segment close gate held at exit 0 across 3 consecutive GREEN runs with triple-SHA BEFORE==AFTER. The Phase-19 human-deferred live-correlation item is closed here by the automated `CorrelationPropagationE2ETests`.

*Reconstructed 2026-06-01 during /gsd-audit-milestone reconciliation. No code changed.*
