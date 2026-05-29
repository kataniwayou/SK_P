# Requirements — Milestone v3.3.0

**Milestone:** v3.3.0 Orchestration L3 → L1 → L2 Build Pipeline
**Created:** 2026-05-28 (via `/gsd-new-milestone`)
**Source:** `.planning/PROJECT.md` Current Milestone section + 4 parallel research outputs in `.planning/research/` + locked decisions captured during interactive milestone gathering.

---

## v3.3.0 Requirements

### INFRA-REDIS — Redis as a new infrastructure tier

- [x] **INFRA-REDIS-01
** — `compose.yaml` runs Redis alongside Postgres / Elasticsearch / Prometheus / OTel Collector, pinned to a Redis 7.4.x-alpine image (Redis 7.4 line is RSALv2/SSPLv1; explicitly NOT Redis 8.0+ which is AGPLv3-encumbered).
- [x] **INFRA-REDIS-02
** — `redis-cli ping` healthcheck on the Redis compose service with `start_period: 5s` + `interval: 5s` + `retries: 10`; `baseapi-service` declares `depends_on: redis: condition: service_healthy`.
- [x] **INFRA-REDIS-03
** — `StackExchange.Redis 2.13.1` (or current 2.13.x at commit time) added to `Directory.Packages.props` with CPM-pinning; consumed by `src/BaseApi.Service/BaseApi.Service.csproj`.
- [ ] **INFRA-REDIS-04** — `ConnectionStrings:Redis` in `appsettings.json` and `appsettings.Development.json`; format includes `abortConnect=false,connectTimeout=5000` (production-must-have per StackExchange.Redis maintainer guidance).
- [ ] **INFRA-REDIS-05** — `Redis:KeyPrefix` configuration section (default `"skp:"`); all L2 keys are written with this prefix.
- [ ] **INFRA-REDIS-06** — Soft Redis dependency: `/health/ready` does NOT include a Redis check. CRUD endpoints continue to serve 200 even if Redis is down; only `/api/v1/orchestration/{start,stop}` fail with 500 + RFC 7807 when Redis is unreachable. Phase 5 HEALTH-01..05 contracts unchanged.

### INFRA-COMP — Composition root + DI

- [ ] **INFRA-COMP-01** — New `AddBaseApiRedis(IServiceCollection, IConfiguration)` extension in `src/BaseApi.Core/DependencyInjection/RedisServiceCollectionExtensions.cs`; chained inside `AddBaseApi<TDbContext>` as call #7 (after Mapping, before Features). Does NOT mirror the Phase 7 D-13 `IHostApplicationBuilder` split (no `ILoggingBuilder` need).
- [ ] **INFRA-COMP-02** — `IConnectionMultiplexer` registered as Singleton (StackExchange.Redis maintainer-blessed pattern: thread-safe, expensive to construct, long-lived).
- [ ] **INFRA-COMP-03** — `IDatabase` resolved from the singleton multiplexer per-call (cheap, stateless).
- [ ] **INFRA-COMP-04** — `RedisProjectionOptions` POCO bound to the `Redis:*` config section via `services.Configure<RedisProjectionOptions>(cfg.GetSection("Redis"))`. Exposes `KeyPrefix` (string) and `Serialization.JsonOptions` (string discriminator).

### ORCH-SPLIT — OrchestrationService decomposition

- [ ] **ORCH-SPLIT-01** — Existing `OrchestrationService` (Phase 9, 92 LOC) split into orchestrator + 4 internal seams: `IWorkflowGraphLoader` (Loading/), `CycleDetector` + `SchemaEdgeValidator` + `PayloadConfigSchemaValidator` (Validation/), `IRedisProjectionWriter` (Projection/). Split happens BEFORE any L1/L2 work lands (Phase 10 bisect-friendly N-commit sequence applies).
- [ ] **ORCH-SPLIT-02** — Seams stay `internal` to `BaseApi.Service.Features.Orchestration` in v3.3.0; promotion to `BaseApi.Core` deferred until a second consumer surfaces.
- [ ] **ORCH-SPLIT-03** — `OrchestrationService.StartAsync` becomes the orchestrator only (target body length ~80 LOC): existence-check → loader → validator chain → writer → `snapshot.Dispose()`.
- [ ] **ORCH-SPLIT-04** — `OrchestrationService.StopAsync` extracted as a NEW method (split from the v3.2.0 `ValidateWorkflowIdsAsync` since Start/Stop semantics now diverge per the v3.2.0 Phase 9 CONTEXT D-09 "refactor when they diverge" note).

### L1-BUILD — Transient in-memory build

- [ ] **L1-BUILD-01** — `WorkflowGraphSnapshot` is a transient record built per Start request. Lives only inside `StartAsync` scope. Implements `IDisposable` for the cleanup contract.
- [ ] **L1-BUILD-02** — `IWorkflowGraphLoader.LoadL1Async(IReadOnlyList<Guid> workflowIds)` fetches from Postgres (L3) using direct `BaseDbContext.Set<>().AsNoTracking()...Where(x => ids.Contains(x.Id))` batch queries — does NOT extend `Repository<TEntity>` (Phase 3 5-method surface unchanged).
- [ ] **L1-BUILD-03** — Loader populates a flat `Dictionary<Guid, EntityDto>` for each entity type: Workflows, Assignments, Steps, Processors, Schemas. Keyed by `Id`. The DTOs are the v3.2.0 ReadDto types.
- [ ] **L1-BUILD-04** — Step traversal iterates every collection: from each `Workflow.EntryStepIds[*]`, walk into each `StepEntity.NextStepIds[*]` via the existing `StepNextSteps` junction. A step with multiple children is followed into ALL children (not just the first). Per-traversal `visited` list (NOT a HashSet keyed on object identity; keyed on StepId).
- [ ] **L1-BUILD-05** — **L1 cleanup contract** — `WorkflowGraphSnapshot.Dispose()` clears all internal dictionaries + traversal lists. Called via `using` declaration in `StartAsync` so cleanup runs on success AND failure paths.

### L1-VALIDATE — Validation gates (mandatory order)

- [ ] **L1-VALIDATE-01** — Validation runs in this exact order: existence → cycles → schema-edge compatibility → Payload↔ConfigSchema. Failure at any gate short-circuits with HTTP 422 + RFC 7807 + `X-Correlation-Id`. L1 cleanup still runs in the `finally` path.
- [ ] **L1-VALIDATE-02** — **Existence gate**: re-uses the v3.2.0 `ValidateWorkflowIdsAsync` path. WorkflowIds not in Postgres → 422 with offending id list.
- [ ] **L1-VALIDATE-03** — **Cycle-detection gate**: `CycleDetector` uses ITERATIVE DFS with an explicit stack/list (NOT recursive — recursion risks `StackOverflowException` which `IExceptionHandler` cannot catch). On each visit, check `visited.Contains(stepId)` BEFORE adding; if true, return cycle-detected 422 with the offending stepId chain.
- [ ] **L1-VALIDATE-04** — **Missing next-step gate**: `NextStepId` referenced by a step but absent from the loaded `Dictionary<Guid, StepEntity>` → 422 with the parent stepId + missing childId. `null` NextStepId is the terminal-step signal and passes.
- [ ] **L1-VALIDATE-05** — **Schema-edge compatibility gate**: for every `(parent step → child step)` edge in the traversal — iterating over EVERY entry in `parent.NextStepIds[*]`, NOT just the first — `parent.Processor.OutputSchemaId` MUST equal `child.Processor.InputSchemaId` (strict `Guid` equality). On mismatch, return 422 with the offending `(parentStepId, childStepId)` pair. If either side is `null` (Phase 10 source/sink/unconfigured processor), the edge passes.
- [ ] **L1-VALIDATE-06** — **Payload↔ConfigSchema gate**: for every Assignment in the L1 snapshot, resolve `Step.ProcessorId → Processor.ConfigSchemaId → Schema.Definition`, then validate `Assignment.Payload` against that JSON Schema (JsonSchema.Net draft 2020-12). Failure → 422 with offending assignmentId + validation errors. If `Processor.ConfigSchemaId` is null, the assignment passes (no schema to validate against).
- [ ] **L1-VALIDATE-07** — **SSRF lockdown extension** — the new Payload validator MUST use a shared `JsonSchemaConfig.DefaultOptions` factory (or equivalent) that mirrors the Phase 8 SchemaEntity validator's SSRF-disabled `SchemaRegistry.Global.Fetch = (_, _) => null` setting. v3.2.0 SSRF defense-in-depth MUST NOT regress.
- [ ] **L1-VALIDATE-08** — **Schema caching** — within a single Start request, parsed `JsonSchema` instances are cached in a `Dictionary<Guid, JsonSchema>` keyed by `Schema.Id`. Each schema is parsed at most once per Start. (JsonSchema.Net 2025 perf guidance: re-parsing is the slow path.)
- [ ] **L1-VALIDATE-09** — **Closes deferred VALID-21 (orchestration-start scope only)** — Assignment-PUT and Assignment-POST endpoints remain "valid JSON only" (v3.2.0 behavior preserved). The Payload↔Schema conformance check happens ONLY at orchestration-start time, NOT at HTTP write time. VALID-21 from v2's deferred list closes for orchestration-start; the HTTP-write side is explicitly out of scope.
- [ ] **L1-VALIDATE-10** — **Closes deferred TEST-03/TEST-04 status** — TEST-03 (Testcontainers.PostgreSql) and TEST-04 (Respawn) remain deferred to a future milestone; v3.3.0 does NOT close them. Documented for traceability.

### L2-PROJECT — Redis projection writer

- [ ] **L2-PROJECT-01** — `IRedisProjectionWriter.UpsertAsync(WorkflowGraphSnapshot)` writes three Redis key spaces using StackExchange.Redis `IDatabase` operations on a `CreateBatch()` pipeline. Atomicity is per-key SET (last-write-wins consistent with the locked concurrency decision); MULTI/EXEC NOT used (avoids server-blocking on large projections).
- [ ] **L2-PROJECT-02** — `RedisProjectionKeys` is the single source of truth for key formatters: `Root(workflowId)` → `"{prefix}{workflowId}"`; `Step(workflowId, stepId)` → `"{prefix}{workflowId}:{stepId}"`; `Processor(processorId)` → `"{prefix}{processorId}"`. All other code MUST format keys through this class.
- [ ] **L2-PROJECT-03** — **`{workflowId}` root key** value shape: `{ entryStepIds[], cron, jobId, liveness, correlationId }`. Serialized as JSON string via System.Text.Json with default options. `entryStepIds` from `WorkflowEntity.EntryStepIds`. `cron` from `WorkflowEntity.CronExpression` (string, may be null). `jobId` is a `Guid` field — Start writes `Guid.Empty` (write semantics deferred). `liveness` is a `{ DateTime timestamp, int interval, string status }` object — Start writes `{ timestamp: now, interval: 0, status: "Pending" }` (defaults; write semantics deferred). `correlationId` is the `X-Correlation-Id` string from the originating Start POST request (the same value that Phase 4 `CorrelationIdMiddleware` echoes in the response header and stamps into log scopes); lets external consumers trace each L2 projection back to the Start request that built it.
- [ ] **L2-PROJECT-04** — **`{workflowId:stepId}` per-step key** value shape: `{ entryCondition, processorId, payload, nextStepIds[] }`. Serialized as JSON string. `entryCondition` from `StepEntity.EntryCondition` enum (serialized as int per the Phase 8 storage convention). `processorId` from `StepEntity.ProcessorId`. `payload` from the corresponding `AssignmentEntity.Payload` (the assignment with `StepId == stepId` within this workflow). `nextStepIds[]` is the FULL list of child StepIds from the `StepNextSteps` junction (NOT just the first). For terminal steps, `nextStepIds` is an empty list `[]` (NOT null) — consumers iterate without null-check.
- [ ] **L2-PROJECT-05** — **`{processorId}` per-processor key** value shape: `{ inputDefinition, outputDefinition, liveness }`. Field names ARE `inputDefinition` / `outputDefinition` (NOT `definitionIn`/`definitionOut`, NOT `definitionInput`/`definitionOutput`). `inputDefinition` is the JSON-Schema body from `Processor.InputSchema.Definition` (or null if `InputSchemaId` is null). `outputDefinition` analogous. `liveness` is a `{ DateTime timestamp, int interval, string status }` object — Start writes defaults `{ timestamp: now, interval: 0, status: "Pending" }`; write semantics deferred.
- [ ] **L2-PROJECT-06** — Mapperly is for entity↔DTO source-gen mapping ONLY. L2 DTO → JSON string serialization uses System.Text.Json directly (NOT Mapperly). Phase 6 Mapperly disciplines (RMG007/RMG012/RMG020/RMG089) preserved unchanged.
- [ ] **L2-PROJECT-07** — `IServer.Keys()` and `KEYS` Redis command are FORBIDDEN in production code (Phase 14+ writer + Stop). Only `SCAN`-based enumeration (cursor-based) is allowed if enumeration is needed. v3.3.0 Start does not enumerate; v3.3.0 Stop does not enumerate (existence-check only).

### ORCH-START — Start endpoint contract

- [ ] **ORCH-START-01** — `POST /api/v1/orchestration/start` request body shape unchanged from v3.2.0: `{ "workflowIds": ["...guid...", ...] }`. `WorkflowIdsValidator` continues to enforce non-empty + all-GUIDs.
- [ ] **ORCH-START-02** — On success (all gates pass, L2 write completes): 204 No Content.
- [ ] **ORCH-START-03** — On any validation failure (existence / cycle / missing-step / schema-edge / payload-config-schema): 422 Unprocessable Entity with RFC 7807 Problem Details, `correlationId`, `instance`, and a structured `errors` extension that identifies the offending entity ids (workflowId / stepId pair / assignmentId).
- [ ] **ORCH-START-04** — On Redis-side failure (`RedisConnectionException` / timeout): 500 with RFC 7807 + correlationId. L1 cleanup still runs in `finally`.
- [ ] **ORCH-START-05** — **Idempotency (PUT-like)** — repeated Start with the same WorkflowIds re-runs the full pipeline and overwrites all L2 keys for those workflows. Returns 204 on each call. No staging-then-RENAME; plain `StringSetAsync` is sufficient because the value shape per key is whole-document (not partial).
- [ ] **ORCH-START-06** — **Concurrency** — two concurrent Starts for the same WorkflowId interleave at the per-key SET level; last-write-wins on each key. NO Redis distributed lock. Documented behavior — a reader between the two writes may observe a mix of partial state across keys. Acceptable per locked concurrency decision.
- [ ] **ORCH-START-07** — `X-Correlation-Id` from the request propagates through the entire pipeline (existence → loader → validators → writer) and into all OTel log scopes + RFC 7807 error bodies. Phase 4 correlation invariant preserved.
- [ ] **ORCH-START-08** — `OrchestrationController.Start` adds `[ProducesResponseType]` for 422 and 500 in addition to the existing 204 / 400; surfaces typed error shapes in Swagger.

### ORCH-STOP — Stop endpoint contract (REVISED — existence check only)

- [ ] **ORCH-STOP-01** — `POST /api/v1/orchestration/stop` request body shape unchanged from v3.2.0: `{ "workflowIds": ["...guid...", ...] }`.
- [ ] **ORCH-STOP-02** — For each WorkflowId, `OrchestrationService.StopAsync` issues `IDatabase.KeyExistsAsync({prefix}{workflowId})` against Redis. If all keys exist → 204 No Content.
- [ ] **ORCH-STOP-03** — If any WorkflowId's `{workflowId}` key does NOT exist in Redis → 422 Unprocessable Entity with RFC 7807 listing the missing workflowIds. (Distinguishes from v3.2.0 Phase 9 behavior which was Postgres-based existence; v3.3.0 Stop is Redis-based existence.)
- [ ] **ORCH-STOP-04** — Stop performs NO DELETE, NO eviction, NO per-step or per-processor key cleanup. L2 entries remain in place after Stop returns. Full Stop-side eviction semantics are deferred to a future milestone.
- [ ] **ORCH-STOP-05** — Stop does NOT touch L3 (Postgres). The WorkflowIds are NOT validated against Postgres; only against Redis. (This differs from v3.2.0 Phase 9 where Stop called `ValidateWorkflowIdsAsync` against the L3 DB.)
- [ ] **ORCH-STOP-06** — Stop is idempotent — repeated Stop with the same WorkflowIds returns the same response (204 if all still exist in L2, 422 otherwise). No state mutation.
- [ ] **ORCH-STOP-07** — On Redis-side failure: 500 + RFC 7807 + correlationId.

### TEST-REDIS — Test infrastructure

- [ ] **TEST-REDIS-01** — `tests/BaseApi.Tests/Composition/RedisFixture.cs` parallels the v3.2.0 `PostgresFixture`. Holds a single shared connection to a host-side Redis (or compose-provided Redis); each test class instance generates a unique `KeyPrefix = "test:cls-{Guid:N}:"` for isolation.
- [ ] **TEST-REDIS-02** — `Phase8WebAppFactory` (or a v3.3.0 successor) encapsulates the `RedisFixture` alongside `PostgresFixture`. `ConfigureWebHost` injects `ConnectionStrings:Redis` + `Redis:KeyPrefix` via `AddInMemoryCollection`. Same Plan 05-02 Pattern C reasoning.
- [ ] **TEST-REDIS-03** — `RedisFixture.DisposeAsync` runs `SCAN MATCH "{KeyPrefix}*"` + `KeyDeleteAsync(keys)`. Then re-`SCAN` with the same prefix and ASSERT count == 0 — throws on violation (fail-loud; analogue of the Phase 3 D-15 byte-identical psql\l discipline). `FLUSHDB` is FORBIDDEN (destroys keys from parallel test classes).
- [ ] **TEST-REDIS-04** — Phase-close gate is extended: in addition to the v3.2.0 `psql \l` SHA-256 BEFORE=AFTER snapshot, the new `redis-cli --scan | sort | sha256sum` BEFORE=AFTER snapshot must be byte-identical across the full test suite.
- [ ] **TEST-REDIS-05** — New `HealthDeadRedisFixture` extends `Phase8WebAppFactory` with a dead Redis port (e.g., `localhost:6380`) to prove `/health/live` stays 200 when Redis is down. (Since INFRA-REDIS-06 makes Redis a soft dependency, `/health/ready` ALSO stays 200 when Redis is down — only Start/Stop fail. Both behaviors are tested.)
- [ ] **TEST-REDIS-06** — Integration facts for the full Start happy-path: real Postgres + real Redis + 3-keyspace assertion (root key shape, per-step chain, per-processor body). Each fact asserts the L2 record matches expected JSON via System.Text.Json deserialization round-trip.
- [ ] **TEST-REDIS-07** — Integration facts for each validation gate's failure path: cycle-detected workflow → 422 + error body shape; missing-next-step → 422; schema-edge mismatch → 422; payload-vs-config-schema failure → 422; all gate failures verified to NOT write to Redis (`SCAN` assertion that no keys exist for the failed workflowId).
- [ ] **TEST-REDIS-08** — Integration facts for the Start idempotency contract: Start twice with same WorkflowIds → L2 keys reflect the second write; concurrent Start regression test (two parallel HTTP requests) documents the last-write-wins / partial-state-interleave behavior.
- [ ] **TEST-REDIS-09** — Integration facts for Stop existence-check: Stop after Start → 204; Stop without prior Start → 422 with missing workflowIds; Stop is verified to NOT delete any L2 keys (post-Stop `SCAN` assertion matches pre-Stop).

### OBSERV-REDIS — Observability (constrained)

- [ ] **OBSERV-REDIS-01** — OpenTelemetry Redis instrumentation (`OpenTelemetry.Instrumentation.StackExchangeRedis`) is NOT wired in v3.3.0. Phase 11 D-03 (no traces backend; `.WithTracing()` stripped) is preserved. Adding the trace-side instrumentation without a backend would create dropped spans + duplicate-span risk (OTel issue #1301).
- [ ] **OBSERV-REDIS-02** — Redis client operations DO appear in standard MEL logs (Phase 5 OTel MEL bridge ships them to Elasticsearch). `X-Correlation-Id` log scopes flow through Redis async ops via AsyncLocal (verified by E2E test extending the Phase 11 SchemasLogsE2ETests pattern).
- [ ] **OBSERV-REDIS-03** — RFC 7807 error bodies for Redis-side failures (`ORCH-START-04`, `ORCH-STOP-07`) include the correlationId + the offending Redis operation (`UpsertAsync` / `KeyExistsAsync`) in the Extensions, surfacing root cause to operators.
- [ ] **OBSERV-REDIS-04** — Future-milestone candidate (documented, not in v3.3.0): Redis-side metrics (latency histograms, command counts) via the `StackExchange.Redis` profiling API → Prometheus exporter. Deferred until there's a real observability need.

---

## Future Requirements (deferred from v3.3.0)

- **FUTURE-STOP-EVICTION** — Full Stop-side L2 eviction: `Stop` deletes `{workflowId}` + each `{workflowId:stepId}` (from a discovery mechanism — `allStepIds[]` on the root, chain-walk, or SCAN-by-prefix). Includes processor-key reference-counting / orphan cleanup. Out of v3.3.0 scope per the locked Stop revision.
- **FUTURE-SCHEDULER** — Real `JobId` + `Liveness` write semantics. Likely an external Scheduler service writes to L2 via a new endpoint after Start. May add Hangfire/Quartz in-process, or stay opaque. Deferred until the Scheduler contract is defined.
- **FUTURE-OTEL-REDIS** — Re-add OTel Redis trace instrumentation when a traces backend exists (re-opens the Phase 11 D-03 decision).
- **FUTURE-GENERATION-ID** — `generationId` (monotonic version number) on the `{workflowId}` root DTO for stale-projection detection by external consumers.
- **FUTURE-VALID-21-HTTP-WRITE** — Closing v2-deferred VALID-21 at Assignment-PUT/POST time (validator with DB roundtrip from Step→Processor→ConfigSchema). v3.3.0 closes VALID-21 ONLY at orchestration-start; HTTP-write path remains "valid JSON only" per the locked decision.

---

## Out of Scope (v3.3.0)

- **Full Stop-side L2 eviction** — see FUTURE-STOP-EVICTION. v3.3.0 Stop is existence-check only. *Why: user reduced scope to keep v3.3.0 focused on the L3→L1→L2 build pipeline.*
- **Scheduler integration (real `JobId` / `Liveness` writers)** — see FUTURE-SCHEDULER. *Why: Scheduler contract not yet defined; v3.3.0 reserves the field shapes only.*
- **OTel Redis trace instrumentation** — see FUTURE-OTEL-REDIS. *Why: no traces backend in v1 per Phase 11 D-03; adding trace-side instrumentation without a backend creates dropped spans.*
- **`generationId` on the L2 root DTO** — see FUTURE-GENERATION-ID. *Why: no multi-writer races in v3.3.0; revisit when Scheduler integration ships.*
- **`allStepIds[]` on the L2 root DTO** — *Why: Stop doesn't delete in v3.3.0, so no enumeration is needed. The wire format stays minimal. If FUTURE-STOP-EVICTION lands, this gets revisited.*
- **VALID-21 at Assignment HTTP-write time** — see FUTURE-VALID-21-HTTP-WRITE. *Why: user-locked decision to validate Payload↔ConfigSchema only at orchestration-start.*
- **Schema-edge STRUCTURAL compatibility** (subset/superset, canonical-form equality) — v3.3.0 uses strict `Schema.Id` equality only. *Why: structural compatibility needs a JSON-Schema subset-check library or canonicalizer; deferred until there's a real use case.*
- **Read-through cache semantics on the 5 entity GETs** — Redis is WRITE-ONLY in v3.3.0; the v3.2.0 read paths (entity controllers) are unchanged. *Why: caching the read side is a separate concern from materialized projection; mixing them risks invalidation pitfalls.*
- **Authentication / authorization on `/api/v1/orchestration/{start,stop}`** — still open per v3.2.0 Out of Scope. *Why: auth boundary still TBD project-wide.*
- **Lua scripts on Redis** — explicitly anti-feature per FEATURES.md research. *Why: Redis docs warn against business logic in Lua; opaque SHA-1 digests; script-cache poisoning; server-blocking.*
- **`MULTI/EXEC` atomic transactions across the 3 keyspaces** — Start uses per-key `StringSetAsync` (Pattern A). *Why: per the locked last-write-wins decision; MULTI/EXEC adds server-blocking without consistency benefit when concurrent Starts already last-write-wins.*
- **`FLUSHDB` for test cleanup** — explicitly forbidden. *Why: destroys keys from parallel-running test classes; hides genuine leaks.*
- **Redis Cluster** — v3.3.0 ships single-node Redis. *Why: no scale driver yet; the disciplines (hashtag preservation, no `SELECT`, no `FLUSHDB`) are encoded so future Cluster migration is additive.*

---

## Traceability

Phase-to-REQ-ID mapping, locked by `/gsd-new-milestone` roadmap creation 2026-05-29.

| Phase | Title | REQ-ID Count | Requirements |
|-------|-------|--------------|--------------|
| 12 | Redis infra + composition + healthcheck + DI registration | 15 | INFRA-REDIS-01..06, INFRA-COMP-01..04, TEST-REDIS-01..05 |
| 13 | OrchestrationService split + L3 fetch + L1 build | 9 | ORCH-SPLIT-01..04, L1-BUILD-01..05 |
| 14 | Validation gates (DFS + schema-edge + payload-config-schema) | 10 | L1-VALIDATE-01..10 |
| 15 | L2 Redis projection write + Stop existence check | 26 | L2-PROJECT-01..07, ORCH-START-01..08, ORCH-STOP-01..07, OBSERV-REDIS-01..04 |
| 16 | Idempotency + concurrency + L1 cleanup + 3-GREEN closeout | 4 | TEST-REDIS-06..09 |
| **Total** | | **64** | All v3.3.0 REQ-IDs covered, each assigned to exactly one phase. |
