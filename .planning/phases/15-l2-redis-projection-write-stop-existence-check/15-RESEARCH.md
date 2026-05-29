# Phase 15: L2 Redis projection write + Stop existence check (REDESIGNED) - Research

**Researched:** 2026-05-29
**Domain:** StackExchange.Redis 2.13.1 L2 projection writes + Redis-side graph traversal cleanup; per-workflow Start orchestration loop; System.Text.Json camelCase pinning; correlationId plumbing via IHttpContextAccessor; SCAN-only test teardown.
**Confidence:** HIGH on codebase seams, SE.Redis batch/expiry/delete API, System.Text.Json field pinning, DI wiring; MEDIUM on a few cross-cutting plan-decomposition choices (left to Claude's Discretion per CONTEXT).

---

## Summary

Phase 15 fills the no-op `RedisProjectionWriter.UpsertAsync` (currently `=> Task.CompletedTask`) so a validated Start projects an L1 `WorkflowGraphSnapshot` into three Redis keyspaces (root / per-step / per-processor) with locked camelCase DTO shapes, AND redesigns Start + Stop around **per-workflow processing**. Start becomes a stop-then-build loop (existence-check all ids → foreach: tolerant cleanup → L3→L1 per-workflow → validate → L1→L2 → dispose L1). Stop becomes an **L2 cleanup** (existence-gate on roots → Redis-side GET-and-follow graph traversal → collect-then-batch-delete root + per-step keys; never processor keys). Processor keys get a refresh-on-write TTL (`Redis:ProcessorKeyTtlDays`, default 100; `<=0` = no expiry).

**This phase deliberately supersedes the stale ROADMAP/REQUIREMENTS Stop description.** The original ORCH-STOP-04/06 ("Stop performs NO DELETE, idempotent 204") are REVERSED by CONTEXT D-06/D-04: Stop now deletes (non-idempotent → 422 on repeat). L2-PROJECT-03's `jobId = Guid.Empty` becomes `Guid.NewGuid()` per Start. The flat single-`skp:`-prefix scheme (no type discriminator) is confirmed unchanged.

**Primary recommendation:** Build a `RedisProjectionKeys` static formatter (verify-then-create; it does NOT exist yet) as the single source of truth for all 3 key formats. In the writer, use `_multiplexer.GetDatabase()` → `CreateBatch()` → queue per-key `StringSetAsync(key, json, expiry)` → `batch.Execute()` → `await Task.WhenAll(tasks)`. Use `KeyDeleteAsync(RedisKey[])` for cleanup (it auto-selects UNLINK on Redis 4.0+ servers — no explicit UNLINK method exists). Pin every projection-DTO field with `[JsonPropertyName]`. Mirror the Phase 13 `WorkflowGraphLoader` iterative BFS `visited`-List pattern for the Redis-side Stop traversal. Extract a shared `StopCleanupAsync(workflowId)` routine reused by both the Stop endpoint (gated) and Start's pre-clean (tolerant).

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

**D-01 — CorrelationId plumbing (Start only)**
- Inject `IHttpContextAccessor` into `OrchestrationService` (already registered via `AddHttpContextAccessor`; same pattern `AuditInterceptor` uses).
- `StartAsync` resolves `HttpContext.Items["CorrelationId"]` **once** and passes it as an **explicit parameter**: `UpsertAsync(snapshot, correlationId, ct)`. Writer stays HTTP-agnostic / unit-testable.
- Public `StartAsync` / `StopAsync` signatures **unchanged** (correlationId resolved internally).
- Root key keeps a **single `correlationId`** field = the Start POST's `X-Correlation-Id`. Stop writes NO correlationId; Stop's error-path correlationId comes from the existing Phase 4 ProblemDetails customizer.

**D-02 — L2 projection DTO modeling**
- Dedicated **internal record DTOs** in `Features/Orchestration/Projection/`: `WorkflowRootProjection`, `StepProjection`, `ProcessorProjection`, shared `LivenessProjection`.
- Each member pinned with `[JsonPropertyName("...")]` (default System.Text.Json does NOT camelCase). Records round-trippable for Phase 16 deserialization asserts.
- Enum-as-int (`entryCondition`) and `cron: null` fall out of default options — no extra config.
- **Mapperly NOT used** for DTO→JSON (System.Text.Json directly, L2-PROJECT-06). ReadDto→projection-record assembly hand-written in the writer (cross-entity: `payload` from Assignments dict keyed by `stepId`; `nextStepIds` coalesced to `[]` when null).
- **Locked value shapes:**
  - **Root** `{prefix}{workflowId}` → `{ entryStepIds[], cron, jobId, liveness{timestamp,interval,status}, correlationId }`
  - **Per-step** `{prefix}{workflowId}:{stepId}` → `{ entryCondition, processorId, payload, nextStepIds[] }` (terminal → `[]`, never null; no liveness)
  - **Per-processor** `{prefix}{processorId}` → `{ inputDefinition, outputDefinition, liveness }` (field names ARE `inputDefinition`/`outputDefinition`); written **with TTL** (D-08); key **never deleted** (D-06)
- **Key scheme unchanged (L2-PROJECT-02):** single shared `KeyPrefix` on all three; `{prefix}{workflowId}`, `{prefix}{workflowId}:{stepId}`, `{prefix}{processorId}`. **No type discriminator** (segmented `wf:`/`proc:` rejected). `RedisProjectionKeys` is the single source of truth.

**D-03 — Start projection batch-write failure semantics**
- `UpsertAsync`: `CreateBatch()` → queue per-key `StringSetAsync` → `batch.Execute()` → `await Task.WhenAll(tasks)` (L2-PROJECT-01; batch ≠ transaction, no MULTI/EXEC).
- Mid-batch failure → first faulting task's exception bubbles to Phase 4 handler → **500 + RFC 7807 + correlationId**; already-written keys left as-is (self-heals: re-Start pre-cleans).
- Emit **one MEL warning naming the `workflowId`** before rethrow. L1 cleanup still runs in the per-workflow `using` disposal.

**D-04 — Stop existence gate + status codes**
- Stop: **batch `KeyExistsAsync`** on requested **root** keys; if **any** missing → **422** with the **full** missing-workflowIds list and NO deletion; all exist → run D-06 cleanup → **204**.
- Collect ALL missing ids (not fail-fast). Redis-side failure → 500 (ORCH-STOP-07).

**D-05 — Liveness & jobId**
- `liveness.status` stays `"Pending"` everywhere (root + processor). NO Start/Stop status lifecycle this phase (`Starting`/`Stopping` deferred).
- Start writes `liveness = { timestamp: now, interval: 0, status: "Pending" }` on root + processor.
- `jobId = Guid.NewGuid()` — fresh Guid per Start (NOT `Guid.Empty`). Re-Start → new jobId.

**D-06 — Stop = L2 cleanup (delete root + per-step; never processors)**
- All-exist path, for each `workflowId`: (1) read root → `entryStepIds[]`; (2) **traverse step graph in Redis** (cycle-safe `visited` set): GET each `{workflowId}:{stepId}`, follow `nextStepIds[]`, collect every reachable per-step key; (3) **collect-then-delete**: traverse fully THEN batch-delete (`CreateBatch` + `KeyDeleteAsync`, UNLINK-style) root + collected per-step keys.
- **NEVER delete `{processorId}` keys** (shared).
- Dangling/absent per-step key during traversal → **skip and continue** (don't fail).
- Partial-failure mid-cleanup → **500**; partially-deleted keys left; re-Stop cleans remaining roots.
- Targeted **GET-and-follow only** — `KEYS`/`IServer.Keys()` forbidden; no Postgres.
- **Repeated Stop non-idempotent**: 2nd identical Stop finds roots deleted → **422** ("nothing to stop").

**D-07 — Start = per-workflow stop-then-build loop**
```
StartAsync(workflowIds):
  existence-check ALL ids upfront          # 404 fast-fail before ANY mutation (D-08 gate preserved)
  foreach workflowId:
    StopCleanup(workflowId)                # shared routine, TOLERANT (absent → no-op, no 422)
    using snapshot = LoadL1(workflowId)    # L3 → L1, PER WORKFLOW
      Validate(snapshot)                   # cycle → schema-edge → payload (gate order preserved)
      UpsertAsync(snapshot, correlationId) # L1 → L2 (D-03)
    # snapshot disposed
```
- Stop cleanup routine is a **shared internal service** reused by Stop endpoint (with D-04 422 gate) and Start's pre-clean (tolerant, no gate).
- L1 snapshot is now **per-workflow** (Phase 13 built one batch snapshot).
- Cleanup-then-build consequence (accepted): re-Start of a now-invalid workflow deletes old L2, then 422s without rebuilding.
- Partial-state across workflows (accepted): mid-loop 422 on B leaves A cleaned+rebuilt and B wiped.

**D-08 — Processor-key TTL (refresh-on-write)**
- Processor keys never deleted (D-06), value-updated every Start.
- Each Start's processor `StringSetAsync` carries `expiry: TimeSpan.FromDays(ttlDays)` → TTL refreshed every Start. Actively-referenced processor never expires; orphaned one self-expires after `ttlDays`.
- Root + per-step keys carry NO TTL.
- New `ProcessorKeyTtlDays` (int, **default 100**) on `RedisProjectionOptions` + `Redis:*` section of appsettings.
- `ProcessorKeyTtlDays <= 0` ⇒ no expiry. Positive ⇒ `TimeSpan.FromDays(value)`.

### Claude's Discretion
- ReadDto → projection-record assembly implementation details.
- Exact MEL log message wording and levels.
- Final `DEL` vs `UNLINK` pick (recommended UNLINK-style `KeyDeleteAsync`) — **RESOLVED below: `KeyDeleteAsync` auto-selects UNLINK on the 7.4.x server.**
- Redis-side traversal data structure (mirror Phase 13 iterative BFS `visited` pattern).
- `liveness.timestamp` source — prefer injected `TimeProvider` (AuditInterceptor precedent) over `DateTime.UtcNow`.
- Whether `RedisProjectionKeys` exists or must be created — **RESOLVED below: it does NOT exist; create it.**

### Deferred Ideas (OUT OF SCOPE)
- `liveness.status` Start/Stop lifecycle (`Starting`/`Stopping`) — deferred; status stays `"Pending"`.
- `stopCorrelationId` — considered, dropped; root keeps single `correlationId`.
- Per-processor liveness lifecycle — never status-managed this phase.
- Full Stop-side per-processor eviction — processor keys intentionally never deleted.
- `jobId` run/scheduling semantics + real `liveness.interval` — only the Guid generated now.
- **OBSERV-REDIS-04** Redis-side metrics (latency/command counts via profiling API → Prometheus) — future milestone.
- `OpenTelemetry.Instrumentation.StackExchangeRedis` — MUST NOT be referenced (OBSERV-REDIS-01).
</user_constraints>

<phase_requirements>
## Phase Requirements

> Amendments from CONTEXT `<amendments>` applied on top of the original line-item text. Where they conflict, CONTEXT wins.

| ID | Description (amended) | Research Support |
|----|-----------------------|------------------|
| L2-PROJECT-01 | `UpsertAsync(snapshot, correlationId, ct)` writes 3 keyspaces via `IDatabase` on a `CreateBatch()` pipeline; per-key SET, no MULTI/EXEC. | SE.Redis batch pattern (Code Examples §1); `_multiplexer.GetDatabase()` per INFRA-COMP-03. |
| L2-PROJECT-02 | `RedisProjectionKeys` single source of truth: `Root`/`Step`/`Processor` formatters. **Create it** (does not exist). | Don't Hand-Roll; Code Examples §2. |
| L2-PROJECT-03 | Root value `{ entryStepIds[], cron, jobId, liveness, correlationId }`. **`jobId = Guid.NewGuid()`** (amended from `Guid.Empty`); `liveness.status="Pending"`. | `WorkflowRootProjection` record (Code Examples §3); WorkflowReadDto fields. |
| L2-PROJECT-04 | Per-step value `{ entryCondition(int), processorId, payload, nextStepIds[] }`; terminal `[]` not null. | `StepProjection` record; payload from Assignments dict keyed by StepId. |
| L2-PROJECT-05 | Per-processor value `{ inputDefinition, outputDefinition, liveness }`; written **with TTL** (amended). | `ProcessorProjection`; `inputDefinition` = `Schema.Definition` of `InputSchemaId` or null. |
| L2-PROJECT-06 | Mapperly NOT used for JSON; System.Text.Json directly. | Pattern 3; hand-written assembly. |
| L2-PROJECT-07 | `IServer.Keys()`/`KEYS` FORBIDDEN; Stop uses targeted GET-and-follow (not enumeration). | Pattern 2 traversal; negative-grep validation. |
| ORCH-START-01 | POST body shape unchanged; `WorkflowIdsValidator` enforced. | OrchestrationController + ExistenceCheckAsync unchanged surface. |
| ORCH-START-02 | All gates pass + L2 write completes → 204. | StartAsync loop. |
| ORCH-START-03 | (Validation gate ordering — already shipped Phase 14.) | CycleDetector→SchemaEdge→Payload order preserved per-workflow. |
| ORCH-START-04 | Redis failure → 500 + RFC 7807 + correlationId; L1 cleanup still runs. | D-03; `using` disposal on throw. |
| ORCH-START-05 | Idempotency = **delete-then-write** per-workflow pre-clean (amended). | D-07 stop-then-build loop. |
| ORCH-START-06 | Concurrency: per-key last-write-wins; no distributed lock. | D-03 partial-state stance. |
| ORCH-START-07 | `X-Correlation-Id` propagates through pipeline + log scopes + RFC 7807. | D-01; CorrelationIdMiddleware AsyncLocal scope. |
| ORCH-START-08 | Controller adds `[ProducesResponseType]` for 422 + 500 (plus existing 204/400). | OrchestrationController.Start. |
| ORCH-STOP-01 | POST body shape unchanged. | OrchestrationController.Stop. |
| ORCH-STOP-02 | Batch `KeyExistsAsync` on root keys; all exist → cleanup → 204. | D-04. |
| ORCH-STOP-03 | Any root missing → 422 RFC 7807 listing missing ids. | D-04; full list (not fail-fast). |
| ORCH-STOP-04 | **REVERSED**: Stop DELETES root + per-step (never processor); still no Postgres. | D-06. |
| ORCH-STOP-05 | Stop does NOT touch Postgres; existence checked against Redis only. | D-06. |
| ORCH-STOP-06 | **CHANGED**: Stop mutates; repeated Stop → 422 (non-idempotent). | D-06. |
| ORCH-STOP-07 | Redis failure → 500 + RFC 7807 + correlationId. | D-04; Phase 4 customizer. |
| OBSERV-REDIS-01 | OTel Redis instrumentation NOT wired; package MUST NOT be referenced. | Negative-grep validation; Phase 12 forbidden-package list. |
| OBSERV-REDIS-02 | Redis ops in MEL logs; `X-Correlation-Id` flows via AsyncLocal; E2E extends Phase 11 SchemasLogsE2ETests. | Validation Architecture §E2E. |
| OBSERV-REDIS-03 | RFC 7807 Redis-failure bodies include correlationId + offending op (`UpsertAsync`/`KeyExistsAsync`) in Extensions. | Pitfall 6; ProblemDetails Extensions. |
| OBSERV-REDIS-04 | Future-milestone (deferred) — Redis metrics. | Out of scope. |
</phase_requirements>

## Project Constraints (from CLAUDE.md)

`./CLAUDE.md` does NOT exist (verified via Read). No `.claude/skills/` or `.agents/skills/` SKILL.md surfaced for this feature. Implicit project rules carried from prior phases (binding):

- **CPM enforcement** — all NuGet versions in `Directory.Packages.props`; csproj `<PackageReference>` carries NO `Version=`. `StackExchange.Redis 2.13.1` already pinned (`Directory.Packages.props:120`) and referenced in `BaseApi.Service.csproj` + `BaseApi.Tests.csproj` (Phase 12). [VERIFIED: grep]
- **`TreatWarningsAsErrors=true`** globally (Directory.Build.props). xUnit1051 requires `CancellationToken` on async tests (use `TestContext.Current.CancellationToken` — see SchemasLogsE2ETests). Mapperly RMG diagnostics are errors — but Mapperly is NOT used for JSON here (L2-PROJECT-06).
- **`internal` DI extensions** in `BaseApi.Core/DependencyInjection/` chained from `AddBaseApi`. Phase 15 touches the feature-level `OrchestrationServiceCollectionExtensions` (in `BaseApi.Service`) to inject `IHttpContextAccessor`, and `RedisProjectionOptions` (in `BaseApi.Core/Configuration`) to add `ProcessorKeyTtlDays`.
- **`psql \l` SHA-256 invariant** `0d98b0de…0aac127` must hold full-suite. Phase 12 added the `redis-cli --scan | sort | sha256sum` BEFORE=AFTER analogue (TEST-REDIS-04) — Phase 15 MUST keep it balanced (see Pitfall 3 / RedisFixture teardown amendment).
- **OrchestrationService ctor is `internal`** and DI-resolved via an explicit factory (CS0051 — internal seam types on a public ctor forbidden). Adding `IHttpContextAccessor` means editing both the ctor and the factory closure in `OrchestrationServiceCollectionExtensions.AddOrchestrationFeature`.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| L1→L2 projection write (3 keyspaces) | API/Backend — `RedisProjectionWriter` | Redis/Storage | Writer owns DTO assembly + serialization + batch SET; Redis is the sink. |
| Key formatting | API/Backend — `RedisProjectionKeys` (NEW) | — | Single source of truth for all 3 formats; pure function, no I/O. |
| Stop L2 cleanup (traverse + delete) | API/Backend — shared `StopCleanup` routine | Redis/Storage | Graph walk + batch delete; reused by Stop endpoint (gated) and Start preclean (tolerant). |
| Start orchestration loop | API/Backend — `OrchestrationService.StartAsync` | — | Per-workflow sequencing of existence→clean→load→validate→project→dispose. |
| CorrelationId resolution | API/Backend — `OrchestrationService` via `IHttpContextAccessor` | Middleware (Phase 4) | Read once from `HttpContext.Items["CorrelationId"]`, pass explicitly to writer. |
| Existence gate (Start 404 / Stop 422) | API/Backend | Postgres (Start only) / Redis (Stop) | Start keeps Postgres `ExistenceCheckAsync` (404); Stop uses Redis `KeyExistsAsync` (422). |
| Processor-key TTL config | API/Backend — `RedisProjectionOptions` | Config/appsettings | New `ProcessorKeyTtlDays` bound from `Redis:*`. |
| Liveness timestamp | API/Backend — injected `TimeProvider` | — | Testable now-source (AuditInterceptor precedent). |
| Observability (MEL logs, correlation, RFC 7807) | API/Backend | Phase 5 OTel→ES bridge | No new OTel instrumentation; logs flow via existing MEL bridge + AsyncLocal scope. |
| Test teardown SCAN+DEL | Test infrastructure — `RedisFixture` | — | Per-class prefix SCAN+KeyDeleteAsync; FLUSHDB forbidden. Must now also sweep processor keys (TTL'd, not Stop-deleted). |

**Why this matters:** every capability sits in API/Backend or Test infra — no browser/SSR/CDN. The only cross-tier mistakes the locks already forbid: putting the cleanup traversal in Postgres (D-06 forbids), or enumerating keys with `KEYS`/`IServer.Keys()` (L2-PROJECT-07 forbids — Stop walks the graph via GET, it does NOT enumerate).

## Standard Stack

### Core (all already pinned — Phase 12)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `StackExchange.Redis` | **2.13.1** | `IConnectionMultiplexer` (Singleton, DI) → `GetDatabase()` → batch SET / GET / EXISTS / DELETE | De-facto .NET Redis client. [VERIFIED: `Directory.Packages.props:120`]. `KeyDeleteAsync` auto-selects UNLINK on servers ≥ 4.0 (the 7.4.x compose server) — no explicit UNLINK API exists. [VERIFIED: GitHub StackExchange.Redis `RedisDatabase.GetDeleteCommand`]. |
| `System.Text.Json` | in-box .NET 8/9 | DTO → JSON string serialization (default options; PascalCase verbatim → `[JsonPropertyName]` required) | L2-PROJECT-06 mandates STJ directly, not Mapperly. |
| `Microsoft.Extensions.Options` | in-box | `IOptions<RedisProjectionOptions>` consumption in the writer | Reads `KeyPrefix` + new `ProcessorKeyTtlDays`. |
| `Microsoft.AspNetCore.Http.Abstractions` (`IHttpContextAccessor`) | in-box | Resolve `HttpContext.Items["CorrelationId"]` in the service | Already registered via `AddHttpContextAccessor` (AuditInterceptor precedent). |
| `Microsoft.Extensions.TimeProvider` (`TimeProvider`) | in-box .NET 8 | Testable `liveness.timestamp` source | AuditInterceptor precedent; prefer over `DateTime.UtcNow`. |

### NuGet Packages Explicitly NOT Added (FORBIDDEN)
| Package | Reason | Source |
|---------|--------|--------|
| `OpenTelemetry.Instrumentation.StackExchangeRedis` (any) | OBSERV-REDIS-01 lock; Phase 11 stripped `.WithTracing()`; dropped/duplicate-span risk. | [VERIFIED: REQUIREMENTS OBSERV-REDIS-01; Phase 12 forbidden list]. |
| `Microsoft.Extensions.Caching.StackExchangeRedis` | L2 is typed JSON projection, not opaque `IDistributedCache`. | [CITED: Phase 12 STACK]. |
| `NRedisStack` / `RedisJSON` | DTOs are STJ strings on `RedisString`. | [CITED: Phase 12 STACK]. |

**Installation:** No new packages. New config key only: `Redis:ProcessorKeyTtlDays` in `appsettings.json` + `appsettings.Development.json`.

**Version verification:** `StackExchange.Redis 2.13.1` confirmed pinned via CPM at `Directory.Packages.props:120` and referenced in service + test projects (Phase 12). Do not bump without revisiting CONTEXT D-locks.

## Architecture Patterns

### System Architecture Diagram

```
  POST /api/v1/orchestration/start  (body: List<Guid>)
        │
        ▼
  OrchestrationController.Start ──► OrchestrationService.StartAsync(ids, ct)
        │
        │  corrId = IHttpContextAccessor.HttpContext.Items["CorrelationId"]   (D-01, read ONCE)
        ▼
  ┌─ ExistenceCheckAsync(ids)  ──► Postgres SELECT id WHERE id IN (...)  ──► 404 if any missing
  │       (UNCHANGED — runs BEFORE any mutation)
  ▼
  foreach workflowId:                                          ┌───────────── Redis (L2) ─────────────┐
     ├─ StopCleanupAsync(workflowId, tolerant:true) ──────────► GET {p}{wf} → entryStepIds            │
     │      (shared routine; absent root → no-op)                BFS GET {p}{wf}:{step} follow next   │
     │                                                            collect → CreateBatch + KeyDelete    │
     │                                                            (root + per-step; NEVER processor)   │
     ├─ using snapshot = loader.LoadL1Async([workflowId])  ◄──── Postgres (L3)                         │
     │      (PER-WORKFLOW now; was batch in P13)                                                       │
     ├─ cycleDetector.Validate(snapshot)         ─┐                                                    │
     ├─ schemaEdgeValidator.Validate(snapshot)    ├─ gate order LOCKED; throws → 422                   │
     ├─ payloadConfigValidator.Validate(snapshot) ─┘                                                   │
     ├─ writer.UpsertAsync(snapshot, corrId, ct) ─────────────► CreateBatch:                           │
     │                                                            SET {p}{wf}        = rootJson         │
     │                                                            SET {p}{wf}:{step} = stepJson  (×N)   │
     │                                                            SET {p}{proc}      = procJson (TTL)   │
     │                                                            Execute() + await Task.WhenAll        │
     └─ snapshot.Dispose()  (L1 cleanup; on success AND throw)  └───────────────────────────────────────┘
        │
        ▼  all workflows done → 204     (any throw → Phase 4 → 422 / 500 + RFC 7807 + correlationId)


  POST /api/v1/orchestration/stop  (body: List<Guid>)
        │
        ▼
  OrchestrationController.Stop ──► OrchestrationService.StopAsync(ids, ct)
        │
        ├─ batch KeyExistsAsync({p}{wf}) for all ids ──► Redis      (collect ALL missing)
        │       any missing → 422 RFC 7807 (full missing list); NO deletion
        ▼
  foreach workflowId (all exist): StopCleanupAsync(workflowId, tolerant:false-gated-already)
        │       (same shared routine; GET-and-follow traverse → collect → batch KeyDelete)
        ▼  204     (repeated Stop → roots gone → 422)     (Redis failure → 500 + RFC 7807)
```

### Recommended Project Structure
```
src/BaseApi.Service/Features/Orchestration/
├── OrchestrationService.cs                 # MODIFY — +IHttpContextAccessor, +TimeProvider; StartAsync loop (D-07); StopAsync→Redis cleanup (D-04/D-06)
├── OrchestrationController.cs              # MODIFY — Start/Stop +[ProducesResponseType] 422/500 (ORCH-START-08)
├── OrchestrationServiceCollectionExtensions.cs  # MODIFY — inject IHttpContextAccessor (+TimeProvider) into factory; register cleanup routine
├── Loading/WorkflowGraphLoader.cs         # REUSE — LoadL1Async called per-workflow (pass single-element list); BFS pattern to mirror
├── WorkflowGraphSnapshot.cs               # REUSE — now per-workflow; disposal contract unchanged
├── Projection/
│   ├── IRedisProjectionWriter.cs          # MODIFY — UpsertAsync(snapshot, correlationId, ct)
│   ├── RedisProjectionWriter.cs           # FILL — batch SET 3 keyspaces (D-02/D-03/D-08)
│   ├── RedisProjectionKeys.cs             # NEW — Root/Step/Processor formatters (L2-PROJECT-02)
│   ├── WorkflowRootProjection.cs          # NEW — [JsonPropertyName] record (D-02)
│   ├── StepProjection.cs                  # NEW — record
│   ├── ProcessorProjection.cs             # NEW — record
│   └── LivenessProjection.cs              # NEW — shared { timestamp, interval, status }
│   └── (cleanup)  RedisL2Cleanup.cs / IRedisL2Cleanup.cs  # NEW — shared GET-and-follow traverse+delete (D-06/D-07)
src/BaseApi.Core/Configuration/
└── RedisProjectionOptions.cs              # MODIFY — +ProcessorKeyTtlDays (int, default 100)
src/BaseApi.Service/
├── appsettings.json                       # MODIFY — Redis:ProcessorKeyTtlDays
└── appsettings.Development.json           # MODIFY — Redis:ProcessorKeyTtlDays
tests/BaseApi.Tests/Composition/
└── RedisFixture.cs                        # ALREADY SCAN+DEL by prefix — verify it sweeps processor keys (it does; pattern "{KeyPrefix}*" matches all 3 key types). See Pitfall 3.
```

### Pattern 1: L2 batch write (D-03 / L2-PROJECT-01)
**What:** One `IDatabase` from the Singleton multiplexer per `UpsertAsync` call; `CreateBatch()` to pipeline all SETs; await `Task.WhenAll` so the first faulting task surfaces.
**When:** The entire writer body.
```csharp
// Source: Context7 /websites/stackexchange_github_io_stackexchange_redis — "Execute Batch Operations"
var db = _multiplexer.GetDatabase();                    // INFRA-COMP-03: per-call, not stored
var batch = db.CreateBatch();
var tasks = new List<Task>();

tasks.Add(batch.StringSetAsync(RedisProjectionKeys.Root(prefix, wf.Id), rootJson));        // no TTL
foreach (var step in steps)
    tasks.Add(batch.StringSetAsync(RedisProjectionKeys.Step(prefix, wf.Id, step.Id), stepJson)); // no TTL
foreach (var proc in processors)
    tasks.Add(batch.StringSetAsync(RedisProjectionKeys.Processor(prefix, proc.Id), procJson, expiry: ttl)); // D-08 TTL

batch.Execute();
await Task.WhenAll(tasks);   // first faulting task throws here → bubbles to Phase 4 → 500
```
- `ttl` = `ProcessorKeyTtlDays <= 0 ? (TimeSpan?)null : TimeSpan.FromDays(ProcessorKeyTtlDays)` (D-08). `StringSetAsync(key, value, expiry: null)` = no expiry — same as omitting it.
- A `batch` is NOT a transaction (no MULTI/EXEC). Per-key last-write-wins (ORCH-START-06). [CITED: L2-PROJECT-01].

### Pattern 2: Redis-side GET-and-follow cleanup (D-06) — mirror Phase 13 BFS
**What:** From the root's `entryStepIds`, BFS-walk the step graph in Redis, deserializing each per-step JSON to read `nextStepIds`, with a cycle-safe `visited` set; collect all reachable per-step keys; THEN batch-delete root + collected keys.
**When:** Shared `StopCleanupAsync` routine.
```csharp
// Mirror WorkflowGraphLoader.LoadStepsBreadthFirstAsync `visited`-List pattern.
var db = _multiplexer.GetDatabase();
var rootJson = await db.StringGetAsync(RedisProjectionKeys.Root(prefix, wf));
if (rootJson.IsNullOrEmpty) return;                     // tolerant: absent root → no-op (Start preclean)
var entryStepIds = JsonSerializer.Deserialize<WorkflowRootProjection>(rootJson!)!.EntryStepIds;

var visited = new List<Guid>();                         // List (not HashSet) — matches Phase 13 REQ convention
var stepKeysToDelete = new List<RedisKey>();
var currentWave = entryStepIds.Where(id => id != Guid.Empty).Distinct().ToList();
while (currentWave.Count > 0)
{
    var toLoad = currentWave.Where(id => !visited.Contains(id)).Distinct().ToList();
    if (toLoad.Count == 0) break;
    var nextWave = new List<Guid>();
    foreach (var stepId in toLoad)
    {
        visited.Add(stepId);
        var key = RedisProjectionKeys.Step(prefix, wf, stepId);
        var stepJson = await db.StringGetAsync(key);
        if (stepJson.IsNullOrEmpty) continue;           // dangling/absent → skip and continue (D-06)
        stepKeysToDelete.Add(key);
        var next = JsonSerializer.Deserialize<StepProjection>(stepJson!)!.NextStepIds;
        nextWave.AddRange(next.Where(id => !visited.Contains(id)));
    }
    currentWave = nextWave.Distinct().ToList();
}
// collect-then-delete (D-06): batch DELETE root + all per-step keys; NEVER processor keys
var batch = db.CreateBatch();
var delTasks = new List<Task> { batch.KeyDeleteAsync(RedisProjectionKeys.Root(prefix, wf)) };
if (stepKeysToDelete.Count > 0) delTasks.Add(batch.KeyDeleteAsync(stepKeysToDelete.ToArray()));
batch.Execute();
await Task.WhenAll(delTasks);
```
- `KeyDeleteAsync(RedisKey[])` deletes multiple keys in one command; auto-selects UNLINK on the 7.4.x server (non-blocking) — satisfies the "UNLINK-style" discretion. [VERIFIED: SE.Redis `GetDeleteCommand`].
- Cleanup reads per-step JSON via `StringGetAsync` (GET) — NOT enumeration; `KEYS`/`IServer.Keys()` never called (L2-PROJECT-07 intact).

### Pattern 3: camelCase-pinned projection records (D-02 / L2-PROJECT-03/04/05)
**What:** Internal records, every member `[JsonPropertyName]`-tagged so the locked field names hold under default `JsonSerializerOptions` (which serializes PascalCase verbatim — no camelCase policy).
```csharp
internal sealed record LivenessProjection(
    [property: JsonPropertyName("timestamp")] DateTime Timestamp,
    [property: JsonPropertyName("interval")]  int Interval,
    [property: JsonPropertyName("status")]    string Status);

internal sealed record WorkflowRootProjection(
    [property: JsonPropertyName("entryStepIds")] List<Guid> EntryStepIds,
    [property: JsonPropertyName("cron")]         string? Cron,            // null falls out of default options
    [property: JsonPropertyName("jobId")]        Guid JobId,              // Guid.NewGuid() per Start
    [property: JsonPropertyName("liveness")]     LivenessProjection Liveness,
    [property: JsonPropertyName("correlationId")] string CorrelationId);

internal sealed record StepProjection(
    [property: JsonPropertyName("entryCondition")] StepEntryCondition EntryCondition,  // serialized as int (default)
    [property: JsonPropertyName("processorId")]    Guid ProcessorId,
    [property: JsonPropertyName("payload")]        string Payload,
    [property: JsonPropertyName("nextStepIds")]    List<Guid> NextStepIds);            // [] for terminal, never null

internal sealed record ProcessorProjection(
    [property: JsonPropertyName("inputDefinition")]  string? InputDefinition,
    [property: JsonPropertyName("outputDefinition")] string? OutputDefinition,
    [property: JsonPropertyName("liveness")]         LivenessProjection Liveness);
```
- **`[property: JsonPropertyName(...)]` is required on POSITIONAL record params** — without the `property:` target the attribute lands on the constructor parameter and STJ ignores it. [VERIFIED: System.Text.Json positional-record behavior; high-confidence training + matches existing codebase record usage.]
- `StepEntryCondition` (enum, values 0–5) serializes **as int** under default options (no `JsonStringEnumConverter`) — matches L2-PROJECT-04 "int per Phase 8 storage convention". [VERIFIED: enum source `StepEntryCondition.cs`.]
- DTO→record assembly is hand-written in the writer:
  - root: `EntryStepIds` from `WorkflowReadDto.EntryStepIds ?? []`; `cron` from `WorkflowReadDto.CronExpression`; `jobId = Guid.NewGuid()`; `correlationId` param.
  - step: `payload` from `snapshot.Assignments.Values.First(a => a.StepId == stepId).Payload` (the Assignment whose `StepId == stepId`); `nextStepIds` from `StepReadDto.NextStepIds ?? []`.
  - processor: `inputDefinition` = `InputSchemaId is { } sid ? snapshot.Schemas[sid].Definition : null` (same for output).

### Pattern 4: CorrelationId resolution (D-01)
```csharp
// In OrchestrationService — injected IHttpContextAccessor (AuditInterceptor precedent)
var correlationId = _httpContextAccessor.HttpContext?.Items["CorrelationId"] as string ?? string.Empty;
// resolved ONCE in StartAsync, passed explicitly: await _writer.UpsertAsync(snapshot, correlationId, ct);
```
- Item key is the literal `"CorrelationId"` (PascalCase) — set by `CorrelationIdMiddleware`. [VERIFIED: `CorrelationIdMiddleware.cs:52` `ItemKey = "CorrelationId"`.]
- Stop does NOT read the accessor (D-01) — its error-path correlationId comes from the Phase 4 ProblemDetails customizer.

### Pattern 5: Per-workflow loader call (D-07)
**No new loader variant needed.** `WorkflowGraphLoader.LoadL1Async(IReadOnlyList<Guid> workflowIds, ct)` already accepts a list; call it with a single-element list `[workflowId]` per iteration. The validators (`CycleDetector`, `SchemaEdgeValidator`, `PayloadConfigSchemaValidator`) take a `WorkflowGraphSnapshot` and already operate per-snapshot — a single-workflow snapshot is a valid, smaller instance. [VERIFIED: `WorkflowGraphLoader.cs:67` signature + `WorkflowGraphSnapshot.cs` validator-scope contract (WR-03).]

### Anti-Patterns to Avoid
- **`IServer.Keys()` / `KEYS`** anywhere in production code (L2-PROJECT-07) — the Stop traversal is GET-and-follow, never enumeration.
- **MULTI/EXEC transaction** for the projection — D-03 mandates a batch (pipeline), not a transaction; MULTI/EXEC blocks the server on large projections.
- **`DateTime.UtcNow`** for `liveness.timestamp` — use injected `TimeProvider.GetUtcNow().UtcDateTime` (testability; AuditInterceptor precedent).
- **camelCase via `JsonNamingPolicy.CamelCase`** instead of `[JsonPropertyName]` — D-02 mandates explicit pins so the shape is independent of the options instance.
- **`Mapperly` for JSON** — L2-PROJECT-06 forbids; STJ directly.
- **Deleting processor keys in Stop** — D-06 explicit; they are shared and TTL-managed only.
- **FLUSHDB in test teardown** — forbidden (would wipe parallel test classes' keys).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Non-blocking multi-key delete | Manual per-key `DEL` loop | `KeyDeleteAsync(RedisKey[])` | Single round-trip; auto-selects UNLINK on ≥4.0 servers. |
| Pipelined multi-SET | Sequential `await StringSetAsync` ×N | `CreateBatch()` + `Task.WhenAll` | One network flush; partial-failure surfaces via faulting task (D-03). |
| Key string formatting | Inline `$"{prefix}{wf}:{step}"` scattered | `RedisProjectionKeys` (NEW) | Single source of truth (L2-PROJECT-02); one place to change scheme. |
| camelCase JSON | `JsonNamingPolicy` config juggling | `[property: JsonPropertyName]` per member | Shape is locked regardless of options; round-trippable for Phase 16. |
| Now-source | `DateTime.UtcNow` | injected `TimeProvider` | Deterministic tests (AuditInterceptor precedent). |
| Correlation propagation into logs | Manual log-field threading | Phase 4 `CorrelationIdMiddleware` MEL scope (AsyncLocal) | Already flows through async Redis ops (OBSERV-REDIS-02). |
| Cycle-safe graph walk | Recursion | Iterative BFS `visited` List | Mirror Phase 13 loader; recursion banned (L1-VALIDATE-03); terminates on cycles. |

**Key insight:** every "tricky" piece (batch semantics, UNLINK selection, cycle-safe traversal, correlation scope) already has an established codebase or library answer — the only genuinely new artifacts are `RedisProjectionKeys`, the 4 projection records, the writer body, the shared cleanup routine, and the `ProcessorKeyTtlDays` config knob.

## Runtime State Inventory

> This phase WRITES new runtime state (Redis keys) and CHANGES teardown expectations, so the inventory matters even though it's not a rename.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data (Redis) | Start now writes 3 keyspaces under `{KeyPrefix}` (prod `skp:`, test `test:cls-{Guid:N}:`). Processor keys persist with a 100-day TTL and are NEVER Stop-deleted. | Test teardown MUST sweep them (see below). Production: orphaned processor keys self-expire via TTL. |
| Live service config | None new — Redis connection string + `Redis:KeyPrefix` already in appsettings (Phase 12). Adding `Redis:ProcessorKeyTtlDays` (code + appsettings, both in git). | appsettings edit (in git); no out-of-git config. |
| OS-registered state | None — no OS scheduler / pm2 / systemd involvement. Verified: this is an HTTP service. | None. |
| Secrets/env vars | None new. `ConnectionStrings__Redis` already set defensively in compose env (Phase 12). | None. |
| Build artifacts | None — no package rename, no egg-info/compiled-binary concern. | None. |

**Critical teardown interaction (CONTEXT `<amendments>` TEST-REDIS):** Because processor keys carry a 100-day TTL (won't expire within a test run) and Stop never deletes them, any test that calls Start leaves residual `{prefix}{processorId}` keys. The Phase 12 `RedisFixture.DisposeAsync` already SCANs `"{KeyPrefix}*"` and `KeyDeleteAsync`-es ALL matches under the per-class prefix — and `{prefix}{processorId}` matches `{prefix}*`. **So the existing fixture already covers it** (verified: `RedisFixture.cs:62` pattern `$"{KeyPrefix}*"`). The amendment's concern is satisfied by the existing prefix-scoped SCAN+DEL; the planner must confirm no test bypasses `Phase8WebAppFactory`/`RedisFixture` when it Starts. FLUSHDB remains forbidden.

## Common Pitfalls

### Pitfall 1: `[JsonPropertyName]` on positional records ignored without `property:` target
**What goes wrong:** Field names serialize as PascalCase (`EntryStepIds`) despite the attribute, breaking the locked shape + Phase 16 asserts.
**Why:** On a positional record, a bare `[JsonPropertyName]` binds to the constructor parameter, not the generated property. STJ reads the property.
**Avoid:** Use `[property: JsonPropertyName("entryStepIds")]` on every positional member.
**Warning signs:** Round-trip test deserializes to defaults; raw JSON shows PascalCase keys.

### Pitfall 2: Forgetting `expiry: null` semantics / applying TTL to root or step keys
**What goes wrong:** TTL accidentally on root/step keys (they must be permanent until explicit cleanup) or a positive TTL applied when `ProcessorKeyTtlDays <= 0`.
**Why:** D-08 scopes TTL to processor keys only; `<=0` ⇒ no expiry.
**Avoid:** Compute `TimeSpan? ttl = days <= 0 ? null : TimeSpan.FromDays(days)`; pass `expiry: ttl` ONLY on the processor `StringSetAsync`; omit expiry (or pass null) on root/step.
**Warning signs:** Root/step keys vanish unexpectedly; `redis-cli TTL {key}` returns a positive value on a root key.

### Pitfall 3: Residual processor keys tripping the SCAN-zero-residual assertion
**What goes wrong:** A test Starts, leaves TTL'd processor keys, and `RedisFixture.DisposeAsync` throws "residual keys" — OR a test cleans with FLUSHDB and silently masks leaks across parallel classes.
**Why:** Processor keys are deliberately never Stop-deleted and outlive the run.
**Avoid:** Rely on the existing prefix-scoped SCAN+`KeyDeleteAsync` in `RedisFixture` (it matches all 3 key types under `{KeyPrefix}*`). Never FLUSHDB. Keep the BEFORE=AFTER `redis-cli --scan` invariant balanced — it holds because every test class's keys live under its unique `test:cls-*` prefix and are swept on dispose.
**Warning signs:** `InvalidOperationException: RedisFixture cleanup violation` at class teardown.

### Pitfall 4: Stop traversal failing on a dangling/missing per-step key
**What goes wrong:** A `null` `StringGetAsync` result throws on deserialize, aborting cleanup mid-walk.
**Why:** Partial-state from a prior failed Start can leave a root whose `entryStepIds` reference a missing step key.
**Avoid:** `if (stepJson.IsNullOrEmpty) continue;` — skip and continue (D-06); do NOT add the missing key to the delete list, do NOT fail.
**Warning signs:** `NullReferenceException`/`JsonException` mid-cleanup; a Stop 500 that should have been 204.

### Pitfall 5: Reusing the SAME `IDatabase`/multiplexer is correct — do NOT create per-key connections
**What goes wrong:** Constructing a new `ConnectionMultiplexer` per operation → connection storm.
**Why:** The multiplexer is a registered Singleton (Phase 12); `GetDatabase()` is a cheap pass-thru.
**Avoid:** Inject `IConnectionMultiplexer`, call `GetDatabase()` per `UpsertAsync`/cleanup; never `Connect()` in feature code.
**Warning signs:** Rising TCP connection count; `RedisConnectionException` under load.

### Pitfall 6: RFC 7807 Redis-failure body missing the offending op (OBSERV-REDIS-03)
**What goes wrong:** A `RedisConnectionException` 500 lacks the `UpsertAsync`/`KeyExistsAsync` operation name in Extensions, hiding root cause.
**Why:** The generic fallback handler doesn't know which Redis op failed.
**Avoid:** Either let the exception carry context (wrap with operation name) or have the Phase 4 customizer add the op to `ProblemDetails.Extensions`. Confirm the existing fallback handler already attaches correlationId (it does, via the customizer). Planner decides the cleanest seam.
**Warning signs:** Operators can't tell a Start-write failure from a Stop-exists failure from the 500 body.

### Pitfall 7: `KeyDeleteAsync` with an empty array
**What goes wrong:** Passing `Array.Empty<RedisKey>()` to `KeyDeleteAsync` is a wasted/edge-case round trip.
**Why:** A workflow with only a root and no reachable steps yields an empty step-key list.
**Avoid:** Delete the root unconditionally; only add the step-key delete task when `stepKeysToDelete.Count > 0` (as in Pattern 2).

## Code Examples

### RedisProjectionKeys (NEW — L2-PROJECT-02)
```csharp
// src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs
namespace BaseApi.Service.Features.Orchestration.Projection;

/// <summary>Single source of truth for all 3 L2 key formats (L2-PROJECT-02). Flat
/// scheme: no type discriminator — root and processor keys are both {prefix}{guid},
/// disambiguated only by GUID namespace.</summary>
internal static class RedisProjectionKeys
{
    public static string Root(string prefix, Guid workflowId) => $"{prefix}{workflowId}";
    public static string Step(string prefix, Guid workflowId, Guid stepId) => $"{prefix}{workflowId}:{stepId}";
    public static string Processor(string prefix, Guid processorId) => $"{prefix}{processorId}";
}
```

### RedisProjectionOptions (MODIFY — add ProcessorKeyTtlDays)
```csharp
/// <summary>Processor-key TTL in days (D-08, default 100). Refresh-on-write: every Start
/// re-SETs processor keys with this expiry. &lt;= 0 ⇒ no expiry (disable from config).</summary>
public int ProcessorKeyTtlDays { get; set; } = 100;
```
appsettings.json / appsettings.Development.json `"Redis"` section gains `"ProcessorKeyTtlDays": 100`.

### OrchestrationService factory (MODIFY — DI wiring)
```csharp
// OrchestrationServiceCollectionExtensions.AddOrchestrationFeature
services.AddScoped<OrchestrationService>(sp => new OrchestrationService(
    sp.GetRequiredService<BaseDbContext>(),
    sp.GetRequiredService<IValidator<IReadOnlyList<Guid>>>(),
    sp.GetRequiredService<IWorkflowGraphLoader>(),
    sp.GetRequiredService<CycleDetector>(),
    sp.GetRequiredService<SchemaEdgeValidator>(),
    sp.GetRequiredService<PayloadConfigSchemaValidator>(),
    sp.GetRequiredService<IRedisProjectionWriter>(),
    sp.GetRequiredService<IHttpContextAccessor>(),   // NEW (D-01)
    sp.GetRequiredService<TimeProvider>()));          // NEW (liveness timestamp) — confirm TimeProvider is registered
```
**Verify:** `TimeProvider` registration — AuditInterceptor uses it, so it is registered somewhere in Core DI; the writer (not the service) may instead consume it. Planner picks where liveness timestamp is stamped (writer is the natural home since it assembles the records). [ASSUMED: TimeProvider is DI-registered — confirm via grep before planning the ctor change.]

## State of the Art

| Old Approach (stale ROADMAP/REQ) | Current Approach (CONTEXT) | When Changed | Impact |
|----------------------------------|----------------------------|--------------|--------|
| Stop = Redis EXISTS only, NO DELETE, idempotent 204 | Stop = existence-gate + L2 cleanup (delete root+step), non-idempotent 422 on repeat | CONTEXT D-04/D-06 (2026-05-29) | ORCH-STOP-04/06 reversed; Phase 16 SC2/SC5 inverted. |
| `jobId = Guid.Empty` | `jobId = Guid.NewGuid()` per Start | CONTEXT D-05 | L2-PROJECT-03 amended; round-trip asserts must allow a fresh Guid. |
| Single batch L1 snapshot for all ids | Per-workflow L1 snapshot in a loop | CONTEXT D-07 | Phase 13 pipeline restructured; Phase 14 ValidationOrderFacts first-failure semantics change. |
| Processor keys no TTL | Processor keys TTL'd (refresh-on-write, default 100d) | CONTEXT D-08 | New config; TEST-REDIS teardown must sweep them (prefix SCAN covers it). |
| Start = plain overwrite | Start = delete-then-write (per-workflow pre-clean) | CONTEXT D-07 | Fixes orphaned per-step keys when a graph shrinks. |

**Deprecated/outdated:** ROADMAP Phase 15 SC3/SC4 ("existence-check, NO DELETE, KeyExistsAsync only") — superseded. REQUIREMENTS ORCH-STOP-04/06 line-item text — superseded. Planner SHOULD update REQUIREMENTS.md / ROADMAP.md to match (per CONTEXT `<domain>` note).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `TimeProvider` is DI-registered in Core (AuditInterceptor precedent) | Code Examples / Pitfalls | LOW — if not registered, register it once; AuditInterceptor's use strongly implies it is. Grep `AddSingleton<TimeProvider>` / `TimeProvider.System` before planning the ctor. |
| A2 | `[property: JsonPropertyName]` is the correct positional-record syntax under default STJ options | Pattern 3 / Pitfall 1 | LOW — well-established STJ behavior; verify with a round-trip unit test in Wave 0. |
| A3 | Existing `RedisFixture` prefix SCAN already sweeps processor keys | Runtime State / Pitfall 3 | LOW — verified pattern `$"{KeyPrefix}*"` at `RedisFixture.cs:62` matches all 3 key types. Confirm no test Starts outside the fixture. |
| A4 | `WorkflowReadDto.EntryStepIds` / `StepReadDto.NextStepIds` are populated by the loader's `with {}` enrichment (non-null on a real snapshot) | Pattern 3 assembly | LOW — verified in `WorkflowGraphLoader.cs:114/122`; coalesce to `[]` defensively anyway. |

## Open Questions

1. **Where is `liveness.timestamp` stamped — writer or service?**
   - Known: the writer assembles the records, so the writer is the natural home for `TimeProvider`.
   - Unclear: D-01 injects `IHttpContextAccessor` into the *service*; D-05 timestamp could go either place.
   - Recommendation: inject `TimeProvider` into the **writer** (it owns record assembly); keep `IHttpContextAccessor` on the **service** (it owns correlationId resolution + passes it down). Cleanest separation.

2. **RFC 7807 Redis-op-name attachment seam (OBSERV-REDIS-03).**
   - Known: Phase 4 customizer attaches correlationId; the fallback handler maps unhandled exceptions to 500.
   - Unclear: cleanest place to add `UpsertAsync`/`KeyExistsAsync` to `Extensions`.
   - Recommendation: planner reads the Phase 4 fallback handler + customizer; either wrap the Redis exception with a typed exception carrying the op name, or extend the customizer. Low-risk either way.

3. **Does the per-workflow loop's partial-state on a mid-loop 422 need any compensating action?**
   - Known: CONTEXT D-07 explicitly ACCEPTS partial state (A cleaned+rebuilt, B wiped).
   - Recommendation: no compensation; document the accepted behavior in tests + XML docs. This is a locked decision, not a bug.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Redis (compose `sk-redis`, host `localhost:6380`) | L2 writes + Stop cleanup + integration tests | Assumed running (Phase 12 compose) | 7.4.x-alpine | None — integration tests fail-loud if compose is down (correct). |
| Postgres (host `localhost:5433`) | L3 load (Start existence + LoadL1) | Assumed running (Phase 2 compose) | 17-alpine | None. |
| Elasticsearch + OTel collector | OBSERV-REDIS-02 E2E (logs round-trip) | Assumed running (Phase 11 compose) | ES 8.15 / collector 0.152.0 | E2E test is `[Category("E2E")]` — skippable if stack not up, but full-suite gate expects it. |
| `redis-cli` (host) | Phase-close BEFORE=AFTER `--scan` SHA-256 gate | Assumed (ships in alpine image; host CLI for the ritual) | — | None. |

**Missing dependencies with no fallback:** none expected in the configured dev environment; all are the standing compose stack from Phases 2/11/12. Integration + E2E tests correctly fail-loud if the stack is down.

## Validation Architecture

> Nyquist validation is ENABLED (`workflow.nyquist_validation: true`).

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit.v3 3.2.2 + xunit.v3.assert |
| Config file | none — convention-based; `Phase8WebAppFactory` (`IClassFixture`) is the integration entry point |
| Quick run command | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Orchestration" -- --max-threads 1` |
| Full suite command | `dotnet test` (requires compose stack up: Postgres, Redis, ES, collector) |

> xUnit1051: every async test MUST pass `TestContext.Current.CancellationToken` (TreatWarningsAsErrors). [VERIFIED: SchemasLogsE2ETests pattern.]

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| L2-PROJECT-01 | 3 keyspaces written via batch on Start | integration (real Redis) | `dotnet test --filter "Name~Upsert_Writes_Three_Keyspaces"` | ❌ Wave 0 |
| L2-PROJECT-02 | Key formats exact (`{p}{wf}`, `{p}{wf}:{step}`, `{p}{proc}`) | unit | `dotnet test --filter "Name~RedisProjectionKeys"` | ❌ Wave 0 |
| L2-PROJECT-03 | Root value shape + camelCase + `jobId=NewGuid` + `correlationId` | integration + round-trip unit | `--filter "Name~RootProjection_Shape"` | ❌ Wave 0 |
| L2-PROJECT-04 | Step value shape; `entryCondition` as int; `nextStepIds=[]` terminal | round-trip unit + integration | `--filter "Name~StepProjection_Shape"` | ❌ Wave 0 |
| L2-PROJECT-05 | Processor value shape; `inputDefinition`/`outputDefinition`; TTL set | integration (assert `KeyTimeToLive`) | `--filter "Name~ProcessorProjection_Ttl"` | ❌ Wave 0 |
| L2-PROJECT-06 | JSON via STJ not Mapperly | negative-grep | `rg "Mapperly|ToRead" src/.../Projection/` returns no JSON-mapping use | ❌ Wave 0 (grep fact) |
| L2-PROJECT-07 | No `IServer.Keys()`/`KEYS` in production | negative-grep | `rg "IServer|\.Keys\(|KEYS" src/BaseApi.Service` returns 0 | ❌ Wave 0 (grep fact) |
| ORCH-START-02 | 204 on full success | integration | `--filter "Name~Start_Returns204"` | ❌ Wave 0 |
| ORCH-START-04 | Redis-down → 500 + RFC 7807 + correlationId | integration (dead-Redis fixture) | `--filter "Name~Start_RedisDown_500"` | ❌ Wave 0 |
| ORCH-START-05 | delete-then-write: shrunk graph leaves no orphan step keys | integration (Start, mutate, re-Start, SCAN) | `--filter "Name~ReStart_Removes_Orphan_Step"` | ❌ Wave 0 |
| ORCH-START-07/08 | correlationId in error body; 422/500 ProducesResponseType | integration + swagger-shape | `--filter "Name~Start_ProducesResponse"` | ❌ Wave 0 |
| ORCH-STOP-02 | all roots exist → cleanup → 204 | integration | `--filter "Name~Stop_AllExist_204"` | ❌ Wave 0 |
| ORCH-STOP-03 | any root missing → 422 full missing list, NO deletion | integration | `--filter "Name~Stop_Missing_422_NoDelete"` | ❌ Wave 0 |
| ORCH-STOP-04 (rev) | root + per-step deleted; processor keys remain | integration (SCAN before/after) | `--filter "Name~Stop_Deletes_Root_Step_Keeps_Processor"` | ❌ Wave 0 |
| ORCH-STOP-06 (rev) | repeated Stop → 422 (non-idempotent) | integration | `--filter "Name~Stop_Repeat_422"` | ❌ Wave 0 |
| ORCH-STOP-07 | Redis-down → 500 + RFC 7807 | integration (dead-Redis) | `--filter "Name~Stop_RedisDown_500"` | ❌ Wave 0 |
| D-06 traversal | cycle-safe walk terminates; dangling step skipped | integration (cyclic graph in Redis) | `--filter "Name~Stop_CyclicGraph_Terminates"` | ❌ Wave 0 |
| OBSERV-REDIS-02 | Redis op log doc with correlationId reaches ES | E2E (extend SchemasLogsE2ETests) | `--filter "Name~OrchestrationLogsE2E"` | ❌ Wave 0 |
| OBSERV-REDIS-03 | RFC 7807 body carries op name + correlationId | integration | `--filter "Name~RedisFailure_ProblemDetails_OpName"` | ❌ Wave 0 |

### Sampling Rate
- **Per task commit:** quick run (`--filter "FullyQualifiedName~Orchestration"` / `~Projection`).
- **Per wave merge:** full suite (`dotnet test`) — requires compose stack.
- **Phase gate:** full suite green + `redis-cli --scan | sort | sha256sum` BEFORE=AFTER balanced + `psql \l` SHA-256 `0d98b0de…0aac127` unchanged, 3 consecutive GREEN.

### Wave 0 Gaps
- [ ] `Projection/RedisProjectionKeysTests.cs` — L2-PROJECT-02 unit facts.
- [ ] `Projection/ProjectionRecordRoundTripTests.cs` — camelCase + enum-as-int + cron-null + `nextStepIds=[]` round-trip (L2-PROJECT-03/04/05).
- [ ] `Orchestration/RedisProjectionWriterFacts.cs` — integration against `RedisFixture` (3-keyspace write + TTL assert via `KeyTimeToLive`).
- [ ] `Orchestration/StartLoopFacts.cs` — per-workflow loop, delete-then-write, partial-state, dead-Redis 500.
- [ ] `Orchestration/StopCleanupFacts.cs` — existence gate, traversal delete, processor-key retention, cyclic-graph termination, dangling-step skip, repeat-422.
- [ ] `Observability/OrchestrationLogsE2ETests.cs` — extend Phase 11 pattern for OBSERV-REDIS-02.
- [ ] Negative-grep facts (or CI assertions) for L2-PROJECT-06 / L2-PROJECT-07 / OBSERV-REDIS-01 (no `IServer.Keys`/`KEYS`, no Mapperly-for-JSON, no OTel-Redis package).
- [ ] Update Phase 14 `ValidationOrderFacts` to per-workflow first-failure semantics (CONTEXT amendment).
- [ ] Invert Phase 16 SC2/SC5 facts (root+step removed post-Stop, processors intact) — likely a Phase 16 task, flag for the planner.
- Framework install: none — xUnit.v3 + `RedisFixture` + `Phase8WebAppFactory` already present.

## Security Domain

> `security_enforcement` not present in config; treat as enabled. This phase is a backend/Redis data-projection feature with no new auth surface.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | No auth introduced this phase (orchestration endpoints inherit existing pipeline). |
| V3 Session Management | no | Stateless HTTP; no sessions. |
| V4 Access Control | no | No new authorization rules. |
| V5 Input Validation | yes | `WorkflowIdsValidator` (existing) enforces non-empty GUID list on Start/Stop body. CorrelationId already CRLF-sanitized by `CorrelationIdMiddleware` (ASCII-printable, ≤128). |
| V6 Cryptography | no | No crypto; no hand-rolled hashing. |
| V7 Error Handling & Logging | yes | RFC 7807 via Phase 4; correlationId in logs + error bodies (OBSERV-REDIS-02/03); no secret/PII leakage in Redis-failure messages. |

### Known Threat Patterns
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Log/CRLF injection via X-Correlation-Id propagated into root key | Tampering | Already sanitized at the middleware (ASCII-printable 0x20–0x7E, ≤128 chars) before it reaches `HttpContext.Items`. [VERIFIED: `CorrelationIdMiddleware.IsValid`.] |
| Untrusted payload string written into `payload` field | Tampering/Injection | `payload` originates from `AssignmentEntity.Payload` (already validated on create); serialized as a JSON string value (STJ escapes) — no injection into the key structure. |
| Redis connection exhaustion (connection storm) | DoS | Singleton multiplexer; `GetDatabase()` per call (Pitfall 5). |
| Information disclosure in 500 body | Information Disclosure | RFC 7807 body carries correlationId + op name, NOT the raw connection string or stack trace (Phase 4 discipline). |

## Sources

### Primary (HIGH confidence)
- Codebase reads (authoritative): `15-CONTEXT.md`, `OrchestrationService.cs`, `RedisProjectionWriter.cs`/`IRedisProjectionWriter.cs`, `RedisProjectionOptions.cs`, `WorkflowGraphLoader.cs`, `WorkflowGraphSnapshot.cs`, `OrchestrationServiceCollectionExtensions.cs`, `OrchestrationController.cs`, `CorrelationIdMiddleware.cs`, `RedisServiceCollectionExtensions.cs`, `RedisFixture.cs`, the 5 `*Dtos.cs`, `StepEntryCondition.cs`, `SchemasLogsE2ETests.cs`, `Directory.Packages.props`, `config.json`, `REQUIREMENTS.md` (L2/ORCH/OBSERV line-items), Phase 12 `12-RESEARCH.md`.
- Context7 `/websites/stackexchange_github_io_stackexchange_redis` — CreateBatch + Task.WhenAll batch pattern; `StringSetAsync(key, value, expiry:)`; SCAN/KEYS server-targeted distinction.

### Secondary (MEDIUM confidence)
- WebSearch (verified against GitHub source `RedisDatabase.GetDeleteCommand`): `KeyDeleteAsync` auto-selects UNLINK on servers supporting it, else DEL — no explicit UNLINK method.

### Tertiary (LOW confidence)
- None — all load-bearing claims verified against codebase or official source.

## Metadata

**Confidence breakdown:**
- Standard stack / SE.Redis API: HIGH — Context7 + GitHub source + already-pinned CPM.
- Architecture / seams: HIGH — every seam read in this session.
- DTO shapes / field pinning: HIGH — DTOs + enum read directly; positional-record `[property:]` is established STJ behavior (A2).
- Test/validation architecture: HIGH — RedisFixture + E2E pattern read directly.
- Two discretion seams (timestamp home, RFC 7807 op-name attachment): MEDIUM — left open intentionally per CONTEXT discretion (Open Questions 1–2).

**Research date:** 2026-05-29
**Valid until:** 2026-06-28 (stable; SE.Redis 2.13.1 is CPM-pinned, codebase seams are current).
