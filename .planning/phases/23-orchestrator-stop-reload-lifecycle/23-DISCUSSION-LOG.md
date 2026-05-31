# Phase 23: Orchestrator Lifecycle - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-31
**Phase:** 23-orchestrator-stop-reload-lifecycle
**Areas discussed:** L1 store; Quartz + cron interpretation; Send addressing + verification; Lifecycle gating/startup/concurrency
**Mode:** prose confirm-loop (SPEC.md locked the WHAT; discussion was HOW-only). Carried-forward locks from Phase 19 (correlation `IExecutionCorrelated`, ack split, InstanceId fan-out) and Phase 22 (L2 structure, `L2ProjectionKeys`, `TimeProvider`) were applied without re-asking.

---

## Area 1 — L1 store

| Option | Description | Selected |
|--------|-------------|----------|
| Singleton thread-safe `IWorkflowL1Store` over `ConcurrentDictionary<Guid, WorkflowL1>` (steps nested) | One shared store, workflow entry owns its steps; no processor/parent-index keys | ✓ |
| Literal two-namespace dictionary mirroring L2 keys | Closer to L2 string shape, flatter | |

**User's choice:** Confirmed the singleton concurrent store with nested steps.

---

## Area 2 — Quartz + cron interpretation

| Option | Description | Selected |
|--------|-------------|----------|
| Raw Quartz.NET + DI job factory + RAMJobStore; **Cronos** computes fire times (self-rescheduling one-shot) | Cron stays 5-field Cronos (VALID-19 consistent); interval = delta of next two Cronos occurrences | ✓ |
| Translate Cronos 5-field → Quartz 6/7-field cron, use Quartz `CronTrigger` | Native Quartz cron, but syntaxes diverge (DOW, seconds) — rejected | |

**User's choice:** Confirmed (part of the 1–3 batch confirm). Cronos-not-Quartz-cron is a hard, deliberate choice.
**Notes:** Surfaced by scouting — `WorkflowDtoValidator` VALID-19 validates a 5-field `Cronos.CronFormat.Standard` and rejects 6-field.

---

## Area 3 — Send addressing + verification

| Option | Description | Selected |
|--------|-------------|----------|
| `ISendEndpointProvider.GetSendEndpoint(new Uri("queue:{processorId:D}"))` → `Send`; synthetic test consumer | Load-balanced `Send` per FUT-SEND-01; verified via synthetic receive endpoint | ✓ |
| `Publish` (fan-out) | Wrong model — FUT-SEND-01 specifies `Send` to a per-processor queue | |

**User's choice:** Confirmed.

---

## Area 4 — Lifecycle gating / startup / concurrency

Discussed iteratively across several sub-questions (Q4a startup ordering, Q4b Redis-at-startup, Q4c re-Start semantics), then refactored by the owner into a unified gated model.

### Q4c — re-Start semantics (evolved)
| Option | Description | Selected |
|--------|-------------|----------|
| Replace (teardown + rehydrate + reschedule) | Re-Start applies updated L2 definition | ✓ (final) |
| Ignore-if-exists (skip) | Simpler, but strands stale definition on re-Start unless Stop-first | (considered, rejected) |
| Every Start = stop-then-start | Same as replace, implemented by reusing Stop teardown | ✓ (chosen form) |

### Q4b — Redis unavailable at startup
| Option | Description | Selected |
|--------|-------------|----------|
| Probe-driven: gate `IStartupGate.MarkReady()` on initial hydration; Redis-down → startup/ready stay red, platform restarts on threshold; live stays green | Parity with WebApi soft-dep degrade-then-recover | ✓ |
| Fail-fast crash | Opposite of WebApi posture — rejected | |
| Come-up-empty, no retry | Active workflows wouldn't resume until re-publish — rejected | |

**Notes:** Owner directed "must be related to the probes." Confirmed WebApi never crashes on Redis-down (`abortConnect=false`, lazy connect, soft-dep health → Degraded, request-time RedisException → 500).

### Q4a — startup hydration vs live consumers + per-workflow concurrency (final, owner-specified)
| Decision | Resolution | Selected |
|----------|------------|----------|
| Global startup gate | While closed (hydration+scheduling running), all consumed Start/Stop **dropped**; opens = `MarkReady()` → probes green | ✓ |
| Per-workflowId gate | Try-lock; while held, same-wfX Start/Stop **dropped** (first-in-flight wins); different ids concurrent | ✓ |
| Start consume | tolerant teardown → hydrate+schedule (replace), gated | ✓ |
| Stop consume | teardown if present, skip if absent, gated | ✓ |
| Gate-held behavior | **drop** (vs wait/serialize) | ✓ drop |
| Residual lost-message/orphan weakness | accept; record reconciliation as deferred hardening | ✓ (a) |

**User's choice:** Drop semantics at both gate levels (owner explicitly chose drop over wait/serialize, accepting first-writer-wins). Residual disconnect-loss/orphan tail accepted; reconciliation pass (using the liveness signal) recorded as deferred hardening.
**Notes:** Confirmed MassTransit acks only after `Consume` completes; receive endpoint is temporary/auto-delete so control messages aren't redelivered across a restart — bulk hydration from L2 is the cross-restart recovery.

## Claude's Discretion

- Per-workflow lock implementation + lifecycle; hydration `BackgroundService` retry/backoff; `WorkflowL1` record shape; Quartz job factory + misfire policy; sequential vs bounded-parallel hydration; test project placement.

## Deferred Ideas

- Periodic reconciliation pass (hardening — the orphan/stale safety net).
- Cross-replica duplicate-dispatch dedup (owner's separate solution; out of scope).
- Processor→orchestrator result round-trip (FUT-SEND-02 / FUT-REQRESP-01).
- Optional WebApi guard against bare re-Start of an active workflow.
