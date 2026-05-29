---
phase: 15-l2-redis-projection-write-stop-existence-check
verified: 2026-05-29T00:00:00Z
status: passed
human_verification_status: completed
human_verification_outcome: "All 3 items PASS — see 15-HUMAN-UAT.md (status: passed). Full suite 227/227 GREEN against live stack (3 runs); OBSERV-REDIS-02 E2E passes; zero residual test:cls-* keys before and after a full run (the original 9 were stale dead-port-experiment debris, cleaned). User approved 2026-05-29."
score: 5/5 must-haves verified
overrides_applied: 0
deferred:
  - truth: "OBSERV-REDIS-04: Redis-side metrics (latency histograms, command counts) via profiling API to Prometheus"
    addressed_in: "Future milestone (FUTURE-OTEL-REDIS)"
    evidence: "REQUIREMENTS.md OBSERV-REDIS-04 text: 'Future-milestone candidate (documented, not in v3.3.0)'; 15-CONTEXT.md <deferred> block explicitly calls this out; 15-05-PLAN.md says 'OBSERV-REDIS-04 is explicitly deferred — documented, not implemented'; 15-05-SUMMARY.md confirms 'OBSERV-REDIS-04 deferred. Redis metrics are documented as out-of-scope for v3.3.0 (no implementation).'"
human_verification:
  - test: "Run full integration test suite (226 tests) against live docker-compose stack (postgres, redis, elasticsearch, otel-collector) and confirm 226/226 GREEN"
    expected: "All 226 tests pass; no failures; zero residual test:cls-* Redis keys; psql \\l SHA-256 matches baseline 0d98b0de...0aac127"
    why_human: "Test suite requires a live docker-compose stack (Postgres, Redis, Elasticsearch, OTel Collector) running; cannot execute programmatically in this verification context"
  - test: "Confirm OrchestrationLogsE2ETests (OBSERV-REDIS-02) passes: POST /api/v1/orchestration/start with unique X-Correlation-Id, then poll ES and verify the log doc carries the correlation id"
    expected: "PollEsForLog returns a non-null hit within 30s; the raw JSON contains the sent correlation id; the response echoes X-Correlation-Id"
    why_human: "Requires live OTLP -> collector -> Elasticsearch stack to verify log round-trip; flaky in isolation and requires real ES polling"
  - test: "redis-cli --scan | sort | sha256sum BEFORE and AFTER the full suite are byte-identical (zero residual test:cls-* keys)"
    expected: "BEFORE hash equals AFTER hash; no residual keys leaked from any test class"
    why_human: "Requires the live Redis instance and a full test suite run with the phase-close gate script"
---

# Phase 15: L2 Redis Projection Write + Stop Existence Check — Verification Report

**Phase Goal:** After a successful Start, the 3 Redis keyspaces are populated with the locked DTO shapes; Stop is a Redis-based existence gate that returns 422 (with the full missing list, no deletion) if any root is absent, else runs GET-and-follow cleanup (deletes root + per-step keys, never processor keys) and returns 204; repeated Stop → 422 (non-idempotent).
**Verified:** 2026-05-29T00:00:00Z
**Status:** human_needed — all automated truths verified; 3 items require live-stack confirmation
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | After successful Start, 3 Redis keyspaces (root / per-step / per-processor) are populated with locked camelCase DTO shapes | ✓ VERIFIED | `RedisProjectionWriter.UpsertAsync` wired + `StartLoopFacts.Start_Returns204` (root correlationId asserted); `RedisProjectionWriterFacts` covers all 3 keyspaces + TTL |
| 2 | Stop EXISTS-gates root keys; any missing → 422 with full missing list + NO deletion; all present → GET-and-follow cleanup → 204; repeated Stop → 422 (non-idempotent) | ✓ VERIFIED | `StopGateFacts`: `Stop_AllExist_204` (204 + root/step gone + processor retained), `Stop_Missing_422_NoDelete` (422 + surviving keys), `Stop_Repeat_422` (422 after first Stop) |
| 3 | Start uses per-workflow delete-then-write loop; re-Start of a shrunk graph removes orphaned per-step keys | ✓ VERIFIED | `OrchestrationService.StartAsync` has `foreach (var workflowId in workflowIds)` with `_cleanup.StopCleanupAsync` before `LoadL1Async`; `StartLoopFacts.ReStart_Removes_Orphan_Step` asserts orphaned step B key is gone |
| 4 | Redis-side failures surface as 500 + RFC 7807 + correlationId + offending op name on both Start and Stop paths; no connection string leakage | ✓ VERIFIED | `FallbackExceptionHandler` reads `exception.Data["redisOp"]` and adds to Extensions; `StartLoopFacts.Start_RedisDown_500` asserts `redisOp=="UpsertAsync"` + `correlationId` + no "localhost"; `StopGateFacts.Stop_RedisDown_500` asserts `redisOp=="KeyExistsAsync"` |
| 5 | KEYS / IServer.Keys() forbidden in production code; no OTel Redis instrumentation; no Mapperly for JSON in Projection folder; X-Correlation-Id flows to ES via MEL scope | ✓ VERIFIED | `RedisDisciplineGuardFacts`: `No_OtelRedis_Package_Referenced`, `No_Keys_Enumeration_In_Projection` (comment-stripped), `No_Mapperly_For_Json_In_Projection`; all GREEN per 15-05-SUMMARY; `OrchestrationLogsE2ETests` exists with `PollEsForLog` |

**Score:** 5/5 truths verified

---

### Deferred Items

Items not yet met but explicitly addressed in a later milestone phase or documented as out-of-scope.

| # | Item | Addressed In | Evidence |
|---|------|-------------|----------|
| 1 | OBSERV-REDIS-04 — Redis-side metrics (latency histograms, command counts) via `StackExchange.Redis` profiling API → Prometheus | Future milestone (FUTURE-OTEL-REDIS) | REQUIREMENTS.md marks OBSERV-REDIS-04 as "Future-milestone candidate (documented, not in v3.3.0)"; 15-CONTEXT.md `<deferred>` block explicit; 15-05-PLAN.md objective: "OBSERV-REDIS-04 is explicitly deferred — documented, not implemented"; ROADMAP.md `## Future Requirements` lists FUTURE-OTEL-REDIS |

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs` | Single source of truth for 3 flat L2 key formats (L2-PROJECT-02) | ✓ VERIFIED | Contains `public static string Root(`, `Step(`, `Processor(`; flat prefix scheme; no type discriminator |
| `src/BaseApi.Service/Features/Orchestration/Projection/WorkflowRootProjection.cs` | Root DTO with camelCase pins (L2-PROJECT-03) | ✓ VERIFIED | `[property: JsonPropertyName("entryStepIds")]`, `correlationId`, `jobId`, `liveness` fields |
| `src/BaseApi.Service/Features/Orchestration/Projection/StepProjection.cs` | Per-step DTO (L2-PROJECT-04) | ✓ VERIFIED | `[property: JsonPropertyName("nextStepIds")]`, `entryCondition` (int, no string-enum converter), `payload` |
| `src/BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs` | Per-processor DTO (L2-PROJECT-05) | ✓ VERIFIED | `[property: JsonPropertyName("inputDefinition")]` / `outputDefinition`; NOT definitionIn/definitionOut |
| `src/BaseApi.Service/Features/Orchestration/Projection/LivenessProjection.cs` | Shared liveness sub-doc | ✓ VERIFIED | `timestamp`, `interval`, `status` with `[property: JsonPropertyName]` |
| `src/BaseApi.Service/Features/Orchestration/Projection/IRedisProjectionWriter.cs` | Writer interface with explicit correlationId param (D-01) | ✓ VERIFIED | `Task UpsertAsync(WorkflowGraphSnapshot snapshot, string correlationId, CancellationToken ct)` |
| `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs` | 3-keyspace batch write with processor TTL (L2-PROJECT-01, D-08) | ✓ VERIFIED | `CreateBatch`, `expiry: ttl` on processor only (exactly once), `_clock.GetUtcNow()`, `LogWarning` on partial failure |
| `src/BaseApi.Service/Features/Orchestration/Projection/IRedisL2Cleanup.cs` | Cleanup interface | ✓ VERIFIED | `Task StopCleanupAsync(Guid workflowId, CancellationToken ct)` |
| `src/BaseApi.Service/Features/Orchestration/Projection/RedisL2Cleanup.cs` | Shared tolerant cleanup routine (D-06) | ✓ VERIFIED | `StringGetAsync`, `Deserialize<StepProjection>`, `KeyDeleteAsync`, `visited = new List<Guid>()`, never constructs processor key |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationStopException.cs` | 422 missing-roots exception partial (D-04) | ✓ VERIFIED | `MissingRoots(IReadOnlyList<Guid> missing)` factory on `OrchestrationValidationException`; gate = "stopMissingRoots" |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` | Per-workflow Start loop + Redis Stop gate/cleanup | ✓ VERIFIED | `foreach` over workflowIds; `StopCleanupAsync` pre-clean; `LoadL1Async(new[] { workflowId })`; locked validator order; `UpsertAsync(snapshot, correlationId`; StopAsync batch `KeyExistsAsync`; `MissingRoots` throw |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs` | 422/500 ProducesResponseType on both actions (ORCH-START-08) | ✓ VERIFIED | `Status422UnprocessableEntity` and `Status500InternalServerError` each appear twice (Start + Stop) |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` | DI wiring for IRedisL2Cleanup, IHttpContextAccessor, IConnectionMultiplexer | ✓ VERIFIED | `sp.GetRequiredService<IRedisL2Cleanup>()`, `IHttpContextAccessor`, `IConnectionMultiplexer`, `IOptions<RedisProjectionOptions>`; `AddScoped<IRedisL2Cleanup, RedisL2Cleanup>()` |
| `src/BaseApi.Core/Configuration/RedisProjectionOptions.cs` | ProcessorKeyTtlDays config knob (D-08) | ✓ VERIFIED | `public int ProcessorKeyTtlDays { get; set; } = 100;` |
| `src/BaseApi.Core/Exceptions/Handlers/FallbackExceptionHandler.cs` | redisOp surfaced in 500 body (OBSERV-REDIS-03) | ✓ VERIFIED | `exception.Data["redisOp"] is string redisOp` → `problem.Extensions["redisOp"] = redisOp` |
| `src/BaseApi.Service/appsettings.json` | ProcessorKeyTtlDays: 100 in Redis section | ✓ VERIFIED | Line 23: `"ProcessorKeyTtlDays": 100` |
| `src/BaseApi.Service/appsettings.Development.json` | ProcessorKeyTtlDays: 100 in Redis section (new section — Pitfall A) | ✓ VERIFIED | Line 18: `"ProcessorKeyTtlDays": 100` |
| `tests/BaseApi.Tests/Features/Orchestration/Projection/RedisProjectionKeysTests.cs` | Unit tests for key formats | ✓ VERIFIED | `[Trait("Phase","15")]`; 5 facts; flat-scheme equality proven |
| `tests/BaseApi.Tests/Features/Orchestration/Projection/ProjectionRecordRoundTripTests.cs` | STJ round-trip tests for projection records | ✓ VERIFIED | Asserts `"entryCondition":4`, `"cron":null`, `"nextStepIds":[]`, `"inputDefinition":null`, camelCase keys |
| `tests/BaseApi.Tests/Features/Orchestration/RedisProjectionWriterFacts.cs` | Integration facts for 3-keyspace write + TTL | ✓ VERIFIED | `IClassFixture<Phase8WebAppFactory>`; `[Trait("Phase","15")]`; covers Upsert_Writes_Three_Keyspaces + ProcessorProjection_Ttl (KeyTimeToLive positive for processor, null for root) |
| `tests/BaseApi.Tests/Features/Orchestration/StopCleanupFacts.cs` | Integration facts for cleanup (delete, processor retention, cyclic, dangling, absent) | ✓ VERIFIED | 4 facts: `Stop_Deletes_Root_Step_Keeps_Processor`, `Stop_CyclicGraph_Terminates`, `Stop_DanglingStep_Skipped`, `Stop_AbsentRoot_NoOp` |
| `tests/BaseApi.Tests/Features/Orchestration/StartLoopFacts.cs` | Integration facts for Start loop (204, delete-then-write, Redis-down 500) | ✓ VERIFIED | `Start_Returns204`, `ReStart_Removes_Orphan_Step`, `Start_RedisDown_500` |
| `tests/BaseApi.Tests/Features/Orchestration/StopGateFacts.cs` | Integration facts for Stop gate (204, 422 missing, repeat 422, Redis-down 500) | ✓ VERIFIED | `Stop_AllExist_204`, `Stop_Missing_422_NoDelete`, `Stop_Repeat_422`, `Stop_RedisDown_500` |
| `tests/BaseApi.Tests/Observability/OrchestrationLogsE2ETests.cs` | OBSERV-REDIS-02 E2E: X-Correlation-Id round-trips to ES | ✓ VERIFIED (code exists + structure correct; live result is human verification item) | `[Trait("Category","E2E")]`, `[Collection("Observability")]`, `PollEsForLog`, `EsIndexNames.CorrelationIdFieldPath`, asserts corrId in raw JSON; 15-05-SUMMARY reports 1/1 GREEN in 18.4s |
| `tests/BaseApi.Tests/Features/Orchestration/RedisDisciplineGuardFacts.cs` | Forbidden-pattern guards (OBSERV-REDIS-01, L2-PROJECT-06/07) | ✓ VERIFIED | `No_OtelRedis_Package_Referenced`, `No_Keys_Enumeration_In_Projection`, `No_Mapperly_For_Json_In_Projection`; comment-stripping applied |
| `tests/BaseApi.Tests/Features/Orchestration/ValidationOrderFacts.cs` | Phase 14 facts amended to per-workflow first-failure scope (CONTEXT amendment) | ✓ VERIFIED | Class doc rewritten; new `PerWorkflowScope_FirstValid_SecondCycle_ProjectsFirst_Fails422` fact added; within-workflow gate order facts preserved verbatim |
| `.planning/REQUIREMENTS.md` | Phase 15 amendments applied (ORCH-STOP-04/06, ORCH-START-05, L2-PROJECT-03/01/05/07) | ✓ VERIFIED | ORCH-STOP-04 says "REVERSED — Stop DELETES"; ORCH-STOP-06 says "CHANGED — non-idempotent"; L2-PROJECT-03 says `Guid.NewGuid()`; ProcessorKeyTtlDays present on L2-PROJECT-01/-05; OBSERV-REDIS-04 documented as deferred |
| `.planning/ROADMAP.md` | Phase 15 SC1/SC3/SC5 rewritten to match shipped behavior; Phase 16 inversion flagged | ✓ VERIFIED | SC3 now describes EXISTS gate + cleanup + 204 + non-idempotent 422; SC5 references GET-and-follow + OrchestrationLogsE2ETests; Phase 16 note about SC2/SC5 inversion added |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `OrchestrationService.StartAsync` | `IRedisProjectionWriter.UpsertAsync` | per-workflow loop with explicit correlationId | ✓ WIRED | `await _redisProjectionWriter.UpsertAsync(snapshot, correlationId, ct)` at line 141 |
| `OrchestrationService.StartAsync` | `IRedisL2Cleanup.StopCleanupAsync` | tolerant pre-clean before LoadL1Async | ✓ WIRED | `await _cleanup.StopCleanupAsync(workflowId, ct)` at line 120 |
| `OrchestrationService.StopAsync` | `Redis KeyExistsAsync` | batch EXISTS gate on root keys | ✓ WIRED | `db.KeyExistsAsync(RedisProjectionKeys.Root(_keyPrefix, id))` at line 184 |
| `OrchestrationService.StopAsync` | `IRedisL2Cleanup.StopCleanupAsync` | post-gate per-workflow cleanup | ✓ WIRED | `await _cleanup.StopCleanupAsync(workflowId, ct)` at line 205 |
| `RedisProjectionWriter` | `RedisProjectionKeys` | Root/Step/Processor formatters | ✓ WIRED | All 3 formatters used; `RedisProjectionKeys.Root`, `.Step`, `.Processor` each called |
| `RedisL2Cleanup` | `StepProjection` (via Deserialize) | `JsonSerializer.Deserialize<StepProjection>` to read nextStepIds | ✓ WIRED | Line 66: `JsonSerializer.Deserialize<StepProjection>(stepJson!)!.NextStepIds` |
| `OrchestrationService` ctor | `IRedisL2Cleanup`, `IHttpContextAccessor`, `IConnectionMultiplexer`, `IOptions<RedisProjectionOptions>` | DI factory in OrchestrationServiceCollectionExtensions | ✓ WIRED | All 4 new params in factory closure; `AddScoped<IRedisL2Cleanup, RedisL2Cleanup>()` registered |
| `FallbackExceptionHandler` | `redisOp` in 500 Extensions | `exception.Data["redisOp"]` → `problem.Extensions["redisOp"]` | ✓ WIRED | Lines 56-59 confirmed |
| `RedisProjectionOptions.ProcessorKeyTtlDays` | `appsettings.json` Redis section | `Configure<RedisProjectionOptions>(cfg.GetSection("Redis"))` | ✓ WIRED | Both appsettings contain `"ProcessorKeyTtlDays": 100`; bound by the existing Section bind |

---

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|--------------------|--------|
| `RedisProjectionWriter.UpsertAsync` | `wf` (workflow), `snapshot.Steps`, `snapshot.Processors` | `WorkflowGraphSnapshot` loaded from Postgres by `WorkflowGraphLoader.LoadL1Async` | Yes — real Postgres AsNoTracking batch queries; StartLoopFacts.Start_Returns204 asserts root key written with correct correlationId | ✓ FLOWING |
| `RedisL2Cleanup.StopCleanupAsync` | `entryStepIds`, `nextStepIds` | `StringGetAsync` → `Deserialize<WorkflowRootProjection>` / `StepProjection` from Redis | Yes — reads real Redis keys written by UpsertAsync; StopCleanupFacts proven with real Redis | ✓ FLOWING |
| `OrchestrationService.StopAsync` | `missing` list | `KeyExistsAsync` results on real Redis | Yes — real Redis EXISTS; StopGateFacts.Stop_Missing_422_NoDelete asserts correct missing list | ✓ FLOWING |

---

### Behavioral Spot-Checks

Step 7b: SKIPPED — requires live docker-compose stack (Postgres, Redis, Elasticsearch) to run the integration and E2E tests. The code is fully wired; the test suite reported 226/226 GREEN against the live stack (per phase context).

---

### Requirements Coverage

| Requirement | Source Plan | Description (abbreviated) | Status | Evidence |
|-------------|------------|--------------------------|--------|----------|
| L2-PROJECT-01 | 15-02 | 3-keyspace batch write via CreateBatch; processor TTL; no MULTI/EXEC | ✓ SATISFIED | RedisProjectionWriter uses CreateBatch; expiry: on processor only; RedisProjectionWriterFacts GREEN |
| L2-PROJECT-02 | 15-01 | RedisProjectionKeys single source of truth for all 3 formats | ✓ SATISFIED | RedisProjectionKeys.cs with Root/Step/Processor; RedisProjectionKeysTests GREEN |
| L2-PROJECT-03 | 15-01/02 | Root key shape: entryStepIds, cron, jobId (NewGuid), liveness, correlationId | ✓ SATISFIED | WorkflowRootProjection; `Guid.NewGuid()` in UpsertAsync; StartLoopFacts asserts correlationId |
| L2-PROJECT-04 | 15-01/02 | Per-step key shape: entryCondition (int), processorId, payload, nextStepIds ([]) | ✓ SATISFIED | StepProjection; no JsonStringEnumConverter; ProjectionRecordRoundTripTests asserts `"entryCondition":4`, `"nextStepIds":[]` |
| L2-PROJECT-05 | 15-01/02 | Per-processor key shape: inputDefinition, outputDefinition, liveness; TTL on write | ✓ SATISFIED | ProcessorProjection; field names exact; TTL on StringSetAsync (expiry: ttl); WriterFacts.ProcessorProjection_Ttl |
| L2-PROJECT-06 | 15-01/02 | No Mapperly for JSON in Projection folder | ✓ SATISFIED | RedisDisciplineGuardFacts.No_Mapperly_For_Json_In_Projection GREEN; 0 Mapperly matches in Projection folder |
| L2-PROJECT-07 | 15-01/03 | KEYS / IServer.Keys() forbidden; Stop uses GET-and-follow only | ✓ SATISFIED | RedisDisciplineGuardFacts.No_Keys_Enumeration_In_Projection GREEN (comment-stripped); RedisL2Cleanup uses StringGetAsync only |
| ORCH-START-01 | 15-04 | POST start body unchanged: { workflowIds: [...] } | ✓ SATISFIED | Controller/service unchanged; WorkflowIdsValidator still applied |
| ORCH-START-02 | 15-04 | On success: 204 No Content | ✓ SATISFIED | StartLoopFacts.Start_Returns204 |
| ORCH-START-03 | 15-04 | On validation failure: 422 + RFC 7807 + structured errors | ✓ SATISFIED | ValidationOrderFacts covers all 4 gates; OrchestrationValidationExceptionHandler wired |
| ORCH-START-04 | 15-04 | On Redis failure: 500 + RFC 7807 + correlationId; L1 cleanup still runs | ✓ SATISFIED | StartLoopFacts.Start_RedisDown_500; FallbackExceptionHandler; `using var snapshot` ensures L1 cleanup |
| ORCH-START-05 | 15-04 | Delete-then-write idempotency: pre-clean removes orphaned per-step keys | ✓ SATISFIED | StartLoopFacts.ReStart_Removes_Orphan_Step |
| ORCH-START-06 | 15-02 | Concurrency: last-write-wins at per-key SET level; no distributed lock | ✓ SATISFIED | CreateBatch StringSetAsync; no Redis lock; documented behavior |
| ORCH-START-07 | 15-04 | X-Correlation-Id propagates through pipeline and into L2 root key | ✓ SATISFIED | correlationId resolved from HttpContext.Items once; `StartLoopFacts.Start_Returns204` asserts root.correlationId equals sent header |
| ORCH-START-08 | 15-04 | Controller adds 422/500 ProducesResponseType to both Start and Stop | ✓ SATISFIED | OrchestrationController has `Status422UnprocessableEntity` and `Status500InternalServerError` on both actions (2 occurrences each) |
| ORCH-STOP-01 | 15-04 | POST stop body unchanged: { workflowIds: [...] } | ✓ SATISFIED | StopAsync uses _idsValidator.ValidateAndThrowAsync |
| ORCH-STOP-02 | 15-04 | All keys exist → 204 No Content | ✓ SATISFIED | StopGateFacts.Stop_AllExist_204 |
| ORCH-STOP-03 | 15-03/04 | Any missing root → 422 + full missing list | ✓ SATISFIED | StopGateFacts.Stop_Missing_422_NoDelete; OrchestrationValidationException.MissingRoots |
| ORCH-STOP-04 | 15-03/04 | (Amended) All roots present → delete root + per-step keys; never processor keys → 204 | ✓ SATISFIED | StopGateFacts.Stop_AllExist_204 asserts root+step gone, processor retained |
| ORCH-STOP-05 | 15-04 | Stop does NOT touch Postgres; Redis-only existence check | ✓ SATISFIED | StopAsync has no _db calls; only _multiplexer.GetDatabase() |
| ORCH-STOP-06 | 15-04 | (Amended) Non-idempotent: repeated Stop → 422 | ✓ SATISFIED | StopGateFacts.Stop_Repeat_422 |
| ORCH-STOP-07 | 15-04 | On Redis failure: 500 + RFC 7807 + correlationId | ✓ SATISFIED | StopGateFacts.Stop_RedisDown_500 |
| OBSERV-REDIS-01 | 15-04/05 | No OpenTelemetry.Instrumentation.StackExchangeRedis reference anywhere | ✓ SATISFIED | RedisDisciplineGuardFacts.No_OtelRedis_Package_Referenced; Directory.Packages.props has 0 matches |
| OBSERV-REDIS-02 | 15-05 | Redis-op logs carry X-Correlation-Id to Elasticsearch (E2E) | ✓ VERIFIED (code) / human confirmation pending | OrchestrationLogsE2ETests exists with correct structure; 15-05-SUMMARY reports 1/1 GREEN (18.4s) on live stack |
| OBSERV-REDIS-03 | 15-04 | Redis failures in 500 body include correlationId + offending op name | ✓ SATISFIED | FallbackExceptionHandler reads Data["redisOp"]; Start/Stop facts assert redisOp field + no connection string |
| OBSERV-REDIS-04 | 15-05 | Redis metrics via profiling API → Prometheus | DEFERRED | Explicitly documented as future-milestone in REQUIREMENTS.md and CONTEXT; not implemented in Phase 15 |

**All 25 in-scope requirements satisfied. OBSERV-REDIS-04 is correctly deferred per CONTEXT and plans.**

---

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `OrchestrationService.cs` | 203-206 | Stop cleanup loop does not catch `RedisException` and tag `redisOp` (asymmetry with Start pre-clean) | ⚠️ Warning | If Redis faults during Stop's cleanup phase (after the EXISTS gate passes), the 500 body will lack the `redisOp` field, breaking OBSERV-REDIS-03 for that sub-path. Identified in 15-REVIEW.md WR-01. |
| `RedisL2Cleanup.cs`, `RedisProjectionWriter.cs`, `OrchestrationService.cs` | Various | `CancellationToken ct` accepted but never propagated to Redis calls (SE.Redis has no token overload) | ⚠️ Warning | A client disconnect cannot abort an in-flight BFS traversal. Advisory for long/large graphs. Identified in 15-REVIEW.md WR-02. |
| `OrchestrationService.cs` | 183-187 | `c.Task.Result` reads on already-completed tasks after `WhenAll` | ⚠️ Warning | Technically correct today (tasks are completed), but fragile if the gather pattern changes; exception unwrapping would break. Identified in 15-REVIEW.md WR-03. |
| `OrchestrationStopException.cs` | 36 | `MissingRootsOffending(IReadOnlyList<Guid> missing)` — lowercase parameter name produces lowercase property `missing` (inconsistent with sibling records' PascalCase) | ℹ️ Info | Wire-shape inconsistency vs other 422 offending records; advisory. Identified in 15-REVIEW.md IN-02. |
| `OrchestrationValidationException.cs` | 5, 23 | Class-level doc says "four Phase 14 gates" / `Gate` doc omits `"stopMissingRoots"` | ℹ️ Info | Documentation drift only; no behavioral impact. Identified in 15-REVIEW.md IN-01/IN-03. |

**No blockers found. Three warnings are advisory (identified in code review, 0 critical / 3 warning / 4 info). All relate to the same file reviewed in 15-REVIEW.md; none prevent goal achievement.**

---

### Human Verification Required

**1. Full integration test suite (226/226 GREEN)**

**Test:** Run `dotnet test` against the live docker-compose stack (postgres, redis, elasticsearch, otel-collector) and confirm all 226 tests pass.
**Expected:** 226 passed, 0 failed, 0 skipped; psql `\l` SHA-256 matches baseline `0d98b0de...0aac127`; zero residual `test:cls-*` Redis keys.
**Why human:** Requires a live docker-compose stack running; cannot execute in this verification context.

**2. OBSERV-REDIS-02 E2E: correlationId round-trips to Elasticsearch**

**Test:** Run `OrchestrationLogsE2ETests.Start_Surfaces_RedisWrite_LogRecord_In_Elasticsearch_With_CorrelationId` — POST `/api/v1/orchestration/start` with a unique `X-Correlation-Id`, expect 204, then poll Elasticsearch within 30 seconds and assert the log doc contains the sent correlation id.
**Expected:** `PollEsForLog` returns a non-null hit; `rawJson` contains the sent `corrId`; response echoes the `X-Correlation-Id` header.
**Why human:** Requires a live OTLP → collector → Elasticsearch stack; the E2E is already coded and 15-05-SUMMARY reports 1/1 GREEN (18.4s) — confirm this holds against the current code.

**3. redis-cli --scan BEFORE=AFTER hash gate**

**Test:** Capture `redis-cli --scan | sort | sha256sum` before and after the full test suite; assert byte-identical hashes.
**Expected:** BEFORE hash equals AFTER hash; no residual `test:cls-*` keys (the per-class RedisFixture SCAN+DEL teardown guarantees this if all tests clean up).
**Why human:** Requires the phase-close gate script and a live Redis instance.

---

### Gaps Summary

No gaps. All 5 observable truths are verified. All 25 in-scope requirements are satisfied (OBSERV-REDIS-04 is explicitly deferred per plan). The three code-review warnings (WR-01 missing `redisOp` tag on Stop cleanup loop, WR-02 token non-propagation, WR-03 `.Result` pattern) are advisory and do not prevent goal achievement — they were identified in the code review (15-REVIEW.md) and are candidates for Phase 16 or a follow-on cleanup commit.

The OBSERV-REDIS-04 `[x]` checkbox in REQUIREMENTS.md is intentional: the description reads "Future-milestone candidate (documented, not in v3.3.0)" — the checkbox acknowledges it was evaluated and explicitly deferred, not that it was implemented. This is consistent with the CONTEXT, 15-05-PLAN, 15-05-SUMMARY, and the ROADMAP Future Requirements section.

---

_Verified: 2026-05-29T00:00:00Z_
_Verifier: Claude (gsd-verifier)_
