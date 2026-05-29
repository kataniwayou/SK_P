# Roadmap: Steps API

## Milestones

- [x] **v3.2.0 Steps API MVP** — Phases 1-11 (shipped 2026-05-28) — see [milestones/v3.2.0-ROADMAP.md](milestones/v3.2.0-ROADMAP.md)
- [ ] **v3.3.0 Orchestration L3 → L1 → L2 Build Pipeline** — Phases 12-16

## Phases

<details>
<summary>v3.2.0 Steps API MVP (Phases 1-11) — SHIPPED 2026-05-28</summary>

11 phases / 41 plans / 142 integration facts GREEN × 3 consecutive runs. Full phase details, decisions, and execution narrative archived to [milestones/v3.2.0-ROADMAP.md](milestones/v3.2.0-ROADMAP.md).

- [x] Phase 1: Repository Scaffold (3/3 plans) — 2026-05-26
- [x] Phase 2: Postgres + Docker Compose (2/2 plans) — 2026-05-26
- [x] Phase 3: EF Core Persistence Base (2/2 plans) — 2026-05-27
- [x] Phase 4: Cross-Cutting Middleware + Error Handling (2/2 plans) — 2026-05-27
- [x] Phase 5: Observability + Health Probes (2/2 plans) — 2026-05-27
- [x] Phase 6: Validation + Mapping Base (2/2 plans) — 2026-05-27
- [x] Phase 7: Generic HTTP Base + Composition Root (2/2 plans) — 2026-05-27
- [x] Phase 8: Entity Build-Out + Migrations + Docker Runtime + Tests (8/8 plans) — 2026-05-28
- [x] Phase 9: Processor.GetBySourceHash + Orchestration Start/Stop (3/3 plans) — 2026-05-28
- [x] Phase 10: Remove SchemaId on AssignmentEntity, add ConfigSchemaId on ProcessorEntity (5/5 plans) — 2026-05-28
- [x] Phase 11: Migrate Prometheus + Elasticsearch from compose stack sk2_1 to sk_p (10/10 plans) — 2026-05-28

</details>

### v3.3.0 Orchestration L3 → L1 → L2 Build Pipeline (Phases 12-16)

- [ ] **Phase 12: Redis infra + composition + healthcheck + DI registration** — Land Redis as a new compose-stack tier, wire `AddBaseApiRedis`, ship `RedisFixture` test infra
- [ ] **Phase 13: OrchestrationService split + L3 fetch + L1 build** — Decompose OrchestrationService into seams + load L1 snapshot (no validation, no Redis write yet)
- [ ] **Phase 14: Validation gates (DFS + schema-edge + payload-config-schema)** — Cycle detection, missing-step, schema-edge compatibility, and Payload↔ConfigSchema validators in mandatory order
- [ ] **Phase 15: L2 Redis projection write + Stop existence check** — RedisProjectionWriter ships 3 keyspaces, Start contract finalized, Stop becomes Redis existence-check
- [ ] **Phase 16: Idempotency + concurrency + L1 cleanup + 3-GREEN closeout** — Idempotency/concurrency regression facts, end-to-end happy path, v3.3.0 close gate

## Phase Details

### Phase 12: Redis infra + composition + healthcheck + DI registration
**Goal**: Redis is a healthy compose-stack tier wired into the API's DI graph with test fixtures and dead-Redis resilience proven, while the Phase 5 HEALTH-01..05 contracts remain untouched.
**Depends on**: Nothing in v3.3.0 (builds on shipped v3.2.0 infrastructure)
**Requirements**: INFRA-REDIS-01, INFRA-REDIS-02, INFRA-REDIS-03, INFRA-REDIS-04, INFRA-REDIS-05, INFRA-REDIS-06, INFRA-COMP-01, INFRA-COMP-02, INFRA-COMP-03, INFRA-COMP-04, TEST-REDIS-01, TEST-REDIS-02, TEST-REDIS-03, TEST-REDIS-04, TEST-REDIS-05
**Success Criteria** (what must be TRUE):
  1. `docker compose ps` shows `redis:7.4.x-alpine` healthy (`redis-cli ping` healthcheck PASS) alongside Postgres / Elasticsearch / Prometheus / OTel Collector.
  2. `AddBaseApiRedis` resolves at API startup — `IConnectionMultiplexer` is registered as Singleton, `IDatabase` is resolvable per-call, and `RedisProjectionOptions` binds the `Redis:*` config section.
  3. With Redis stopped, `GET /health/live` returns 200 AND `GET /health/ready` returns 200 (soft dependency) AND the v3.2.0 HEALTH-01..05 acceptance facts still pass byte-for-byte.
  4. `Phase8WebAppFactory` (or its v3.3.0 successor) boots with the `RedisFixture` attached; per-test-class `KeyPrefix = "test:cls-{Guid:N}:"` isolation works and `RedisFixture.DisposeAsync` SCAN-asserts zero residual keys (fail-loud on violation; `FLUSHDB` never called).
  5. Phase-close gate extended: `redis-cli --scan | sort | sha256sum` BEFORE = AFTER across the full integration suite, in addition to the v3.2.0 `psql \l` SHA-256 invariant.
**Plans:** 8 plans
Plans:
- [x] 12-01-PLAN.md — CPM pin StackExchange.Redis 2.13.1 + csproj references + OBSERV-REDIS-01 negative-grep
- [x] 12-02-PLAN.md — compose.yaml redis service block (7.4.x-alpine, sk-redis, 6380:6379, ping healthcheck) + baseapi-service depends_on + env var
- [x] 12-03-PLAN.md — appsettings.json (Docker-internal) + appsettings.Development.json (host-side) + Redis defaults section (KeyPrefix=skp:, JsonOptions=default)
- [x] 12-04-PLAN.md — RedisProjectionOptions POCO + RedisServiceCollectionExtensions.AddBaseApiRedis + composition-root call #7
- [ ] 12-05-PLAN.md — RedisFixture + RedisFixtureFacts + Phase8WebAppFactory in-place D-07/D-08 extension (TEST-REDIS-01..03)
- [ ] 12-06-PLAN.md — HealthDeadRedisFixture + 2 soft-dep acceptance facts (INFRA-REDIS-06 + TEST-REDIS-05)
- [ ] 12-07-PLAN.md — BaseApiCompositionFacts + RedisProjectionOptionsBindingFacts + ComposeYamlFacts + AppsettingsFacts (CI-enforceable INFRA-COMP-01..04 + INFRA-REDIS-01..05 guards)
- [ ] 12-08-PLAN.md — Phase-close gate scripts (PowerShell + Bash) + 3-GREEN cadence + psql\\l + redis-cli --scan SHA-256 BEFORE=AFTER + STATE.md close entry (TEST-REDIS-04)
**v3.2.0 invariants MUST NOT regress**: HEALTH-01..05 (live never touches external state; ready tag discipline); INFRA-06 (compose stack still boots end-to-end); Phase 11 D-03 (no traces backend — do NOT add `OpenTelemetry.Instrumentation.StackExchangeRedis`); Mapperly RMG007/RMG012/RMG020/RMG089 build-error discipline; byte-identical `psql \l` SHA-256 `0d98b0de…0aac127`.

### Phase 13: OrchestrationService split + L3 fetch + L1 build
**Goal**: `OrchestrationService` becomes a thin orchestrator; a transient `WorkflowGraphSnapshot` (L1) is loaded from Postgres on every Start request and disposed deterministically — no validation gates, no Redis write yet.
**Depends on**: Phase 12 (DI graph + RedisFixture must exist so the service can declare future Redis dependencies without breaking the build)
**Requirements**: ORCH-SPLIT-01, ORCH-SPLIT-02, ORCH-SPLIT-03, ORCH-SPLIT-04, L1-BUILD-01, L1-BUILD-02, L1-BUILD-03, L1-BUILD-04, L1-BUILD-05
**Success Criteria** (what must be TRUE):
  1. `OrchestrationService.StartAsync` body shrinks to ~80 LOC and delegates to 4 new internal seams (`IWorkflowGraphLoader`, plus 3 validation seams + `IRedisProjectionWriter` registered as no-op or skipped this phase) inside `Features/Orchestration/{Loading,Validation,Projection}/`.
  2. `OrchestrationService.StopAsync` exists as a separate method (Start/Stop semantics no longer share `ValidateWorkflowIdsAsync`; the Phase 9 D-09 "refactor when they diverge" note is honored).
  3. For a known multi-workflow Start request, the L1 snapshot contains all expected entities — flat `Dictionary<Guid, EntityDto>` for Workflows, Assignments, Steps, Processors, Schemas — loaded via `BaseDbContext.Set<>().AsNoTracking()...Where(x => ids.Contains(x.Id))` batch queries (the Phase 3 5-method `IRepository<>` surface is unchanged).
  4. `WorkflowGraphSnapshot.Dispose()` runs deterministically on BOTH success AND failure paths (verified by integration test that forces a throw mid-Start and asserts cleanup ran).
  5. Step traversal walks every entry in `Workflow.EntryStepIds[*]` and follows every entry in `StepEntity.NextStepIds[*]` (multi-child fan-out) using a per-traversal `visited` list keyed on StepId.
**Plans**: TBD
**v3.2.0 invariants MUST NOT regress**: Phase 3 5-method `IRepository<>` surface (no `IQueryable<>` leakage); Phase 9 `OrchestrationService` ctor-injection of all 5 entity mappers; Phase 4 X-Correlation-Id propagation through service-layer call chain; Phase 7 `Program.cs` ≤10 non-trivial body-line cap; Mapperly RMG codes; byte-identical `psql \l` SHA-256.

### Phase 14: Validation gates (DFS + schema-edge + payload-config-schema)
**Goal**: A broken workflow Start request returns a deterministic 422 + RFC 7807 at the first failed gate, with the offending entity ids in the error body and L1 cleanup guaranteed even on validation failure. Closes v2-deferred VALID-21 at orchestration-start scope.
**Depends on**: Phase 13 (L1 snapshot is the input to every validator)
**Requirements**: L1-VALIDATE-01, L1-VALIDATE-02, L1-VALIDATE-03, L1-VALIDATE-04, L1-VALIDATE-05, L1-VALIDATE-06, L1-VALIDATE-07, L1-VALIDATE-08, L1-VALIDATE-09, L1-VALIDATE-10
**Success Criteria** (what must be TRUE):
  1. A cycle-containing workflow returns 422 with the offending stepId chain in the RFC 7807 error body; cycle detection uses ITERATIVE DFS with an explicit stack (no recursion — `StackOverflowException` is uncatchable by `IExceptionHandler`).
  2. A workflow whose Step references a missing `NextStepId` returns 422 with `(parentStepId, missingChildId)`; a Step with `null` `NextStepId` is accepted as terminal.
  3. A workflow with a schema-edge mismatch (`parent.Processor.OutputSchemaId != child.Processor.InputSchemaId`) returns 422 with the offending `(parentStepId, childStepId)` pair; null-on-either-side (Phase 10 source/sink/unconfigured processor) passes.
  4. An Assignment whose Payload fails JSON Schema validation against its resolved `Step.Processor.ConfigSchema.Definition` returns 422 with the offending `assignmentId`; per-Start `Dictionary<Guid, JsonSchema>` cache parses each schema at most once.
  5. Validation runs in the exact order existence → cycles → schema-edge → Payload↔ConfigSchema (verified by integration tests that supply multi-failure workflows and assert the FIRST gate's 422); L1 cleanup still runs in the `finally` path on every validation failure.
**Plans**: TBD
**v3.2.0 invariants MUST NOT regress**: Phase 8 JSON Schema SSRF lockdown (`SchemaRegistry.Global.Fetch = (_, _) => null` + draft 2020-12 + `<500ms` regression assertion); Phase 4 RFC 7807 + X-Correlation-Id + Postgres SQLSTATE → HTTP mapping; Phase 6 FluentValidation 12 wiring (no `AddFluentValidation`; manual `ValidateAsync`); Assignment-PUT / Assignment-POST remain "valid JSON only" (VALID-21 only closes at orchestration-start); Mapperly RMG codes; byte-identical `psql \l` SHA-256.

### Phase 15: L2 Redis projection write + Stop existence check
**Goal**: After a successful Start, the 3 Redis keyspaces are populated with the locked DTO shapes (`inputDefinition`/`outputDefinition`, `correlationId`, `liveness` defaults); Stop is a Redis-based existence-check that returns 204 / 422 without any DELETE.
**Depends on**: Phase 14 (only validated L1 snapshots may be projected to L2)
**Requirements**: L2-PROJECT-01, L2-PROJECT-02, L2-PROJECT-03, L2-PROJECT-04, L2-PROJECT-05, L2-PROJECT-06, L2-PROJECT-07, ORCH-START-01, ORCH-START-02, ORCH-START-03, ORCH-START-04, ORCH-START-05, ORCH-START-06, ORCH-START-07, ORCH-START-08, ORCH-STOP-01, ORCH-STOP-02, ORCH-STOP-03, ORCH-STOP-04, ORCH-STOP-05, ORCH-STOP-06, ORCH-STOP-07, OBSERV-REDIS-01, OBSERV-REDIS-02, OBSERV-REDIS-03, OBSERV-REDIS-04
**Success Criteria** (what must be TRUE):
  1. After a successful `POST /api/v1/orchestration/start`, `redis-cli GET {prefix}{workflowId:N}` returns the expected JSON shape — `{ entryStepIds[], cron, jobId, liveness{timestamp,interval,status}, correlationId }` — where `correlationId` matches the inbound `X-Correlation-Id` header.
  2. For each step in the workflow, `redis-cli GET {prefix}{workflowId:N}:{stepId:N}` returns `{ entryCondition, processorId, payload, nextStepIds[] }` with `nextStepIds[]` as the FULL list of children from `StepNextSteps` (terminal steps return `[]` not `null`); for each referenced processor, `redis-cli GET {prefix}{processorId:N}` returns `{ inputDefinition, outputDefinition, liveness }` (exact field names — NOT `definitionIn`/`definitionOut`).
  3. `POST /api/v1/orchestration/stop` returns 204 when `EXISTS` returns 1 for every requested `{workflowId}` key in Redis; returns 422 with the missing workflowIds list when ANY key is absent; performs NO DELETE/UNLINK; Stop does NOT touch Postgres.
  4. Redis-side failures (`RedisConnectionException`, timeout) surface as 500 + RFC 7807 + `correlationId` from BOTH Start (`UpsertAsync`) and Stop (`KeyExistsAsync`); L1 cleanup still runs in `finally` on the Start path.
  5. `OpenTelemetry.Instrumentation.StackExchangeRedis` is NOT referenced anywhere in the solution; Redis ops appear in MEL logs with X-Correlation-Id flowing via AsyncLocal (verified by E2E test extending the Phase 11 `SchemasLogsE2ETests` pattern); `IServer.Keys()` and `KEYS` are forbidden in production code (only SCAN-based enumeration allowed).
**Plans**: TBD
**v3.2.0 invariants MUST NOT regress**: Phase 11 D-03 no-traces-backend (`.WithTracing()` stays stripped; no Redis OTel trace instrumentation); Phase 4 `X-Correlation-Id` middleware + RFC 7807 + correlation propagation through OTel to ES (Phase 11 E2E contract); MEL → ES correlation propagation; Mapperly is for entity↔DTO mapping only (L2 DTO → JSON uses `System.Text.Json` directly); INFRA-REDIS-06 soft Redis dependency (CRUD endpoints still serve 200 with Redis down); byte-identical `psql \l` SHA-256.

### Phase 16: Idempotency + concurrency + L1 cleanup + 3-GREEN closeout
**Goal**: Start idempotency, concurrent-Start last-write-wins semantics, Stop idempotency, and end-to-end happy-path are all verified against real Postgres + real Redis; the v3.3.0 phase-close gate (3 consecutive GREEN + `psql \l` SHA-256 BEFORE=AFTER + `redis-cli --scan` SHA-256 BEFORE=AFTER) is satisfied.
**Depends on**: Phase 15 (L2 write + Stop existence check must be in place to run end-to-end facts)
**Requirements**: TEST-REDIS-06, TEST-REDIS-07, TEST-REDIS-08, TEST-REDIS-09
**Success Criteria** (what must be TRUE):
  1. The full Start happy-path integration suite runs against real Postgres + real Redis and asserts all 3 keyspaces via `System.Text.Json` round-trip deserialization; each validation gate failure path (cycle / missing-step / schema-edge / payload-vs-config-schema) returns 422 AND a `SCAN` confirms no L2 keys were written for the failed workflowId.
  2. Start-twice with same WorkflowIds confirms L2 keys reflect the second write (idempotent overwrite); a concurrent-Start regression test (two parallel HTTP requests) documents the last-write-wins / partial-state-interleave behavior; Stop-after-Start returns 204; Stop-without-prior-Start returns 422; Stop is verified to NOT delete any L2 keys (post-Stop `SCAN` matches pre-Stop).
  3. 3 consecutive GREEN integration test runs (Phase 3 D-18 cadence) covering the full v3.2.0 baseline plus the v3.3.0 additions.
  4. Byte-identical `psql \l` SHA-256 BEFORE = AFTER the full suite (Phase 3 D-15 invariant — would be the 5th consecutive phase to record the `0d98b0de…0aac127` baseline).
  5. Byte-identical `redis-cli --scan | sort | sha256sum` BEFORE = AFTER the full suite (new v3.3.0 analogue of the `psql \l` invariant); zero residual `test:cls-*` keys.
**Plans**: TBD
**v3.2.0 invariants MUST NOT regress**: Phase 3 D-15 byte-identical `psql \l` SHA-256 no-leak; Phase 3 D-18 3-consecutive-GREEN phase-close cadence; Phase 11 D-03 no-traces-backend; all 11 Phase 1-11 success criteria (142/142 baseline must still GREEN); Mapperly RMG codes; `FLUSHDB` is FORBIDDEN in test cleanup; `KEYS`/`IServer.Keys()` are FORBIDDEN in production code (SCAN-only).

## Progress

| Phase | Milestone | Plans Complete | Status      | Completed  |
| ----- | --------- | -------------- | ----------- | ---------- |
| 1-11  | v3.2.0    | 41/41          | Complete    | 2026-05-28 |
| 12    | v3.3.0    | 4/8            | In progress | —          |
| 13    | v3.3.0    | 0/0            | Not started | —          |
| 14    | v3.3.0    | 0/0            | Not started | —          |
| 15    | v3.3.0    | 0/0            | Not started | —          |
| 16    | v3.3.0    | 0/0            | Not started | —          |

---
*v3.3.0 roadmap created 2026-05-28 via `/gsd-new-milestone`. Phase 12 planned 2026-05-29 via `/gsd-plan-phase 12` (8 plans, 4 waves). Next: `/gsd-execute-phase 12` to begin Wave 1.*
