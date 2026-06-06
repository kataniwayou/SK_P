# Phase 39: Keeper Observability + Real-Stack E2E + Close Gate - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-06
**Phase:** 39-keeper-observability-real-stack-e2e-close-gate
**Areas discussed:** E2E outage induction, dlq_pushed{reason} taxonomy, recovery_duration scope, Close-gate DLQ net-zero

---

## Area selection

| Option | Description | Selected |
|--------|-------------|----------|
| E2E outage induction | Real `docker stop` vs WRONGTYPE GET-key poison for dead-lettering both paths | ✓ |
| dlq_pushed{reason} taxonomy | reason label values + cross-counter tag scheme | ✓ |
| recovery_duration scope | both-outcome vs recovered-only; custom buckets; measurement site | ✓ |
| Close-gate DLQ net-zero | how both DLQs + scratch keys stay repeatably net-zero across 3 runs | ✓ |

**User's choice:** All four areas.

---

## Key reframing discovered during scout

`tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs` (Phase 36) is **already RealStack** and already
ships the two facts TEST-01/02 describe — `KeeperRecovery_RecoversBothPaths` and
`KeeperRecovery_GivesUp_ParksToDlq` — using the WRONGTYPE-poison recipe and already ACK-draining
`keeper-dlq` in teardown. This collapsed the "outage induction" and most of the "DLQ net-zero" areas
from build decisions into inherit/extend decisions.

---

## E2E outage induction

| Option | Description | Selected |
|--------|-------------|----------|
| Extend existing Phase-36 facts | Add `keeper_*` Prometheus assertions to the two RealStack facts; keep WRONGTYPE poison | ✓ |
| New dedicated metrics E2E | Author a separate `KeeperMetricsRoundTripE2ETests` | |
| `docker stop sk-redis` | Real container outage instead of poison | (rejected) |

**User's choice:** 1a — extend existing facts, keep WRONGTYPE poison.
**Notes:** `docker stop` rejected (breaks every console's Redis soft-dep, flaky). TEST-01 "both paths"
stays real-dispatch + synthetic-`Fault<ExecutionResult>` as Phase 36 built it.

## dlq_pushed{reason} taxonomy

| Option | Description | Selected |
|--------|-------------|----------|
| reason + separate fault_type tag | `reason="probe_exhausted"` (forward-looking enum) + `fault_type∈{dispatch,result}` + `ProcessorId` | ✓ |
| fold both into reason | `reason="dispatch_give_up"\|"result_give_up"`, drop fault_type | |

**User's choice:** 2a.
**Notes:** Uniform `{fault_type, ProcessorId}` across consumed/recovered; `{ProcessorId}` only on
paused/resumed (workflow-scoped); no `workflowId` label anywhere.

## recovery_duration scope

| Option | Description | Selected |
|--------|-------------|----------|
| both outcomes + custom second buckets | tag `outcome∈{recovered,gave_up}`, `Advice<double>` ≈ {1,5,10,30,60,120}s, duration-in-consumer, in_flight-in-probe | ✓ |
| recovered-only + default buckets | skip the give-up tail, OTel default ms buckets | |

**User's choice:** 3a.
**Notes:** Give-up ~60s tail (12×5) is the saturation signal. `recovery_duration` measured in the
consumer (intake→terminal Stopwatch); `keeper_in_flight` UpDownCounter inside `L2ProbeRecovery.RunAsync`.

## Close-gate DLQ net-zero

| Option | Description | Selected |
|--------|-------------|----------|
| gate asserts depth==0 on both DLQs + triple-SHA | clone phase-33-close.ps1; `list_queues name messages` depth==0 on keeper-dlq + `*_error`; drain stays in test teardown | ✓ |
| test teardown + name-SHA only | rely on the existing drain + name-SHA, no depth assertion | |

**User's choice:** 4a.
**Notes:** No gate-side `purge_queue` (would mask a real leak). Inherit pre-flight stable-processor
seed + container rebuild from phase-33-close.ps1.

---

## Claude's Discretion

- `KeeperMetrics` wiring details, exact `Advice<double>` bucket values, tag-constant naming/location,
  `keeper_in_flight` numeric type, exact `Stopwatch` placement, optional shared tag-builder helper.
- Everything in the locked house meter pattern (`MeterName="Keeper"`, IMeterFactory, `AddMeter`).

## Deferred Ideas

- `reinject_failed` as a second `dlq_pushed{reason}` value (not emitted today).
- Grafana dashboard / Prometheus alert rule for the new series.
- A `keeper_processing` intermediate outcome.
