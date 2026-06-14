# Phase 65: Fan-Out Workflow Seeder & Clean-State Stack — Research

**Researched:** 2026-06-14
**Domain:** .NET 8 / C# orchestration engine — REST seeding contracts (Postgres L3 + Redis L2 + RabbitMQ), PowerShell docker/redis/psql infra harness, Docker Compose bring-up
**Confidence:** HIGH (all findings grounded in live source at cited file:line; no external libraries to verify)

## Summary

Phase 65 ships three artifacts, all of which reuse machinery that already exists in the repo — no new API surface, no compose edits, no processor changes. The seeder is a **C# RealStack E2E fixture** that extends the four `internal static` HTTP seed helpers in `SampleRoundTripE2ETests.cs:341-431`; the reset is a **PowerShell script** mirroring the `scripts/phase-NN-close.ps1` docker/redis/psql conventions; the bring-up is a **PowerShell script** running `docker compose up -d` against the default profile (badconfig already excluded by `profiles: ["badconfig"]` at `compose.yaml:300`).

The single most important contract discovery: **an Assignment binds to its Step directly via a non-nullable `StepId` FK** (`AssignmentCreateDto.StepId`, `AssignmentEntity.StepId`, migration column `assignments.step_id`), AND the workflow lists those assignment ids in `WorkflowCreateDto.AssignmentIds` → the `workflow_assignments` junction. So each of the 9 `{number,label}` assignments needs BOTH a step-bound create and inclusion in the workflow's `AssignmentIds`. DAG edges wire cleanest via **reverse-topological create** using `StepCreateDto.NextStepIds` (sinks F1/F2 first), because every `step_next_steps` FK is `OnDelete(Restrict)` and the `next_step_id` must already exist at create time.

**Primary recommendation:** Reverse-topo step create (F1,F2 → E1,E2 → D1,D2 → C → B → A) with `NextStepIds` populated at create; create one step-bound assignment per step then a single workflow carrying `EntryStepIds=[A]`, `AssignmentIds=[all 9]`, `CronExpression="*/30 * * * * *"`; idempotency via GET-list-`/api/v1/workflows`-match-by-sentinel-Name `v8-fanout-proof` and no-op on hit. Reset uses the migration's `Down()` drop order as the FK-safe DELETE order, connecting `docker compose exec -T postgres psql -U postgres -d stepsdb -c "..."`.

## User Constraints (from CONTEXT.md)

### Locked Decisions

- **D-01:** Seeder = a **C# RealStack E2E fixture** in `tests/BaseApi.Tests`, NOT PowerShell/console. Extends/reuses `SeedProcessorAsync`/`SeedStepAsync`/`SeedWorkflowAsync`/`SeedConfigSchemaAsync` (`SampleRoundTripE2ETests.cs:341-431`), adding fan-out topology + per-step assignments.
- **D-02:** Invoked via `dotnet test --filter` (e.g. `~FanOutSeeder`), the way close gates invoke `[Trait("Category","RealStack")]` facts. This is the runnable artifact 67/68 call.
- **D-03:** Fixture is **self-verifying** — queries the DB back and asserts SPEC counts inline (1 workflow w/ cron + entry A; 9 steps same processor_id; 8 edges; 9 assignments w/ payload regex).
- **D-04:** Fixture **runs itself twice without reset** and asserts WF-02 idempotency: 1/9/9/8 unchanged + workflow id stable.
- **D-05:** Reset = **standalone PowerShell** `scripts/phase-65-reset.ps1` (separate from seeder), matching the 18-script `phase-NN-close.ps1` convention.
- **D-06:** Reset sequence: (1) `docker exec sk-redis redis-cli FLUSHALL`; (2) heal-wait; (3) `psql` DELETE of workflow-graph rows + junctions in FK-safe order PRESERVING `processors`+`config_schemas`; (4) `docker ps` assert running set == `{processor-sample}`, remove orphans. Stack stays up — NO `compose down`, NO `-v`.
- **D-07:** Heal-wait gates on liveness keys reappearing — poll `docker exec sk-redis redis-cli --scan skp:proc:*` until ≥1 fresh `processor-sample` instance key re-written, bounded timeout, fail loud. (Container-readiness probe rejected — container can be ready before re-writing L2.)
- **D-08:** Assignment `number` = distinct per-node int in SPEC label order: A=1,B=2,C=3,D1=4,E1=5,F1=6,D2=7,E2=8,F2=9. `label` is verbatim `Step_*` token.
- **D-09:** Bring-up = standalone PowerShell `scripts/phase-65-up.ps1` — `docker compose up -d` (default profile excludes badconfig, no compose edit), wait 10 service types healthy, assert 0 badconfig. `processor-sample` keeps `deploy.replicas: 2`.

### Claude's Discretion

- DAG edge-wiring mechanism (reverse-topo create vs flat-then-PUT) — **this research recommends reverse-topo create** (see Architecture).
- Assignment→step attachment mechanism — **resolved below: `AssignmentCreateDto.StepId` direct FK + `WorkflowCreateDto.AssignmentIds` junction.**
- Sentinel workflow name — `v8-fanout-proof` (example; any stable string).
- FK-safe DELETE ordering, exact psql invocation, heal-wait timeout/poll values — **resolved below.**
- Health-readiness probe specifics in bring-up (which endpoint/`docker inspect` field) — **resolved below.**

### Deferred Ideas (OUT OF SCOPE)

None deferred in discussion. Roadmap-scoped to later phases: analyzer/PASS-FAIL engine → Phase 66; fault-injection harness → Phase 67; live 5-min observation runs / 7-scenario proof → Phase 68. Also out: any `SampleProcessor`/`SampleConfig` change (Phase 64-locked); removing `Processor.BadConfig` project; per-step cron/processor; single-replica processor-sample; DB/volume drop per run.

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| WF-01 | Seeder creates fan-out `A→B→C→{D1→E1→F1, D2→E2→F2}`, all steps → one `processor-sample`, workflow cron `*/30 * * * * *` | `StepCreateDto(Name,Version,Desc,ProcessorId,NextStepIds,EntryCondition)` (`StepDtos.cs:13-19`) wires edges via `step_next_steps`; `WorkflowCreateDto.CronExpression` validated 6-field by `CronFieldForm` (`WorkflowDtoValidator.cs:50-53,64-71`); processor resolved by-source-hash GET (`SampleRoundTripE2ETests.cs:341-374`) |
| WF-02 | Each step has `{number,label:"Step_*"}` assignment; seeder idempotent by sentinel workflow name | `AssignmentCreateDto(Name,Version,Desc,StepId,Payload)` (`AssignmentDtos.cs:11-16`), Payload jsonb validated as JSON (`AssignmentDtoValidator.cs:47-65`); idempotency via GET-list `/api/v1/workflows` match-by-Name (mirrors `SeedConfigSchemaAsync` `:383-400`) |
| ENV-01 | Minimal stack: 10 service types healthy, `processor-badconfig` excluded | `processor-badconfig` `profiles:["badconfig"]` (`compose.yaml:300`); 10 services each have healthcheck (`compose.yaml`); default `compose up -d` excludes profile-gated services |
| ENV-02 | Deterministic per-run reset (Redis FLUSHALL + heal-wait + row-scoped Postgres delete preserving processors/schemas + processor-set assert), stack stays up | Migration FK graph (`20260528074618_InitialCreate.cs`) gives FK-safe DELETE order; liveness key `skp:proc:{procId:D}:{instanceId}` (`L2ProjectionKeys.cs:44-51`); heartbeat Interval default 10s (`ProcessorLivenessOptions.cs:21-22`); docker/redis/psql patterns (`phase-62-close.ps1`) |

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Seed workflow graph (rows + junctions) | API / Backend (REST POST) | DB (Postgres L3) | Seeder writes ONLY via `/api/v1/{processors,steps,assignments,workflows}` — the REST layer owns FK + cron + payload validation; never raw SQL inserts |
| Self-verify seeded counts | Test fixture (Npgsql read) | DB (Postgres L3) | Read-back assertions query snake_case tables directly via `NpgsqlConnection` (`StepsIntegrationTests.cs:73-79` precedent) — REST read DTOs don't surface junction rows (`StepReadDto` v1 returns `NextStepIds=null`) |
| Redis reset | Infra script (redis-cli) | Redis L2 | `docker exec sk-redis redis-cli FLUSHALL` — shell-native, no app code |
| Heal-wait (liveness reconverge) | Infra script (redis-cli --scan) | Redis L2 (written by live processor replicas) | The reset reads the exact L2 state `ProcessorLivenessValidator` checks (`skp:proc:*`); live replicas re-write it on their heartbeat loop |
| Postgres row reset | Infra script (psql) | DB (Postgres L3) | `docker compose exec -T postgres psql -d stepsdb -c "DELETE..."` — destructive, must respect FK Restrict order |
| Processor-set assertion / orphan removal | Infra script (docker ps/rm) | Docker daemon | `docker compose ps` + container removal — shell-native |
| Bring-up health convergence | Infra script (docker compose ps --format json) | Docker daemon | Per-service `.Health -eq 'healthy'` poll (`phase-62-close.ps1:292-309` precedent) |

## Standard Stack

No new libraries. Everything is in-repo. The seeder fixture compiles against:

| Component | Source | Purpose |
|-----------|--------|---------|
| `SampleRoundTripE2ETests` seed helpers | `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs:341-431` | `internal static` `SeedProcessorAsync`/`SeedConfigSchemaAsync`/`SeedStepAsync`/`SeedWorkflowAsync` — reuse as-is, add an assignment helper + fan-out wiring |
| `RealStackWebAppFactory` | same file `:443-536` | in-process WebApi pointed at host stack (Postgres 5433 / Redis 6380 / RMQ 5673 / otel 4317), net-zero teardown discipline |
| `Npgsql` (`NpgsqlConnection`/`NpgsqlCommand`) | already referenced (`StepsIntegrationTests.cs:7,73-79`) | direct DB read-back for self-verification of junction/row counts |
| `StackExchange.Redis` (`ConnectionMultiplexer`) | already referenced (`SampleRoundTripE2ETests.cs:11,220`) | liveness-key poll if the fixture itself needs it (heal-wait is primarily the PS reset's job) |
| FluentValidation / Cronos | server-side, already wired | cron + payload validation on POST (no client work) |

**Reset/bring-up scripts:** pure PowerShell + `docker`/`docker compose`/`redis-cli`/`psql` (all already used across the 18 `phase-NN-close.ps1` scripts). No installs.

**Version verification:** N/A — no package additions. Container images are pinned in `compose.yaml` (postgres:17-alpine, redis:7.4.9-alpine, rabbitmq:4.1.8, elasticsearch:8.15.5, otel-collector:0.152.0, prometheus:v3.11.3) and unchanged by this phase.

## Architecture Patterns

### System Architecture Diagram (seed + reset data flow)

```
 [dotnet test --filter ~FanOutSeeder]            [scripts/phase-65-reset.ps1]        [scripts/phase-65-up.ps1]
            |                                              |                                   |
            v                                              v                                   v
  RealStackWebAppFactory (in-proc WebApi)       1. docker exec sk-redis              docker compose up -d
   pointed at host stack                            redis-cli FLUSHALL                 (default profile)
            |                                              |                                   |
   GET /processors/by-source-hash/{h} ----> resolve  2. HEAL-WAIT: poll                wait: docker compose ps
     (or POST if absent)        procId(sample)         redis-cli --scan skp:proc:*       --format json per svc
            |                                          until >=1 fresh sample             .Health == 'healthy'
   GET /workflows  --match Name "v8-fanout-proof"?       instance key (live replica         x10 service types
            |  HIT -> no-op (idempotent)                  re-wrote it, <=~30s)                   |
            |  MISS -> build graph:                          |                            assert 0 badconfig
            |    reverse-topo step create (NextStepIds) 3. psql -d stepsdb DELETE        containers
            |      F1,F2 -> E1,E2 -> D1,D2 -> C -> B -> A     in Down() FK order:
            |    per step: POST /assignments {StepId,         step_next_steps ->
            |      payload:{number,label:"Step_*"}}           workflow_entry_steps ->
            |    POST /workflows {EntryStepIds:[A],            workflow_assignments ->
            |      AssignmentIds:[9], cron:"*/30 * * * * *"}   assignments -> steps ->
            |                                                  workflows
            v                                          (PRESERVE processors + config_schemas)
   SELF-VERIFY via Npgsql:                           4. docker compose ps:
     SELECT count(*) FROM workflows/steps/              assert running set == {processor-sample},
     assignments/step_next_steps/workflow_entry_steps   docker rm any orphan processor-* container
   assert 1/9/9/8/1; run AGAIN -> id stable             (stack stays UP)
            |
            v
   PASS = WF-01 + WF-02 proven in one fact
```

### Pattern 1: Assignment → Step → Workflow binding (the core contract)

**What:** An assignment is a **leaf entity owned by a step**. Binding is two-sided:
1. `AssignmentCreateDto.StepId` (non-null FK) attaches the assignment to its step at create (`AssignmentDtos.cs:11-16`; `AssignmentEntity.StepId` non-nullable, FK `fk_assignment_step_id` `OnDelete(Restrict)`, `AssignmentEntity.cs:26`, migration `:141-147`).
2. `WorkflowCreateDto.AssignmentIds` lists assignment ids → `workflow_assignments` junction (`WorkflowDtos.cs:24`; `WorkflowService.SyncJunctionsAsync` `:96-105`).

So the seeder, per node: `POST /api/v1/assignments` with `{ Name, Version, Description:null, StepId:<that step>, Payload:"{\"number\":N,\"label\":\"Step_X\"}" }`, collect all 9 ids, then `POST /api/v1/workflows` with `AssignmentIds=[all 9]`.

**Example (new helper to add, mirroring existing helpers):**
```csharp
// Source: derived from AssignmentDtos.cs:11-16 + SeedStepAsync pattern (SampleRoundTripE2ETests.cs:402-415)
internal static async Task<Guid> SeedAssignmentAsync(
    HttpClient client, Guid stepId, int number, string label, CancellationToken ct)
{
    var payload = JsonSerializer.Serialize(new { number, label }); // {"number":1,"label":"Step_A"}
    var dto = new AssignmentCreateDto(
        Name: $"asg-{label}-{Guid.NewGuid():N}",
        Version: "1.0.0",
        Description: null,
        StepId: stepId,
        Payload: payload);
    var resp = await client.PostAsJsonAsync("/api/v1/assignments", dto, ct);
    resp.EnsureSuccessStatusCode();
    var read = await resp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);
    return read!.Id;
}
```

### Pattern 2: DAG edge wiring — reverse-topological create with `NextStepIds`

**What:** `StepCreateDto.NextStepIds` (`StepDtos.cs:18`) populates `step_next_steps` rows in `StepService.SyncJunctionsAsync` (`StepService.cs:65-74`), one row `{StepId, NextStepId}` per id. Both FKs (`fk_step_next_steps_step_id`, `fk_step_next_steps_next_step_id`) are `OnDelete(Restrict)` (migration `:159-170`) → **the next-step rows must already exist when a step is created with `NextStepIds`**.

**Why reverse-topo (recommended over flat-then-PUT):** create sinks first so every `NextStepId` referenced is already persisted:
```
order: F1, F2  (NextStepIds: null — sinks, 0 outgoing)
       E1 (Next:[F1]), E2 (Next:[F2])
       D1 (Next:[E1]), D2 (Next:[E2])
       C  (Next:[D1, D2])         <- the fan-out node, 2 outgoing
       B  (Next:[C])
       A  (Next:[B])              <- entry step
```
Yields exactly 8 edges: A→B, B→C, C→D1, C→D2, D1→E1, D2→E2, E1→F1, E2→F2. No PUT needed. (A `StepUpdateDto`/PUT exists and does remove-and-replace of junctions — `StepService.cs:54-63` — but flat-then-PUT requires 9 creates + up-to-7 PUTs and is strictly more API calls; reverse-topo is the cleaner default Claude's-discretion choice.)

**When to use flat-then-PUT instead:** only if a future topology has a cycle (none here — this is a DAG).

### Pattern 3: Idempotency by sentinel workflow name (GET-list-match)

**What:** Workflows have **no uniqueness constraint** on `name` (migration `workflows` table has only `pk_workflows`, `:49-52`). So idempotency is GET-list-and-match, identical to `SeedConfigSchemaAsync` (`:383-400`):
```csharp
// Source: SeedConfigSchemaAsync pattern, SampleRoundTripE2ETests.cs:386-391
var all = await client.GetFromJsonAsync<List<WorkflowReadDto>>("/api/v1/workflows", ct);
var existing = all!.FirstOrDefault(w => w.Name == "v8-fanout-proof");
if (existing is not null) return existing.Id; // 2nd run no-ops; id stable (WF-02)
// else: build the full graph, POST workflow with Name="v8-fanout-proof"
```
**Processor idempotency** is stronger — `uq_processor_source_hash` unique index (migration `:241-245`) + the `by-source-hash` GET-or-create already in `SeedProcessorAsync` (`:341-374`). Reuse verbatim; the reset PRESERVES processor rows so the genuine-hash row the live container heartbeats against stays stable.

### Pattern 4: Self-verification via direct Npgsql read

**What:** REST read DTOs do NOT surface junction rows (`StepReadDto.NextStepIds` is always `null` on read, `StepDtos.cs:36-44`; same for `WorkflowReadDto.EntryStepIds`/`AssignmentIds`, `WorkflowDtos.cs:42-56`). Verification MUST query snake_case tables directly:
```csharp
// Source: StepsIntegrationTests.cs:71-79 (the established junction-verification strategy)
await using var conn = new NpgsqlConnection(HostPostgres); // Host=localhost;Port=5433;Database=stepsdb;...
await conn.OpenAsync(ct);
// count edges:
"SELECT count(*) FROM step_next_steps"                          // expect 8
"SELECT count(*) FROM steps"                                    // expect 9 (clean DB)
"SELECT count(DISTINCT processor_id) FROM steps"                // expect 1
"SELECT count(*) FROM assignments"                              // expect 9
"SELECT count(*) FROM workflows WHERE cron_expression = '*/30 * * * * *'" // expect 1
"SELECT count(*) FROM workflow_entry_steps"                     // expect 1 (step A)
// payload regex + sink-zero-outgoing assertions per node as needed
```
Table/column names are **snake_case** (migration `20260528074618_InitialCreate.cs`: `steps`, `workflows`, `assignments`, `step_next_steps`, `workflow_entry_steps`, `workflow_assignments`, columns `step_id`/`next_step_id`/`workflow_id`/`assignment_id`/`processor_id`/`cron_expression`/`payload`). The `HostPostgres` constant already exists in the fixture (`SampleRoundTripE2ETests.cs:474-475`).

### Anti-Patterns to Avoid

- **Raw SQL inserts in the seeder:** would bypass cron/payload/FK validation and drift from the REST contract the rest of the system uses. Seed via REST only.
- **Reusing `"Steps"` PascalCase table names** (a stale reference appears in STATE.md line 378): the LIVE schema is snake_case (`steps`). Verified against the migration AND `StepsIntegrationTests.cs:76` (`FROM step_next_steps`).
- **Using container-readiness as the heal-wait signal** (CONTEXT D-07 explicitly rejects this): a container reports `/health/ready` before it has re-written its `skp:proc:*` L2 key after FLUSHALL. Gate on the key, not the container.
- **`docker compose down` in the reset:** SPEC/CONTEXT forbid it (stack stays up). Reset is row+keyspace-scoped only.
- **5-field cron:** `* * * * *` is the OLD test cron (`SampleRoundTripE2ETests.cs:121`). Phase 65 MUST use 6-field `*/30 * * * * *` (validated as `CronFormat.IncludeSeconds`, `WorkflowDtoValidator.cs:64-71`).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Processor identity resolution | New by-name lookup | `GET /api/v1/processors/by-source-hash/{hash}` GET-or-create (`SampleRoundTripE2ETests.cs:341-374`) | Steps reference processor by UUID; the genuine embedded hash is the stable key the live container also resolves; `uq_processor_source_hash` guards duplicates |
| FK-safe delete order | Hand-reasoned order | Migration `Down()` drop order (`20260528074618_InitialCreate.cs:271-288`) | EF already computed the topological FK order; mirror it for row DELETEs |
| Cron field-form detection | New parser | Server-side `CronFieldForm` + validator (`WorkflowDtoValidator.cs`) — just POST the string | Validator accepts 6-field `*/30 * * * * *`; no client parsing needed |
| Compose health polling | Custom HTTP probes per service | `docker compose ps <svc> --format json` → `.Health -eq 'healthy'` (`phase-62-close.ps1:292-309`) | Each service already declares a healthcheck; compose surfaces the verdict |
| Redis/psql/docker invocation shape | New connection code | `docker exec sk-redis redis-cli ...` / `docker compose exec -T postgres psql -U postgres ...` (`phase-62-close.ps1:314,320,326`) | Proven container names + auth; sk-redis/sk-rabbitmq are named, postgres is not (use `compose exec`) |

**Key insight:** Phase 65 is almost entirely *composition of existing seams*. The only genuinely new logic is (a) the fan-out topology + assignment loop in the fixture, (b) the destructive psql DELETE + heal-wait poll in the reset (net-new — close scripts only SHA-compare, never delete), and (c) the bring-up health-wait loop.

## Runtime State Inventory

> Phase 65 is greenfield-additive for *artifacts*, but ENV-02 (the reset) is a state-mutation operation, so this inventory is load-bearing for the reset's correctness.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data (L3 Postgres) | Workflow-graph rows: `workflows`, `steps`, `assignments` + junctions `step_next_steps`, `workflow_entry_steps`, `workflow_assignments`. PRESERVED: `processors`, `config_schemas`(=`schemas`). | Reset DELETEs the 6 graph tables in FK order; PRESERVES processors+schemas (idempotent, re-seeding wasteful) |
| Stored data (L2 Redis) | `skp:data:{guid}` (execution output, 5s TTL via `Processor__ExecutionDataTtl`), `skp:msg:{guid}` (slot-array index), `skp:{wfId}` / `skp:{wfId}:{stepId}` (orchestration roots), `skp:proc:{procId:D}` (instance-index SET) + `skp:proc:{procId:D}:{instanceId}` (per-instance liveness) | Reset `FLUSHALL`s ALL of it, then heal-waits for `skp:proc:*` to be re-written by live replicas before seeding (the only keys that MUST reconverge before a Start would 422) |
| Live service config | n8n/Datadog/etc: **None — verified.** This is a self-contained docker-compose stack; no external SaaS config holds the renamed strings. | none |
| OS-registered state | **None — verified.** No Task Scheduler / launchd / pm2 registrations; everything runs in compose containers. | none |
| Secrets/env vars | `.env`: `POSTGRES_DB=stepsdb`, `POSTGRES_USER=postgres`, `POSTGRES_PASSWORD=postgres` (read for psql auth). RMQ guest/guest in compose env. No secret rename. | Reset reads these for the psql/redis invocation only; no mutation |
| Build artifacts | `Processor.Sample.dll` / `Processor.BadConfig.dll` embedded `SourceHash` (read by `phase-62-close.ps1:139-168` + the fixture `:111-113`). Stale only if processor source changes — out of scope this phase. | none (Phase 64 locked the processor) |

**The canonical reset question:** after FLUSHALL + row-DELETE, what runtime state still references the prior run? → The live `processor-sample` replicas keep re-writing `skp:proc:*` (intended — heal-wait depends on it). The processor + schema ROWS survive (intended — preserve). Everything else is wiped → a fresh seed produces a clean, per-run-attributable baseline.

## Common Pitfalls

### Pitfall 1: Heal-wait timeout budget too tight (FLUSHALL → key reappearance)
**What goes wrong:** Reset FLUSHALLs, then seeds immediately; the Start liveness gate 422s because `skp:proc:*` hasn't been re-written yet.
**Why it happens:** the live `processor-sample` replicas re-write the per-instance liveness key on their **heartbeat loop, Interval default 10s** (`ProcessorLivenessOptions.cs:21-22`, no override in `compose.yaml` for processor-sample → 10s applies). The key carries a derived TTL `max(interval×2, Ttl)` = `max(20, 30)` = 30s (`ProcessorLivenessOptions.cs:30-36`), and the reader's staleness math is `timestamp + interval×3` (mirrored in the fixture poll, `SampleRoundTripE2ETests.cs:240`). FLUSHALL does NOT restart the container, so no 30s `start_period` cold-start is incurred — the next heartbeat tick (≤10s) re-writes it.
**How to avoid:** poll `redis-cli --scan skp:proc:*` (filter for a `processor-sample` instance key) with a **bounded timeout of ~60s** (6× the 10s heartbeat — generous slack for tick jitter), poll interval ~1-2s, **fail loud** on non-convergence (CONTEXT D-07). 60s is safe and matches the fixture's `LivenessPollTimeoutMs = 90_000` ceiling order-of-magnitude.
**Warning signs:** a subsequent seed's `/orchestration/start` returns 422; or `--scan skp:proc:*` empty after 60s → container down or contract-version mismatch.

### Pitfall 2: Assignment bound to step but omitted from workflow's AssignmentIds (or vice-versa)
**What goes wrong:** 9 assignment rows exist and 9 `assignments.step_id` FKs are set, but `workflow_assignments` has 0 rows (or wrong count) → the workflow doesn't "carry" the assignments.
**Why it happens:** the binding is **two independent writes** — `AssignmentCreateDto.StepId` (`AssignmentDtos.cs:14`) AND `WorkflowCreateDto.AssignmentIds` (`WorkflowDtos.cs:24`). Forgetting the second leaves the junction empty.
**How to avoid:** collect all 9 assignment ids from the create loop, pass the full list as `AssignmentIds` on the single workflow POST. Self-verify `SELECT count(*) FROM workflow_assignments` == 9.
**Warning signs:** `workflow_assignments` count ≠ 9 in the read-back.

### Pitfall 3: psql connecting to the wrong database (db-list vs stepsdb)
**What goes wrong:** the reset DELETE runs against the default `postgres` maintenance DB and silently affects nothing.
**Why it happens:** the close scripts use `psql -U postgres -lqt` (database LIST — `phase-62-close.ps1:314`), which does NOT connect to `stepsdb`. The reset must add `-d stepsdb`.
**How to avoid:** `docker compose exec -T postgres psql -U postgres -d stepsdb -c "DELETE FROM ..."`. DB name `stepsdb` is from `.env` `POSTGRES_DB=stepsdb`; the fixture's connection string confirms `Database=stepsdb` (`SampleRoundTripE2ETests.cs:475`). `postgres` is NOT a named container (no `container_name` in compose) → use `docker compose exec`, NOT `docker exec sk-postgres`.
**Warning signs:** row counts unchanged after the reset's DELETE.

### Pitfall 4: Orphan-processor removal hitting the live processor-sample replicas
**What goes wrong:** the "remove stray/orphan processor containers" step kills the 2 healthy `processor-sample` replicas.
**Why it happens:** `processor-sample` has NO `container_name` (replicas:2 → compose-generated names like `<project>-processor-sample-1/2`); a naive `docker rm` matching `processor*` would catch them.
**How to avoid:** the assertion is "running set == `{processor-sample}` type". Enumerate via `docker compose ps processor-sample --format json` (the supported replicas) and only `docker rm -f` containers whose compose service is a *processor type OTHER THAN* `processor-sample` (e.g. a leftover `sk-processor-badconfig` from a prior `--profile badconfig` run). Badconfig IS named `sk-processor-badconfig` (`compose.yaml:304`) so it's targetable by exact name.
**Warning signs:** processor-sample replica count drops to 0; subsequent seeds 422 on liveness.

### Pitfall 5: 6-field cron silently treated as 5-field
**What goes wrong:** cron `*/30 * * * * *` rejected or misscheduled.
**Why it happens:** pre-Phase-63 only 5-field was accepted. The CONTEXT depends on Phase 63's `CronFieldForm` being present.
**How to avoid:** verified present — `CronFieldForm.IsValidFieldCount` accepts 5 or 6 tokens, `IsSecondsForm` maps 6→`CronFormat.IncludeSeconds` (`CronFieldForm.cs:14-23`), and `WorkflowDtoValidator.BeValidStandardCron` uses it (`WorkflowDtoValidator.cs:64-71`). Just POST `"*/30 * * * * *"` as `CronExpression`. No client-side handling.
**Warning signs:** workflow POST returns 400 with "CronExpression must be a valid 5- or 6-field..." → Phase 63 not deployed in the running WebApi image.

## Code Examples

### FK-safe reset DELETE (psql, in migration Down() order)
```sql
-- Source: 20260528074618_InitialCreate.cs:271-288 (Down() drop order = FK-safe delete order)
-- Run via: docker compose exec -T postgres psql -U postgres -d stepsdb -c "<each statement>"
DELETE FROM step_next_steps;        -- self-ref junction (Restrict both FKs)
DELETE FROM workflow_assignments;   -- junction (Cascade on wf, Restrict on assignment)
DELETE FROM workflow_entry_steps;   -- junction (Cascade on wf, Restrict on step)
DELETE FROM assignments;            -- leaf (Restrict FK to steps)
DELETE FROM workflows;              -- parent of the two wf junctions
DELETE FROM steps;                  -- referenced by assignments + step_next_steps
-- PRESERVE: processors, schemas (config_schemas) — NOT deleted (CONTEXT D-06)
```
A single transactional statement is cleaner and avoids partial-delete-on-error:
```sql
BEGIN;
DELETE FROM step_next_steps; DELETE FROM workflow_assignments; DELETE FROM workflow_entry_steps;
DELETE FROM assignments; DELETE FROM workflows; DELETE FROM steps;
COMMIT;
```

### Heal-wait poll (PowerShell, reset)
```powershell
# Source: liveness key shape L2ProjectionKeys.cs:44 (skp:proc:{procId:D}:{instanceId});
#         heartbeat interval ProcessorLivenessOptions.cs:21-22 (10s default)
docker exec sk-redis redis-cli FLUSHALL | Out-Null
$deadline = (Get-Date).AddSeconds(60)   # 6x the 10s heartbeat — generous, fail-loud
$healed = $false
while ((Get-Date) -lt $deadline) {
    $keys = @(docker exec sk-redis redis-cli --scan --pattern 'skp:proc:*' | Where-Object { $_ -match ':' -and $_ -notmatch '^skp:proc:[^:]+$' })
    # a per-instance key (has a trailing :{instanceId}) means a live replica re-wrote it
    if ($keys.Count -ge 1) { $healed = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $healed) { Write-Host "Liveness did not reconverge in 60s after FLUSHALL — aborting." -ForegroundColor Red; exit 2 }
```
*(Note: `skp:proc:{procId:D}` is the index SET; `skp:proc:{procId:D}:{instanceId}` is the per-instance key. The regex distinguishes the per-instance key by its second `:`-segment after `proc:`.)*

### Bring-up health-wait (PowerShell)
```powershell
# Source: phase-62-close.ps1:291-309 (NDJSON-per-replica parse, all-instances-healthy)
docker compose up -d | Out-Null   # default profile — badconfig excluded (profiles:["badconfig"])
$services = @('postgres','redis','rabbitmq','otel-collector','elasticsearch','prometheus','orchestrator','keeper','baseapi-service','processor-sample')
foreach ($svc in $services) {
    # poll up to ~start_period+slack; otel-collector has no in-container healthcheck (compose.yaml:69-79) -> treat 'running' as ready
    $instances = @(docker compose ps $svc --format json | Where-Object { $_ -match '\S' } | ForEach-Object { $_ | ConvertFrom-Json })
    $health = if ($instances.Count -eq 0) { 'not-running' }
              else { $u=@($instances | Where-Object { $_.Health -ne 'healthy' }); if ($u.Count -gt 0) { $u[0].Health } else { 'healthy' } }
    if ($health -ne 'healthy' -and $svc -ne 'otel-collector') { <# keep polling / fail loud after deadline #> }
}
# assert 0 badconfig:
$bad = @(docker ps --filter 'name=sk-processor-badconfig' --format '{{.Names}}')
if ($bad.Count -ne 0) { Write-Host "badconfig container running — ENV-01 violated." -ForegroundColor Red; exit 2 }
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| 1-step linear workflow seed (`SeedWorkflowAsync` + single step) | 9-step fan-out + 9 assignments | Phase 65 (this) | The seeder is the first multi-node topology in the codebase |
| 5-field cron `* * * * *` (`SampleRoundTripE2ETests.cs:121`) | 6-field seconds cron `*/30 * * * * *` | Phase 63 | `CronFieldForm` + validator accept 6-field; seeder uses it |
| Assignments NOT seeded for graph workflows (only step+workflow) | per-step `{number,label}` assignment carried via StepId FK + workflow AssignmentIds | Phase 64/65 | `SampleConfig(int Number, string? Label)` deserializes the payload |
| Close scripts SHA-compare only (never destructive) | Reset FLUSHALLs + row-DELETEs + heal-waits | Phase 65 (this) | ENV-02's destructive reset is net-new; no existing script template for the DELETE half |
| Flat liveness key `skp:{procId}` | Per-replica `skp:proc:{procId:D}:{instanceId}` + index SET `skp:proc:{procId:D}` | Phase 59-61 (v7.0.0) | Heal-wait must scan the `skp:proc:*` namespace, not a flat key |

**Deprecated/outdated:**
- Flat `skp:{procId}` liveness key — deleted Phase 61 D-11 (`L2ProjectionKeys.cs:14,39-43`). Do NOT poll for it.
- `"Steps"` PascalCase table name in STATE.md line 378 — stale; live schema is snake_case `steps`.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `processor-sample` has no `Processor__Interval` override → liveness heartbeat = 10s default (so heal-wait ≤~10s typical, 60s budget ample) | Pitfall 1 | LOW — confirmed no override in `compose.yaml:265-291`; if an env var were added elsewhere the 60s budget still absorbs up to a 30s startup-interval |
| A2 | 60s heal-wait timeout is sufficient (FLUSHALL does not restart containers, so no 30s `start_period` cold-start) | Pitfall 1, heal-wait example | LOW — FLUSHALL is a keyspace op, not a container restart; verified containers stay up. If a replica happened to be mid-restart for another reason, fail-loud surfaces it |
| A3 | A single combined-transaction `DELETE` across the 6 graph tables won't deadlock against live orchestrator/keeper reads | Code Examples | LOW — reset runs between test runs (no active orchestration); if contention arises, the per-statement form is the fallback |
| A4 | `docker compose ps <svc> --format json` emits `.Health` field for services with healthchecks (used by bring-up wait) | Code Examples | NONE — directly reused from `phase-62-close.ps1:297-303` which is proven |

**Note:** No `[ASSUMED]`-tagged claims affect a *locked decision* — all assumptions are about timeout/robustness margins, not contract shapes. Contract shapes (DTO fields, table names, FK order, key formats) are all `[VERIFIED: codebase]`.

## Open Questions

1. **Should the seeder fixture FLUSHALL/reset before seeding, or assume the reset script already ran?**
   - What we know: CONTEXT separates seeder (dotnet test) from reset (PS script); 67/68 call reset THEN seeder. The seeder's self-verify asserts counts (1/9/9/8) which presume a clean graph.
   - What's unclear: whether the fixture should tolerate a non-clean DB (idempotency no-op handles the workflow, but stray pre-existing steps would inflate `SELECT count(*) FROM steps`).
   - Recommendation: have the fixture's self-verify scope its counts to the seeded workflow's graph (join through `workflow_entry_steps`/reachability) OR document that the seeder presumes a reset-clean DB (the harness contract). Planner's call — lean toward presuming reset-clean (matches the 67/68 call order) with a clear assertion message.

2. **Idempotency-twice run: same fixture process or two `dotnet test` invocations?**
   - What we know: D-04 says "runs itself twice without a reset" and asserts id stable.
   - Recommendation: do it in ONE `[Fact]` — call the seed routine twice in-process, capture both workflow ids, assert equal + re-verify 1/9/9/8. Simpler and deterministic than two test invocations.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| docker / docker compose v2 | bring-up, reset, seeder (host stack) | ✓ (assumed — entire project is compose-based) | — | none (hard requirement) |
| `sk-redis` container + redis-cli | reset FLUSHALL + heal-wait | ✓ (compose `redis` → `container_name: sk-redis`) | redis:7.4.9-alpine | none |
| postgres container + psql | reset DELETE, seeder self-verify | ✓ (compose `postgres`, NO container_name → `docker compose exec`) | postgres:17-alpine | none |
| .NET 8 SDK (`dotnet test`) | seeder fixture | ✓ (project is net8.0) | net8.0 | none |
| Npgsql | seeder self-verify | ✓ (already referenced in tests) | — | none |
| Host ports 5433/6380/5673/4317 | RealStackWebAppFactory overrides | ✓ (compose port maps) | — | none |

**Missing dependencies with no fallback:** none identified — all required tooling is the project's existing stack.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (v3 — `TestContext.Current.CancellationToken`, `[Fact]`, `[Trait]`) + FluentValidation server-side |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~FanOutSeeder" --configuration Release` |
| Full suite command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release` |

The seeder IS a test — `[Trait("Category","RealStack")]` fact, filtered by `~FanOutSeeder` (matching the close-gate convention of running RealStack facts live; hermetic runs exclude via `Category!=RealStack`, `SampleRoundTripE2ETests.cs:67`). The reset + bring-up are PS scripts validated by their own exit codes + assertions.

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command / Signal | File Exists? |
|--------|----------|-----------|----------------------------|-------------|
| WF-01 | 1 workflow (cron, entry A), 9 steps same processor, 8 edges | RealStack E2E (self-verify) | `dotnet test --filter ~FanOutSeeder`; Npgsql `SELECT count(*) FROM workflows WHERE cron_expression='*/30 * * * * *'`=1, `FROM steps`=9, `count(DISTINCT processor_id)`=1, `FROM step_next_steps`=8, `FROM workflow_entry_steps`=1 | ❌ Wave 0 (new fixture) |
| WF-02 | 9 `{number,label}` assignments + idempotency (twice → id stable, 1/9/9/8) | RealStack E2E (self-verify, run-twice) | same fact: assert each `payload` JSON has int `number` + `label ~ ^Step_(A\|B\|C\|D1\|E1\|F1\|D2\|E2\|F2)$`, all 9 distinct; run seed twice, assert workflow id equal + counts unchanged | ❌ Wave 0 |
| ENV-01 | 10 services healthy, 0 badconfig | PS script assertion | `scripts/phase-65-up.ps1` exit 0; per-svc `.Health=='healthy'`; `docker ps --filter name=sk-processor-badconfig` count==0 | ❌ Wave 0 (new script) |
| ENV-02 | FLUSHALL + heal-wait + row-DELETE preserving processors/schemas + processor-set assert | PS script assertion + post-reset probe | `scripts/phase-65-reset.ps1` exit 0; post-reset Redis has 0 `skp:data:*`/`skp:msg:*`; graph tables empty; `processors`/`schemas` count unchanged; running set=={processor-sample} | ❌ Wave 0 (new script) |

### Measurable Signals (per requirement)
- **WF-01/WF-02 row counts:** `NpgsqlConnection` `SELECT count(*)` against `workflows`/`steps`/`assignments`/`step_next_steps`/`workflow_entry_steps`/`workflow_assignments` (snake_case, `StepsIntegrationTests.cs:73-79` pattern).
- **Edge-set exactness:** `SELECT step_id, next_step_id FROM step_next_steps` → assert the 8-tuple set (map step ids back to node labels via the assignment payloads or step names).
- **Sink zero-outgoing:** `SELECT count(*) FROM step_next_steps WHERE step_id = <F1>` = 0 and `<F2>` = 0.
- **Idempotency id-equality:** capture `WorkflowReadDto.Id` on both seed calls; `Assert.Equal`.
- **Redis clean-state probe (ENV-02):** `redis-cli --scan --pattern 'skp:data:*'` count==0 and `'skp:msg:*'`==0 before seeding; `'skp:proc:*'` ≥1 per-instance key after heal-wait.
- **docker ps assertion (ENV-01/ENV-02):** `docker compose ps processor-sample --format json` → 2 healthy replicas; `docker ps --filter name=sk-processor-badconfig` → 0.

### Sampling Rate
- **Per task commit:** `dotnet test --filter ~FanOutSeeder` (the seeder fact) + `pwsh -File scripts/phase-65-reset.ps1` / `phase-65-up.ps1` dry-run where applicable.
- **Per wave merge:** full `dotnet test` suite (the seeder fact must stay green among RealStack facts; the `Processor.BadConfig` project must still compile — `GateACompositionE2ETests.cs:88`).
- **Phase gate:** seeder fact green + both scripts exit 0 + the SPEC's 11 acceptance checks demonstrably satisfied (manual UAT or a wrapper that runs up → reset → seed → assert).

### Wave 0 Gaps
- [ ] New fixture file `tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs` (or extend `SampleRoundTripE2ETests`) — covers WF-01/WF-02, `[Trait("Category","RealStack")]`, reuses `RealStackWebAppFactory`.
- [ ] New `SeedAssignmentAsync` helper (Pattern 1) added alongside the existing 4 `internal static` helpers.
- [ ] `scripts/phase-65-reset.ps1` — FLUSHALL + heal-wait + psql DELETE + processor-set assert.
- [ ] `scripts/phase-65-up.ps1` — `compose up -d` + 10-service health wait + 0-badconfig assert.
- [ ] (no framework install — xUnit/Npgsql/StackExchange.Redis already referenced).

## Security Domain

> `security_enforcement` is not set in config — treated as enabled. This phase is a self-contained dev-stack seeder/reset with no internet-facing surface, no new auth path, and no new data persistence shape. Most ASVS categories N/A.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | dev stack, guest/guest RMQ, no auth tier in scope |
| V3 Session Management | no | no sessions |
| V4 Access Control | no | no new endpoints (seeder uses existing CRUD) |
| V5 Input Validation | yes (existing) | Assignment `Payload` JSON-validated + 1 MB cap (`AssignmentDtoValidator.cs:47-65`); cron validated (`WorkflowDtoValidator.cs`); StepId/EntryStepIds non-empty Guid checks. Seeder relies on these — no new validation needed |
| V6 Cryptography | no | no crypto; SourceHash is identity, not a secret |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| SQL injection via reset DELETE | Tampering | Reset DELETEs are static literal statements (no interpolated user input) — `psql -c "DELETE FROM step_next_steps;"`. Seeder writes go through parameterized EF Core / validated DTOs. |
| Destructive reset hitting wrong DB/data | Tampering / DoS (dev) | Scope to `-d stepsdb`, static table list PRESERVING processors/schemas, NO `compose down`/`-v` (CONTEXT D-06). Reset is dev-only (never run against a prod-like volume). |
| Plaintext dev creds in `.env`/compose | Info Disclosure | Accepted dev-only posture (documented across phases; guest/guest, postgres/postgres). Not changed by this phase. |

## Sources

### Primary (HIGH confidence — live codebase)
- `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs:341-431` — the 4 reusable seed helpers + RealStackWebAppFactory host overrides (`:443-536`), HostPostgres/HostRedis constants (`:433,473-475`), liveness poll (`:218-255`).
- `src/BaseApi.Service/Features/Assignment/{AssignmentDtos.cs:11-16, AssignmentEntity.cs:24-28, AssignmentDtoValidator.cs:33-65}` — assignment StepId FK + jsonb Payload contract.
- `src/BaseApi.Service/Features/Step/{StepDtos.cs:13-19, StepService.cs:44-75, StepNextSteps.cs}` — NextStepIds → step_next_steps junction.
- `src/BaseApi.Service/Features/Workflow/{WorkflowDtos.cs:20-26, WorkflowService.cs:52-106, WorkflowDtoValidator.cs:50-71, WorkflowEntrySteps.cs, WorkflowAssignments.cs}` — EntryStepIds/AssignmentIds junctions + cron validation.
- `src/BaseApi.Service/Persistence/Migrations/20260528074618_InitialCreate.cs` — full table/FK/index schema; `Down()` (`:271-288`) = FK-safe delete order; `uq_processor_source_hash` (`:241-245`).
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:31-65` — liveness key shapes `skp:proc:{procId:D}:{instanceId}` + index `skp:proc:{procId:D}`, data `skp:data:{guid}`, msg `skp:msg:{guid}`.
- `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs:21-51` — heartbeat Interval 10s / StartupInterval 30s / Ttl 30s defaults.
- `src/Messaging.Contracts/CronFieldForm.cs:10-24` — 6-field → IncludeSeconds detection (Phase 63 seam).
- `compose.yaml` — all 10 service definitions + healthchecks; `processor-badconfig` `profiles:["badconfig"]` (`:300`); `processor-sample`/`keeper` `deploy.replicas:2`; container_names `sk-redis`/`sk-rabbitmq`/`sk-elasticsearch`/`sk-prometheus`/`sk-orchestrator`/`sk-otel-collector`/`sk-processor-badconfig` (postgres/keeper/processor-sample/baseapi-service unnamed).
- `scripts/phase-62-close.ps1:139-309` — docker/redis-cli/psql invocation patterns, NDJSON-per-replica health parse, embedded-SourceHash read.
- `tests/BaseApi.Tests/Integration/StepsIntegrationTests.cs:7,40-79` — Npgsql direct junction-count verification pattern + snake_case table confirmation.
- `.env` — `POSTGRES_DB=stepsdb`, `POSTGRES_USER=postgres`, `POSTGRES_PASSWORD=postgres`.
- `src/BaseApi.Service/Features/Processor/ProcessorController.cs:37,53` — `by-source-hash/{sourceHash}` GET route.

### Secondary (MEDIUM)
- `.planning/STATE.md` (lines 29,378,1535) — v8.0.0 milestone goal (fan-out topology, cron, 7 scenarios), Npgsql-verification precedent. Note: line 378's `"Steps"` PascalCase is stale vs the live snake_case schema.

### Tertiary (LOW)
- None — all findings tool-verified against source.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new libraries; all reuse is cited at file:line.
- Architecture (binding, edge-wiring, idempotency, verification): HIGH — DTOs, services, migration, and an existing verification test all read directly.
- Pitfalls: HIGH — each grounded in a concrete contract fact (key shape, interval default, db name, FK order, cron seam).
- Heal-wait timeout value: MEDIUM — 60s is a reasoned margin (6× the verified 10s heartbeat), not a measured reconvergence time; A1/A2 flag it.

**Research date:** 2026-06-14
**Valid until:** 2026-07-14 (stable — internal contracts; revisit if Phase 63/64 images or the liveness interval config change before the v8.0.0 proof runs)
