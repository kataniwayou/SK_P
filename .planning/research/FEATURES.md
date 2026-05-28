# Feature Research — v3.3.0 Orchestration L3 → L1 → L2 Build Pipeline

**Domain:** Workflow / data-pipeline orchestration projection layer — a materialized read model in Redis (L2) built from a relational source-of-truth (L3 Postgres) via a transient in-memory compile step (L1), feeding external Orchestrator + Scheduler consumers.
**Researched:** 2026-05-28
**Confidence:** HIGH on ecosystem patterns (cross-referenced across Temporal / Argo / Airflow / generic Redis materialized-view literature); MEDIUM on exact DTO shape preferences (consumer-dependent, but converged conclusions are well-supported); HIGH on Stop/Start mechanics (clear ecosystem precedent).

---

## How to Read This Document

Categories drive what lands in v3.3.0 requirements vs. defers to v3.4+:

- **TABLE STAKES** — Must ship in v3.3.0. Missing = external Orchestrator/Scheduler cannot consume the projection or cannot reason about correctness after a Start call.
- **DIFFERENTIATOR** — Adds material value, may include in v3.3.0 if cheap; otherwise defer to v3.4+. Optional for ship.
- **ANTI-FEATURE** — Looks attractive (often suggested by external operators or appears in adjacent workflow systems) but is explicitly OUT-OF-SCOPE for v3.3.0, either by milestone scope or by ecosystem evidence that the cost exceeds the value at this stage.

PROJECT.md / MILESTONES.md anchors are cited as `[LOCKED:<topic>]` when the v3.3.0 milestone scope text or v3.2.0 invariants are the source.

---

## 1. L3 Fetch + L1 Build (Transient In-Memory Materialization)

### Table Stakes

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| **Bounded fetch scoped to requested `WorkflowIds`** | Consumers send a finite list; loading every workflow into memory would scale-bomb the API as the catalog grows | LOW | Single `WHERE WorkflowId = ANY(@ids)` per entity table (5 SELECTs total). Reuse existing `BaseDbContext.Set<TEntity>()`. `[LOCKED: L1 transient]` |
| **Flat `Dictionary<Guid, EntityDto>` per entity type** | Compile step needs O(1) lookup by id when walking edges; nested-object trees force re-traversal | LOW | Five dictionaries (Workflows, Assignments, Steps, Processors, Schemas). Use existing Mapperly mappers to convert Entity → ReadDto. |
| **Existence validation before traversal** | Walking edges into missing ids is the most common bug in graph compilers — silent skip vs. loud failure is a correctness choice; loud is the only safe one for a projection consumers will rely on | LOW | After fetch, for each requested WorkflowId not present → 422 with id list. For each `Workflow.EntryStepIds[*]` not present → 422. `[LOCKED: validation order]` |
| **Explicit teardown contract (try/finally)** | A request-scoped dictionary leaked across requests is invisible until OOM at 3am. Explicit `.Clear()` in finally makes the lifetime visible in the code | LOW | `try { ... } finally { dict.Clear(); visited.Clear(); }`. .NET GC will eventually collect, but explicit Clear() makes ownership obvious in code review. `[LOCKED: L1 cleanup]` |
| **Single transactional snapshot of L3 reads** | A Start that reads Schema, then Processor, then Step from a mutating database can build a self-inconsistent L1 (e.g., Processor references a Schema that was deleted between the two reads) | LOW | Wrap all 5 fetches in a single `using var tx = await ctx.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead)` (or rely on Postgres MVCC + a single round-trip per entity). Reuse v3.2.0 `BaseDbContext`. |

### Differentiators

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| **Parallel fetch of independent entity tables** | Schema/Processor/Step/Assignment/Workflow have no fetch-order dependency; `await Task.WhenAll(...)` shaves ~5× the per-query latency at scale | LOW | Free; just `await Task.WhenAll(loadSchemas, loadProcessors, ...)`. Risk: shared `DbContext` is NOT thread-safe — must spin up scoped contexts or serialize. Defer unless profiling shows latency pain. |
| **L1 build telemetry counters** | Operators want to know "compile took N ms for M steps across K workflows" without scraping per-request logs | LOW | One Histogram + 3 Counters on the existing OTel `Meter`. Reuse Phase 5 metric pipeline; add `orchestration.compile.duration_ms`, `orchestration.compile.steps_walked`, `orchestration.compile.errors_total`. |

### Anti-Features (v3.3.0)

| Feature | Why Tempting | Why Out of Scope | Alternative |
|---------|--------------|------------------|-------------|
| **Long-lived in-memory cache of L1 between requests** | "Why rebuild the same workflow's L1 on every Start?" | The whole point of v3.3.0 is that L2 (Redis) is the cache. L1 is a per-Start scratchpad; caching it adds invalidation logic (an entire CRUD-side cache-busting surface) that v3.2.0 deliberately avoided | Make L1 short-lived; if consumers want a snapshot, they read L2. |
| **Lazy fetching during traversal** | "Don't load steps we don't walk" | Lazy fetching during DFS leaks `DbContext` into the traversal layer, breaks the "fetch → validate → build → write" phase model, and N+1s under concurrent load | Eager-fetch all 5 entity tables up front; traversal works against memory only. |

---

## 2. Workflow-Graph Traversal & Validation Gates

### Table Stakes

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| **DFS from every `Workflow.EntryStepIds[*]`** | Workflow is a DAG with possibly multiple entry points; missing an entry = silent-incomplete projection | LOW | Standard iterative DFS with a `Stack<Guid>` + a per-traversal `HashSet<Guid> visited`. Walks `StepEntity.NextStepIds` via the existing `StepNextSteps` junction (already loaded as part of Step DTO). `[LOCKED: traversal scope]` |
| **Cycle detection with explicit 422** | A → B → A loops the projection writer forever; consumers cannot reason about a cyclic graph; even one-step self-loops must be caught | LOW | Standard DFS three-color (white/gray/black) or per-path `HashSet` — if next step is already in current-path set → cycle. Return 422 with the offending `(parentStepId, childStepId)` pair. `[LOCKED: cycle 422]` |
| **Missing `NextStepId` is 422 (not silent skip)** | A Step pointing to a deleted child is a data-integrity bug, not "the graph ends here" | LOW | Lookup in `Steps` dict; absent → 422 with `(parentStepId, childStepId)`. **Distinction:** `NextStepIds` collection that's empty/null = terminal step (legal). `NextStepIds` containing an id that resolves to no Step = error. |
| **Strict Schema-edge compatibility gate** | An external consumer that reads `(processorId → outputDefinition)` and the downstream processor's `inputDefinition` will assume they're compatible because the projection said the workflow compiled. Silent acceptance = type-unsafe pipeline at runtime | MEDIUM | For each edge `(parent → child)` walked: compare `parent.Processor.OutputSchemaId` vs `child.Processor.InputSchemaId`. Strict Guid equality; either-side null passes (Phase 10 source/sink semantics). 422 with offending `(parentStepId, childStepId)`. `[LOCKED: strict equality, null passes]` |
| **Payload ↔ ConfigSchema validation gate** | Closes v3.2.0-deferred VALID-21; validating Payload at Assignment-PUT was rejected (N2) because the Payload may legitimately predate the Processor; validating at orchestration-start is the right ratchet | MEDIUM | For each Assignment: resolve `Step.ProcessorId → Processor.ConfigSchemaId → Schema.Definition`. Run existing v3.2.0 JSON Schema validator (draft 2020-12, SSRF-disabled). 422 with offending `assignmentId` on failure. `[LOCKED: VALID-21 closes here]` |
| **Mandatory validation order** | Reordering gates produces different error messages for the same broken input — a consumer correcting issues will hit a different 422 next time, with no clear sense of progress | LOW | Lock the order: **existence → cycles → schema-edge compat → Payload↔ConfigSchema → L1 build → L2 write → cleanup**. Order is explicit in service code; integration tests assert it. `[LOCKED: mandatory order]` |

### Differentiators

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| **Aggregate-error mode (return all failures, not first)** | Consumer fixing 12 schema-edge mismatches gets all 12 at once vs. one-at-a-time | LOW-MEDIUM | Collect all gate failures into a `List<ProblemDetail>` per gate; emit 422 with `errors[]` array. Mirrors FluentValidation's behavior at the DTO layer. Increases test surface ~2×. Defer if not asked for. |
| **Structural Schema compatibility (vs. strict Id equality)** | Two distinct Schema rows with byte-identical Definitions should be considered compatible | HIGH | Requires canonical JSON Schema diffing — entire research milestone of its own. `[LOCKED: defer to v3.4+ candidate]` |
| **Per-step compile metadata stamped into L2** | Consumer wants to know "which Schema.Id validated this edge" for audit | LOW | Add `compileMetadata { schemaEdge: { parentOutputSchemaId, childInputSchemaId } }` to the chain record. Cheap; defer unless audit consumer asks for it. |

### Anti-Features (v3.3.0)

| Feature | Why Tempting | Why Out of Scope | Alternative |
|---------|--------------|------------------|-------------|
| **Auto-fix or auto-propose corrections** | "If parent.Output is null and child.Input is X, suggest setting parent.Output to X" | Adds an entire repair-recommendation engine to a milestone whose scope is "validate and project." Couples the projection layer to entity-edit logic | Return clear 422; let the operator decide. |
| **Validation gates running in parallel** | "Run all 4 gates concurrently to lower latency" | Each gate's failure mode is different; running in parallel makes the error surface non-deterministic and reorders error messages between runs | Sequential, ordered, deterministic. Latency is fine — these are in-memory passes. |
| **Validating at Assignment-PUT instead of (or in addition to) at Start** | "Catch errors at write-time, not at orchestration-time" | v3.2.0 N2 explicitly chose Assignment-PUT = "valid JSON only." Adding Schema-aware validation at PUT breaks that contract and re-opens the choice. v3.3.0 closes VALID-21 at Start only. | Document the asymmetry in the OpenAPI spec; the Start endpoint is the type-safety gate. |

---

## 3. L2 (Redis) Materialized Projection — DTO Shape & Write Semantics

### Table Stakes

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| **Three key spaces with clear, hierarchical naming** | Consumers need to know what to read where without out-of-band knowledge; Redis-community convention is colon-delimited hierarchical keys (e.g., `{workflowId}`, `{workflowId}:{stepId}`, `{processorId}`) | LOW | The `:` separator is the universal convention (Redis docs, every client library). Use bare UUID-N (no hyphens — matches the Guid `"N"` format used by `CorrelationIdMiddleware` in v3.2.0 — for symmetry and grep-ability). `[LOCKED: 3 key spaces]` |
| **Per-record reads (no projection-wide scan required for consumer hot path)** | Consumers reading `{workflowId:stepId}` billions of times per day cannot SCAN; the key shape must support direct `GET` / `HGETALL` | LOW | The chain-form `{workflowId:stepId}` shape already supports this — caller knows both ids from their context. Confirmed against orchestrator-side reading patterns. |
| **JSON-string-value-in-string-key (single `SET`) for each record** | Hash data type has per-field overhead and a >1-level depth restriction Redis itself documents; serializing the DTO as JSON in a single STRING value avoids both | LOW | `SET key json-payload`. Single round-trip read = `GET key` → deserialize. Aligns with how RedisJSON markets itself for partial-read workloads, but using plain STRING + System.Text.Json keeps the deps minimal. **See section 4 for the hash-vs-JSON tradeoff explicitly.** |
| **Idempotent Start = full replace of all 3 key spaces for the workflow(s) involved** | Consumer must never read a half-rebuilt projection where some chain records point to a Processor that no longer exists in the projection | MEDIUM | Two viable approaches — both ship a deterministic post-condition. See "How idempotent Start is implemented" subsection below. `[LOCKED: PUT-like, last-write-wins]` |
| **Idempotent Stop = delete all 3 key spaces for given WorkflowIds, 204 even when empty** | A 404-on-empty-Stop forces consumers to handle the "already gone" race; 204 makes Stop a true idempotent verb | LOW | Track or compute the key set per WorkflowId; issue `UNLINK` (non-blocking DEL); return 204. `[LOCKED: 204 always]` |
| **Liveness as a stored DTO field (timestamp + interval + status)** | The v3.3.0 contract is "liveness is a stored field shape" — not a Redis TTL, not a computation. Consumers read the field and decide aliveness themselves | LOW | DTO: `{ timestamp: DateTime, interval: int (seconds), status: string }`. Writer (this milestone) stamps `timestamp = utcNow`, `interval = configured`, `status = "Active"` at Start time. **Scheduler integration that updates these is deferred.** `[LOCKED: stored field, not computation]` |
| **`JobId` as a stored Guid field (not Hangfire/Quartz integration)** | The v3.3.0 contract is "JobId is an L2 DTO field shape only" — write semantics deferred. Reserve the field shape now so consumers can read it later without a projection-shape migration | LOW | DTO: `jobId: Guid`. Writer stamps `jobId = Guid.Empty` (or omits / nulls — TBD) at Start time. **Scheduler integration that writes the real JobId is deferred.** `[LOCKED: field shape, not behavior]` |
| **Field-name discipline: `inputDefinition` / `outputDefinition`** | Tiny detail with consumer-contract weight: drift between docs and code is a real source of integration breaks | LOW | Lock the names in DTO classes; integration test asserts the serialized JSON contains exactly these keys. `[LOCKED: input/output Definition exact spelling]` |

### How idempotent Start is typically implemented (canonical patterns)

Three options found in the ecosystem; each ships the same post-condition but with different read-during-rebuild semantics:

| Pattern | How it works | Read-during-rebuild semantics | When to choose |
|---------|--------------|-------------------------------|----------------|
| **A. Per-key `SET` (overwrite-in-place)** | For each key in the new projection, issue `SET key newValue` overwriting any old value. Pre-existing keys not in the new projection are deleted by name. | Reader can briefly see a mix of old and new records during the write window (each key flips atomically, but the set of keys doesn't). | **Recommended for v3.3.0.** Simple, no Lua, no naming dance. Acceptable because the milestone is single-replica and Start is human-triggered (Orchestrator service) — not a hot loop. Consumers read entire chain in a fresh call; in-flight reads naturally tolerate per-key freshness. |
| **B. Stage-then-rename (atomic swap)** | Build the new projection under a staging prefix (e.g., `staging:{workflowId}:...`); when complete, `RENAME` each staging key to the live key. Pre-Redis 7 has limited multi-key RENAME atomicity; Redis 8 cluster-aware RENAME works only within a single hash slot. | Reader sees old projection until the swap, then new. Cleaner consistency window. | Defer to v3.4+ if/when consumers complain about read-during-rebuild artifacts. Adds key-naming complexity and cluster-slot constraints. |
| **C. MULTI/EXEC over all keys** | Wrap all SETs + DELs in a single transaction. | Atomic from server's POV (commands queued, executed contiguously). | Works but is ugly at scale: every workflow rebuild crams N steps × M assignments worth of commands into one EXEC; blocks server during execution; loses pipelining benefits. Anti-pattern at high projection sizes. |

**Recommendation for v3.3.0:** Pattern A (per-key SET + targeted DEL of removed keys). Cheapest, no Lua dependency, consumer-acceptable. Document the read-during-rebuild window in the milestone's PITFALLS.md. Upgrade to Pattern B if/when projection size or consumer SLAs demand it.

### How Stop is typically implemented (DEL strategies)

| Strategy | Tradeoffs | Recommendation |
|----------|-----------|----------------|
| **Compute key set in code + `UNLINK key1 key2 ...`** | Requires knowing all keys (workflow key + N chain keys + M processor keys). The L1 build can produce this list as a side effect of compilation; cache it once at Start, look it up at Stop. **Cleanest.** | **Recommended for v3.3.0.** Use `UNLINK` not `DEL` (non-blocking; frees memory asynchronously; supported since Redis 4.0). |
| **`SCAN` for matching keys, then `UNLINK`** | Pattern: `SCAN cursor MATCH workflowId:*` then DEL. Works without bookkeeping. | Acceptable fallback for unknown-key Stops (e.g., projection-cleanup admin tool). Per-Start Stop should not need SCAN — we know the keys. Avoid in the hot Stop path. |
| **`KEYS pattern` + `DEL`** | `KEYS` is blocking on the server; explicitly contraindicated in production by Redis docs (despite Redis 8 single-slot optimization). | **Anti-pattern.** Never use in v3.3.0. |
| **TTL-based expiry (no explicit delete)** | Set TTL at Start = max-expected-Stop-window; let Redis evict | Couples Stop semantics to wall-clock TTL — fragile; an extended workflow run becomes a "where did my projection go?" mystery. Anti-pattern for an L2 that's the consumer's authoritative read model. |
| **Status flag flip (write `status = "Stopped"`)** | Mark each record with a status field; consumer filters on it; physical delete deferred | Defers a cleanup problem; means consumers must always check status; doubles read cost. Anti-pattern unless audit-trail of stopped workflows is required (it isn't in v3.3.0). |

**Recommendation for v3.3.0:** Compute the key set at L1 build time, persist it transiently for Stop, issue a single `UNLINK key1 key2 ...` per workflow at Stop. Return 204 unconditionally.

### Liveness — typical field shapes

| Field shape | Pros | Cons | Recommendation |
|-------------|------|------|----------------|
| `{ timestamp, interval, status }` | Minimal; matches v3.3.0 locked shape | No causality / no fencing token | **The locked v3.3.0 shape.** Sufficient for v3.3.0 because no concurrent writer races exist yet (Scheduler integration deferred). |
| `{ timestamp, interval, status, sequenceNumber }` | Monotonic seqnum lets consumers detect missed updates / out-of-order | Requires monotonic-counter source (Redis INCR or DB sequence) | Differentiator: add when Scheduler integration arrives. |
| `{ timestamp, interval, status, generationId }` | A generation/epoch token (regenerated on each Start) lets consumers reject stale liveness writes from a prior Start | Couples liveness write path to Start lifecycle | Differentiator: add when multi-writer concurrency becomes real. |
| `{ timestamp, interval, status, causalityToken }` | Full causal-consistency vector | Overkill for a single-writer projection | Anti-feature for v3.3.0. |

**Recommendation:** Keep the locked `{ timestamp, interval, status }` shape for v3.3.0. Reserve `sequenceNumber` and `generationId` as named v3.4+ candidates — they're additive to the DTO so future addition doesn't break existing consumers.

### Differentiators

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| **`X-Projection-Version` / `generation` field on the `{workflowId}` root record** | Consumer can detect "this projection was rebuilt since my last read" without diffing every chain record | LOW | Stamp `generationId = Guid.NewGuid()` (or a monotonic counter) into `{workflowId}` root at every Start. Cheap; high audit value. |
| **Projection size telemetry** | Operators want to know "this workflow projects to N keys, M bytes" before it bloats Redis | LOW | One Histogram on the existing OTel Meter — `orchestration.projection.bytes`, `orchestration.projection.keys_total`. Defer if not asked for. |
| **Dry-run Start (`?dryRun=true`)** | Operator can validate a Workflow's compilability without touching Redis. Useful for CI / pre-prod gates | MEDIUM | Implement the full L1 build pipeline and report what *would* be written; skip the L2 write step. Common pattern in dbt / Snakemake / KubeVela ecosystems. Defer unless operator asks for it. |
| **Projection-diff between current and proposed Start** | "What's about to change?" output — useful for change-management workflows | HIGH | Requires reading current L2, computing diff vs. proposed projection. Defer to v3.4+; not needed when Start is idempotent and last-write-wins. |
| **Audit trail of Start invocations** | Compliance / debugging — "who started workflow X at time Y?" | MEDIUM | Append-only log table or Redis stream of `(workflowId, timestamp, correlationId, userId)`. v3.2.0 already has `CreatedBy` plumbing; reuse for the audit row. Defer unless audit is a v3.3.0 requirement. |
| **Pre-warm Strategy (Background Service)** | At startup, replay last-known-good Start for every workflow with non-null cron — projection is hot from second 0 | HIGH | Couples startup ordering to projection state; requires a "last successful Start" persistence mechanism (currently not in L3 schema). **Defer** — risks startup-probe regressions on the v3.2.0 health-probe contract. |

### Anti-Features (v3.3.0)

| Feature | Why Tempting | Why Out of Scope | Alternative |
|---------|--------------|------------------|-------------|
| **Lua scripts encoding compile/validation logic** | "Run the whole projection write as one atomic Lua block" | Redis docs and community explicitly warn against business logic in Lua: blocks the server during execution, hard to debug (SHA-1 digests are opaque), forces all client instances to maintain the script copy, dynamic-script-generation is an anti-pattern (script cache poisoning). All v3.3.0 logic is in .NET — Lua adds a second language and a second test harness for zero benefit. | Pattern A (per-key SET) requires no Lua. Keep all business logic in `OrchestrationService.StartAsync`. |
| **Read-through cache semantics (lazy fill on miss)** | "If consumer reads a missing key, build the projection on demand" | Mixes the materialized-view semantic ("writer materializes, reader reads") with cache semantics ("reader triggers build on miss"). Double the failure modes; consumer reads become unbounded-latency; cache stampede on cold start. The whole point of the L1→L2 pipeline is that writes materialize once at Start; reads are always pre-built | If consumer reads a miss, return 404 / let Orchestrator-side handle it. Don't auto-rebuild on read. |
| **Eager invalidation of L2 on CRUD writes to L3** | "If someone edits a Step in L3, invalidate the affected projections in L2" | Re-opens the entire CRUD surface to "which L2 keys does this L3 entity touch?" tracking. A v3.3.0 maintenance hellscape. The contract is explicit: L2 is rebuilt by Start; CRUD does not touch L2 | Document explicitly: L3 edits do not invalidate L2. Operators must re-Start. |
| **Mixing flat HASH and nested JSON in the same key space** | "Use HSET for top-level fields and JSON for nested ones" | Splits the read path: consumers need 2 commands per record (HGETALL + GET). Doubles round-trips at billions-of-reads-per-day scale. Redis-community guidance is: pick one shape per key space and stay there | Pick JSON-string-in-STRING for all 3 key spaces. Single GET per record. |
| **`KEYS pattern` in Stop** | "Easiest way to find all of this workflow's keys" | KEYS is blocking on the server; production-contraindicated by Redis docs across all major versions | Compute key set in code from L1; UNLINK explicitly. |
| **TTL-based expiry on projection keys** | "Auto-cleanup if Stop is never called" | Couples projection lifetime to wall-clock; a long workflow run becomes a "where did my projection go?" mystery; Stop becomes optional which removes its audit weight | No TTL. Stop is the explicit teardown. |
| **Status-flag flip instead of physical delete on Stop** | "Keep the projection for audit; just mark it Stopped" | Doubles read cost on the hot path (consumer must filter every chain record); requires consumer cooperation; defers a cleanup problem | Physical UNLINK on Stop. Audit goes in the audit log (separate, deferred). |
| **Redis distributed lock around the Start operation** | "Prevent concurrent Starts from racing" | The locked v3.3.0 contract is "last-write-wins, no Redis lock." A lock creates a "what if the lock owner dies?" failure mode (lease renewal, fencing tokens, the full RedLock saga). v3.3.0 is single-replica; concurrent Start collisions are operator-triggered and rare | Last-write-wins is acceptable. Document the semantics in the milestone. |

---

## 4. Expected DTO Shapes — Hash vs. JSON, Flat vs. Nested

### Recommendation Matrix

| Key space | Recommended shape | Storage type | Reason |
|-----------|-------------------|--------------|--------|
| `{workflowId}` | Single JSON object, JSON-encoded | Redis STRING (`SET`/`GET`) | One read per workflow; small payload; nested array (`entryStepIds[]`) + nested object (`liveness{}`) don't fit cleanly in a flat HASH |
| `{workflowId:stepId}` | Single JSON object, JSON-encoded | Redis STRING (`SET`/`GET`) | The hot read path. Consumers read this billions of times per day. Single GET, single deserialization. Payload is small (~few hundred bytes). |
| `{processorId}` | Single JSON object, JSON-encoded | Redis STRING (`SET`/`GET`) | Includes `inputDefinition` / `outputDefinition` (JSON Schema documents — can be large, often >1KB, deeply nested). HASH is contraindicated by Redis docs for >1-level depth |

### Hash vs. JSON tradeoff (ecosystem evidence)

- **Hashes** are simple field-value pairs, "default recommendation" for shallow flat records (Redis docs). But they degrade for nested data; the JSON object should not be deeper than one level to maintain HASH optimization.
- **JSON-as-STRING** (this milestone's recommendation) keeps the value serialized; access requires deserialization but a single `GET` returns the entire record in one round-trip. Matches consumer pattern of "read whole step record, decide next action."
- **RedisJSON module** offers partial-field retrieval and in-place updates, advertised as ~90× lower latency than MongoDB for nested-JSON workloads. Overkill for v3.3.0: requires a Redis module dependency in the compose stack, and v3.3.0 doesn't have a partial-read use case (consumer always reads the full chain record).

**Conclusion:** Plain STRING + `System.Text.Json` for all 3 key spaces. Defer RedisJSON to v3.4+ only if a partial-update use case emerges.

### Field shape — exact contracts

```jsonc
// {workflowId} — STRING value:
{
  "entryStepIds": ["<guidN>", "<guidN>"],
  "cron": "0 */5 * * *",            // nullable; mirrors v3.2.0 Workflow.CronExpression
  "jobId": "<guidN>",                // Guid; v3.3.0 = Guid.Empty (scheduler-write deferred)
  "liveness": {
    "timestamp": "2026-05-28T00:00:00Z",
    "interval": 60,                  // seconds
    "status": "Active"
  }
}
```

```jsonc
// {workflowId:stepId} — STRING value (chain form, one nextStepId per record):
{
  "entryCondition": "PreviousProcessing", // existing StepEntity.EntryCondition enum
  "processorId": "<guidN>",
  "payload": { /* arbitrary JSON from Assignment.Payload */ },
  "nextStepId": "<guidN>"                  // single id; null = terminal
}
```

```jsonc
// {processorId} — STRING value:
{
  "inputDefinition": { /* full JSON Schema from Processor.InputSchema.Definition; may be null */ },
  "outputDefinition": { /* full JSON Schema from Processor.OutputSchema.Definition; may be null */ },
  "liveness": {
    "timestamp": "2026-05-28T00:00:00Z",
    "interval": 60,
    "status": "Active"
  }
}
```

**Chain form note:** A Step with multiple `NextStepIds` produces multiple `{workflowId:stepId}` records, each with one `nextStepId`. This is the "fan-out flattening" the milestone scope already specifies and is consumer-friendly — readers walk one record per edge, never an array. `[LOCKED: chain form]`

---

## 5. Feature Dependencies

```
[Bounded L3 fetch by WorkflowIds]
    └──required-by──> [L1 Dictionary build]
                          └──required-by──> [Existence validation]
                                                └──required-by──> [Cycle detection (DFS)]
                                                                      └──required-by──> [Schema-edge compat gate]
                                                                                            └──required-by──> [Payload↔ConfigSchema gate]
                                                                                                                  └──required-by──> [L2 projection write (3 key spaces)]
                                                                                                                                        └──required-by──> [L1 cleanup (try/finally)]

[Stop: compute key set] ── derives from ──> [L1 build artifact (key list cache)]
[Stop: UNLINK] ── precondition ──> [Redis client (StackExchange.Redis) in compose stack]

[Liveness DTO shape] ── shape-only-in-v3.3.0 ──> (Scheduler writes deferred)
[JobId DTO shape] ── shape-only-in-v3.3.0 ──> (Hangfire/Quartz integration deferred)
```

### Dependency Notes

- **L1 → L2 ordering is non-negotiable:** an L2 write before L1 is fully built risks projecting an incomplete graph. The "build → validate → write → cleanup" pipeline is sequential.
- **Validation order (existence → cycles → schema-edge → payload→config) is consumer-facing:** swapping the order changes which 422 a broken workflow produces. Locked in milestone scope.
- **Stop depends on L1's key-list output:** the cleanest implementation has Start return / cache the list of keys it wrote so Stop can UNLINK them without SCAN. Alternative is SCAN at Stop time (slower, has cursor semantics) — defer that to a v3.4+ admin-cleanup tool.
- **Redis dependency is new in compose.yaml:** adds operational footprint (one more container, one more health probe). Document in PITFALLS.md as the v3.3.0 infrastructure delta.

---

## 6. MVP Definition — v3.3.0 Scope

### Launch With (v3.3.0 — Table Stakes)

- [ ] **Bounded L3 fetch + flat Dictionary L1 build** — 5 entity types loaded once per Start request, in-memory dicts, explicit teardown.
- [ ] **DFS traversal with cycle detection + missing-next-step 422** — per-traversal `visited` set, deterministic error messages.
- [ ] **Strict Schema-edge compatibility gate** — Guid equality, null passes, 422 on mismatch with `(parentStepId, childStepId)`.
- [ ] **Payload↔ConfigSchema gate (closes VALID-21)** — reuses v3.2.0 JsonSchema.Net validator (draft 2020-12, SSRF-disabled), 422 on failure with offending `assignmentId`.
- [ ] **3 Redis key spaces with locked DTO shapes** — `{workflowId}`, `{workflowId:stepId}`, `{processorId}`; STRING + JSON; exact field names (`inputDefinition` / `outputDefinition`, `liveness{timestamp,interval,status}`, `jobId`).
- [ ] **Idempotent Start (Pattern A: per-key SET overwrite, targeted DEL of removed keys)** — last-write-wins on concurrent Starts; no Redis lock; 204 response.
- [ ] **Idempotent Stop (compute-key-set + UNLINK)** — 204 even when no keys exist; never SCAN, never KEYS, never status-flag flip.
- [ ] **Mandatory validation order** — existence → cycles → schema-edge → Payload↔Config → L1 build → L2 write → cleanup, asserted by integration tests.
- [ ] **L1 cleanup contract** — explicit `.Clear()` in finally block, success or failure path.
- [ ] **Redis client added to `compose.yaml`** — alongside Postgres/ES/Prom; v3.2.0 health-probe pattern extended to include Redis reachability in `/health/ready`.

### Add After Validation (v3.4+ — Differentiators)

- [ ] **Aggregate-error mode for validation gates** — return all 12 mismatches at once vs. first-failure-only.
- [ ] **`generationId` on `{workflowId}` root** — consumer can detect rebuild without diffing chain records.
- [ ] **Projection-size telemetry** — Histograms on `orchestration.projection.{bytes,keys_total}`.
- [ ] **Dry-run Start (`?dryRun=true`)** — full validation pipeline, skip L2 write. Useful for CI / change-management.
- [ ] **Audit trail of Start invocations** — `(workflowId, timestamp, correlationId, userId)` log row per Start.
- [ ] **Scheduler integration** — who writes the real `JobId`; who emits `Liveness.timestamp` updates; Hangfire vs. Quartz vs. external Scheduler service decision.
- [ ] **Compile metadata stamped into L2** — `compileMetadata.schemaEdge.{parentOutputSchemaId, childInputSchemaId}` per chain record for audit consumers.
- [ ] **Stage-then-rename atomic swap (Pattern B)** — upgrade from per-key SET if consumers complain about read-during-rebuild artifacts.

### Future Consideration (v3.5+ — Defer)

- [ ] **Structural Schema compatibility** — canonical JSON Schema diffing instead of strict Guid equality. Entire research milestone of its own.
- [ ] **Projection-diff between current and proposed Start** — "what's about to change" output for change-management consumers.
- [ ] **RedisJSON module integration** — partial-read / in-place-update of nested fields; defer until a partial-read use case appears.
- [ ] **Pre-warm strategy (background service replays last-known-good Start at API startup)** — risks startup-probe regression; defer until consumers demand cold-start latency improvements.
- [ ] **Distributed lock around Start (RedLock or similar)** — only relevant once multi-replica is in play. v3.3.0 is single-replica.
- [ ] **Sequence numbers / generation IDs / causal-consistency tokens in `Liveness`** — needed once Scheduler integration introduces multi-writer races.

---

## 7. Feature Prioritization Matrix

| Feature | User Value (External Orchestrator + Scheduler) | Implementation Cost | Priority |
|---------|------------------------------------------------|---------------------|----------|
| Bounded L3 fetch + L1 build + cleanup | HIGH (correctness foundation) | LOW | **P1 — v3.3.0** |
| DFS traversal + cycle detection | HIGH (consumer safety) | LOW | **P1 — v3.3.0** |
| Strict Schema-edge gate | HIGH (type-safety contract) | MEDIUM | **P1 — v3.3.0** |
| Payload↔ConfigSchema gate (closes VALID-21) | HIGH (closes deferred v3.2.0 debt) | MEDIUM | **P1 — v3.3.0** |
| 3 Redis key spaces with locked DTO shapes | HIGH (consumer-contract surface) | LOW | **P1 — v3.3.0** |
| Idempotent Start (Pattern A) | HIGH (operator-trigger semantics) | MEDIUM | **P1 — v3.3.0** |
| Idempotent Stop (compute-key-set + UNLINK) | HIGH (operator-trigger semantics) | LOW | **P1 — v3.3.0** |
| Mandatory validation order | MEDIUM (deterministic error messages) | LOW | **P1 — v3.3.0** |
| `generationId` on workflow root | MEDIUM (rebuild detection) | LOW | **P2 — v3.4** |
| Dry-run Start (`?dryRun=true`) | MEDIUM (CI gating) | MEDIUM | **P2 — v3.4** |
| Aggregate-error mode | MEDIUM (DX) | LOW | **P2 — v3.4** |
| Projection-size telemetry | MEDIUM (ops) | LOW | **P2 — v3.4** |
| Audit trail of Start | MEDIUM (compliance) | MEDIUM | **P2 — v3.4** |
| Scheduler integration (real JobId/Liveness writers) | HIGH (full system completeness) | HIGH | **P2 — v3.4** (separate milestone) |
| Stage-then-rename swap (Pattern B) | LOW until consumer asks | HIGH | **P3 — v3.5+** |
| Structural Schema compat | LOW until ecosystem matures | HIGH | **P3 — v3.5+** |
| Projection diffing | LOW | HIGH | **P3 — v3.5+** |
| RedisJSON module | LOW until partial-read use case | HIGH | **P3 — v3.5+** |
| Pre-warm strategy | LOW (risk > value at this stage) | HIGH | **P3 — v3.5+** |
| Distributed Start lock | LOW (single-replica) | HIGH | **P3 — v3.5+** |

**Priority key:**
- **P1**: Must have for v3.3.0 ship (consumer-contract or correctness)
- **P2**: Should have, candidate for v3.4 milestone
- **P3**: Nice to have, defer until trigger emerges

---

## 8. Ecosystem Pattern Analysis (Comparable Systems)

| Pattern | Temporal | Argo Workflows | Airflow | sk_p v3.3.0 (our approach) |
|---------|----------|----------------|---------|---------------------------|
| **How is the graph stored?** | Event-sourced history per workflow execution | YAML CRD in K8s etcd | Python DAG file parsed at scheduler-tick | Postgres L3 → projected to Redis L2 (this milestone) |
| **Compile-time validation** | Workflow code is just Go/Java/TS — compile errors caught by language toolchain | YAML schema validated by k8s admission | DAG-bag parser at scheduler-tick (runtime error if broken) | Explicit gates at Start: existence, cycles, schema-edge, payload↔config |
| **Cycle detection** | N/A (event-sourced, replays history; no compile-time graph) | YAML structural lint | Implicit (DAG = Directed *Acyclic* Graph; parser rejects cycles) | Explicit DFS at Start (LOCKED) |
| **Idempotent start** | `WorkflowIdReusePolicy` enum (ALLOW_DUPLICATE / REJECT_DUPLICATE / TERMINATE_IF_RUNNING) | `kubectl apply` is idempotent by K8s convention | Trigger by `dag_id` + `execution_date` — date is the idempotency key | PUT-like idempotent Start; last-write-wins |
| **Stop semantics** | `TerminateWorkflowExecution` API; durable state preserved | `argo terminate` — sets phase=Failed; CRD remains | `dag.set_dag_run_state(state=DagRunState.FAILED)` | DEL the L2 projection; 204 unconditionally |
| **Projection / read model** | Visibility store (ES) for queries; primary store is event log | K8s API queries the CRD directly | Metadata DB queries (Postgres/MySQL) | Redis L2 — the consumer-facing read model |

**Takeaway:** sk_p's L3→L1→L2 split mirrors a CQRS read-model pattern more than a Temporal-style event-sourced pattern. The L2 projection is the read model; L3 is the write model. v3.3.0 introduces the read-model materializer.

---

## Sources

### Ecosystem patterns
- [Workflow Orchestration Best Practices for ETL, ELT, and ML Pipelines (ml4devs)](https://www.ml4devs.com/what-is/workflow-orchestration/) — schema validation, separation of orchestration from business logic, treating DAGs as code
- [Temporal vs Airflow vs Argo: Workflow Orchestration Guide (xgrid)](https://www.xgrid.co/resources/temporal-vs-airflow-vs-argo-workflow-orchestration/) — comparison of graph storage and validation approaches
- [Workflow Engine design proposal (Architecture Weekly)](https://www.architecture-weekly.com/p/workflow-engine-design-proposal-tell) — graph compilation and projection patterns
- [Idempotency when creating workflows · workflow-core #828](https://github.com/danielgerlag/workflow-core/issues/828) — WorkflowIdReusePolicy ecosystem precedent
- [The Myths of Idempotent APIs in Practices](https://medium.com/@qlong/the-myths-of-idempotent-apis-in-practices-9025a94487f2) — Indeed Workflow Engine StartWorkflow idempotency pattern

### Redis projection patterns
- [Redis Atomic Update Patterns (Antirez)](https://redis.antirez.com/fundamental/atomic-updates.html) — atomic swap / RENAME patterns
- [How to Use RENAME and RENAMENX in Redis (OneUptime, 2026)](https://oneuptime.com/blog/post/2026-03-31-redis-how-to-use-rename-and-renamenx-in-redis-to-rename-keys/view) — staging-key-then-rename pattern
- [What is idempotency in Redis? (Redis blog)](https://redis.io/blog/what-is-idempotency-in-redis/) — SET NX vs Lua tradeoffs
- [Faster KEYS and SCAN (Redis blog)](https://redis.io/blog/faster-keys-and-scan-optimized/) — Redis 8 single-slot optimization; production caveats
- [Redis SCAN vs KEYS command (Medium)](https://medium.com/@shaskumar/redis-scan-vs-keys-command-9df7f51b7162) — production-safety guidance
- [Redis Pattern Matching: How to Use KEYS and SCAN Effectively (DEV)](https://dev.to/rijultp/redis-pattern-matching-how-to-use-keys-and-scan-effectively-5dkp) — anti-pattern catalog
- [Redis Key Design and Naming Conventions (OneUptime, 2026)](https://oneuptime.com/blog/post/2026-01-21-redis-key-design-naming/view) — colon-delimited hierarchical convention

### Materialized view / projection theory
- [Materialized View pattern (Azure Architecture Center)](https://learn.microsoft.com/en-us/azure/architecture/patterns/materialized-view) — work distribution: read-time vs. write-time; rebuild patterns
- [Materialized Views: An alternative to full-blown cache systems (Distributed Computing Musings, 2022)](https://distributed-computing-musings.com/2022/11/materialized-views-an-alternative-to-full-blown-cache-systems/) — read-through vs. materialized; when not to mix

### Redis Hash vs JSON
- [Hash vs JSON Storage (Redis docs)](https://redis.io/docs/latest/develop/ai/redisvl/user_guide/hash_vs_json/) — depth-of-nesting tradeoff (HASH ≤ 1 level)
- [RedisJSON: Performance Benchmarking (Redis blog)](https://redis.io/blog/redisjson-public-preview-performance-benchmarking/) — partial-read latency claims
- [JSON Performance (Redis docs)](https://redis.io/docs/latest/develop/data-types/json/performance/) — when RedisJSON helps and when it doesn't

### Lua-as-anti-pattern
- [Scripting with Lua (Redis docs)](https://redis.io/docs/latest/develop/programmability/eval-intro/) — explicit warnings: dynamic script generation is an anti-pattern; debugging is hard; server blocks during execution; business logic in Lua leads to bad architecture
- [Limitations of scripting with Lua in Redis (Boyner Tech, Medium)](https://medium.com/boyner-technology/limitations-of-scripting-with-lua-in-redis-b4381bd9629f) — maintainability constraints

### Liveness / heartbeat shape
- [HeartBeat (Martin Fowler, Patterns of Distributed Systems)](https://martinfowler.com/articles/patterns-of-distributed-systems/heartbeat.html) — canonical pattern, field shapes (timestamp + sequence + node_id)
- [Heartbeats in Distributed Systems (Arpit Bhayani)](https://arpitbhayani.me/blogs/heartbeats-in-distributed-systems/) — interval selection vs. round-trip time
- [Redis-Powered User Session Tracking with Heartbeat-Based Expiration (Tilt Engineering)](https://medium.com/tilt-engineering/redis-powered-user-session-tracking-with-heartbeat-based-expiration-c7308420489f) — stored-field vs. TTL-as-liveness tradeoffs

### Dry-run pattern
- [Dry Run (KubeVela docs)](https://kubevela.io/docs/tutorials/dry-run/) — workflow-system dry-run precedent
- [A Guide to dbt Dry Runs (Towards Data Engineering)](https://medium.com/towards-data-engineering/a-guide-to-dbt-dry-runs-safe-simulation-for-data-engineers-7e480ce5dcf7) — data-pipeline dry-run idiom

### Partial-failure / atomic batch
- [AIP-233: Batch methods: Create (Google API Improvement Proposals)](https://google.aip.dev/233) — atomic vs. partial-success batch semantics
- [How to Handle Partial Success in Bulk API Operations (OneUptime, 2026)](https://oneuptime.com/blog/post/2026-02-02-rest-bulk-api-partial-success/view) — response shape patterns
- [Error handling in distributed systems (Temporal)](https://temporal.io/blog/error-handling-in-distributed-systems) — compensation / saga patterns

### .NET Redis client
- [Pipelines and transactions (Redis .NET docs)](https://redis.io/docs/latest/develop/clients/dotnet/transpipe/) — IDatabase batching, MULTI/EXEC, CreateBatch
- [Pipelines and Multiplexers (StackExchange.Redis docs)](https://stackexchange.github.io/StackExchange.Redis/PipelinesMultiplexers.html) — auto-pipelining on async calls
- [How to Use Redis Pipelining in C# (OneUptime, 2026)](https://oneuptime.com/blog/post/2026-03-31-redis-pipelining-csharp/view) — batch HashSetAsync patterns

---

*Feature research scoped to milestone v3.3.0 — Orchestration L3 → L1 → L2 Build Pipeline*
*Researched: 2026-05-28*
*Confidence: HIGH on ecosystem patterns; MEDIUM on exact DTO shapes; HIGH on Stop/Start mechanics*
