# Phase 65: Fan-Out Workflow Seeder & Clean-State Stack - Context

**Gathered:** 2026-06-14
**Status:** Ready for planning

<domain>
## Phase Boundary

Ship two runnable artifacts for the v8.0.0 proof, plus a minimal-stack bring-up:

1. **Seeder** — builds the fan-out workflow `A→B→C→{D1→E1→F1, D2→E2→F2}` (1 workflow, 9 steps, 8 edges, 9 assignments; entry A; one shared `processor-sample`; cron `*/30 * * * * *`; each step assignment payload `{ number, label:"Step_*" }`), idempotent by a stable sentinel workflow name.
2. **Reset** — returns the running stack to a deterministic, per-run-attributable baseline before each test (Redis FLUSHALL + health re-converge; row-scoped Postgres reset of workflow-graph rows + junctions, preserving `processors`/`config_schemas`; processor-set assertion). Stack stays up.
3. **Bring-up** — starts the minimal stack (10 service types, `processor-badconfig` excluded) healthy.

This phase delivers the seeder + reset + bring-up that Phases 67/68 harnesses call. It does NOT build the analyzer/PASS-FAIL engine (Phase 66), the fault-injection harness (Phase 67), or the live 5-min observation runs (Phase 68).

</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**4 requirements are locked.** See `65-SPEC.md` for full requirements, boundaries, and acceptance criteria (WF-01, WF-02, ENV-01, ENV-02 — 11 pass/fail acceptance checks incl. exact row counts + label set).

Downstream agents MUST read `65-SPEC.md` before planning or implementing. Requirements are not duplicated here.

**In scope (from SPEC.md):**
- An idempotent fan-out workflow seeder (1 wf / 9 steps / 8 edges / 9 assignments, entry A, cron `*/30 * * * * *`) — a runnable artifact.
- The 9 `{ number, label:"Step_*" }` assignment payloads with the fixed label set.
- A reusable, runnable clean-state reset routine (Redis FLUSHALL + health re-converge wait; reset of workflow-graph Postgres rows + junctions; processor-set assertion).
- A documented/scripted minimal-stack bring-up that excludes `processor-badconfig` and brings the 10 service types healthy.

**Out of scope (from SPEC.md):**
- The Prometheus + ES analyzer / PASS-FAIL engine — Phase 66.
- The fault-injection harness — Phase 67.
- The 5-minute live observation runs / 7-scenario resilience proof — Phase 68.
- Any change to `SampleProcessor`/`SampleConfig` or the `{number,label}` consumption logic — locked in Phase 64.
- Removing the `Processor.BadConfig` project (must keep compiling — `GateACompositionE2ETests.cs:88` references its type).
- Per-step cron or per-step processor variation; forcing `processor-sample` to a single replica (replicas:2 retained); dropping/recreating the Postgres DB or volume per run.

</spec_lock>

<decisions>
## Implementation Decisions

### Seeder artifact form (WF-01, WF-02)
- **D-01:** The seeder is a **C# RealStack E2E fixture** in `tests/BaseApi.Tests`, NOT a PowerShell script or a standalone console tool. It **extends/reuses the existing `internal static` HTTP seed helpers** (`SeedProcessorAsync`, `SeedStepAsync`, `SeedWorkflowAsync`, `SeedConfigSchemaAsync`) in `SampleRoundTripE2ETests.cs:341-431`, adding fan-out topology + per-step assignments. Rationale: those helpers already encode the REST API contracts and the GET-or-create idempotency (by-source-hash for processors, sentinel-Name for schemas); reimplementing them in PowerShell would duplicate contract knowledge and risk writer↔reader drift (this codebase's anti-desync discipline — cf. Phase 21 `L2ProjectionKeys` hoist, Phase 63 shared cron detector).
- **D-02:** Invocation is via `dotnet test --filter` (e.g. `~FanOutSeeder`), exactly how the existing close gates invoke `[Trait("Category","RealStack")]` E2E facts. This is the "runnable artifact" Phases 67/68 call to seed.
- **D-03:** The fixture is **self-verifying** — after seeding it queries the DB back and asserts the SPEC acceptance criteria inline: 1 workflow with `cron_expression = '*/30 * * * * *'` + entry step = A; 9 steps all bound to the single `processor-sample` `processor_id`; exactly 8 `step_next_steps` edges matching `A→B, B→C, C→D1, C→D2, D1→E1, D2→E2, E1→F1, E2→F2` (F1, F2 have 0 outgoing); 9 assignments each `payload` matching `{ "number": <int>, "label": "^Step_(A|B|C|D1|E1|F1|D2|E2|F2)$" }` with all 9 labels distinct and covering the node set.
- **D-04:** The fixture **runs itself twice without a reset** and asserts the WF-02 idempotency contract: counts stay 1/9/9/8 (no duplicates) and the **workflow id is unchanged** across the two runs (detected-and-no-op'd by the stable sentinel workflow name). Phase 65 thus delivers both the artifact AND its WF-01/WF-02 proof in one runnable fact.

### Reset routine (ENV-02)
- **D-05:** The reset is a **standalone PowerShell script** `scripts/phase-65-reset.ps1`, separate from the seeder. Rationale: all three reset operations (Redis FLUSHALL, Postgres row-delete, docker container assertion/orphan-removal) are shell-native and match the established 18-script `scripts/phase-NN-close.ps1` harness convention. The 67/68 harness calls `phase-65-reset.ps1` then the `dotnet test` seeder.
- **D-06:** Reset sequence: (1) `docker exec sk-redis redis-cli FLUSHALL`; (2) **heal-wait** (see D-07); (3) `psql` DELETE of only the workflow-graph rows + junctions — `step_next_steps`, `workflow_entry_steps`, `workflow_assignments`, `assignments`, `steps`, `workflows` (in FK-safe order) — explicitly **PRESERVING** `processors` and `config_schemas` rows (they are idempotent via source-hash / sentinel-name; re-seeding them is wasteful and unnecessary); (4) `docker ps` assert the running processor set is exactly `{processor-sample}`, removing any stray/orphan processor containers from prior phases. The stack stays up — NO `docker compose down`, NO `-v`, NO DB/volume drop.

### Heal-wait signal (ENV-02)
- **D-07:** After FLUSHALL, the reset gates "ready to seed" on **liveness keys reappearing** — poll `docker exec sk-redis redis-cli --scan skp:proc:*` until ≥1 fresh `processor-sample` instance key has been re-written by the live v7.0.0 per-replica liveness loop (the G-62-01 liveness-refresh self-heal), with a **bounded timeout that fails loud** on non-convergence. This is the exact L2 state the orchestration-start liveness gate (`ProcessorLivenessValidator`) reads, so it directly proves a subsequent seed+activate won't 422. A coarser container/HTTP health probe was rejected because a container can report ready before it has re-written its L2 liveness key. An optional final WebAPI `/ready` check is acceptable but the liveness-key poll is the gating signal.

### Assignment payload `number` values (WF-02)
- **D-08:** Each step's `{ number, label }` assignment payload carries a **distinct per-node int in the SPEC label order**: `Step_A=1, Step_B=2, Step_C=3, Step_D1=4, Step_E1=5, Step_F1=6, Step_D2=7, Step_E2=8, Step_F2=9`. Rationale: the value is immaterial to proof correctness (Phase 64's `SampleProcessor` adds `Random.Shared.Next(0,100)` and logs the sum; Phase 66 aggregates by correlationId+label), but distinct base addends make each step's log line self-distinguishing for debugging the per-correlationId trace. `label` is the verbatim `Step_*` token (Phase 64 D-10 — no extra prefix).

### Bring-up (ENV-01)
- **D-09:** Bring-up is a **standalone PowerShell script** `scripts/phase-65-up.ps1`, separate from the reset (ENV-01 "start once" vs ENV-02 "reset before each run, stack stays up" are deliberately distinct per SPEC). It runs `docker compose up -d` (default profile — `processor-badconfig` is already gated behind `profiles: ["badconfig"]` in `compose.yaml:300`, so it is excluded with **no compose edit**), waits for all 10 service types (`postgres`, `redis`, `rabbitmq`, `otel-collector`, `elasticsearch`, `prometheus`, `orchestrator`, `keeper`, `baseapi-service`, `processor-sample`) to report healthy/ready, and asserts **zero** `processor-badconfig` containers. `processor-sample` keeps `deploy.replicas: 2` (one TYPE, two replicas — attribution comes from `correlationId`, not single-instance). Programmatically callable by the 67/68 harness.

### Claude's Discretion
- **DAG edge-wiring mechanism** — whether steps are created in reverse-topological order (sinks F1/F2 first) with `StepCreateDto.NextStepIds` populated at create time, or created flat then PUT to add edges. `StepCreateDto` supports `NextStepIds` (→ `step_next_steps`), so reverse-topo create is the cleaner default, but the planner may choose either provided the 8-edge set is exactly correct.
- **Assignment→step attachment mechanism** — exact use of `AssignmentController` / `workflow_assignments` junction to bind each assignment's `{number,label}` payload to its step. Resolve from the live API contract.
- **Sentinel workflow name** — `v8-fanout-proof` is the SPEC's example; exact string is Claude's discretion provided it is stable and used for the detect-and-no-op idempotency.
- **FK-safe DELETE ordering** in the reset, the exact `psql` connection/auth invocation, and the heal-wait timeout/poll-interval values — handle robustly; not user decisions.
- **Health-readiness probe specifics** in bring-up (which endpoint/`docker inspect` field per service type) — planner's call.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked requirements (read first)
- `.planning/phases/65-fan-out-workflow-seeder-clean-state-stack/65-SPEC.md` — Locked requirements WF-01/WF-02/ENV-01/ENV-02, boundaries, constraints, 11 acceptance checks. MUST read before planning.

### Seeder reuse + API contracts
- `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` §341-431 — the existing `internal static` HTTP seed helpers to extend (`SeedProcessorAsync` by-source-hash GET-or-create, `SeedConfigSchemaAsync` by-sentinel-Name, `SeedStepAsync`, `SeedWorkflowAsync`); also §117-121 (current trivial 1-step linear seed) and the `HostRedis`/host-override + net-zero teardown discipline at §433+.
- `src/BaseApi.Service/Features/Step/` — `StepCreateDto.NextStepIds` is the edge-wiring seam (→ `step_next_steps`).
- `src/BaseApi.Service/Features/Assignment/AssignmentController.cs` + `AssignmentService.cs` — the assignment API for the 9 `{number,label}` payloads.
- `src/BaseApi.Service/Persistence/Migrations/20260528074618_InitialCreate.cs` — data model: `workflows.cron_expression` (cron on the workflow), `steps.processor_id` (UUID, not name), `assignments.payload` (jsonb), junctions `workflow_entry_steps` / `workflow_assignments` / `step_next_steps`.

### Reset / bring-up harness convention
- `scripts/phase-62-close.ps1` and `scripts/phase-58-close.ps1` — the proven PowerShell harness patterns for `docker exec ... redis-cli`, `psql`, container health pre-flight, and the `skp:proc:*` / `skp:data:*` / `skp:msg:*` keyspace semantics the reset must respect (note: close scripts only SHA-compare; they never FLUSHALL/DELETE — the reset's destructive ops are net-new).
- `compose.yaml` §300 — `processor-badconfig` behind `profiles: ["badconfig"]` (default `up` excludes it); the full 10-service-type stack composition; `processor-sample` `deploy.replicas: 2`.

### Carried-forward dependency decisions
- `.planning/phases/63-seconds-granularity-cron/63-CONTEXT.md` — the `*/30 * * * * *` 6-field seconds cron is validator-accepted + schedulable (the seeder attaches it).
- `.planning/phases/64-processor-work-structured-logging/64-CONTEXT.md` — `SampleConfig(int Number, string? Label)` deserialized from the assignment payload; `label` is the verbatim `Step_*` token; `ProcessAsync` adds `Random.Shared.Next(0,100)` and logs `Step_<label>` + sum.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `SampleRoundTripE2ETests.cs:341-431` seed helpers — directly reusable for processor (by-source-hash idempotent), schema (by-sentinel-name), step, and workflow creation; extend with assignments + fan-out `NextStepIds`.
- The `[Trait("Category","RealStack")]` + host-override (`localhost:5433/6380/5673/4317`) + net-zero teardown pattern — the fixture template for a RealStack-targeting seeder invokable by `dotnet test --filter`.
- 18 `scripts/phase-NN-close.ps1` scripts — the PowerShell template for docker/redis-cli/psql infra ops, container health pre-flight, and keyspace handling.

### Established Patterns
- **GET-or-create idempotency**: processors keyed by embedded source-hash (`uq_processor_source_hash`), schemas by sentinel Name (no uniqueness constraint → match-or-create). Phase 65 adds the **workflow** keyed by sentinel Name for WF-02 detect-and-no-op.
- **Cron on the workflow row** (not per-step); **processor referenced by UUID** (resolved from the seeded row), not by name.
- **DAG via junctions**: 9 step rows + 8 `step_next_steps` edges + 1 `workflow_entry_steps` (A) + 9 `workflow_assignments`.
- **v7.0.0 liveness self-heal**: live replicas re-write `skp:proc:{procId}:{instanceId}` keys on their startup/heartbeat loop — the reset's heal-wait depends on this (G-62-01 liveness-refresh).

### Integration Points
- REST API (`/api/v1/processors`, `/steps`, `/assignments`, `/workflows`) — the seeder's write surface.
- `ProcessorLivenessValidator` orchestration-start gate reads `skp:proc:*` — the reset heal-wait targets the exact state this gate checks.
- `compose.yaml` default profile — bring-up's start surface (badconfig auto-excluded).
- Postgres workflow-graph tables + junctions — the reset's row-scoped delete surface.

</code_context>

<specifics>
## Specific Ideas

- Fixed label set (verbatim, Phase 64-consumed): `Step_A, Step_B, Step_C, Step_D1, Step_E1, Step_F1, Step_D2, Step_E2, Step_F2`.
- Fixed `number` mapping: A=1, B=2, C=3, D1=4, E1=5, F1=6, D2=7, E2=8, F2=9.
- Fixed edge set (8): `A→B, B→C, C→D1, C→D2, D1→E1, D2→E2, E1→F1, E2→F2`; sinks F1, F2 have 0 outgoing.
- Cron: exactly `*/30 * * * * *`. Entry step: A only. Sentinel workflow name example: `v8-fanout-proof`.
- The 10 healthy service types for bring-up: `postgres`, `redis`, `rabbitmq`, `otel-collector`, `elasticsearch`, `prometheus`, `orchestrator`, `keeper`, `baseapi-service`, `processor-sample`.

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope. (Analyzer/PASS-FAIL → Phase 66; fault harness → Phase 67; live 5-min runs → Phase 68 — all already scoped in the roadmap.)

</deferred>

---

*Phase: 65-fan-out-workflow-seeder-clean-state-stack*
*Context gathered: 2026-06-14*
