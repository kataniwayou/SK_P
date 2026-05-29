# Phase 15: L2 Redis projection write + Stop existence check - Context

**Gathered:** 2026-05-29
**Status:** Ready for planning

<domain>
## Phase Boundary

Fill the Phase 13 no-op `RedisProjectionWriter` seam so a validated Start populates the 3 Redis keyspaces (root / per-step / per-processor) with the locked DTO shapes, and redefine Stop and Start orchestration around per-workflow processing:

- **Start** becomes a **per-workflow stop-then-build loop**: clean up the workflow's existing L2 keys, then L3 → L1 → L2, then dispose L1 — per workflow.
- **Stop** becomes an **L2 cleanup** operation: gated on existence, it deletes the root + per-step keys for each workflowId (never the shared processor keys).

> **NOTE — this discussion deliberately amends several requirements locked in REQUIREMENTS.md / ROADMAP.md and shipped in Phases 13/14/16-plan.** See `<amendments>` below. Downstream agents MUST treat `<amendments>` as authoritative where it conflicts with the original requirement text, and the planner SHOULD update REQUIREMENTS.md / ROADMAP.md to match.

</domain>

<decisions>
## Implementation Decisions

### D-01 — CorrelationId plumbing (Start only)
- Inject `IHttpContextAccessor` into `OrchestrationService` (already registered via `AddHttpContextAccessor`; same pattern `AuditInterceptor` uses).
- `StartAsync` resolves `HttpContext.Items["CorrelationId"]` **once** and passes it as an **explicit parameter** to the writer: `UpsertAsync(snapshot, correlationId, ct)`. The writer stays HTTP-agnostic and unit-testable.
- Public `StartAsync` / `StopAsync` signatures are **unchanged** (correlationId resolved internally).
- The root key keeps a **single `correlationId`** field = the Start POST's `X-Correlation-Id`. Stop does **not** write any correlationId; Stop's error-path correlationId is supplied by the existing Phase 4 ProblemDetails customizer (reads `HttpContext.Items`), so the accessor is not needed on the Stop write path.

### D-02 — L2 projection DTO modeling
- Dedicated **internal record DTOs** in `Features/Orchestration/Projection/`: `WorkflowRootProjection`, `StepProjection`, `ProcessorProjection`, plus a shared `LivenessProjection`.
- Each member is pinned with **`[JsonPropertyName("...")]`** so the locked camelCase field names are guaranteed regardless of the `JsonSerializerOptions` passed (default `System.Text.Json` options do NOT camelCase). Records are round-trippable for Phase 16 deserialization asserts.
- Enum-as-int (`entryCondition`) and `cron: null` fall out of default options — no extra config.
- **Mapperly is NOT used** for DTO → JSON (System.Text.Json directly, per L2-PROJECT-06). ReadDto → projection-record assembly is hand-written in the writer (cross-entity: `payload` from the Assignments dict keyed by `stepId`; `nextStepIds` coalesced to `[]` when the `StepReadDto.NextStepIds` is null).

**Locked value shapes:**
- **Root** `{prefix}{workflowId}` → `{ entryStepIds[], cron, jobId, liveness{timestamp,interval,status}, correlationId }`
- **Per-step** `{prefix}{workflowId}:{stepId}` → `{ entryCondition, processorId, payload, nextStepIds[] }` (terminal → `[]`, never null; no liveness)
- **Per-processor** `{prefix}{processorId}` → `{ inputDefinition, outputDefinition, liveness }` (field names ARE `inputDefinition`/`outputDefinition`)

### D-03 — Start projection batch-write failure semantics
- `UpsertAsync` uses `CreateBatch()` → queue per-key `StringSetAsync` → `batch.Execute()` → `await Task.WhenAll(tasks)` (per L2-PROJECT-01; batch ≠ transaction, no MULTI/EXEC).
- On a mid-batch failure the first faulting task's exception bubbles to the Phase 4 handler → **500 + RFC 7807 + correlationId**; already-written keys are **left as-is** (partial-state stance; self-heals because re-Start now pre-cleans).
- Emit **one MEL warning naming the `workflowId`** ("partial state may exist") before the exception rethrows. L1 cleanup still runs in the per-workflow `using` disposal.

### D-04 — Stop existence gate + status codes
- Stop endpoint: **batch `KeyExistsAsync`** on the requested **root** keys; if **any** missing → **422** with the **full** missing-workflowIds list and **no deletion**; if all exist → run D-06 cleanup → **204**.
- Collect ALL missing ids (not fail-fast). Redis-side failure → 500 (ORCH-STOP-07).

### D-05 — Liveness & jobId
- `liveness.status` is left at **`"Pending"`** everywhere (root + processor). There is **NO Start/Stop status lifecycle** in this phase — `Starting`/`Stopping` is explicitly **deferred** (see `<deferred>`).
- Start writes `liveness = { timestamp: now, interval: 0, status: "Pending" }` on root + processor keys (`interval` deferred).
- `jobId = Guid.NewGuid()` — a **fresh Guid generated per Start** (NOT `Guid.Empty`). A re-Start generates a new jobId.

### D-06 — Stop = L2 cleanup (delete root + per-step; never processors)
- On the all-exist path, for each `workflowId`:
  1. Read the root key → `entryStepIds[]`.
  2. **Traverse the step graph in Redis** (cycle-safe `visited` set): GET each `{workflowId}:{stepId}`, follow `nextStepIds[]`, collecting every reachable per-step key.
  3. **Collect-then-delete**: traverse fully first, THEN batch-delete (`CreateBatch` + `KeyDeleteAsync`, UNLINK-style non-blocking) the root key + collected per-step keys.
- **NEVER delete `{processorId}` keys** (shared across workflows).
- Dangling/absent per-step key during traversal → **skip and continue** (don't fail the cleanup).
- Partial-failure mid-cleanup → **500**; partially-deleted keys left as-is; a re-Stop cleans whatever roots still exist.
- Targeted **GET-and-follow only** — `KEYS` / `IServer.Keys()` remain forbidden; no Postgres.
- **Repeated Stop is non-idempotent**: a 2nd identical Stop finds the roots already deleted → **422** ("nothing to stop").

### D-07 — Start = per-workflow stop-then-build loop
```
StartAsync(workflowIds):
  existence-check ALL ids upfront          # 404 fast-fail before ANY mutation (D-08 gate preserved)
  foreach workflowId:
    StopCleanup(workflowId)                # shared routine, TOLERANT (absent → no-op, no 422)
    using snapshot = LoadL1(workflowId)    # L3 → L1, PER WORKFLOW (was batch in Phase 13)
      Validate(snapshot)                   # cycle → schema-edge → payload (gate order preserved)
      UpsertAsync(snapshot, correlationId) # L1 → L2 (D-03)
    # snapshot disposed                    # clean up L1
```
- The **Stop cleanup routine is a shared internal service** reused by the Stop endpoint (with the D-04 422 gate) and Start's pre-clean (tolerant, no gate).
- L1 snapshot is now **per-workflow** (Phase 13 built a single batch snapshot).
- **Cleanup-then-build consequence (accepted):** a re-Start of a now-invalid workflow deletes its old L2, then 422s without rebuilding — the prior good projection is gone. This is the intended "stop then start" order.
- **Partial-state across workflows (accepted):** a mid-loop 422 on workflow B leaves A already cleaned + rebuilt and B wiped (consistent with the documented partial-state stance).

### Claude's Discretion
- ReadDto → projection-record assembly implementation details.
- Exact MEL log message wording and levels.
- Final `DEL` vs `UNLINK` pick for cleanup (recommended UNLINK-style `KeyDeleteAsync`).
- Redis-side traversal data structure (mirror the Phase 13 iterative BFS `visited` pattern).
- `liveness.timestamp` source — prefer injected `TimeProvider` (AuditInterceptor precedent) over `DateTime.UtcNow` for testability.
- Whether `RedisProjectionKeys` (L2-PROJECT-02 key formatter) already exists or must be created — verify; it is the single source of truth for all 3 key formats.

</decisions>

<amendments>
## Requirement Amendments (authoritative over original text)

This discussion intentionally changes the following. Planner SHOULD update REQUIREMENTS.md / ROADMAP.md to match.

- **ORCH-STOP-04** — REVERSED. Was "Stop performs NO DELETE/eviction; L2 entries remain in place." Now Stop **deletes** the root + per-step keys (never processor keys). Still no Postgres.
- **ORCH-STOP-06** — CHANGED. Was "no state mutation; repeated Stop → 204 (idempotent)." Now Stop mutates (deletes); repeated Stop → **422** (non-idempotent).
- **ORCH-START-05** — CHANGED. Idempotency is now **delete-then-write** (per-workflow pre-clean), not plain overwrite. Fixes orphaned per-step keys when a graph shrinks between Starts.
- **L2-PROJECT-03** — CHANGED. `jobId` `Guid.Empty` → **`Guid.NewGuid()` per Start**. `correlationId` field name is **unchanged** (the stopCorrelationId/rename idea was considered and dropped). `liveness.status` default stays `"Pending"` (no lifecycle this phase).
- **L2-PROJECT-07 / Phase 15 SC5** — Stop now performs **targeted GET-and-follow traversal** for cleanup. `KEYS` / `IServer.Keys()` remain forbidden; the SCAN-only enumeration rule is intact (traversal is by GET, not enumeration).
- **Phase 13 pipeline (ORCH-SPLIT)** — restructured from a single batch snapshot to a **per-workflow loop** with a per-workflow snapshot + per-iteration disposal.
- **Phase 14 `ValidationOrderFacts`** — first-failure semantics change from "first failing gate across the whole batch" to "first failing gate within the first failing workflow, in iteration order." These facts likely need updating.
- **Phase 16 SC2 / SC5** — REVERSED. Post-Stop, root + per-step keys are **gone** (processor keys remain). The "Stop does NOT delete any L2 keys / post-Stop SCAN matches pre-Stop" facts must be **inverted** (assert root + per-step removed, processors intact). The `redis-cli --scan` BEFORE=AFTER full-suite invariant still holds across a *balanced* suite (residual `test:cls-*` = 0).

</amendments>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Requirements & roadmap (read with `<amendments>` applied on top)
- `.planning/REQUIREMENTS.md` — L2-PROJECT-01..07, ORCH-START-01..08, ORCH-STOP-01..07, OBSERV-REDIS-01..04 (the line-item spec; field shapes, key formatter contract, SSRF/observability discipline)
- `.planning/ROADMAP.md` §"Phase 15" — success criteria SC1..SC5 + the "v3.2.0 invariants MUST NOT regress" block; also §"Phase 12/13/14" for the Redis/orchestration history this builds on

### Existing orchestration code (the seams being filled)
- `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` — StartAsync/StopAsync to be restructured (D-07/D-06); ExistenceCheckAsync (D-08 gate to preserve)
- `src/BaseApi.Service/Features/Orchestration/Projection/IRedisProjectionWriter.cs` + `RedisProjectionWriter.cs` — no-op seam to fill (D-02/D-03)
- `src/BaseApi.Service/Features/Orchestration/WorkflowGraphSnapshot.cs` — L1 snapshot (now per-workflow); WR-03 validation-scope contract
- `src/BaseApi.Service/Features/Orchestration/Loading/WorkflowGraphLoader.cs` — LoadL1Async (may need per-workflow variant); Phase 13 iterative BFS pattern to mirror for Redis-side traversal
- `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` — DI factory (inject IHttpContextAccessor into the service)
- `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs` — `HttpContext.Items["CorrelationId"]` source (D-01)

### No external specs
- No external ADRs/design docs — v3.2.0 ADRs are archived under `milestones/`. REQUIREMENTS.md + ROADMAP.md + this CONTEXT are the spec of record.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `IRedisProjectionWriter` / `RedisProjectionWriter` no-op seam — fill `UpsertAsync` (now `UpsertAsync(snapshot, correlationId, ct)`).
- `OrchestrationService.StartAsync` / `StopAsync` + private `ExistenceCheckAsync` (Postgres id-projection 404 gate — keep for the upfront batch check).
- `WorkflowGraphSnapshot` (per-workflow now) + `IWorkflowGraphLoader.LoadL1Async`.
- `IHttpContextAccessor` — already registered (`AddHttpContextAccessor`, idempotent).
- `RedisProjectionOptions` (KeyPrefix `skp:`) + `IConnectionMultiplexer`/`IDatabase` from Phase 12 DI; `StackExchange.Redis` 2.13.1.
- `RedisProjectionKeys` (L2-PROJECT-02 key formatter — verify exists / create; single source of truth for all key formats).

### Established Patterns
- Phase 13 iterative cycle-terminating BFS — reuse the `visited`-set traversal shape for Redis-side GET-and-follow cleanup (D-06).
- Phase 4 RFC 7807 + `X-Correlation-Id` + SQLSTATE→HTTP mapping; ProblemDetails customizer reads `HttpContext.Items`.
- Phase 5 OTel MEL → Elasticsearch bridge; `X-Correlation-Id` log scope flows via AsyncLocal (OBSERV-REDIS-02).
- `System.Text.Json` default options (PascalCase verbatim → `[JsonPropertyName]` required for camelCase).
- `TimeProvider` injection (AuditInterceptor) for testable timestamps.

### Integration Points
- `OrchestrationServiceCollectionExtensions.AddOrchestrationFeature` — add `IHttpContextAccessor` to the explicit `OrchestrationService` factory.
- `OrchestrationController` — 422/500 `[ProducesResponseType]` (ORCH-START-08); Stop endpoint surfaces the D-04 422 list.
- Phase 16 will assert all 3 keyspaces + the inverted Stop-cleanup facts against real Postgres + Redis.

</code_context>

<specifics>
## Specific Ideas

- "For simplicity the Start flow: perform stop service then start service, per workflow" — Start = stop-then-build loop (D-07).
- "L3 to L1 to L2 then clean up L1, per workflow" — per-workflow pipeline with per-iteration L1 disposal (D-07).
- "Stop will clean up L2 keys of the workflowIds[]: start with root and follow the steps; never remove the processor keys" — D-06 traversal + selective delete.
- "Don't update the liveness.status (will be discussed further but not now)" — lifecycle deferred (D-05).
- "Every start generate guid for jobId" — D-05.

</specifics>

<deferred>
## Deferred Ideas

- **`liveness.status` Start/Stop lifecycle** (`Starting` / `Stopping`) — explicitly deferred by the user ("discussed further but not now"). Status stays `"Pending"` this phase.
- **`stopCorrelationId`** — considered (split correlationId into start/stop), then dropped; root keeps a single `correlationId`.
- **Per-processor liveness lifecycle** — processor `liveness.status` is never status-managed this phase.
- **Full Stop-side per-processor eviction** — processor keys are intentionally never deleted (shared); any future processor GC is a separate concern.
- **`jobId` run/scheduling semantics + real `liveness.interval`** — only the Guid is generated now; downstream meaning deferred.
- **OBSERV-REDIS-04** Redis-side metrics (latency/command counts via profiling API → Prometheus) — future milestone per the requirement.

</deferred>

---

*Phase: 15-l2-redis-projection-write-stop-existence-check*
*Context gathered: 2026-05-29*
