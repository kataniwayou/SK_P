# Research Summary -- v3.3.0 Orchestration L3 to L1 to L2 Build Pipeline

**Project:** Steps API (BaseApi.Core + BaseApi.Service)
**Milestone:** v3.3.0 -- Orchestration L3 to L1 to L2 Build Pipeline
**Domain:** Redis-backed materialized projection layer on a hardened .NET 8 modular monolith
**Researched:** 2026-05-28
**Confidence:** HIGH (stack + pitfalls); MEDIUM (DTO shape details; atomic-write pattern selection)

---

## Snapshot

v3.3.0 adds a Redis (L2) materialized projection pipeline driven by POST /api/v1/orchestration/start. On Start, the service fetches from Postgres (L3), builds a transient in-memory graph (L1), validates it through four sequential gates, writes it to Redis across three key spaces, then discards L1. Stop evicts all Redis keys for the given WorkflowIds. Both endpoints are idempotent (PUT-like); Start is last-write-wins with no Redis lock.

The four research files reached broad agreement on the solution shape. Three decisions MUST be made at requirements-time before phase planning begins:

1. **Redis health-check hard vs. soft dependency** -- ARCHITECTURE.md says hard (mirror Postgres ready-check); STACK.md says soft (CRUD survives Redis outage). Genuine tradeoff with operational consequences.
2. **Atomic-overwrite write pattern for Start** -- FEATURES.md recommends Pattern A (per-key SET); PITFALLS.md recommends stage-then-RENAME for single-key atomicity; PROJECT.md locks last-write-wins but does not specify the per-key write mechanism.
3. **allStepIds[] field on the {workflowId} root DTO** -- PITFALLS.md flags it as required for O(stepCount) Stop without SCAN; PROJECT.md locked DTO shape does not currently include it; requires explicit PROJECT.md amendment if adopted.
---

## Key Findings

### Consolidated Stack Additions

Three NuGet packages and one Docker Compose service. Zero changes to the v3.2.0 stack. Versions verified against NuGet.org as of 2026-05-28.

**Add to Directory.Packages.props:**

```xml
<!-- v3.3.0 -- Redis L2 projection -->
<PackageVersion Include="StackExchange.Redis" Version="2.13.1" />
<PackageVersion Include="OpenTelemetry.Instrumentation.StackExchangeRedis" Version="1.15.1-beta.1" />
<PackageVersion Include="Testcontainers.Redis" Version="4.12.0" />
```

Note: OpenTelemetry.Instrumentation.StackExchangeRedis is pinned but NOT referenced in any csproj in v3.3.0. STACK.md Option B and PITFALLS.md Pitfall 13 both agree: defer Redis OTel instrumentation to v3.4 (Contradiction 5 -- no conflict between files).

**Add to compose.yaml:** redis:7.4.2-alpine service. 7.4.x deliberately chosen -- RSALv2/SSPLv1 only, no AGPLv3 viral-network-distribution clause that Redis 8.0+ adds. redis-cli ping healthcheck, persistence disabled (L2 is rebuildable from L3 on demand), no volume mount, ~30MB Alpine image.

**Connection string (appsettings.json):** localhost:6379,abortConnect=false,connectTimeout=5000,syncTimeout=5000. abortConnect=false is mandatory -- without it, a Redis outage at boot kills the API process (Pitfall 2).

**Graph cycle detection:** hand-roll iterative DFS in OrchestrationService (~40 LOC). No new NuGet package. Matches v3.2.0 minimal-dependency posture.

### Feature Scope

**P1 Table Stakes -- 8 features, all mandatory for v3.3.0 ship:**

| # | Feature | Notes |
|---|---------|-------|
| 1 | Bounded L3 fetch + flat Dictionary L1 build + explicit try/finally teardown | 5 entity tables, one request scope, .Clear() in finally |
| 2 | DFS traversal (iterative only) with cycle detection + missing-next-step 422 | Two-set visited/inPath pattern; recursive DFS is a StackOverflow hazard |
| 3 | Strict Schema-edge compatibility gate | parent.OutputSchemaId == child.InputSchemaId; either-side null passes; 422 with offending parentStepId/childStepId |
| 4 | Payload vs ConfigSchema validation gate, closes VALID-21 | Reuses JsonSchema.Net 9.2.1 draft 2020-12 with Phase 8 SSRF lockdown extended |
| 5 | Three Redis key spaces with locked DTO shapes | {workflowId}, {workflowId:stepId}, {processorId}; JSON strings; inputDefinition/outputDefinition field names |
| 6 | Idempotent Start (per-key SET overwrite + targeted DEL of removed keys) | Last-write-wins; no Redis lock; 204 response |
| 7 | Idempotent Stop (evict L2 keys + 204 always) | No KEYS command; no SCAN in hot path; 204 even when no keys exist |
| 8 | Mandatory validation order | existence -> cycles -> schema-edge -> Payload/Config -> L1 build -> L2 write -> cleanup; asserted by integration tests |

**P2 Differentiators -- cheap enough to consider in milestone (USER DECIDES):**

| Feature | Cost | Notes from FEATURES.md |
|---------|------|------------------------|
| generationId on {workflowId} root DTO | LOW | Consumer detects rebuild without diffing chain records. Additive field; no breaking change. |
| Aggregate-error mode (all failures not first) | LOW-MEDIUM | Doubles test surface; DX improvement. Defer if not explicitly requested. |
| Projection-size telemetry | LOW | orchestration.projection.bytes + keys_total Histograms on existing OTel Meter. |
| Dry-run Start via ?dryRun=true | MEDIUM | Full L1 pipeline, skip L2 write. Common in dbt/KubeVela. Defer unless operator asks. |

All P2 features are additive and can be deferred to v3.4 without schema migration.

**P3 Defer -- do not include in v3.3.0:** Stage-then-rename atomic swap, structural schema compatibility, projection-diff, RedisJSON, pre-warm background service, distributed Start lock, scheduler integration (real JobId/Liveness writers).
### Architecture Seam Topology

Four seams inside Features/Orchestration/. OrchestrationService shrinks to orchestrator calling seams in validation order. Seams kept internal to the feature folder; promote to BaseApi.Core only if a second consumer surfaces.

| Seam | Interface | Lifetime | Responsibility |
|------|-----------|----------|----------------|
| Orchestrator | OrchestrationService (rewritten) | Scoped | Calls seams in order; owns try/finally L1 cleanup |
| Loader | IWorkflowGraphLoader | Scoped | L3 batch-fetch (5 entity tables); builds flat Dictionary L1 |
| Validator | IWorkflowGraphValidator | Singleton (stateless) | DFS cycle detect + schema-edge gate + Payload/ConfigSchema gate |
| Writer | IRedisProjectionWriter | Scoped | Owns 3 key spaces; holds IConnectionMultiplexer; formats JSON |

**Folder layout (from ARCHITECTURE.md Section 7):**

```
src/BaseApi.Service/Features/Orchestration/
|-- OrchestrationController.cs                    (UNCHANGED)
|-- OrchestrationService.cs                       (body rewrite -- orchestrator only ~80 LOC)
|-- OrchestrationServiceCollectionExtensions.cs   (EXTEND -- registers 4 services + validators)
|-- WorkflowIdsValidator.cs                       (UNCHANGED)
|-- Loading/
|   +-- WorkflowGraphLoader.cs                    (NEW)
|-- Validation/
|   |-- CycleDetector.cs                          (NEW)
|   |-- SchemaEdgeValidator.cs                    (NEW)
|   +-- PayloadConfigSchemaValidator.cs            (NEW)
+-- Projection/
    |-- RedisProjectionWriter.cs                  (NEW -- owns 3 key spaces)
    +-- RedisProjectionKeys.cs                    (NEW -- key constants; Guid via ToString("N"))

src/BaseApi.Core/DependencyInjection/
|-- RedisServiceCollectionExtensions.cs           (NEW -- AddBaseApiRedis, chained as call #7)
+-- Configuration/RedisProjectionOptions.cs       (NEW)

tests/BaseApi.Tests/Composition/
+-- RedisFixture.cs                               (NEW -- per-class isolation)
```

**DI placement:** AddBaseApiRedis(IServiceCollection) chained inside AddBaseApi<TDbContext> as call #7. NOT a separate IHostApplicationBuilder overload -- no ILoggingBuilder surface needed, unlike D-13 observability split.

**IConnectionMultiplexer:** MUST be Singleton. IDatabase resolved per-operation via _multiplexer.GetDatabase() -- not a class field.

**Data flow:** Redis is WRITE-ONLY from BaseApi.Service in v3.3.0. No cache-invalidation on CRUD. No IDistributedCache integration.
### Suggested Phase Decomposition (Phases 12-16)

Quoted verbatim from ARCHITECTURE.md Section 9:

| Phase | Title | What lands |
|-------|-------|------------|
| 12 | Redis infra + composition + healthcheck + DI | compose.yaml redis service, AddBaseApiRedis, AspNetCore.HealthChecks.Redis, appsettings entries, RedisFixture test infra, RedisProjectionOptions, Redis-dead health test |
| 13 | OrchestrationService split + L3 fetch + L1 build (no validation no Redis write) | WorkflowGraphSnapshot, WorkflowGraphLoader.LoadL1Async, OrchestrationService body shrunk, DI wiring, L1-snapshot-shape tests |
| 14 | Validation gates -- DFS + schema-edge + payload-config-schema | CycleDetector, SchemaEdgeValidator, PayloadConfigSchemaValidator, DI wiring, StartAsync chains 3 validators, 422 error path, ~30 unit tests cycle detection, ~10 integration tests. Closes VALID-21. |
| 15 | L2 (Redis) projection write + Stop endpoint | RedisProjectionKeys, 3 L2 DTOs (WorkflowL2Dto/StepL2Dto/ProcessorL2Dto with inputDefinition/outputDefinition), RedisProjectionWriter.UpsertAsync, DeleteAsync, OrchestrationService.StopAsync, 3-keyspace assertion tests |
| 16 | Idempotency + concurrency + L1 cleanup + 3-GREEN closeout | Start-twice regression test, concurrent Start regression test, Stop-without-prior-Start test, finally-block cleanup verification, E2E with real Postgres + Redis, 3-consecutive-GREEN gate, psql l SHA-256 + redis-cli --scan SHA-256 snapshots |

**Phase-close gate (all 5 phases):** 3-consecutive-GREEN, byte-identical psql l SHA-256, byte-identical redis-cli --scan SHA-256 (NEW for v3.3.0), bisect-friendly N-commit sequence.

**Research flags:** All phases use well-documented patterns. Skip per-phase research during planning. Phase 14 requires explicit implementation note on SSRF lockdown extension (not a research gap).

### Top 10 Highest-Stakes Pitfalls

Drawn from PITFALLS.md 32 entries, prioritizing the 10 v3.2.0 regression guards and the R4 L2-writer cluster (11 pitfalls in R4 alone).

| # | Pitfall | Problem | Mitigation | Phase |
|---|---------|---------|-----------|-------|
| 1 | ConnectionMultiplexer registered Scoped/Transient | One TCP connection per request; maxclients hit; 500 cascade | AddSingleton<IConnectionMultiplexer>; add lifetime-assertion fact | Phase 12 |
| 2 | AbortOnConnectFail not set | Process crashes on startup if Redis briefly unreachable; HEALTH-01 violated | Always set AbortOnConnectFail=false in ConfigurationOptions | Phase 12 |
| 3 | OTel Redis instrumentation re-enables traces pipeline | AddRedisInstrumentation revives Phase 11 D-03 stripped traces pipeline | Do NOT reference package in v3.3.0; use ILogger + custom Meter; build-guard fact in NoTracesBackendFacts | Phase 12/16 |
| 4 | Redis healthcheck missing tag discipline | Untagged AddRedis puts Redis in live probe; Redis blip kills pod | .AddRedis(..., tags: [ready]) only; extend Redis-down -> /health/live=200 acceptance test | Phase 12 |
| 5 | Recursive DFS -> StackOverflowException | Workflow >10k steps crashes process; uncatchable; bypasses Phase 4 exception handler | Always use iterative DFS with explicit Stack<>; add 50,000-node fact | Phase 14 |
| 6 | Single HashSet in cycle detection | Diamond DAG (A->B, A->C, B->D, C->D) misclassified as cycle; OR real cycle missed | Two sets: visited (subtree done) + inPath (current path); add diamond-DAG discriminating fact | Phase 14 |
| 7 | JsonSchema.Net SSRF defense regressed | New Payload gate uses fresh EvaluationOptions without Phase 8 SSRF lockdown | Shared JsonSchemaConfig.DefaultOptions factory; both gates must use it; extend <500ms regression test | Phase 14 |
| 8 | JsonSchema.Net schema re-parsed per validation | 100 assignments x 5 distinct schemas = 100 parses; perf hit + GC pressure | Per-Start Dictionary<Guid, JsonSchema> lazy cache; shared EvaluationOptions instance | Phase 14 |
| 9 | DEL-then-SET anti-pattern (nil window) | Reader sees nil between delete and set during Start | Use plain StringSetAsync (no preceding DEL) for overwrite; batch DEL removed children AFTER new SETs | Phase 15 |
| 10 | allStepIds[] missing -> Stop forced to SCAN | Stop cannot find {workflowId:stepId} children without O(N) keyspace SCAN | Include allStepIds[] on root DTO; Stop reads root, issues targeted UNLINK; never use IServer.Keys() | Phase 15 |

**v3.2.0 regression guards defended by PITFALLS.md:** 142/142 GREEN x3 (P16), psql l SHA-256 no-leak (P16), Mapperly RMG codes as build errors (P17), Phase 11 D-03 no traces backend (P13), HEALTH-01 live never touches external state (P14), X-Correlation-Id E2E through OTel to ES (P15), StartupCompletionService LogCritical/no-rethrow contract (P31), RFC 7807 with offending field names in Extensions (P29), JsonSchema.Net SSRF disabled (P11), L2 DTO field names inputDefinition/outputDefinition locked (P30).
---

## Cross-File Contradictions

This is the most important synthesis output. Five items below surface conflicts between research files. Items 1-4 require user resolution at requirements-time. Item 5 is resolved (both files agree).

### Contradiction 1: Redis health-check hard vs. soft dependency

- ARCHITECTURE.md (Section 3): Redis MUST join /health/ready with tags: [ready] -- mirrors Postgres pattern; ensures operators know Redis is a runtime dependency.
- STACK.md (Integration Notes): Recommend deferring Redis readiness check; CRUD continues to work when Redis is down; graceful degradation (Start fails 503, CRUD still works).
- Conflict: Hard dependency (ARCHITECTURE) vs. soft dependency (STACK). Both are reasonable positions.
- User must decide: Does a Redis outage constitute not-ready for the whole API, or does the API stay ready with only orchestration endpoints degraded?
- Recommendation: Soft dependency is technically more accurate; hard dependency is operationally simpler. Either is defensible. Lock before Phase 12.

### Contradiction 2: Test isolation -- Testcontainers.Redis vs. host-side Redis with prefix scoping

- STACK.md: Per-class ephemeral Testcontainers.Redis 4.12.0. Ryuk-managed cleanup, mirrors Phase 3 D-15 throwaway-Postgres discipline exactly. Full RESP3 + MULTI/EXEC semantics tested.
- ARCHITECTURE.md (Section 5): Single shared container + per-class GUID-prefix isolation + SCAN/DEL teardown. Per-class container boot (1-2s x N classes) adds ~30-60s to suite, violating the 163s/161s/162s cadence established at v3.2.0 close (142 facts).
- Conflict: Isolation purity (STACK) vs. suite-speed discipline (ARCHITECTURE).
- User must decide: Accept suite slowdown for container purity, or use prefix isolation to preserve cadence.
- Recommendation: ARCHITECTURE reasoning is stronger given established cadence discipline. Use prefix isolation on a shared container; FLUSHDB and KEYS * are both forbidden; use IServer.KeysAsync(pattern) (SCAN-backed) for teardown. Revisit if suite exceeds 3x baseline.

### Contradiction 3: allStepIds[] extension to the {workflowId} root DTO

- PITFALLS.md (Pitfall 12): allStepIds[] on the root DTO is REQUIRED for O(stepCount) Stop without SCAN. The milestone roadmap must encode this upfront.
- PROJECT.md locked shape: { entryStepIds[], cron, jobId, liveness } -- does NOT include allStepIds[].
- Conflict: PITFALLS says the field must exist on the root DTO; PROJECT.md locked shape does not include it.
- User must decide:
  - Option (a): Extend root DTO with allStepIds[] -- recommended; traversal already enumerates all steps for cycle detection; collecting into the DTO is essentially free. Requires PROJECT.md amendment.
  - Option (b): In-memory key-list passed from Start to Stop -- fragile; process-restart loses it.
  - Option (c): SCAN-based Stop -- O(keyspace); rejected by PITFALLS.md for production use.
- Recommendation: Option (a). Requires explicit PROJECT.md amendment to the locked {workflowId} DTO shape.

### Contradiction 4: Stage-then-RENAME vs. plain per-key SET for atomic overwrite

- FEATURES.md (Pattern A): Per-key StringSetAsync (overwrite-in-place) recommended for v3.3.0. No nil window because SET replaces atomically. Acknowledges cross-key inconsistency window as accepted given last-write-wins semantics.
- PITFALLS.md (Pitfall 5): Stage-then-RENAME recommended to prevent nil window. Calls DEL-then-SET the canonical bug.
- Reconciliation: Contradiction dissolves on closer reading. FEATURES recommends SET (not DEL-then-SET). StringSetAsync unconditionally replaces with no nil window for in-place overwrites. PITFALLS targets DEL-then-SET specifically. Plain SET for existing-key overwrite is safe. The genuine open question is removed-child keys (steps in previous Start but not in new projection) -- those require a DEL after new SETs, creating the acknowledged cross-key window.
- User must decide: Accept the cross-key interleave window (document per Pitfall 20), or require stage-then-RENAME for root key atomicity (defer to v3.4+).
- Recommendation: Plain StringSetAsync (no preceding DEL) for all key overwrites; batch DEL removed children after new SETs; document accepted window. Stage-then-RENAME deferred to v3.4+ if consumers complain.

### Contradiction 5: OTel Redis instrumentation (not a contradiction -- both files agree)

- STACK.md: Option B -- defer to v3.4; add TODO marker in AddBaseApiObservability.
- PITFALLS.md (Pitfall 13): Do NOT reference the package in v3.3.0; use structured ILogger + custom Meter; add build-guard fact in NoTracesBackendFacts.
- No contradiction. ARCHITECTURE.md is silent (consistent with deferral). Decision: defer Redis OTel instrumentation to v3.4. No user decision needed.
---

## Open Questions for Requirements

Consolidated from all four research files, deduplicated:

| # | Question | Source Files | Disposition |
|---|----------|-------------|-------------|
| 1 | Redis health-check hard vs. soft dependency? | ARCHITECTURE S3, STACK Integration Notes | Decide at requirements (Contradiction 1) |
| 2 | Test isolation: Testcontainers.Redis vs. host-side prefix isolation? | STACK Test infra, ARCHITECTURE S5 | Decide at requirements (Contradiction 2) |
| 3 | Add allStepIds[] to {workflowId} root DTO? | PITFALLS P12, FEATURES S3, PROJECT.md | Decide at requirements (Contradiction 3); PROJECT.md amendment required if yes |
| 4 | Stage-then-RENAME vs. plain SET for writer? | FEATURES Pattern A/B, PITFALLS P5 | Decide at requirements (Contradiction 4); recommendation is plain SET with no prior DEL |
| 5 | OTel Redis instrumentation in v3.3.0? | STACK, PITFALLS P13 | Decided: defer to v3.4 (both files agree) |
| 6 | P2 differentiators include in v3.3.0? (generationId, aggregate-error, telemetry, dry-run) | FEATURES S7 P2 table | Decide at requirements; all are additive and deferrable to v3.4 |
| 7 | L1Snapshot disposal: IDisposable+using vs. try/finally+.Clear()? | ARCHITECTURE open questions, PITFALLS P7 | Decide at Phase 13 planning; PITFALLS prefers try/finally for clarity |
| 8 | Stop key strategy (tied to Question 3) | ARCHITECTURE open questions, PITFALLS P12, FEATURES S3 | Decide at requirements (follows from Contradiction 3) |
| 9 | JobId initial value: Guid.Empty or null? | FEATURES S3 {workflowId} shape | Decide at requirements; Guid.Empty is more explicit for a typed field |
| 10 | MULTI/EXEC vs. IBatch for L2 write? | STACK atomic write, PITFALLS P4 | Decide at Phase 15 planning; PITFALLS recommends plain IBatch (last-write-wins removes transaction need) |
| 11 | Compose host port for Redis: 6379 or 6380? | STACK compose snippet (6379), PITFALLS P32 (6380) | Decide at Phase 12; PITFALLS recommends 6380:6379 mirroring v3.2.0 Postgres 5433:5432 |
| 12 | Startup gate extended to probe Redis reachability? | PITFALLS P31 | Decide at Phase 12; PITFALLS strongly recommends adding Redis ping to StartupCompletionService before MarkReady() |

---

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | All NuGet pins verified against NuGet.org 2026-05-28. StackExchange.Redis 2.13.1 is Context7-verified de facto standard since 2014. Docker image licensing verified against Redis blog + Percona analysis + InfoQ. |
| Features | HIGH on table stakes and mechanics; MEDIUM on exact DTO shape details | Table-stakes cross-referenced against Temporal/Argo/Airflow/Redis community. Exact DTO shapes MEDIUM (consumer-dependent); PROJECT.md locked shapes are authoritative. |
| Architecture | HIGH for seam topology and DI placement; MEDIUM for test isolation tactic | Seam decomposition verified against actual v3.2.0 source files. Test isolation tactic is a genuine requirements tradeoff. |
| Pitfalls | HIGH | 32 pitfalls verified against StackExchange.Redis docs + GitHub issues #2537/#1169/#885, OTel contrib issues #1301/#674/#3257, JsonSchema.Net docs, Redis canonical command docs. MEDIUM only for Cluster-mode edge cases (not relevant in v3.3.0 single-node). |

**Overall confidence:** HIGH on everything needed to proceed to requirements. The four open requirements-level decisions (Contradictions 1-4) are genuinely unresolved and require user input -- they are not research gaps.

### Gaps to Address

- **allStepIds[] DTO field:** PROJECT.md must be amended explicitly if Contradiction 3 resolves to Option (a). The locked {workflowId} DTO shape does not currently include it.
- **Compose host port for Redis:** PITFALLS recommends 6380; ARCHITECTURE and STACK examples use 6379. Decide before Phase 12 to avoid re-editing compose.yaml.
- **Redis persistence policy documentation:** The compose service disables RDB + AOF intentionally (L2 is rebuildable from L3). Document in a compose comment to prevent a future operator from re-enabling persistence as a fix.

---

## Sources

Full source lists with URLs live in the individual research files (.planning/research/STACK.md, FEATURES.md, ARCHITECTURE.md, PITFALLS.md).

**Primary (HIGH confidence -- verified within 30 days):**
- StackExchange.Redis 2.13.1 -- NuGet.org + official docs (singleton pattern, MULTI/EXEC semantics, Configuration docs) + GitHub issues #2537/#1169/#885
- OpenTelemetry.Instrumentation.StackExchangeRedis 1.15.1-beta.1 -- NuGet.org + OTel contrib CHANGELOG + issues #1301/#674/#3257
- Testcontainers.Redis 4.12.0 -- NuGet.org + Testcontainers .NET xUnit integration docs
- Redis 7.4.2-alpine -- Docker Hub + Redis blog (AGPLv3 license change announcement) + Percona analysis + InfoQ
- JsonSchema.Net 9.2.1 -- NuGet.org + json-everything docs (SSRF defense, SchemaRegistry, 2025 perf work)
- Redis canonical command docs (RENAME, SCAN, MULTI/EXEC, KEYS) -- redis.io
- AspNetCore.HealthChecks.Redis 9.0.0 -- Xabaril package surface
- sk_p .planning/PROJECT.md -- locked constraints (authoritative; overrides research preferences)
- sk_p v3.2.0 source files (verified live): BaseApiServiceCollectionExtensions.cs, ObservabilityServiceCollectionExtensions.cs, HealthServiceCollectionExtensions.cs, OrchestrationService.cs

**Secondary (MEDIUM confidence):**
- Temporal / Argo Workflows / Airflow pattern comparisons -- idempotent start, stop semantics, graph projection approaches
- Azure Architecture Center Materialized View pattern -- read-time vs write-time materialization
- Martin Fowler HeartBeat pattern -- liveness field shapes
- Redis Hash vs JSON tradeoff -- Redis docs hash depth limit, RedisJSON perf benchmarks
- Lua-as-anti-pattern -- Redis docs scripting warnings (no business logic in Lua)
- Dry-run pattern -- KubeVela docs, dbt guide precedent
- Redis database anti-pattern -- multi-DB for test isolation deprecated in Cluster mode
- Valkey BSD fork -- future licensing fallback if Redis licensing escalates; not adopted in v3.3.0

---
*Research completed: 2026-05-28*
*Synthesized: 2026-05-28*
*Ready for requirements: yes -- pending 4 user decisions (Contradictions 1-4)*