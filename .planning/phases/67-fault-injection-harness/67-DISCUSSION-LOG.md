# Phase 67: Fault-Injection Harness - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-14
**Phase:** 67-fault-injection-harness
**Areas discussed:** Harness form & wiring, Fault-injection mechanism, Phase-67 reference scenario, Scenario seam + teardown

---

## Harness form & wiring

| Question | Option | Description | Selected |
|----------|--------|-------------|----------|
| Form of orchestrator | PowerShell orchestrator | scripts/phase-67-harness.ps1; shells phase-65-up/reset, dotnet test seeder+analyzer, curl start, docker fault ops | ✓ |
| | C# orchestrator fixture | RealStack xUnit fixture drives everything in-process | |
| | Thin mix | PS outer driver + small C# activate helper | |
| Workflow-id handoff | psql query for sentinel id | SELECT id WHERE name='v8-fanout-proof'; seeder stays pure | ✓ |
| | Seeder writes id to file | FanOutSeeder writes seeded-workflow-id.txt | |
| | Fold activate into seeder | Seeder fixture calls orchestration/start itself | |
| Activation gate | Hard-gate on 204, then observe | Require 204 (liveness gate passed) before the window | ✓ |
| | Best-effort call | Log result, begin window regardless | |
| | You decide | Claude discretion | |
| Final success signal | Mirror analyzer verdict | Exit code == analyzer verdict; infra aborts distinct codes; prints report path | ✓ |
| | Separate harness verdict | Harness computes own combined PASS/FAIL | |
| | You decide | Claude discretion | |

**Notes:** Grounding facts surfaced during discussion — orchestration/start is validation-only/no-side-effect (cron drives firing); the seeder computes the wf id internally but does not surface it; every container has restart:unless-stopped.

---

## Fault-injection mechanism

| Question | Option | Description | Selected |
|----------|--------|-------------|----------|
| Crash mechanism | stop → dwell → start | Deterministic controlled outage; kill would auto-resurrect via restart policy | ✓ |
| | kill (policy auto-restarts) | ~1-2s non-deterministic outage | |
| | restart (single blip) | kill+immediate start, no dwell | |
| 2-replica tiers | Whole tier (all replicas) | Tier genuinely down during dwell | ✓ |
| | Single replica | Surviving replica keeps serving | |
| | You decide | Per-scenario field | |
| Injection timing | After N observed fires (~midpoint) | Trigger off observed activity, not blind wall-clock | ✓ |
| | Fixed wall-clock offset | T+150s regardless of activity | |
| | You decide | Claude discretion | |
| Dwell duration | ≥ one cron interval | ≥1 full 30s fire entirely inside the outage (~45-60s) | ✓ |
| | Sub-interval blip | < one cron interval | |
| | You decide | Claude discretion | |

**Notes:** restart:unless-stopped on all containers is the pivotal fact — only `docker stop` yields a harness-controlled, deterministic outage.

---

## Phase-67 reference scenario

| Question | Option | Description | Selected |
|----------|--------|-------------|----------|
| Canonical fault proof | Processor crash | TEST-02-shaped; richest redelivery/dedup recovery path; stateless worker | ✓ |
| | RabbitMQ crash | TEST-06-shaped; broker durability/reconnection | |
| | Orchestrator crash | TEST-03-shaped; dispatcher/scheduler recovery | |
| | You decide | Claude discretion | |
| Run baseline too? | Yes — baseline then crash | TEST-01 no-fault first to isolate harness bugs from injection bugs | ✓ |
| | No — single crash only | Happy path becomes one of Phase 68's 7 | |
| | You decide | Claude discretion | |
| Acceptance bar | End-to-end + verdict; expect PASS | 67 = mechanism (must produce a verdict, no human step); 68 = proof results (asserts PASS) | ✓ |
| | Must PASS to pass | Folds system-recovery guarantee into the harness phase | |
| | You decide | Claude discretion | |

**Notes:** Clean separation — Phase 67 proves the harness machinery; Phase 68 formally asserts PASS for all 7. A non-PASS reference run is a real finding, not a harness failure.

---

## Scenario seam + teardown

| Question | Option | Description | Selected |
|----------|--------|-------------|----------|
| Phase-68 plug-in | Scenario config table | scenarioId → {targetContainers, faultType, injectAfterNFires, dwellSeconds, notes}; 68 adds rows | ✓ |
| | Hardcoded branches | switch on scenarioId in harness body | |
| | You decide | Claude discretion | |
| Observe→analyze cohort | Window by timestamps; reset halts fires | No cron-halt step; next run's reset deletes workflow rows; orchestration/stop is a no-op | ✓ |
| | Explicit cron-halt step | Remove/disable workflow or stop orchestrator at window-end | |
| | You decide | Claude discretion | |
| Between runs | phase-65-reset, stack stays up | FLUSHALL + heal + row-scoped DB reset, re-seed; fast, clean attribution | ✓ |
| | Full down/up between runs | Max isolation, far slower across 7 | |
| | You decide | Claude discretion | |
| Final teardown | docker compose down (keep volumes/images) | No lingering containers; fast next up; reset handles state | ✓ |
| | down -v (drop volumes) | Pristine slate, slow cold start | |
| | Leave stack up | Friendly for iteration; doesn't satisfy "tear down" | |
| | You decide | Claude discretion | |

**Notes:** orchestration/start AND /stop are both validation-only/no-op — the cron drives firing and the next run's reset (workflow-row delete) halts it. The Phase 66 analyzer header already flags that a crashed+restarted tier resets its Prom counters mid-window → ES-primary completeness is the binding arbiter.

---

## Claude's Discretion

- Exact N-observed-fires threshold + per-fire observation mechanism (D-07); precise dwell duration (D-08).
- Scenario-config table file format/field names (D-12).
- Exact psql connection/auth for the sentinel id lookup (D-02).
- Distinct non-zero exit-code numbering per infra-abort class (D-04).
- Harness operator-trace / log + artifact layout.

## Deferred Ideas

None — discussion stayed within phase scope.
