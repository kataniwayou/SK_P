# Phase 22: L2 Root-Parent Restructure + Processor Self-Registration Boundary - Context

**Gathered:** 2026-05-31
**Status:** Ready for planning

<domain>
## Phase Boundary

Writer/contracts side only. Add a single parent-index Redis SET enumerating active workflow IDs, hardcode the L2 key prefix as a compile-time `const`, stop the Start flow from *creating* processor L2 entries, and add a Start-time validator that confirms each participating processor's self-registered L2 entry exists and is live (`timestamp + interval*2 > now`), failing 422 like the other validation gates. Edge-schema validation is preserved untouched.

Orchestrator reload/stop lifecycle, SREM-on-Stop *flow*, publish-jobIds, and the processor's own self-registration write path are **Phase 23 / out of scope**.

</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**5 requirements are locked** (originally 6 — **L2IDX-02 was dropped during this discussion**; see D-13 and the SPEC Amendments section). See `22-SPEC.md` for full requirements, boundaries, and acceptance criteria — downstream agents MUST read it before planning/implementing. Requirements are not duplicated here.

Locked IDs: **L2IDX-01** (parent index SET), **L2PREFIX-01** (const prefix), **PROC-NOCREATE-01** (writer stops creating processor entries), **PROC-LIVE-01** (processor existence + liveness validation → 422), **PROC-EDGE-01** (edge validation unchanged).

**In scope (from SPEC.md):** parent-index Redis SET on Start; hardcoded prefix const + config removal on both sides + both `appsettings.json`; removal of writer's processor-entry creation; processor existence + timestamp-liveness validation returning 422; updating the locked golden/round-trip/writer/cleanup tests; keeping the triple-SHA close gate green.

**Out of scope (from SPEC.md):** processor-side self-registration write path; orchestrator startup/start reload, stop unlink + cascade-delete, publish-jobIds; removing a workflow from the index on Stop (Phase 23 — this phase only `SADD`s on Start and `SREM`s inside the shared cleanup routine); Quartz/scheduler; any change to `SchemaEdgeValidator` logic.

</spec_lock>

<decisions>
## Implementation Decisions

### Final L2 structure (owner-confirmed)

```
skp:                  → Redis SET  { wf1, wf2, ... }                                  ← NEW (L2IDX-01)
skp:{wf}              → JSON       { entryStepIds[], cron, jobId, liveness, correlationId }   (UNCHANGED)
skp:{wf}:{stepId}     → JSON       { entryCondition, processorId, payload, nextStepIds[] }    (UNCHANGED)
skp:{procId}          → self-registered by external processor; NOT written here; liveness-validated only
```

The **only** new key is the parent index `skp:`, and it is the **only** Redis SET in the phase. Root and step VALUE shapes do not change. `stepIds` in the root are **entry steps only** (where the workflow starts) — not a full member list.

### Key builders & prefix const (L2IDX-01, L2PREFIX-01)
- **D-01:** Add `public const string Prefix = "skp:"` to `L2ProjectionKeys` (`src/Messaging.Contracts/Projections/L2ProjectionKeys.cs`). One const, consumed by every builder. The `string prefix` parameter is **dropped** from all builders → `Root(Guid)`, `Step(Guid, Guid)`, `Processor(Guid)`. There is exactly one prefix everywhere — root, child, index, processor — no second prefix concept.
- **D-02:** Add builder `L2ProjectionKeys.ParentIndex()` returning the bare `Prefix` const verbatim (`"skp:"`) — the parent-index SET key. Workflow IDs stored in `D` (hyphenated) GUID format, consistent with `Root`.
- **D-03:** No member-set key and no `Members(...)` builder — L2IDX-02 dropped (D-13).
- **D-04:** Update forwarders to the new parameterless signatures, preserving HARDEN-03 single-source-of-truth: writer `RedisProjectionKeys` (`.../Projection/RedisProjectionKeys.cs`) and reader `OrchestratorL2Keys` (`src/Orchestrator/Messaging/OrchestratorL2Keys.cs`) both forward to `L2ProjectionKeys`.

### Config removal (L2PREFIX-01)
- **D-05:** Remove `KeyPrefix` from `RedisProjectionOptions` (`src/BaseApi.Core/Configuration/RedisProjectionOptions.cs`) — keep `ProcessorKeyTtlDays` + `Serialization`. Update writer (`RedisProjectionWriter` line 55) and cleanup (`RedisL2Cleanup` line 38) which currently read `_options.KeyPrefix`. Also `OrchestrationService.cs:98` reads `options.Value.KeyPrefix` for the Stop-gate keys — rework to the const.
- **D-06:** Remove `Redis:KeyPrefix` from `src/BaseApi.Service/appsettings.json` and `src/Orchestrator/appsettings.json`.
- **D-07:** Reader side: `OrchestratorRedisOptions` (`src/Orchestrator/Messaging/OrchestratorRedisOptions.cs`) is a `record(string KeyPrefix)` whose **only** field is the prefix; the consumers (`StartOrchestrationConsumer.cs:33`, `StopOrchestrationConsumer.cs:29`) call `OrchestratorL2Keys.Root(options.KeyPrefix, wf)` and `Program.cs:20` builds it from `Configuration["Redis:KeyPrefix"]`. Drop the prefix read → call `OrchestratorL2Keys.Root(wf)`; delete `OrchestratorRedisOptions` if the prefix was its only purpose.
- **Acceptance:** `grep src/` shows zero configurable key-prefix reads feeding key construction.

### Writer changes (L2IDX-01, PROC-NOCREATE-01)
- **D-08:** `RedisProjectionWriter.UpsertAsync` adds `SADD ParentIndex() {wf.Id:D}` on Start (idempotent on re-Start).
- **D-09:** **Remove** the per-processor write loop (`RedisProjectionWriter.cs` ~lines 100–113, `StringSetAsync(Processor(...))` with TTL). The writer creates **zero** `skp:{procId}` keys. `ProcessorKeyTtlDays` is now unused by the write path — keep the option field or prune per planner discretion (not load-bearing).

### Cleanup wiring (Fork 2 — owner-confirmed)
- **D-10:** `RedisL2Cleanup.StopCleanupAsync(wf)` becomes the complete per-wf teardown:
  1. `SREM ParentIndex() {wf}` — remove this one workflow from the parent index,
  2. `DEL Root(wf)` — the root key,
  3. `DEL Step(wf, stepId)` for **every** step, the step ids found by the **existing BFS traversal** (`entryStepIds → nextStepIds` GET-and-follow).
  No member-set deletion (none exists).
- **D-11:** `SREM` is introduced in Phase 22 but **scoped to this cleanup routine** (invoked by the Start pre-clean as idempotent GC). The Stop *flow* — publish jobIds, cascade-delete-on-Stop — stays Phase 23.
- **D-12:** Cleanup keeps the **Redis BFS** (NOT the in-memory snapshot). It runs *before* `LoadL1Async`, and its job is to catch **stale** keys from a shrunk previous graph that the fresh L3-built snapshot would not contain. This is the one place traversal-of-Redis is unavoidable.

### L2IDX-02 amendment
- **D-13:** **L2IDX-02 (per-workflow member step-id Redis SET, no-traversal enumeration) is DROPPED.** Member enumeration stays BFS-based; the parent index is the only Redis SET. SPEC.md was amended in this discussion (requirement removed, acceptance line dropped, "member lists are SETs" constraint relaxed, Amendments section added). Owner rationale: the separate member SET was over-specification; traversal already serves enumeration, and the parent index is the valuable new structure.

### Processor liveness validator (PROC-LIVE-01)
- **D-14:** New `internal sealed ProcessorLivenessValidator` in `src/BaseApi.Service/Features/Orchestration/Validation/`. Signature `Task ValidateAsync(WorkflowGraphSnapshot snapshot, CancellationToken ct)` (async — it reads Redis, unlike the three sync gates). Deps: `IConnectionMultiplexer` + `TimeProvider`. Registered `AddScoped` in `OrchestrationServiceCollectionExtensions`, injected into `OrchestrationService`.
- **D-15:** Invoked in `OrchestrationService.StartAsync` **after** the three sync gates (cycle → schema-edge → payload-config, lines 142–144) and **before** `UpsertAsync` (line 150) — matches SPEC "after L1 is built." A Redis fault here tagged `redisOp` consistent with the existing OBSERV-REDIS-03 pattern.
- **D-16:** For each `snapshot.Processors.Values` (the participating processors, already in memory): `GET Processor(procId)`. **Absent** → throw. Else deserialize `ProcessorProjection` and require `liveness.timestamp + liveness.interval*2 > now` (`now` from injected `TimeProvider`; `interval` sourced from the entry, **not** the hardcoded `0`). **Stale** (`≤ now`) → throw.
- **D-17:** New factory `OrchestrationValidationException.ProcessorNotLive(Guid procId, string reason)` with `Gate = "processorLiveness"` and offending payload record `ProcessorLivenessOffending(Guid procId, string reason)` where `reason ∈ {"absent","stale"}`. Same 422 / RFC-7807 path as the other gates via `OrchestrationValidationExceptionHandler` — no new HTTP behavior. Mirrors the existing `SchemaEdge(...)` factory pattern (`OrchestrationValidationException.cs`).

### Validators share one in-memory materialization
- **D-18:** All four Start-time validators read the **single `WorkflowGraphSnapshot` (L1)** built once per workflow by `LoadL1Async` — cycle (DFS over `snapshot.Steps`), schema-edge (per-edge over `snapshot.Steps`), payload-config (over `snapshot.Assignments`), processor-liveness (iterates `snapshot.Processors.Values`). No validator re-walks the graph or hits Redis to *discover* steps. The liveness validator's only Redis reads are the per-processor liveness `GET`s — external self-registered data that is not in the snapshot, hence unavoidable and **not** a graph traversal.

### Edge validation preserved (PROC-EDGE-01)
- **D-19:** `SchemaEdgeValidator` is untouched — separate class, synchronous, in-memory, strict-equality per-edge with null-on-either-side passing. It stays decoupled from the L2 processor-entry changes; existing tests remain green.

### Test isolation (Fork 4 — Option 2, owner-confirmed)
- **D-20:** Hardcoding the prefix removes the per-class `Redis:KeyPrefix` isolation seam (today `RedisFixture.KeyPrefix = "test:cls-{Guid:N}:"` injected via `Phase8WebAppFactory` `["Redis:KeyPrefix"]`). **Tests stay on DB0** — the prod/gate keyspace — so the triple-SHA close gate keeps scanning the real keyspace the tests use.
- **D-21:** Per-workflow/step/processor keys (`skp:{guid}...`) remain collision-free via unique GUIDs. The **only** contention point is the shared `skp:` parent-index key.
- **D-22:** Tests that touch the parent index go into a **single non-parallel xUnit collection** (serialized); each cleans up its own workflow ids (`SREM`) so the index is empty between tests.
- **D-23:** Per-class cleanup changes from `SCAN MATCH {prefix}*` (`RedisFixture.DisposeAsync`) to **deleting the specific keys the test created** (known GUIDs) — prefix-scan would now catch sibling classes' keys. `RedisFixture` no longer carries a unique prefix; `Phase8WebAppFactory` no longer injects `Redis:KeyPrefix`.
- **D-24:** Expected, in-scope test updates: `L2ProjectionKeysTests` (golden strings — new signatures + `ParentIndex`), `RedisProjectionOptionsBindingFacts` (drop `KeyPrefix` binding tests), `AppsettingsFacts` (drop the `Redis:KeyPrefix` assertion), `RedisFixture`/`RedisFixtureFacts` (prefix → known-key cleanup), `RedisProjectionWriterFacts` (no processor keys + parent-index `SADD`), `StopCleanupFacts` (`SREM` index), `GateNoWriteFacts`, plus new `ProcessorLivenessValidator` tests (204 all-live / 422 absent / 422 stale, L2 seeded directly to simulate self-registration).

### Claude's Discretion
- Exact wave-vs-recursive form of the cleanup BFS (mirror the existing `RedisL2Cleanup` iterative wave-BFS).
- Whether to keep or prune the now-unused `ProcessorKeyTtlDays` option field.
- Batch/pipeline grouping of the new `SADD`/`SREM` with the existing root/step writes/deletes.
- DI registration ordering for the new validator.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked requirements
- `.planning/phases/22-l2-root-parent-restructure-processor-self-registration/22-SPEC.md` — 5 locked requirements, boundaries, acceptance criteria, Amendments (L2IDX-02 drop). **MUST read before planning.**

### L2 key contracts (single source of truth — HARDEN-03)
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — where `const Prefix` + `ParentIndex()` go; signatures lose the `prefix` param.
- `src/Messaging.Contracts/Projections/WorkflowRootProjection.cs` — root value shape (unchanged: entryStepIds/cron/jobId/liveness/correlationId).
- `src/Messaging.Contracts/Projections/LivenessProjection.cs` — `{ timestamp, interval, status }`; liveness math reads `timestamp + interval*2`.

### Writer / cleanup (Start L2-write path)
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs` — add parent-index `SADD`; remove the processor-write loop (~100–113).
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs` — writer-side forwarder (new signatures).
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisL2Cleanup.cs` — add `SREM` index; keep BFS; the GET-and-follow walk to mirror.
- `src/BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs` — `{ inputDefinition, outputDefinition, liveness }`; deserialized by the liveness validator.

### Start flow + validation
- `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` — `StartAsync` validator order (142–144) + `UpsertAsync` (150); pre-clean call (129); KeyPrefix read (98).
- `src/BaseApi.Service/Features/Orchestration/Validation/SchemaEdgeValidator.cs` — the gate to mirror in shape; PROC-EDGE-01 keeps it untouched.
- `src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs` (+ `OrchestrationValidationExceptionHandler.cs`) — add `ProcessorNotLive` factory; 422/RFC-7807 path.
- `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` — validator DI registration (62–74).

### Config removal (both sides)
- `src/BaseApi.Core/Configuration/RedisProjectionOptions.cs` — remove `KeyPrefix`.
- `src/BaseApi.Service/appsettings.json` (line ~27) + `src/Orchestrator/appsettings.json` (line ~21) — remove `Redis:KeyPrefix`.
- `src/Orchestrator/Messaging/OrchestratorRedisOptions.cs`, `src/Orchestrator/Program.cs` (line 20), `src/Orchestrator/Messaging/OrchestratorL2Keys.cs`, `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` (33), `StopOrchestrationConsumer.cs` (29) — reader prefix removal.

### Tests (isolation rewrite + golden updates)
- `tests/BaseApi.Tests/Composition/RedisFixture.cs`, `Phase8WebAppFactory.cs`, `RedisFixtureFacts.cs`, `RedisProjectionOptionsBindingFacts.cs`, `AppsettingsFacts.cs`.
- `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs`, plus `HappyPathE2EFacts.cs`, `IdempotencyFacts.cs`, `GateNoWriteFacts.cs`, `StopCleanupFacts`, `RedisProjectionWriterFacts`.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`OrchestrationValidationException` factory pattern** — single class + `Gate` discriminator + per-gate offending record; add `ProcessorNotLive` the same way. Handler already maps to 422 + RFC-7807 with correlationId/instance added by the Phase 4 customizer.
- **`TimeProvider` injection** — writer already injects it for liveness timestamps; the liveness validator reuses it for `now`.
- **Existing Redis BFS** in `RedisL2Cleanup` (iterative wave-BFS, `visited` as a plain `List<Guid>`, `Distinct()` dedupe) — the cleanup pattern to extend with `SREM`/`DEL` index+root.
- **`AddScoped<Validator>()` + constructor injection into `OrchestrationService`** — the registration/wiring pattern for the new validator.

### Established Patterns
- **HARDEN-03 single source of truth:** all L2 key construction flows through `L2ProjectionKeys`; writer/reader are thin forwarders. The const + `ParentIndex()` must live there; no hand-copied interpolation.
- **LOCKED Phase 14 validator order** in `OrchestrationService.StartAsync` — the new async validator slots after the sync trio, before the write.
- **camelCase JSON via `[property: JsonPropertyName]`** on positional records (load-bearing — STJ ignores bare attrs on ctor params).
- **OBSERV-REDIS-03 op-tagging** — `ex.Data["redisOp"]` on `RedisException` for a stable 500 body op name.

### Integration Points
- `RedisProjectionWriter.UpsertAsync` — `SADD` parent index; drop processor writes.
- `RedisL2Cleanup.StopCleanupAsync` — `SREM` parent index + `DEL` root/steps.
- `OrchestrationService.StartAsync` — insert `await _processorLivenessValidator.ValidateAsync(snapshot, ct)` between the sync gates and the write.
- Both `appsettings.json` + reader `Program.cs`/consumers — prefix de-config.

</code_context>

<specifics>
## Specific Ideas

- Owner drew the final structure explicitly (see "Final L2 structure" above). The parent index is the **only** new key; root/step values are byte-shape-unchanged.
- `stepIds` in the root are **entry steps only** — confirmed twice; not a full member list.
- Deleting a workflow = `SREM` from `skp:` + `DEL` root + `DEL` all steps (steps found by `entryStepIds → nextStepIds` traversal).
- Member enumeration "always does traversal" — owner's words; no member SET.

</specifics>

<deferred>
## Deferred Ideas

- **L2IDX-02 (per-workflow member step-id Redis SET / no-traversal enumeration)** — dropped this phase. If a future phase needs `SMEMBERS`-style enumeration without traversal, reintroduce a `skp:{wf}:members` SET then.
- **Phase 23 (already scoped):** orchestrator startup/start reload (hydrate L1 from L2 transiently), Stop unlink + cascade-delete, publish jobIds instead of workflowIds.
- **Processor self-registration write path** — the external processor app writing/refreshing its own `skp:{procId}` timestamp/interval (its own phase/milestone; tests here seed L2 directly to simulate it).
- **Scheduler/Quartz + real source of `interval`/`Cron`** — deferred beyond v3.4.0.

</deferred>

---

*Phase: 22-l2-root-parent-restructure-processor-self-registration*
*Context gathered: 2026-05-31*
