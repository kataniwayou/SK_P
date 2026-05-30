# Phase 20: Correlation Propagation Proof + Synthetic Harness + Triple-SHA Closeout - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-30
**Phase:** 20-correlation-propagation-proof-synthetic-harness-triple-sha-closeout
**Areas discussed:** Real-broker test strategy & Phase-19 reconciliation; ES correlation-proof mechanics; Triple-SHA close gate & script reuse; Phase-19 tech-debt fold-in

---

## Area 1 — Real-broker test strategy & Phase-19 reconciliation

| Option | Description | Selected |
|--------|-------------|----------|
| Fork A | In-memory MassTransit harness as the DEFAULT test transport (reconciles the 16 red Phase-19 facts instantly, publish assertable, suite stays hermetic); real Docker stack ONLY for TEST-RMQ-02; broker-down test uses real transport at a dead endpoint; fan-out + outbound stay in-memory | ✓ |
| Fork B | Point the whole orchestration suite at `localhost:5673` and require a live broker for all orchestration tests | |

**User's choice:** Fork A, confirmed.
**Notes:** Claude amended its own opening recommendation (which had leaned toward pointing the factory at localhost:5673) after reasoning that the WebApi now always publishes on /start|/stop, so coupling the whole suite to a live broker would keep the ~1m40s timeout hangs. The split is by what each test actually proves.

---

## Area 2 — ES correlation-proof mechanics (CORR-04 / TEST-RMQ-02)

| Option | Description | Selected |
|--------|-------------|----------|
| Fork A | Add a publish-side structured log on OrchestrationService; read both the WebApi-published doc and the orchestrator seam doc from ES; assert webapi-guid == orchestrator-guid AND both != HTTP X-Correlation-Id (full per-stage chain proof) | ✓ |
| Fork B | Observable-only: assert the orchestrator CorrelationId is a non-empty Guid and differs from the HTTP id (no app code change) | |

**User's choice:** Fork A, confirmed.
**Notes:** Topology is hybrid — real Orchestrator container + in-process WebApi (WebApplicationFactory pointed at real localhost endpoints), the single exception to Area-1's in-memory default. Reuse ElasticsearchTestClient/OrchestrationLogsE2ETests pattern. Seed the L2 root in real Redis first.

---

## Area 3 — Triple-SHA close gate & script reuse (TEST-RMQ-05)

| Option | Description | Selected |
|--------|-------------|----------|
| Fork A | Copy phase-18/17-close → phase-20-close, add the rabbitmq SHA (`docker exec sk-rabbitmq rabbitmqctl list_queues name`), AND fix the three deferred v3.3.0 close-gate smells | ✓ |
| Fork B | Extend to triple-SHA but leave the deferred smells (smaller diff) | |

**User's choice:** Fork A, confirmed.
**Notes:** Net-zero queue-leak assertion (temporary/auto-delete + per-class queues). Full stack must be up for TEST-RMQ-02 during the gate. Flagged dependency on the Area 4 wget fix (baseapi-service must report healthy).

---

## Area 4 — Phase-19 tech-debt fold-in

| Option | Description | Selected |
|--------|-------------|----------|
| Fork A | Fold in both 4a (baseapi-service wget healthcheck fix — close-gate prerequisite) and 4b (IN-02 Stop-consumer mislog + test assertion fix) | ✓ |
| Fork B | Fold only 4a (the close-gate blocker); leave IN-02 for a later polish pass | |
| Fork C | Fold neither; handle separately | |

**User's choice:** Fork A, confirmed.
**Notes:** Code-review warnings WR-01 (published set vs completed set) and WR-02 (Start dual-write atomicity / outbox) explicitly NOT folded — deferred design concerns recorded in CONTEXT deferred section.

## Claude's Discretion

In-memory-harness wiring specifics, fixture class names/placement, ES query shape/poll timeout, publish-side log text/level, two-bus harness construction, close-gate script internals beyond triple-SHA + smell fixes, and test placement — all left to planner/executor within the locked decisions.

## Deferred Ideas

WR-02 (outbox), WR-01 (per-stage completed set), stop writing vestigial `correlationId`, and informational code-review items IN-01/03/04/05.
