# Phase 34: Keeper Console Foundation - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-05
**Phase:** 34-keeper-console-foundation
**Areas discussed:** Multi-replica round-robin proof, Compose multi-replica expression, Readiness gate (confirmed)

**Format note:** Per the user's documented preference (prose confirm-loops, not menu-driven `AskUserQuestion`), gray areas were presented as a single prose briefing with numbered forks + recommendations and a plain confirm. The requirements (KEEP-01/02/03 in REQUIREMENTS.md) read as a locked SPEC, so the discussion covered only the genuine HOW forks.

---

## Fork 1 — How does Phase 34 prove "multi-replica round-robin" (KEEP-02)?

| Option | Description | Selected |
|--------|-------------|----------|
| 1a | Ship a minimal placeholder competing-consumer (trivial `IConsumer<T>` on a stable shared endpoint) to materialize the queue and make round-robin live-verifiable now; Phase 35 swaps it for the real `Fault<T>` consumers | ✓ |
| 1b | Ship no consumers — bus starts, readiness flips on bus-started, container healthy, but round-robin defers to Phase 35 (a `ReceiveEndpoint` needs a consumer to exist) | |

**User's choice:** 1a
**Notes:** Recommended option. Makes KEEP-02 honest at its own phase and de-risks the endpoint topology before the real consumers land. Placeholder is explicitly throwaway (no-op/log-only).

---

## Fork 2 — How is multi-replica expressed in `compose.yaml`?

| Option | Description | Selected |
|--------|-------------|----------|
| 2a | Define the `keeper` tier WITHOUT `container_name`, WITH `deploy.replicas: 2` — plain `docker compose up` brings up 2 healthy replicas, round-robin is the default reality | ✓ |
| 2b | Keep single (`container_name: sk-keeper`, 1 instance); prove multi-replica only via `docker compose up --scale keeper=N` in the Phase 38 E2E | |

**User's choice:** 2a
**Notes:** Recommended option. `orchestrator`/`processor-sample` pin `container_name`, which blocks Compose scaling; dropping it + `deploy.replicas: 2` is the one deliberate divergence so "multi-replica healthy tier" is the default rather than a manual scale step.

---

## Readiness gate (confirmed, not a fork)

**Resolution:** Keeper keeps the `BaseConsole.Core` default `StartupCompletionService` (readiness flips on bus-started). It does NOT copy the Orchestrator's removal of `StartupCompletionService` — that removal exists only to gate readiness on L1 hydration, which Keeper has none of in this phase. User raised no objection.

## Claude's Discretion

- Health port 8083 (follows 8080/8081/8082 convention, container-internal).
- Placeholder message/consumer naming and no-op body.
- Dockerfile restore-cache layer ordering.
- Whether the stable queue name is a `KeeperQueues` const in `Messaging.Contracts` (house precedent) or local.
- appsettings.json key set (mirror Orchestrator).

## Deferred Ideas

- Real `Fault<T>` intake + correlation — Phase 35.
- L2 probe loop + `keeper-dlq` + two DLQs — Phase 36.
- Pause/Resume contracts + orchestrator coordination — Phase 37.
- Keeper meter + E2E + close gate — Phase 38.
