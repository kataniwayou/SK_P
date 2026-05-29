---
phase: 15
slug: l2-redis-projection-write-stop-existence-check
status: secured
threats_open: 0
threats_total: 20
threats_closed: 17
accepted_risks: 3
asvs_level: 1
created: 2026-05-29
---

# Phase 15 — Security: L2 Redis Projection Write + Stop Existence Check

**Generated:** 2026-05-29
**ASVS Level:** 1
**Suite:** 227/227 GREEN (verified this session)

---

## Threat Verification Summary

**Threats Closed:** 17 / 17 mitigate-disposition threats CLOSED
**Accepted Risks:** 3 / 3 accept-disposition threats logged below

---

## Mitigate Threats — All CLOSED

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-15-01 | Tampering — field-name drift | mitigate | CLOSED | All 4 records carry `[property: JsonPropertyName(...)]` on every member: `LivenessProjection.cs:12-14`, `WorkflowRootProjection.cs:12-16`, `StepProjection.cs:13-17`, `ProcessorProjection.cs:11-14`. Round-trip test asserts exact camelCase keys: `ProjectionRecordRoundTripTests.cs:38-57`. |
| T-15-03 | Tampering — enum as string | mitigate | CLOSED | No `JsonStringEnumConverter` in `src/BaseApi.Service/Features/Orchestration/Projection/` (grep: 0 matches). `ProjectionRecordRoundTripTests.cs:61-73` asserts `"entryCondition":4` and `DoesNotContain("Always")`. |
| T-15-04 | Tampering / Injection — user payload/schema JSON in step/processor values | mitigate | CLOSED | Values written via `JsonSerializer.Serialize(stepProjection)` / `JsonSerializer.Serialize(procProjection)` (STJ escapes embedded quotes/control chars): `RedisProjectionWriter.cs:95,106`. Key construction uses only `RedisProjectionKeys.Step/Processor` with Guid interpolation — no string concatenation: `RedisProjectionKeys.cs:20-24`. |
| T-15-05 | Tampering (CRLF) — correlationId echoed into root value | mitigate | CLOSED | `CorrelationIdMiddleware.IsValid` (line 97-106) enforces ASCII-printable (0x20..0x7E) + length ≤ 128 before writing to `HttpContext.Items["CorrelationId"]`. Writer reads that value unchanged: `RedisProjectionWriter.cs:52,71`. |
| T-15-06 | DoS — connection storm | mitigate | CLOSED | `IConnectionMultiplexer` injected as Singleton via ctor (line 46, `RedisProjectionWriter.cs`). Only `GetDatabase()` called per operation (line 74) — no `Connect()`/`ConnectAsync()` anywhere in the writer or cleanup. |
| T-15-07 | Info Disclosure — RedisConnectionException leaking connection string | mitigate | CLOSED | Writer only rethrows (line 120, `RedisProjectionWriter.cs`). `FallbackExceptionHandler.cs:43-58` emits `Title: "Internal Server Error"`, `Detail: "An unexpected error occurred."` — no connection string, message, or stack in the body. Only the `redisOp` fixed literal is added to Extensions (line 56-59). |
| T-15-08 | DoS — unbounded key enumeration | mitigate | CLOSED | `RedisL2Cleanup.cs` uses only `StringGetAsync` (line 63) and `KeyDeleteAsync` (line 76-77). Grep for `IServer`, `.Keys(`, `.KeysAsync(`, `KEYS` across `src/BaseApi.Service/Features/Orchestration/*.cs`: 0 code matches (single comment-only hit in `IRedisL2Cleanup.cs:17` — in XML doc, stripped by `RedisDisciplineGuardFacts.No_Keys_Enumeration_In_Projection`). |
| T-15-09 | DoS — runaway traversal on cyclic graph | mitigate | CLOSED | `visited` is `List<Guid>` (line 48, `RedisL2Cleanup.cs`); `!visited.Contains(id)` guard (lines 55, 68) terminates on cycles. `StopCleanupFacts.Stop_CyclicGraph_Terminates` (line 96-120) seeds A→B→A and asserts the call returns without hanging. |
| T-15-10 | Tampering — deletion of shared processor keys | mitigate | CLOSED | `RedisL2Cleanup.cs` never constructs `RedisProjectionKeys.Processor(...)` (grep: 0 matches). `StopCleanupFacts.Stop_Deletes_Root_Step_Keeps_Processor` (line 64-91) asserts `KeyExistsAsync(ProcKey)` is TRUE after cleanup. |
| T-15-11 | Availability — cleanup aborting on dangling step | mitigate | CLOSED | `if (stepJson.IsNullOrEmpty) continue;` at `RedisL2Cleanup.cs:64` skips and continues the BFS walk. `StopCleanupFacts.Stop_DanglingStep_Skipped` (line 124-143) proves no throw and root still deleted. |
| T-15-12 | Spoofing/Tampering — malformed/duplicate/empty Guid list | mitigate | CLOSED | `OrchestrationService.StartAsync`: `ExistenceCheckAsync` (line 103) runs null-body guard + `_idsValidator.ValidateAndThrowAsync` FIRST, before any Redis/Postgres mutation. `StopAsync` (lines 168-176): identical null-body guard + `_idsValidator.ValidateAndThrowAsync` run before the Redis EXISTS batch. Both paths reject bad input as 400 before touching Redis. |
| T-15-13 | Info Disclosure — 500 body leaking connection string/stack | mitigate | CLOSED | `FallbackExceptionHandler.cs:46-48`: generic title/detail only. Lines 56-59: only the fixed `redisOp` string from `exception.Data["redisOp"]` is added to Extensions. `StartLoopFacts.Start_RedisDown_500` (line 215) and `StopGateFacts.Stop_RedisDown_500` (line 210) both assert `DoesNotContain("localhost")` and `DoesNotContain("RedisConnectionException")`. |
| T-15-14 | Tampering (log injection) — X-Correlation-Id echoed into error body + root value | mitigate | CLOSED | `CorrelationIdMiddleware.IsValid` (lines 97-106) enforces ASCII-printable (0x20..0x7E) before the value enters `HttpContext.Items`. The writer and handler are read-only consumers of the already-sanitized value. |
| T-15-15 | Repudiation — Redis ops not correlated in logs | mitigate | CLOSED | `CorrelationIdMiddleware.InvokeAsync` (line 80-81) calls `_logger.BeginScope(new Dictionary { ["CorrelationId"] = corrId })`, pushing the id onto the MEL AsyncLocal scope for all logs emitted on that request. No new OTel-Redis instrumentation added (OBSERV-REDIS-01 negative-grep confirmed). |
| T-15-17 | Repudiation — Redis ops not traceable | mitigate | CLOSED | `OrchestrationLogsE2ETests.Start_Surfaces_RedisWrite_LogRecord_In_Elasticsearch_With_CorrelationId` (line 95-143) drives a real Start, polls ES for a log doc with the sent `X-Correlation-Id`, and asserts the hit is non-null and the correlationId is present in the raw JSON. |
| T-15-18 | Tampering — re-introduction of OTel-Redis package | mitigate | CLOSED | `RedisDisciplineGuardFacts.No_OtelRedis_Package_Referenced` (line 37-58) asserts `Directory.Packages.props` and all `.csproj` files do not contain `OpenTelemetry.Instrumentation.StackExchangeRedis`. Grep against `Directory.Packages.props` confirms 0 matches. |
| T-15-19 | DoS — re-introduction of KEYS/IServer.Keys() | mitigate | CLOSED | `RedisDisciplineGuardFacts.No_Keys_Enumeration_In_Projection` (line 63-88) strips comments then regex-matches `IServer\b`, `.Keys\s*(`, `.KeysAsync\s*(`, `\bKEYS\b` across all `.cs` under `Features/Orchestration/`. All code-level matches: 0. |

---

## Accepted Risks

| Threat ID | Category | Rationale |
|-----------|----------|-----------|
| T-15-02 | Info Disclosure — TTL config | `ProcessorKeyTtlDays` is a plain integer day-count with no secret content. It lives in `appsettings.json` alongside `KeyPrefix`, both committed to the repository (same trust level as all other configuration knobs). No disclosure risk. |
| T-15-16 | DoS — unbounded Start/Stop work | Work is bounded by the caller-supplied workflow-id list size and each workflow's reachable step graph. v1 carries no distributed lock (last-write-wins; partial-state across concurrent Starts is accepted per ORCH-START-06). No new mitigation warranted at this ASVS level. |
| T-15-20 | Info Disclosure — correlationId in ES log doc | The correlationId is a non-secret, per-request opaque identifier (either a caller-supplied ASCII-printable value ≤ 128 chars or a server-generated `Guid.NewGuid():N`). Its presence in Elasticsearch is intentional for traceability (OBSERV-REDIS-02). The ASCII-sanitization at `CorrelationIdMiddleware.IsValid` prevents injection of sensitive data via the header. |

---

## Unregistered Threat Flags

None. All threat flags from SUMMARY.md map to registered threat IDs above.

---

## Key Implementation References

| File | Relevant Threats |
|------|-----------------|
| `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs` | T-15-04 (Guid-only interpolation, no string concat) |
| `src/BaseApi.Service/Features/Orchestration/Projection/LivenessProjection.cs` | T-15-01 (`[property: JsonPropertyName]` on all members) |
| `src/BaseApi.Service/Features/Orchestration/Projection/WorkflowRootProjection.cs` | T-15-01, T-15-05 (correlationId field) |
| `src/BaseApi.Service/Features/Orchestration/Projection/StepProjection.cs` | T-15-01, T-15-03 (enum int, no string converter) |
| `src/BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs` | T-15-01, T-15-04 |
| `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs` | T-15-04, T-15-06, T-15-07 |
| `src/BaseApi.Service/Features/Orchestration/Projection/RedisL2Cleanup.cs` | T-15-08, T-15-09, T-15-10, T-15-11 |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` | T-15-12, T-15-13, T-15-14, T-15-15 |
| `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs` | T-15-05, T-15-14 |
| `src/BaseApi.Core/Exceptions/Handlers/FallbackExceptionHandler.cs` | T-15-07, T-15-13 |
| `tests/.../Projection/ProjectionRecordRoundTripTests.cs` | T-15-01, T-15-03 |
| `tests/.../Orchestration/StopCleanupFacts.cs` | T-15-09, T-15-10, T-15-11 |
| `tests/.../Orchestration/StopGateFacts.cs` | T-15-12, T-15-13 |
| `tests/.../Orchestration/StartLoopFacts.cs` | T-15-12, T-15-13 |
| `tests/.../Orchestration/RedisDisciplineGuardFacts.cs` | T-15-15, T-15-18, T-15-19 |
| `tests/.../Observability/OrchestrationLogsE2ETests.cs` | T-15-17 |
