# Phase 33: Fault-Recovery Spike (De-Risk) - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-05
**Phase:** 33-fault-recovery-spike-de-risk
**Areas discussed:** Spike Vehicle & Disposition, Fault-Trip Realism, Negative Command-Fault Proof, `_error` Retention, Observability (side-thread)

---

## Spike Vehicle & Disposition

| Option | Description | Selected |
|--------|-------------|----------|
| Throwaway-but-kept RealStack E2E test | Clone `IdempotentExactlyOnceE2ETests`; in-test `IBusControl` registers the two `Fault<T>` consumers; keep as RealStack regression guard | ✓ |
| Minimal disposable consumer seeding Phase-35 | Build a small throwaway consumer that seeds the production path | |
| Temporary consumer in existing Orchestrator | Wire a `Fault<T>` consumer inline into the running console | |

**User's choice:** Vehicle option 1 (kept E2E test). No production Keeper code in Phase 33.
**Notes:** Strong precedent — the clone source already Sends a reconstructed dispatch twice and proves `flag[H]` collapse with net-zero teardown.

---

## Fault-Trip Realism

| Option | Description | Selected |
|--------|-------------|----------|
| Both faults live via symmetric WRONGTYPE | Dispatch: poison `skp:data:{hash}` as LIST; result: poison the `ResultConsumer` `flag[m.H]` key the same way | ✓ |
| Dispatch live + result synthetic | Trip dispatch live; cover result with a synthetic `Fault<ExecutionResult>` publish | (fallback only) |

**User's choice:** Both live (D-05), with synthetic `Fault<ExecutionResult>` retained as a **documented fallback for the result type only** (D-06) if a clean deterministic orchestrator-result trip proves fiddly. Exact orchestrator-result poison surface = research item.
**Notes:** The novel risk is fully exercised by the dispatch trip; the result trip mainly proves the second endpoint/type. Re-inject forwards the extracted message verbatim (same `H`); collapse proven by double re-inject + one-effect assertion.

---

## Negative Command-Fault Proof

| Option | Description | Selected |
|--------|-------------|----------|
| 3a Structural only | Never bind command-fault consumers; argue by design | |
| 3b Active synthetic negative | Publish `Fault<Start/Stop>`; assert spike consumers capture zero | ✓ |
| 3c Organic trip | Trip a real command to exhaustion | |

**User's choice:** 3b (D-09).
**Notes:** 3a is tautological; 3c rejected because `Start`'s main exception `WorkflowRootNotFoundException` is `Ignore`d (won't Fault) — high induction cost, little extra signal. 3b directly proves the bindings are type-scoped.

---

## `_error` Retention

| Option | Description | Selected |
|--------|-------------|----------|
| TTL'd forensic → DLQ-1 (source-agnostic) | `{procId}_error` consolidates into one source-agnostic TTL'd forensic queue; never Keeper's worklist | ✓ |
| Suppress `_error` | Drop the forensic copy entirely | |

**User's choice:** TTL'd forensic, consolidates source-agnostically into DLQ-1 (D-10).
**Notes:** User correction — the milestone already locked **two DLQs split by mechanism** (DLQ-1 forensic / DLQ-2 `keeper-dlq` give-up alert). Operator triage axis is mechanism, not origin component ("doesn't matter which processor or the orchestrator"). Phase 33 records the decision only; topology built in Phase 36.

---

## Observability (side-thread — not a gray area, resolved to no-op)

**User question:** What business metrics for exhausted? Consistent across orchestrator/processor/webapi? What for number of attempts?
**Finding surfaced:** No `*_exhausted` counter exists on any producer (the closest, `workflow_cancelled`, was reverted in Phase 32.1). Convention is uniform but instrument sets differ; WebApi has no messaging metrics. No attempts metric (only `processor_dispatch_consumed` delta = N+1, or runtime `GetRetryAttempt()`). All Keeper metrics (KMET-01..04) are Phase 38/35.
**User's decision:** "Leave it as is" — no metric work in Phase 33 (D-11). Producer-side exhaustion metric noted as a deferred future-milestone idea.

## Claude's Discretion

- Re-inject plumbing (`GetSendEndpoint` URI, short-lived `IBusControl` lifecycle), settle-window durations, exact `PollEsForLog` query shape, the seeded processor/workflow topology.

## Deferred Ideas

- Producer-side `*_exhausted` business metric + `GetRetryAttempt()` as a tag/log field — explicitly left as-is; future-milestone candidate.
