---
status: open
kind: product-gap
discovered_in: 62-live-proof-close-gate (TEST-01b live lifecycle proof)
origin_phase: 60-dual-loop-writer-in-memory-l1-liveness-record
affects_requirements: [STATE-03, LIVE-04, LOOP-02, PROBE-01, PROBE-02, TEST-01]
severity: high
discovered: 2026-06-13
---

# GAP — Liveness-timestamp refresh is coupled to `IsHealthy` (alive-but-unhealthy replicas go stale/absent)

## One-line

The processor liveness heartbeat only refreshes the L1/L2 timestamp **when the replica is
Healthy**, so an **alive-but-unhealthy** replica (e.g. a Gate-A-clash processor) stops being
refreshed after startup — its L2 gate key TTL-expires (→ **absent**) and its L1 watchdog record
goes stale (→ the self-watchdog falsely reports a **crashed/stale loop** for a loop that is in
fact alive).

This contradicts the v7 two-state-liveness + self-watchdog intent: the timestamp is the
"loop-is-alive" evidence and must be refreshed for **any alive, identity-resolved replica**
regardless of health; the **status** field (Healthy/Unhealthy) is the orthogonal health signal.

## How it was found

Phase-62 TEST-01b (the live lifecycle proof that a durably-broken replica is observable as
`Unhealthy`, NOT absent). The Phase-62 close gate (TEST-03) PASSED and TEST-01a / TEST-02 passed,
but TEST-01b could not be satisfied live. These lifecycle proofs are the half the xUnit harness
cannot do (no real container lifecycle), so this only surfaces in the live close run.

## Live evidence (2026-06-13, v7 stack: 2 healthy `processor-sample` replicas + 1 `processor-badconfig`)

- Sample procId `2f6f59b0-2125-4e4a-9fd9-d849441ef91e`, BadConfig procId `67462a01-fca9-4793-8ad4-4eae6fc22152`.
- **Healthy replicas (control, PASS):** `SMEMBERS skp:proc:2f6f59b0…` → 2 instanceIds (`604e8c283200`, `4bfe2a739f0a`), each per-instance key `status=Healthy`, TTL continuously refreshed by the heartbeat.
- **BadConfig (the gap):**
  - After ~2h uptime + a redis `FLUSHALL`: `EXISTS skp:proc:67462a01…` = `0` (absent); the per-instance key never reappeared on its own.
  - On a fresh `docker restart sk-processor-badconfig`: the per-instance key appeared with `status=Unhealthy` and TTL **56→51→45→…→-2** (monotonic decrease, **never refreshed**), expiring at 60s. The index SET member lingered (SET members are not auto-expired), so the index "exists" with a stale member but the per-instance key `GET` → `nil`.
  - Net: badconfig is observable as `Unhealthy` for **only one ~60s startup-TTL window** per (re)start, then **absent**.

## Root cause (code)

The two liveness loops share one writer but only one of them runs for an unhealthy replica:

- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs`
  - Loop A writes the entry (state + timestamp) once after identity resolves (`WriteUnhealthyAsync(); break;`).
  - Loop B re-writes `Unhealthy` **only while a schema definition is still unresolved**.
  - On the **Gate-A clash** path it calls `MarkReady`, **withholds `MarkHealthy`**, does not bind, and **returns** (terminal) — no further writes, and no hand-off to a refresh loop.
- `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs`
  - The heartbeat is **`IsHealthy`-gated**: `if (_context.IsHealthy && _context.Id is { } id)` (line 76). A not-Healthy replica **no-ops the tick** (its own comment: *"writes nothing so the gate reader sees it as `absent`"*, lines 21-22).
  - It also only ever writes a hardcoded all-`Success` "frozen-Healthy" entry — it has **no path** to refresh an `Unhealthy` entry.
- `src/BaseProcessor.Core/Liveness/ProcessorLivenessWriter.cs`
  - The shared `WriteAsync` correctly updates **both** L1 (`_l1.Update`, unconditional) and L2 (`StringSetAsync` per-instance + idempotent `SetAddAsync` index), with TTL = `max(entry.Interval × 2, Ttl-floor)` (heartbeat interval 10 → TTL 30; startup interval 30 → TTL 60). The writer is fine — it just never gets called again for an unhealthy replica.
- `src/BaseProcessor.Core/Liveness/LivenessWatchdogHealthCheck.cs`
  - The self-watchdog decides liveness purely on **L1 timestamp staleness**: `Current == null` → "liveness loop not started"; `now >= Current.Timestamp + Interval×2` → "**liveness loop stale**"; else Healthy. This confirms the timestamp is the loop-alive signal — and that a non-refreshed alive-but-unhealthy replica will be **falsely** flagged stale.

## Impact

1. **Orchestration-start gate:** an unhealthy replica is `absent` (not observably `Unhealthy`) after ~60s. The gate still BLOCKS (absent = not-healthy), so the **functional** block is preserved, but the v7 "observable unhealthy, not absent" guarantee (STATE-03 / RF-01) is not met.
2. **Self-watchdog (PROBE):** an **alive** but unhealthy replica's L1 timestamp goes stale after `Interval×2`, so `/health/live` would flip Unhealthy *"liveness loop stale"* and a future K8s liveness probe would **restart a process that never crashed** — a false-positive restart that conflates "unhealthy" with "crashed."

## Designed-vs-intended

- **Designed (shipped):** liveness-timestamp refresh is coupled to `IsHealthy`. Only healthy replicas keep a fresh timestamp; unhealthy → absent + (eventually) watchdog-stale.
- **Intended (per the watchdog's own staleness-as-crash semantic + two-state-liveness goal):** once identity resolves, the loop refreshes the timestamp **every interval regardless of health**, writing the **current** status (Healthy or Unhealthy). "Loop alive" (timestamp) is decoupled from "healthy" (status).

## Recommended fix (Phase-60 scope — NOT Phase-62)

- Make the post-identity refresh loop **identity-gated, not health-gated**: every interval, write the **current** status entry (Unhealthy or Healthy) with a fresh timestamp via the existing shared `ProcessorLivenessWriter`.
- Either (a) have `ProcessorLivenessHeartbeat` run once identity is resolved and write the actual status (not frozen-Healthy), or (b) have the startup orchestrator's terminal Gate-A-clash path hand off to a dedicated unhealthy-refresh loop instead of returning.
- Preserve the orchestration-start gate's admit rule (still admit only Healthy+fresh) — an unhealthy-but-fresh replica must continue to be **blocked** by the gate, but now **observably Unhealthy** rather than absent, and **not** falsely restarted by the self-watchdog.
- Re-validate against the hermetic `LivenessWatchdogHealthCheckTests` (verdict math) and re-run Phase-62 TEST-01b live.

## Verify-after-fix

On a fresh `docker restart sk-processor-badconfig`, the per-instance key `skp:proc:67462a01…:{instanceId}` should remain present with `status=Unhealthy` and a **TTL that resets each interval** (refreshed indefinitely while the process is alive), and `/health/live` on badconfig should reflect the real (unhealthy) status without a false "loop stale" once the refresh loop is running.

## What is NOT affected (and stays)

- The Phase-62 close gate (`scripts/phase-62-close.ps1`) PASSED net-zero (psql/redis/rabbitmq SHA HELD, DLQ 0, `skp:msg` 0, 604×3 GREEN).
- TEST-01a (two healthy replicas self-register) and TEST-02 (`/health/live` 200 + summary on a healthy replica) passed live.
- The Phase-62 test fixes committed this run (fabricated-key liveness-index isolation + stable per-test processor identities for rabbitmq net-zero) are legitimate and remain.
