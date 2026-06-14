# Phase 65: Fan-Out Workflow Seeder & Clean-State Stack — Specification

**Created:** 2026-06-14
**Ambiguity score:** 0.18 (gate: ≤ 0.20)
**Requirements:** 4 locked

## Goal

Ship two runnable artifacts for the v8.0.0 proof: (1) an **idempotent seeder** that creates the fan-out workflow `A→B→C→{D1→E1→F1, D2→E2→F2}` (9 steps, entry A, fan-out at C, sinks F1+F2) — every step referencing the one shared `processor-sample`, the workflow carrying the `*/30 * * * * *` cron, and every step's assignment carrying a `{ number, label:"Step_*" }` payload — and (2) a **clean-state reset routine** that returns the running stack to a deterministic, per-run-attributable baseline before each test. The proof runs on the minimal stack (sample processor only; `processor-badconfig` excluded).

## Background

Grounded in the current codebase (scouted 2026-06-14):

- **No workflow/step/assignment seeder exists.** The only workflow creation today is inline in `[Trait("Category","RealStack")]` E2E tests via `internal static` `HttpClient` helpers in `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs:341-431` (`SeedProcessorAsync`, `SeedConfigSchemaAsync`, `SeedStepAsync`, `SeedWorkflowAsync`). Every test seeds a trivial **1-step linear** workflow (`SampleRoundTripE2ETests.cs:117-121`). No fan-out topology exists anywhere.
- **Data model** (`src/BaseApi.Service/Persistence/Migrations/20260528074618_InitialCreate.cs`): `workflows.cron_expression` holds the cron (cron is on the **workflow**, not per-step); `steps.processor_id` references the processor **by UUID** (not by name string); `assignments.payload` is `jsonb` (the `{ number, label }` carrier); the DAG lives in junction tables `workflow_entry_steps`, `workflow_assignments`, `step_next_steps`. A 9-step fan-out requires 9 step rows, **8** `step_next_steps` edges (A→B, B→C, C→D1, C→D2, D1→E1, D2→E2, E1→F1, E2→F2), 9 assignment rows, 1 workflow row, 1 entry step (A), 9 workflow_assignments.
- **No clean-state reset mechanism exists.** Close scripts (`scripts/phase-58-close.ps1`, `scripts/phase-62-close.ps1`) do BEFORE/AFTER SHA **comparison** only — never `FLUSHALL`, never `TRUNCATE`/`DELETE` of graph rows, never `docker compose down`. ENV-02's reset is net-new.
- **`processor-badconfig` exclusion is already achievable** — it is behind `profiles: ["badconfig"]` in `compose.yaml:300`, so a default `docker compose up` already excludes it. No compose edit is needed to satisfy ENV-01. The `Processor.BadConfig` *project* must remain in the solution: `GateACompositionE2ETests.cs:88` references `typeof(Processor.BadConfig.BadConfigProcessor)`, so removing the project would break the test build.
- **Stack composition** (compose.yaml): infra `postgres`/`redis`/`rabbitmq`; observability `elasticsearch`/`otel-collector`/`prometheus`; app `baseapi-service`/`orchestrator`/`keeper` (replicas:2); processors `processor-sample` (replicas:2) + profile-gated `processor-badconfig`.
- **Depends on:** Phase 63 (seconds-cron `*/30 * * * * *` must be schedulable + validator-accepted) and Phase 64 (each step's assignment carries the int+string `Step_*` payload the reshaped `SampleProcessor` consumes).

## Requirements

1. **WF-01 — Fan-out workflow seeder**: A seeder creates the fan-out workflow `A→B→C→{D1→E1→F1, D2→E2→F2}` with every step referencing the one shared `processor-sample` and the workflow carrying the `*/30 * * * * *` cron.
   - Current: No seeder exists; only inline 1-step linear workflows are created in E2E tests. Processor referenced by UUID; cron on the workflow row.
   - Target: A runnable seeder artifact creates exactly **1 workflow** (cron `*/30 * * * * *`, entry step = A only), **9 steps** all bound to the single `processor-sample` processor id, and **8 `step_next_steps` edges** forming the DAG (A→B, B→C, C→D1, C→D2, D1→E1, D2→E2, E1→F1, E2→F2). Sinks F1 and F2 have no outgoing edges.
   - Acceptance: After running the seeder once against a clean DB, a query confirms: 1 workflow with `cron_expression = '*/30 * * * * *'`; 9 steps all with the same `processor_id` (the `processor-sample` row); exactly 8 `step_next_steps` rows matching the edge set above; exactly 1 `workflow_entry_steps` row (step A); F1 and F2 each have 0 outgoing edges.

2. **WF-02 — Per-step assignments + idempotency**: Each of the 9 steps has an assignment carrying a `{ number, label:"Step_*" }` payload; the seeder is idempotent via a stable workflow identity.
   - Current: No assignments are seeded for graph workflows; `SeedStepAsync` always creates a fresh step with no uniqueness key; steps/assignments have no natural idempotency key.
   - Target: Each of the 9 steps gets exactly 1 assignment whose `payload` jsonb is `{ "number": <int>, "label": "Step_<node>" }`. The 9 labels are `Step_A, Step_B, Step_C, Step_D1, Step_E1, Step_F1, Step_D2, Step_E2, Step_F2` (the `label` value IS the full `Step_*` token consumed verbatim by Phase 64's `SampleProcessor`). The seeder keys off a **stable sentinel workflow name** (e.g. `v8-fanout-proof`): a second run detects the existing workflow by name and no-ops.
   - Acceptance: Running the seeder **twice** without a reset in between yields exactly 1 workflow / 9 steps / 9 assignments / 8 edges (no duplicates), and the workflow id is **unchanged** across the two runs. Each assignment payload parses as JSON with an integer `number` and a `label` matching `^Step_(A|B|C|D1|E1|F1|D2|E2|F2)$`; the 9 labels are all distinct and cover the full node set.

3. **ENV-01 — Minimal clean-state stack**: The proof runs a minimal stack with the single `processor-sample` processor type (`processor-badconfig` excluded) alongside the full infra + observability tiers.
   - Current: `processor-badconfig` is profile-gated (`profiles: ["badconfig"]`); default `compose up` already excludes it. The other 9 services are always defined.
   - Target: A documented/scripted bring-up command starts exactly these services healthy: `postgres`, `redis`, `rabbitmq`, `otel-collector`, `elasticsearch`, `prometheus`, `orchestrator`, `keeper`, `baseapi-service`, and `processor-sample` — and does **not** start `processor-badconfig`. `processor-sample` runs as **one processor TYPE** (compose `deploy.replicas: 2` is retained so v7.0.0 dedupe/keeper redundancy is still exercised; attribution comes from `correlationId`, not single-instance).
   - Acceptance: After bring-up + health convergence, the running container set contains a `processor-sample` service and **zero** `processor-badconfig` containers; all 10 service types report healthy/ready. The `Processor.BadConfig` project still compiles as part of `dotnet build` (project not removed).

4. **ENV-02 — Deterministic per-run clean-state reset**: A reset routine returns the running stack to a deterministic baseline before each test so a run's metrics/logs are attributable to that run only.
   - Current: No reset exists. Redis is never flushed; graph rows are never deleted; no `compose down`. Close scripts only SHA-compare.
   - Target: A runnable reset artifact that, with the stack **up**: (a) `FLUSHALL` the Redis keyspace, then waits for live `processor-sample`/`keeper` replicas to re-write their `skp:proc:*` liveness keys and for health to re-converge before seeding; (b) resets only the workflow-graph Postgres rows — `workflows`, `steps`, `assignments`, and their junctions (`workflow_entry_steps`, `workflow_assignments`, `step_next_steps`) — while **preserving** `processors` and `config_schemas` rows (already idempotent via source-hash / sentinel-name); (c) asserts the running processor set is exactly `{processor-sample}` (badconfig never started; any stray/orphan processor containers from prior phases removed). The stack stays up across the reset (no full teardown of app containers).
   - Acceptance: Running the reset then a fresh seed yields a clean baseline verifiable as: Redis keyspace contains no leftover `skp:data:*`/`skp:msg:*` run-data keys from a prior run before seeding; the workflow/step/assignment tables contain only the freshly-seeded fan-out (1/9/9); `processors` and `config_schemas` rows are preserved (not deleted); no `processor-badconfig` container is running. Two consecutive reset→seed→observe cycles produce metrics/logs with disjoint `correlationId` sets attributable to each cycle.

## Boundaries

**In scope:**
- An idempotent fan-out workflow seeder (creates 1 workflow / 9 steps / 8 edges / 9 assignments, entry A, cron `*/30 * * * * *`) — a runnable artifact.
- The 9 `{ number, label:"Step_*" }` assignment payloads with the fixed label set.
- A reusable, runnable clean-state reset routine (Redis FLUSHALL + health re-converge wait; reset of workflow-graph Postgres rows + junctions; processor-set assertion).
- A documented/scripted minimal-stack bring-up that excludes `processor-badconfig` and brings the 10 service types healthy.

**Out of scope:**
- The Prometheus + ES analyzer / PASS-FAIL engine that consumes this log+metric shape — Phase 66.
- The fault-injection harness (toxiproxy/container kill/etc.) — Phase 67.
- The 5-minute live observation runs and the 7-scenario resilience proof — Phase 68 (this phase only provides the seeder + reset those harnesses call).
- Any change to `SampleProcessor`/`SampleConfig` or the `{number,label}` consumption logic — locked in Phase 64.
- Removing the `Processor.BadConfig` project from the solution — excluded because `GateACompositionE2ETests.cs` references its type and the project must keep compiling; ENV-01 is about the running container, not the project.
- Per-step cron or per-step processor variation — cron is on the workflow; all 9 steps share the one `processor-sample` (by design).
- Forcing `processor-sample` to a single replica — replicas:2 is retained (decision: "one type, keep 2 replicas").
- Dropping/recreating the Postgres database or volume per run — reset is row-scoped, not a volume nuke.

## Constraints

- **Cron**: the seeded workflow uses exactly `*/30 * * * * *` (6-field seconds cron) — requires the Phase 63 seconds-cron support to be present and validator-accepted.
- **Payload shape**: assignment `payload` jsonb must be `{ "number": <int>, "label": "Step_<node>" }` — the shape Phase 64's typed seam deserializes into `SampleConfig(int Number, string? Label)`; `label` is the verbatim `Step_*` token (no extra prefix).
- **Processor binding**: steps reference `processor-sample` by its UUID (resolved from the seeded processor row, e.g. via the existing GET-or-create-by-source-hash pattern), not by name string.
- **Idempotency key**: stable sentinel workflow name (e.g. `v8-fanout-proof`); re-run detects-and-no-ops (workflow id stable across runs).
- **Reset preserves identity rows**: `processors` and `config_schemas` are NOT deleted by the reset (they are idempotent and re-seeding them is wasteful); only workflow-graph rows + junctions are reset.
- **Redis reset is total** (`FLUSHALL`) followed by a health-reconvergence wait — liveness keys (`skp:proc:*`) are expected to be re-written by live replicas before seeding proceeds (relevant to the v7.0.0 G-62-01 liveness-refresh behavior).
- **Stack stays up** across resets — no `docker compose down`/`-v`; only stray/orphan processor containers are removed.

## Acceptance Criteria

- [ ] Seeder creates exactly 1 workflow (cron `*/30 * * * * *`, entry step A) on a clean DB.
- [ ] Seeder creates 9 steps, all bound to the single `processor-sample` processor id.
- [ ] Seeder creates exactly 8 `step_next_steps` edges matching A→B, B→C, C→D1, C→D2, D1→E1, D2→E2, E1→F1, E2→F2; F1 and F2 have 0 outgoing edges.
- [ ] Seeder creates 9 assignments, each `payload` = `{ "number": <int>, "label": "Step_<node>" }`; labels = exactly {Step_A, Step_B, Step_C, Step_D1, Step_E1, Step_F1, Step_D2, Step_E2, Step_F2}.
- [ ] Running the seeder twice (no reset) leaves 1/9/9/8 rows and an unchanged workflow id (idempotent by sentinel name).
- [ ] Minimal-stack bring-up starts the 10 service types healthy and starts ZERO `processor-badconfig` containers.
- [ ] `dotnet build` still compiles `Processor.BadConfig` (project not removed).
- [ ] Reset performs `FLUSHALL` and waits for liveness/health re-convergence before seeding.
- [ ] Reset deletes workflow/step/assignment rows + their junctions, and PRESERVES `processors` + `config_schemas` rows.
- [ ] Reset asserts the running processor set is exactly `{processor-sample}` (no badconfig; orphans removed).
- [ ] Two consecutive reset→seed cycles yield disjoint `correlationId` sets with no leftover prior-run `skp:data:*`/`skp:msg:*` keys at seed time.

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                                 |
|--------------------|-------|------|--------|-----------------------------------------------------------------------|
| Goal Clarity       | 0.85  | 0.75 | ✓      | Topology, idempotency, two-artifact deliverable all fixed             |
| Boundary Clarity   | 0.80  | 0.70 | ✓      | Deliverable form locked; badconfig project-vs-container split clear   |
| Constraint Clarity | 0.82  | 0.65 | ✓      | Redis FLUSHALL+heal, row-scoped reset, idempotency key, cron, payload |
| Acceptance Criteria| 0.80  | 0.70 | ✓      | 11 pass/fail checks incl. exact row counts + label set                |
| **Ambiguity**      | 0.18  | ≤0.20| ✓      | All dimensions above minimum                                          |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

| Round | Perspective        | Question summary                                  | Decision locked                                                                 |
|-------|--------------------|---------------------------------------------------|--------------------------------------------------------------------------------|
| 1     | Researcher/Boundary| What artifacts does Phase 65 ship?                | Seeder + clean-state reset, BOTH as runnable scripted artifacts (67/68 consume) |
| 1     | Boundary Keeper    | "exactly one processor-sample" — replicas or type?| One processor TYPE (sample only); keep `deploy.replicas: 2`; attribution by correlationId |
| 1     | Boundary Keeper    | Clean-state Postgres reset scope?                 | Reset only workflow/step/assignment + junctions; preserve processors + schemas |
| 2     | Constraint         | Redis reset granularity (stack stays up)?         | `FLUSHALL`, then wait for liveness/health to self-heal before seeding           |
| 2     | Constraint         | "leftover processor containers removed" = ?       | Assert running set == {processor-sample}; badconfig never started; remove orphans only; stack stays up |
| 2     | Acceptance         | WF-02 idempotency contract when run twice?        | Stable identity by sentinel workflow name; 2nd run no-ops; counts 1/9/9/8, id unchanged |

---

*Phase: 65-fan-out-workflow-seeder-clean-state-stack*
*Spec created: 2026-06-14*
*Next step: /gsd-discuss-phase 65 — implementation decisions (seeder artifact form: PowerShell vs C# tool vs E2E fixture; reset script structure; exact `number` seed values; bring-up command wiring)*
