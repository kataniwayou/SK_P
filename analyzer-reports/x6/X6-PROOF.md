# v9.0.0 — N=6 Live Resilience Repeatability Proof

**Campaign:** 6 consecutive full 7-scenario sweeps (`scripts/phase-68-sweep.ps1` ×6) against the live Docker stack.
**Code under test:** post-Phase-71 (`Processor*` origin-split contracts + new `OrchestratorResultPipeline` on the result path + WR-02 `OrchestratorInjectConsumer` TTL fix).
**Run window:** 2026-06-16 18:58 → 2026-06-17 00:14 (+03:00), ~5h16m, fully autonomous, no fail-fast.

## Result: 6/6 sweeps GREEN — 42/42 scenario-runs PASS

| Sweep | Result | Fan-out runs (complete) | End (local) |
|-------|--------|-------------------------|-------------|
| 1 | 7/7 PASS | 130 | 19:51 |
| 2 | 7/7 PASS | 130 | 20:44 |
| 3 | 7/7 PASS | 126 | 21:36 |
| 4 | 7/7 PASS | 126 | 22:28 |
| 5 | 7/7 PASS | 130 | 23:21 |
| 6 | 7/7 PASS | 130 | 00:14 |

**Aggregate:** 42/42 scenario-runs PASS · 42/42 zero-missing · 42/42 effect-once · **772 fan-out workflow runs** observed, all complete.

Scenarios per sweep (each a 5-min/30s-cron window with mid-run fault injection + in-window recovery):
TEST-01 baseline · TEST-02 processor crash · TEST-03 orchestrator crash · TEST-04 keeper crash (both replicas) · TEST-05 redis crash · TEST-06 rabbitmq crash · TEST-07 redis+rabbitmq crash.

## Verification methodology (per `PassFailEngine.cs`)

Binding correctness arbiter = **Elasticsearch logs, per `correlationId`** (NOT metrics):
- **STARTED** = distinct `(correlationId, executionId)` with ≥1 `Step_*` log.
- **zero-missing (OBS-01)** = each started run's distinct `Step_*` set equals the full 9-label DAG `{A,B,C,D1,E1,F1,D2,E2,F2}` — necessarily reaching **both sinks F1 + F2**.
- **effect-once (OBS-02, fail-closed)** = no duplicate `(correlationId, Step_label)`.
- **Verdict = Pass iff (every started run complete) AND (no duplicates).**
- **Prometheus counters = corroboration only** — they can raise a warning but never flip a green ES verdict (deliberately, because Prom counters reset on the mid-window container crash-restarts these scenarios perform). Every run reconciled clean.

## Notes

- **TEST-06 (rabbitmq)** — historically a `VERDICT_FAIL [MISSING:2]` before the `Processor__ExecutionDataTtl 5→300` fix (commit `da91d32`) — was green in **all 6 runs** (`KeeperReinjectDroppedDelta: 0`), confirming the TTL fix holds under sustained repetition rather than by timing luck.
- Per-run machine-readable roll-ups: `sweep-run-1.json` … `sweep-run-6.json` (this directory). Latest single-sweep roll-up: `../phase-68-summary.json`.
- Full console capture (`sweep-x6.out`, ~1 MB) retained locally only — not committed (build-output noise).
