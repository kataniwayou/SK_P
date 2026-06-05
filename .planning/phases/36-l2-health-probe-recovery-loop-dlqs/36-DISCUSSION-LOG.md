# Phase 36: L2 Health-Probe Recovery Loop & DLQs - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-05
**Phase:** 36-l2-health-probe-recovery-loop-dlqs
**Areas discussed:** Probe loop + L2 client, DLQ-1 consolidation, keeper-dlq (DLQ-2) park, Re-inject ↔ Phase 37 boundary

---

## Area selection

Requirements are locked (PROBE-01..05, DLQ-01..04 via ROADMAP + REQUIREMENTS; no SPEC.md for this phase). Discussion was HOW-level only.

| Option | Description | Selected |
|--------|-------------|----------|
| Probe loop + L2 client | Loop structure, Redis client via AddBaseConsoleRedis, read+write probe, config + ack-timeout bound | ✓ |
| DLQ-1 consolidation | Shared TTL'd error transport across all consoles | ✓ |
| keeper-dlq (DLQ-2) park | give-up Send-then-ack, envelope vs inner | ✓ |
| Re-inject ↔ Phase 37 boundary | re-inject only, resume deferred, crash-survivability + concurrency + test vehicle | ✓ |

**User's choice:** All four areas.

---

## Area 1 — Probe loop + L2 client

Proposed D-01..D-04: Redis via existing `AddBaseConsoleRedis` (no firewall break); probe = read `skp:data:{entryId}` + write-then-delete scratch key, success only if both ops complete without a Redis exception; scratch key `skp:keeper:probe:{h}` with ~30s TTL (crash net-zero safety net); config `"Probe": { DelaySeconds: 5, MaxAttempts: 12 }` (60s window, 30× margin under RabbitMQ's 30-min consumer_timeout).

**User's choice:** Confirmed as proposed.
**Notes:** Flagged probe defaults (D-04) as a likely amendment candidate — accepted unchanged.

---

## Area 2 — DLQ-1 consolidation

Proposed D-05..D-07: one shared `skp-dlq-1` queue with `x-message-ttl`, configured once in `BaseConsole.Core` so all three consoles inherit (DLQ-04), TTL 7 days. Flagged the exact MassTransit-on-RabbitMQ exhaustion-routing mechanism as the TOP research item (ROADMAP's "confirmed in Phase-33 spike" is inaccurate — only the decision was recorded; MassTransit republishes to `_error` rather than nack-to-DLX, so the hook must be confirmed).

**User's choice:** Confirmed as proposed.
**Notes:** TTL (D-07) flagged as an amendment candidate (24h alternative offered) — accepted 7 days unchanged.

---

## Area 3 — keeper-dlq (DLQ-2) park

Proposed D-08..D-10: new `KeeperQueues.DeadLetter = "keeper-dlq"`, plain durable queue NO TTL (primary persistent operator alert); give-up = GetSendEndpoint + Send then ack (Send-fault → Immediate(N) → DLQ-1); park the original `Fault<T>` envelope (not inner) for exception-context triage.

**User's choice:** Confirmed as proposed.
**Notes:** Envelope-vs-inner (D-10) flagged as an amendment candidate — accepted envelope unchanged.

---

## Area 4 — Re-inject ↔ Phase 37 boundary

Proposed D-11..D-13: Phase 36 re-injects ONLY (no Pause/Resume contracts — Phase 37); "stays paused" SC clause is structurally vacuous in 36; ack-after-loop automatic (loop awaited in Consume); proof split hermetic (loop logic, no premature ack) + RealStack (recover-both-paths + give-up), kill-mid-loop as operator runbook (Phase 39 authoritative); default prefetch/concurrency (60s hold bounds head-of-line, Claude's discretion).

**User's choice:** Confirmed as proposed.
**Notes:** None.

## Claude's Discretion

- ProbeOptions placement; shared probe-loop helper vs inline; StackExchange.Redis call shapes (mirror ResultConsumer).
- RealStack test vehicle (extend spike-family vs new test), settle windows, PollEsForLog query shapes.
- Prefetch/concurrency limit (D-13).

## Deferred Ideas

- PauseWorkflow/ResumeWorkflow + orchestrator pending-recovery set — Phase 37.
- Keeper meter + probe/DLQ-depth counters — Phase 38.
- keeper-dlq-depth Prometheus alert + real-stack close gate + kill-mid-loop live proof — Phase 39.
