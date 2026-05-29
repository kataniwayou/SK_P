# Phase 15: L2 Redis projection write + Stop existence check - Pattern Map

**Mapped:** 2026-05-29
**Files analyzed:** 19 (8 NEW, 11 MODIFY)
**Analogs found:** 19 / 19 (every new/modified file has a concrete in-repo analog)

> Read with CONTEXT `<amendments>` authoritative. Line/path refs below are read-verified this session.

---

## File Classification

| New/Modified File | New/Mod | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|---------|------|-----------|----------------|---------------|
| `Features/Orchestration/Projection/RedisProjectionKeys.cs` | NEW | utility (pure formatter) | transform | `RES §"RedisProjectionKeys"` example; no prior static-key class — closest in-repo idiom is the const-string discipline in `CorrelationIdMiddleware.cs:51-53` | role-match |
| `Features/Orchestration/Projection/LivenessProjection.cs` | NEW | model (DTO record) | transform | `WorkflowGraphSnapshot.cs:52` positional record; entity ReadDtos (`SchemaDtos.cs:33`) | exact (record idiom) |
| `Features/Orchestration/Projection/WorkflowRootProjection.cs` | NEW | model (DTO record) | transform | `WorkflowDtos.cs:57` `WorkflowReadDto` | exact |
| `Features/Orchestration/Projection/StepProjection.cs` | NEW | model (DTO record) | transform | `StepDtos.cs:45` `StepReadDto` | exact |
| `Features/Orchestration/Projection/ProcessorProjection.cs` | NEW | model (DTO record) | transform | `ProcessorDtos.cs:42` `ProcessorReadDto` + `SchemaDtos.cs:33` (`Definition` source) | exact |
| `Features/Orchestration/Projection/RedisProjectionWriter.cs` | MODIFY (fill no-op) | service (writer) | CRUD / batch-write | self (no-op seam) + `WorkflowGraphLoader.cs` assembly idiom; SE.Redis batch (RES Pattern 1) | role-match |
| `Features/Orchestration/Projection/IRedisProjectionWriter.cs` | MODIFY (signature) | service interface | request-response | self (`IRedisProjectionWriter.cs:7`) | exact |
| `Features/Orchestration/Projection/IRedisL2Cleanup.cs` (+ impl `RedisL2Cleanup.cs`) | NEW | service (shared routine) | event-driven traversal + batch-delete | `WorkflowGraphLoader.LoadStepsBreadthFirstAsync` (`WorkflowGraphLoader.cs:139-176`) — BFS `visited`-List to mirror Redis-side | role-match (traversal pattern is exact) |
| `Features/Orchestration/OrchestrationService.cs` | MODIFY | service (orchestrator) | request-response / loop | self (`OrchestrationService.cs:75-92`) + `AuditInterceptor.cs:40-61` (IHttpContextAccessor) | exact |
| `Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` | MODIFY (DI factory) | config (DI) | — | self (`OrchestrationServiceCollectionExtensions.cs:52-64`) | exact |
| `Features/Orchestration/OrchestrationController.cs` | MODIFY (`[ProducesResponseType]` 422/500) | controller | request-response | self (`OrchestrationController.cs:43-46`) | exact |
| `BaseApi.Core/Configuration/RedisProjectionOptions.cs` | MODIFY (+`ProcessorKeyTtlDays`) | config (options) | — | self (`RedisProjectionOptions.cs:19`) | exact |
| `BaseApi.Service/appsettings.json` | MODIFY (`Redis:ProcessorKeyTtlDays`) | config | — | self (`appsettings.json:21-26`) | exact |
| `BaseApi.Service/appsettings.Development.json` | MODIFY (add `Redis` section + key) | config | — | `appsettings.json:21-26` (section is MISSING in Development — see Pitfall A) | partial |
| Stop 422 / Redis-op exception type (reuse or extend) | NEW/REUSE | exception + handler | request-response | `OrchestrationValidationException.cs:21` + `OrchestrationValidationExceptionHandler.cs:25` | exact (422 RFC 7807 idiom) |
| `tests/.../Projection/RedisProjectionKeysTests.cs` | NEW | test (unit) | — | existing `tests/BaseApi.Tests` xUnit.v3 facts | role-match |
| `tests/.../Projection/ProjectionRecordRoundTripTests.cs` | NEW | test (unit, STJ round-trip) | — | xUnit.v3 facts | role-match |
| `tests/.../Orchestration/{RedisProjectionWriterFacts,StartLoopFacts,StopCleanupFacts}.cs` | NEW | test (integration) | — | `Phase8WebAppFactory.cs:27-35` (already wires `RedisFixture`) + `RedisFixture.cs` | exact |
| `tests/.../Observability/OrchestrationLogsE2ETests.cs` | NEW | test (E2E) | — | `SchemasLogsE2ETests.cs:34-90` | exact |
| `tests/.../Composition/RedisFixture.cs` | VERIFY (no change expected) | test infra | — | self (`RedisFixture.cs:52-94`) — prefix SCAN+DEL already sweeps processor keys | exact |

---

## Pattern Assignments

### `RedisProjectionKeys.cs` (NEW — utility/formatter, L2-PROJECT-02)

**Analog:** const-string discipline in `CorrelationIdMiddleware.cs:51-53` (single source of truth for literal keys); shape from RES Code Examples §"RedisProjectionKeys".

**Pattern:** internal static class, three pure formatters, no I/O. Flat single-prefix scheme, NO type discriminator (D-02 key-scheme note).
```csharp
internal static class RedisProjectionKeys
{
    public static string Root(string prefix, Guid workflowId) => $"{prefix}{workflowId}";
    public static string Step(string prefix, Guid workflowId, Guid stepId) => $"{prefix}{workflowId}:{stepId}";
    public static string Processor(string prefix, Guid processorId) => $"{prefix}{processorId}";
}
```
Note: Root and Processor formats are identical (`{prefix}{guid}`) — disambiguated only by GUID namespace. This is intentional (D-02).

---

### Projection record DTOs (NEW — model, D-02)

**Analog:** positional-record idiom is everywhere — `WorkflowGraphSnapshot.cs:52` (`internal sealed record … : IDisposable`) and every entity ReadDto (`WorkflowDtos.cs:57`, `StepDtos.cs:45`, `ProcessorDtos.cs:42`, `SchemaDtos.cs:33`). **None of those use `[JsonPropertyName]`** — this phase introduces it because default STJ does NOT camelCase (RES Pattern 3 / Pitfall 1).

**Pattern (`[property: JsonPropertyName]` REQUIRED on positional members):**
```csharp
internal sealed record LivenessProjection(
    [property: JsonPropertyName("timestamp")] DateTime Timestamp,
    [property: JsonPropertyName("interval")]  int Interval,
    [property: JsonPropertyName("status")]    string Status);   // "Pending" everywhere this phase (D-05)
```
- **Enum-as-int:** `StepEntryCondition` (`StepEntryCondition.cs:14-22`, values 0–5) serializes as int under default options — DO NOT add `JsonStringEnumConverter` (L2-PROJECT-04).
- **`cron: null`** falls out of default options; `nextStepIds`/`entryStepIds` coalesce to `[]`, never null.

**Hand-written ReadDto → record assembly (in the writer, L2-PROJECT-06 — NOT Mapperly):** mirror the `with { … }` enrichment idiom from `WorkflowGraphLoader.cs:106-123`. Field sources confirmed from the DTOs read this session:
| Projection field | Source | DTO ref |
|---|---|---|
| root `entryStepIds` | `WorkflowReadDto.EntryStepIds ?? []` | `WorkflowDtos.cs:62` (nullable) |
| root `cron` | `WorkflowReadDto.CronExpression` | `WorkflowDtos.cs:64` |
| root `jobId` | `Guid.NewGuid()` per Start (D-05) | — |
| root `correlationId` | param from service (D-01) | — |
| step `entryCondition` | `StepReadDto.EntryCondition` (int) | `StepDtos.cs:52` |
| step `processorId` | `StepReadDto.ProcessorId` | `StepDtos.cs:50` |
| step `payload` | `snapshot.Assignments.Values.First(a => a.StepId == stepId).Payload` | `AssignmentDtos.cs:43-44` (`StepId`, `Payload`) |
| step `nextStepIds` | `StepReadDto.NextStepIds ?? []` | `StepDtos.cs:51` (nullable) |
| proc `inputDefinition` | `InputSchemaId is { } sid ? snapshot.Schemas[sid].Definition : null` | `ProcessorDtos.cs:48` + `SchemaDtos.cs:38` |
| proc `outputDefinition` | `OutputSchemaId is { } sid ? snapshot.Schemas[sid].Definition : null` | `ProcessorDtos.cs:49` + `SchemaDtos.cs:38` |

---

### `RedisProjectionWriter.cs` (MODIFY — fill no-op; service, batch CRUD, D-03/D-08)

**Current state (`RedisProjectionWriter.cs:5-8`):** `UpsertAsync(snapshot, ct) => Task.CompletedTask` — empty seam.

**Analogs:**
- DI/multiplexer: `RedisServiceCollectionExtensions.cs:11-14` doc — `IConnectionMultiplexer` is the registered Singleton; call `GetDatabase()` per op (INFRA-COMP-03), never `Connect()`.
- TimeProvider for `liveness.timestamp`: `AuditInterceptor.cs:41,60` — `_clock.GetUtcNow().UtcDateTime`. `TimeProvider` IS DI-registered (`PersistenceServiceCollectionExtensions.cs:24` `services.AddSingleton(TimeProvider.System)`) — A1 RESOLVED, no new registration needed.
- Options: read `RedisProjectionOptions.KeyPrefix` + new `ProcessorKeyTtlDays` via `IOptions<RedisProjectionOptions>` (`RedisServiceCollectionExtensions.cs:63` binds `Redis:*`).

**Core batch-write pattern (RES Pattern 1):**
```csharp
var db = _multiplexer.GetDatabase();
var batch = db.CreateBatch();
var tasks = new List<Task>();
tasks.Add(batch.StringSetAsync(RedisProjectionKeys.Root(prefix, wf.Id), rootJson));        // no TTL
foreach (...) tasks.Add(batch.StringSetAsync(RedisProjectionKeys.Step(...), stepJson));      // no TTL
TimeSpan? ttl = days <= 0 ? null : TimeSpan.FromDays(days);                                   // D-08
foreach (...) tasks.Add(batch.StringSetAsync(RedisProjectionKeys.Processor(...), procJson, expiry: ttl));
batch.Execute();
await Task.WhenAll(tasks);   // first faulting task surfaces → bubbles to Phase 4 → 500
```
- Emit one MEL warning naming `workflowId` before rethrow on partial failure (D-03). Logging idiom: `WorkflowGraphSnapshot.cs:71` `Logger.LogDebug(...)`.
- TTL **only** on processor SET; root/step carry none (Pitfall 2).

**Recommendation (RES Open Q1):** stamp `liveness.timestamp` here (writer owns record assembly) — inject `TimeProvider` into the WRITER; keep `IHttpContextAccessor` on the SERVICE (correlationId passed down as a param). Cleanest separation.

---

### `IRedisProjectionWriter.cs` (MODIFY — signature)

**Current (`IRedisProjectionWriter.cs:7`):** `Task UpsertAsync(WorkflowGraphSnapshot snapshot, CancellationToken ct);`
**Change (D-01):** add explicit `string correlationId` param → `UpsertAsync(WorkflowGraphSnapshot snapshot, string correlationId, CancellationToken ct)`. Keeps the writer HTTP-agnostic.

---

### `IRedisL2Cleanup` + `RedisL2Cleanup.cs` (NEW — shared Stop-cleanup routine, D-06/D-07)

**Analog (exact traversal shape):** `WorkflowGraphLoader.LoadStepsBreadthFirstAsync` at `WorkflowGraphLoader.cs:139-176`. Mirror its iterative wave-BFS:
- `var visited = new List<Guid>();` (List, NOT HashSet — `WorkflowGraphLoader.cs:142`, REQ convention; termination on cycles).
- `currentWave = entryStepIds.Where(id => id != Guid.Empty).Distinct().ToList();` (`WorkflowGraphLoader.cs:146`).
- `while (currentWave.Count > 0) { toLoad = currentWave.Where(id => !visited.Contains(id))…; if (toLoad.Count == 0) break; … }` (`WorkflowGraphLoader.cs:148-152`).
- Multi-child fan-out: collect ALL `nextStepIds` into the next wave (`WorkflowGraphLoader.cs:170-172`).

**Difference from analog:** the loader walks the `StepNextSteps` junction in Postgres; this routine GETs `{prefix}{wf}:{stepId}` JSON from Redis and reads `nextStepIds` off the deserialized `StepProjection` (RES Pattern 2). `KEYS`/`IServer.Keys()` FORBIDDEN (L2-PROJECT-07) — GET-and-follow only.

**Cleanup tolerance + delete (D-06):**
```csharp
var rootJson = await db.StringGetAsync(RedisProjectionKeys.Root(prefix, wf));
if (rootJson.IsNullOrEmpty) return;                         // tolerant: absent root → no-op (Start preclean)
...
if (stepJson.IsNullOrEmpty) continue;                       // dangling step → skip+continue (D-06 / Pitfall 4)
...
var batch = db.CreateBatch();
var delTasks = new List<Task> { batch.KeyDeleteAsync(RedisProjectionKeys.Root(prefix, wf)) };
if (stepKeysToDelete.Count > 0) delTasks.Add(batch.KeyDeleteAsync(stepKeysToDelete.ToArray()));  // Pitfall 7
batch.Execute(); await Task.WhenAll(delTasks);
```
- `KeyDeleteAsync(RedisKey[])` auto-selects UNLINK on the 7.4.x server — satisfies "UNLINK-style" discretion.
- **NEVER** delete `{processorId}` keys (D-06).
- Existing precedent for `KeyDeleteAsync(RedisKey[])` + cursor SCAN coexisting: `RedisFixture.cs:62-69`.

**Routine has TWO callers (D-07):** Stop endpoint (gated — runs only after the D-04 all-exist check) and Start preclean (tolerant — absent root → no-op). The routine itself is always tolerant; the GATE lives in `StopAsync`, not the routine.

**DI:** register `services.AddScoped<IRedisL2Cleanup, RedisL2Cleanup>();` next to the writer registration (`OrchestrationServiceCollectionExtensions.cs:64`), and add it to the `OrchestrationService` factory closure.

---

### `OrchestrationService.cs` (MODIFY — orchestrator, D-01/D-06/D-07)

**Current StartAsync (`OrchestrationService.cs:75-84`):** single-snapshot pipeline (existence-check → ONE `LoadL1Async(workflowIds)` → 3 validators → `UpsertAsync`).
**Current StopAsync (`OrchestrationService.cs:91-92`):** delegates to `ExistenceCheckAsync` (Postgres). **Replace** with Redis EXISTS gate + cleanup.

**Restructure StartAsync to per-workflow loop (D-07)** — keep `ExistenceCheckAsync` (`OrchestrationService.cs:109-143`) UNCHANGED as the upfront 404 gate (runs before any mutation):
```csharp
await ExistenceCheckAsync(workflowIds, ct);                 // unchanged 404 fast-fail
var correlationId = _httpContextAccessor.HttpContext?.Items["CorrelationId"] as string ?? string.Empty;  // D-01, ONCE
foreach (var workflowId in workflowIds)
{
    await _cleanup.StopCleanupAsync(workflowId, ct);        // tolerant
    using var snapshot = await _loader.LoadL1Async(new[] { workflowId }, ct);   // PER-WORKFLOW (single-element list)
    _cycleDetector.Validate(snapshot);                      // gate order LOCKED (Phase 14)
    _schemaEdgeValidator.Validate(snapshot);
    _payloadConfigSchemaValidator.Validate(snapshot);
    await _redisProjectionWriter.UpsertAsync(snapshot, correlationId, ct);
}   // snapshot disposed each iteration (using) — L1 cleanup on success AND throw
```
- **No new loader variant** (RES Pattern 5): `LoadL1Async(IReadOnlyList<Guid>)` (`WorkflowGraphLoader.cs:67`) accepts a list — pass `new[] { workflowId }`.
- CorrelationId resolution analog: `AuditInterceptor.cs:61` reads the accessor; item key literal `"CorrelationId"` from `CorrelationIdMiddleware.cs:52,69`.

**StopAsync (D-04/D-06):** rule-validate ids (reuse `_idsValidator` as `ExistenceCheckAsync` does at `OrchestrationService.cs:127`) → batch `KeyExistsAsync` on root keys → collect ALL missing → if any missing throw the 422 exception with full list (NO deletion) → else `foreach` `StopCleanupAsync`. Redis failure → 500 (bubbles to Fallback).

**Ctor + DI:** add `IHttpContextAccessor` (+ `IRedisL2Cleanup`) to the internal ctor (`OrchestrationService.cs:50-66`) with the same `?? throw new ArgumentNullException` guard idiom. `TimeProvider` goes to the WRITER not the service (RES Open Q1).

---

### `OrchestrationServiceCollectionExtensions.cs` (MODIFY — DI factory)

**Analog:** self, the explicit internal-ctor factory at `OrchestrationServiceCollectionExtensions.cs:52-59`. The factory closure exists precisely because the ctor is internal (CS0051) — add the new deps as `sp.GetRequiredService<…>()` lines:
```csharp
services.AddScoped<OrchestrationService>(sp => new OrchestrationService(
    sp.GetRequiredService<BaseDbContext>(),
    sp.GetRequiredService<IValidator<IReadOnlyList<Guid>>>(),
    sp.GetRequiredService<IWorkflowGraphLoader>(),
    sp.GetRequiredService<CycleDetector>(),
    sp.GetRequiredService<SchemaEdgeValidator>(),
    sp.GetRequiredService<PayloadConfigSchemaValidator>(),
    sp.GetRequiredService<IRedisProjectionWriter>(),
    sp.GetRequiredService<IRedisL2Cleanup>(),              // NEW
    sp.GetRequiredService<IHttpContextAccessor>()));       // NEW (D-01) — registered via AddHttpContextAccessor (idempotent)
services.AddScoped<IRedisL2Cleanup, RedisL2Cleanup>();    // NEW, beside line 64 writer registration
```
- `IHttpContextAccessor` already registered (AuditInterceptor consumes it). `TimeProvider` already Singleton (`PersistenceServiceCollectionExtensions.cs:24`) — inject into the writer (a separate `AddScoped`/factory at line 64).

---

### `OrchestrationController.cs` (MODIFY — `[ProducesResponseType]`, ORCH-START-08)

**Analog:** self, `OrchestrationController.cs:43-46` (Start) / `:59-62` (Stop). Add `[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]` and `…Status500InternalServerError` to BOTH actions (existing 204/400/404 attributes stay). The `ProblemDetails`/`StatusCodes` usings (`OrchestrationController.cs:2-3`) are already present. No method-body change (service throws; Phase 4 handlers map).

---

### `RedisProjectionOptions.cs` (MODIFY — +ProcessorKeyTtlDays, D-08)

**Analog:** self, `RedisProjectionOptions.cs:19` (`KeyPrefix` auto-property with default).
```csharp
/// <summary>Processor-key TTL in days (D-08, default 100). Refresh-on-write. &lt;= 0 ⇒ no expiry.</summary>
public int ProcessorKeyTtlDays { get; set; } = 100;
```
Bound automatically by the existing `cfg.GetSection("Redis")` bind at `RedisServiceCollectionExtensions.cs:63`.

---

### `appsettings.json` / `appsettings.Development.json` (MODIFY)

**`appsettings.json`** — analog self, the `"Redis"` section at `appsettings.json:21-26`. Add `"ProcessorKeyTtlDays": 100` inside it.

**`appsettings.Development.json`** — **PARTIAL analog (see Pitfall A):** this file currently has NO `"Redis"` section (only `ConnectionStrings:Redis` at `appsettings.Development.json:11`). Add a `"Redis": { "ProcessorKeyTtlDays": 100 }` section mirroring `appsettings.json:21-26`. (KeyPrefix default `"skp:"` comes from the options class, so Development can override TTL only.)

---

### Stop 422 + Redis-op-name exception (NEW/REUSE — D-04 / OBSERV-REDIS-03)

**Analog (exact):** `OrchestrationValidationException.cs:21-74` (static factory methods, `Gate`/`Title`/`Offending`, `ErrorsExtension` envelope) + `OrchestrationValidationExceptionHandler.cs:25-56` (claims it → 422 + RFC 7807; sets ONLY Status/Title/Detail/Extensions["errors"]; Phase 4 customizer adds correlationId + instance).

**For Stop missing-list (D-04):** add a `StopExistence(IReadOnlyList<Guid> missing)` static factory to `OrchestrationValidationException` (or a sibling exception) producing a 422 with the full missing-id list as `Offending` — same `string.Join(", ", missing)` detail shape already used at `OrchestrationService.cs:141`. Reuse the existing handler (it claims `OrchestrationValidationException`).

**For OBSERV-REDIS-03 op-name in 500 body:** the `FallbackExceptionHandler.cs:43-58` writes a bare ProblemDetails; the op name (`UpsertAsync`/`KeyExistsAsync`) is NOT currently attached. RES Open Q2: either wrap the Redis exception in a typed exception carrying the op name and have a small handler add it to `Extensions`, OR extend the Phase 4 customizer. Planner picks the seam; both handlers above show the `Extensions["…"]` write idiom.

---

### Tests (NEW)

**Unit (`RedisProjectionKeysTests`, `ProjectionRecordRoundTripTests`):** xUnit.v3 `[Fact]` facts; round-trip asserts camelCase + enum-as-int + `cron:null` + `nextStepIds:[]` (Pitfall 1 catch). No fixture needed (pure).

**Integration (`RedisProjectionWriterFacts`, `StartLoopFacts`, `StopCleanupFacts`):** analog `Phase8WebAppFactory.cs:27-35` — it ALREADY composes `RedisFixture` (fields `_redisFixture`, `_redisConnectionStringOverride`, `_skipRedisFixture`). Use it as `IClassFixture`. Per-class `KeyPrefix` isolation from `RedisFixture.cs:40`. Assert TTL via `db.KeyTimeToLive(...)`. Every async test MUST pass `TestContext.Current.CancellationToken` (xUnit1051, TreatWarningsAsErrors — see `SchemasLogsE2ETests.cs:46`).

**E2E (`OrchestrationLogsE2ETests`):** analog `SchemasLogsE2ETests.cs:34-90` verbatim structure — `[Trait("Category","E2E")]`, `[Collection("Observability")]`, per-test unique `X-Correlation-Id`, `ElasticsearchTestClient().PollEsForLog(...)`. Swap the Schema POST for an orchestration Start; assert the Redis-op log doc carries correlationId (OBSERV-REDIS-02). Do NOT assert on version string (CHECKER WARNING #7, `SchemasLogsE2ETests.cs:24-32`).

**`RedisFixture.cs` (VERIFY, no change expected):** the prefix SCAN+DEL at `RedisFixture.cs:62-69` matches `{KeyPrefix}*` → already sweeps processor keys (which are TTL'd, never Stop-deleted). RES Runtime State / Pitfall 3 / A3: the existing teardown satisfies the CONTEXT TEST-REDIS amendment. Planner confirms no test Starts OUTSIDE `Phase8WebAppFactory`/`RedisFixture`.

---

## Shared Patterns

### Iterative cycle-safe BFS (`visited`-List)
**Source:** `WorkflowGraphLoader.cs:139-176`.
**Apply to:** `RedisL2Cleanup` Stop traversal (D-06). Mirror the wave loop + `visited` List (NOT HashSet) + `Distinct()` dedup + multi-child fan-out. Recursion is banned (L1-VALIDATE-03).

### SE.Redis multiplexer usage (Singleton → GetDatabase per op)
**Source:** `RedisServiceCollectionExtensions.cs:11-14` doc; `RedisFixture.cs:56` (`Multiplexer.GetDatabase()`).
**Apply to:** writer + cleanup. Inject `IConnectionMultiplexer`, call `GetDatabase()` per op; never `Connect()` in feature code (Pitfall 5).

### IHttpContextAccessor + null-safe Items read
**Source:** `AuditInterceptor.cs:40,61` (`_httpContextAccessor.HttpContext?.…`). Item key literal `"CorrelationId"` from `CorrelationIdMiddleware.cs:52,69`.
**Apply to:** `OrchestrationService.StartAsync` correlationId resolution (D-01). Coalesce to `string.Empty` when null.

### TimeProvider for testable now
**Source:** `AuditInterceptor.cs:41,60` (`_clock.GetUtcNow().UtcDateTime`). Registered Singleton at `PersistenceServiceCollectionExtensions.cs:24`.
**Apply to:** `RedisProjectionWriter` `liveness.timestamp` (D-05; prefer over `DateTime.UtcNow`).

### RFC 7807 via IExceptionHandler (handler sets Status/Title/Detail/Extensions; customizer adds correlationId)
**Source:** `OrchestrationValidationExceptionHandler.cs:36-54` (422) and `FallbackExceptionHandler.cs:41-58` (500). Handlers DO NOT set correlationId/instance (`OrchestrationValidationException.cs:11-13` doc).
**Apply to:** Stop 422 missing-list, Redis-failure 500 + op-name (D-04 / ORCH-STOP-07 / OBSERV-REDIS-03).

### `?? throw new ArgumentNullException(nameof(x))` ctor guards
**Source:** `OrchestrationService.cs:59-65`, `WorkflowGraphLoader.cs:47-53`.
**Apply to:** every new/extended ctor (writer, cleanup, modified OrchestrationService).

### `with { … }` record enrichment for hand assembly
**Source:** `WorkflowGraphLoader.cs:114,122`.
**Apply to:** projection-record assembly in the writer (L2-PROJECT-06; STJ not Mapperly).

---

## No Analog Found

None. Every file maps to a concrete in-repo analog. The only genuinely NEW artifacts (`RedisProjectionKeys`, the 4 projection records, the cleanup routine) follow established idioms (static-formatter, positional-record, BFS-traversal) even where no identical prior file exists.

---

## Pitfalls Surfaced During Mapping

**Pitfall A — `appsettings.Development.json` has NO `Redis` section.** Only `ConnectionStrings:Redis` exists (`appsettings.Development.json:9-12`); the `Redis:KeyPrefix`/`Serialization` section lives ONLY in `appsettings.json:21-26`. Adding `Redis:ProcessorKeyTtlDays` to Development requires CREATING the `"Redis"` object there (mirror `appsettings.json:21-26` but TTL-only). RES §"Recommended Project Structure" assumed both files have the section — they do not.

**Pitfall B — `[property: JsonPropertyName]` is NEW to this codebase.** No existing record uses it; existing DTOs round-trip via default STJ PascalCase. Wave 0 round-trip test is the guard (Pitfall 1).

**Pitfall C — `WorkflowReadDto.EntryStepIds` / `StepReadDto.NextStepIds` are declared NULLABLE** (`WorkflowDtos.cs:62`, `StepDtos.cs:51`) even though the loader enriches them non-null (`WorkflowGraphLoader.cs:114,122`). Coalesce to `[]` defensively in the writer (A4).

**Pitfall D — `StopCleanupAsync` tolerance vs gate split.** The shared routine is ALWAYS tolerant (absent root → no-op). The 422 existence GATE lives in `StopAsync` (batch `KeyExistsAsync` BEFORE the loop), NOT inside the routine — otherwise Start's preclean would 422 (D-07).

## Metadata

**Analog search scope:** `src/BaseApi.Service/Features/Orchestration/**`, `src/BaseApi.Core/{Configuration,Middleware,Persistence,Exceptions,DependencyInjection}/**`, `src/BaseApi.Service/Features/{Workflow,Step,Processor,Assignment,Schema}/*Dtos.cs`, `tests/BaseApi.Tests/{Composition,Observability}/**`, `appsettings*.json`.
**Files read this session:** 22.
**Pattern extraction date:** 2026-05-29.
