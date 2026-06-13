# Phase 59: Per-Instance L2 Keyspace & Two-State Liveness Value - Context

**Gathered:** 2026-06-13
**Status:** Ready for planning

<domain>
## Phase Boundary

Reshape the **contract surface** for processor-replica liveness — the L2 (Redis) **key builders** and the **value shape** — as the prerequisite foundation that the dual-loop writer (Phase 60) and the WebAPI gate + self-watchdog probe (Phase 61) both consume.

In scope (vocabulary/shape only, like shipped Phases 43 & 50):
- New per-instance key builder `skp:proc:{processorId}:{instanceId}` + per-processor instance-index SET key `skp:proc:{processorId}` in `L2ProjectionKeys` (golden-test-pinned). (KEY-01, KEY-02)
- A shared `instanceId` resolver reusing the existing `POD_NAME → HOSTNAME → MachineName → GUID` chain. (KEY-03)
- A new liveness-only value type carrying two-state `status` + per-schema `summary`; `inputDefinition`/`outputDefinition` dropped from the L2 value. (KEY-04, STATE-01, STATE-02)

Explicitly NOT in this phase (downstream):
- The startup + heartbeat **writing** logic, unhealthy-is-written, `SADD` index, TTL, split intervals, in-memory L1 record → **Phase 60**.
- The WebAPI `SMEMBERS`→`GET`-each ≥1-healthy gate + lazy-`SREM` + the self-watchdog probe → **Phase 61**.
- Anything touching the **workflow-root** liveness (`WorkflowLifecycle`/`WorkflowFireJob` `LivenessProjection` with status `active`) — that is a different concern and is **out of scope** for the whole milestone.

</domain>

<decisions>
## Implementation Decisions

### Value Type Identity
- **D-01:** Introduce a **new, isolated processor-liveness-only value record** (e.g. `ProcessorLivenessEntry { timestamp, interval, status, summary }`) rather than reshaping the existing `ProcessorProjection` in place. Rationale: the nested `LivenessProjection` record is **shared** with the out-of-scope workflow-root liveness path (`WorkflowLifecycle.cs:110`, `WorkflowFireJob.cs:94`, `RedisProjectionWriter.cs:61` all write `LivenessProjection` with status `"active"`/`"Pending"`). A new record fully isolates the processor-replica path so the reshape cannot ripple into out-of-scope code. The name also signals the breaking contract change. Accepted cost: `timestamp`/`interval`/`status` are duplicated rather than reused from `LivenessProjection`.
- The legacy `ProcessorProjection` (carrying `inputDefinition`/`outputDefinition` + nested `LivenessProjection`) and `L2ProjectionKeys.Processor(Guid)` are **left in place** in this phase (see D-03 posture) and are retired when their last caller is swapped in Phase 60/61.

### summary + status Modeling
- **D-02:** **String-const Single-Source-of-Truth** modeling, matching the existing `LivenessStatus` / `L2ProjectionKeys` / `OrchestratorQueues` discipline (one const both writer and reader consume so they cannot desync; JSON-stable strings; no `JsonStringEnumConverter` wiring). Concretely:
  - Add `LivenessStatus.Unhealthy` alongside the existing `LivenessStatus.Healthy` const.
  - New `SchemaOutcome` SoT const class with `Success` / `Fail`.
  - `summary` is a nested record `{ inputSchema, outputSchema, configSchema }`, each a `SchemaOutcome` string.
  - **Invariant encoded in the type via a static factory/smart-constructor:** `status` is **derived** from `summary` — **any `Fail` ⇒ `Unhealthy`**; a **null schema id ⇒ `Success` (null-is-skip / not-failing)**. The Phase-60 writer feeds per-schema outcomes into the factory and cannot produce a `status` that contradicts the `summary`. (STATE-01, STATE-02; satisfies the SC-3 "any FAIL ⇒ unhealthy" and SC-4 "null-is-skip" semantics at the contract layer.)
- **D-02a (configSchema source):** the `configSchema` outcome is **derived from the v6.0.0 Gate A startup config-compat result — never recomputed** (a null `ConfigSchemaId` ⇒ `Success`, consistent with input/output). Phase 59 defines the *field + factory mapping*; the actual Gate A → `summary` plumbing is exercised by the Phase-60 writer. The contract must make "configSchema is the Gate A outcome, null-is-skip" expressible without recomputation.

### Reshape Posture
- **D-03:** **Additive contract surface** (NOT a coupled clean-break). Phase 59 adds the new key builders + the new value record + the shared instanceId resolver + golden/shape/factory **hermetic tests**, leaving the heartbeat writer (`ProcessorLivenessHeartbeat`) and the orchestration-start reader (`ProcessorLivenessValidator`) on the old `skp:{id}` / `ProcessorProjection` path **untouched**. The solution builds 0-warning purely by addition (nothing removed, no consumer compile-forced). The old `L2ProjectionKeys.Processor(Guid)` and `ProcessorProjection` are **deleted in Phase 60 (writer swap) / Phase 61 (reader swap)** when their last caller moves. Mirrors the shipped Phase-50 decomposition (50-01 additive surface → later teardown). Rationale: the writer logic is explicitly Phase 60 and the reader logic Phase 61, so a clean-break-now would pull writer/reader touches into 59 and overlap those phase boundaries; additive keeps the boundaries clean while still satisfying SC-5 (builds green, hermetic suite green against the reshaped contract).
  - Note for planner: "replacing the single `skp:{processorId}` key" (KEY-01 / SC-1) is satisfied at the **builder/contract** level here — the new scheme is THE scheme going forward; the physical deletion of the old builder is deferred to the consumer swap in 60/61. The SC-3 "serialization/shape test confirms `inputDefinition`/`outputDefinition` absence" is asserted against the **new** value record (which never had them), not by mutating `ProcessorProjection`.

### instanceId Resolution
- **D-04:** Phase 59 **extracts a single shared `instanceId` resolver** (an SoT, mirroring how `L2ProjectionKeys` is the key SoT) that hoists the currently-**duplicated** `POD_NAME → HOSTNAME → MachineName → GUID` chain (present independently in `BaseApi.Core/.../ObservabilityServiceCollectionExtensions.cs:110-113` and `BaseConsole.Core/.../BaseConsoleObservabilityExtensions.cs:101-104`). The Phase-60 writer injects/consumes it. Satisfies KEY-03 "reused, no new mechanism" at the contract/foundation layer and removes the existing duplication. The key builder takes a plain `string instanceId` parameter (resolution is the resolver's job, not the builder's).
  - Open for the planner/researcher to confirm: the cleanest home for the shared resolver (a leaf both processors and the existing observability extensions can depend on without a cycle), and whether the two existing observability copies are repointed to it now (dedupe) or left until a later sweep. The chain's `GUID` fallback uses `ToString("N")` today — keep that exact rendering.

### Claude's Discretion
- Exact type/file names (`ProcessorLivenessEntry`, `SchemaOutcome`, the resolver type) — planner/executor choose names consistent with the leaf's conventions; the *shapes and semantics* above are locked.
- Golden-test string-pinning mechanics and the home of the new hermetic tests (mirror the existing `L2ProjectionKeys` golden tests).
- `[property: JsonPropertyName(...)]` placement on the new record — but it is **load-bearing** (RESEARCH Pitfall 1 in `LivenessProjection`/`ProcessorProjection`): on a positional record the attribute MUST target the property, or STJ ignores it. The new record must follow the same `[property: JsonPropertyName]` pattern with explicit lower-camel JSON names.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Milestone source of truth
- `.planning/ROADMAP.md` §"Phase 59" + the v7.0.0 milestone header (lines ~16-43) — the locked goal, success criteria, and build order.
- `.planning/REQUIREMENTS.md` — KEY-01/02/03/04, STATE-01/02 (this phase); STATE-03 + LOOP-* + L1-01 (Phase 60), GATE-* + PROBE-* (Phase 61). The "Out of Scope" section (workflow-root liveness; no `HEXPIRE`; Gate A/B logic unchanged) is binding.

### The contracts being reshaped (leaf: `Messaging.Contracts.Projections`)
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — the static-const key SoT; add `PerInstance(Guid,string)` + `InstanceIndex(Guid)`; `Prefix = "skp:"` const; existing `Processor(Guid)` is the builder being superseded.
- `src/Messaging.Contracts/Projections/ProcessorProjection.cs` — the legacy processor value (definitions + nested Liveness); **left in place** in 59, retired in 60/61.
- `src/Messaging.Contracts/Projections/LivenessProjection.cs` — **SHARED** nested liveness sub-doc; do **NOT** reshape (workflow-root depends on it). Read for the `[property: JsonPropertyName]` load-bearing pattern.
- `src/Messaging.Contracts/Projections/LivenessStatus.cs` — string-const SoT; add `Unhealthy` (additive).

### Writer / reader the new shape must remain compatible with (untouched in 59; swapped in 60/61)
- `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` — the current only-when-Healthy writer to `skp:{id}` (Phase-60 swap target).
- `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` — the current orchestration-start reader of `skp:{id}` (Phase-61 swap target; shows the malformed/absent/stale → 422 contract and the `timestamp + interval*2 > now` staleness math).
- `src/BaseProcessor.Core/Identity/IProcessorContext.cs` + `ProcessorContext.cs` — source of the Gate A outcome (`ConfigSchemaId`/`ConfigDefinition`, null-is-skip) that D-02a maps into the `configSchema` summary field (Phase-60 plumbing).

### instanceId resolution (D-04)
- `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs:98-113` — one copy of the `POD_NAME → HOSTNAME → MachineName → GUID` chain (`GUID` = `ToString("N")`).
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs:89-104` — the duplicated second copy.

### Pattern precedent (prior "vocabulary/shape" phases)
- `.planning/ROADMAP.md` §Phase 43 (Message Contracts & L2 Key Reshape) and §Phase 50 (Contracts & Slot-Array L2 Key Reshape) — the additive-surface-then-teardown decomposition this phase mirrors (D-03).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`L2ProjectionKeys` static-const SoT** (`Messaging.Contracts/Projections/L2ProjectionKeys.cs`) — extend with the two new builders; `const Prefix = "skp:"` already owned here. Golden tests already pin its strings — mirror them for the new builders.
- **`LivenessStatus` const SoT** — add `Unhealthy` additively (`Healthy` already present and consumed by the heartbeat + validator).
- **The `[property: JsonPropertyName]` positional-record pattern** in `LivenessProjection`/`ProcessorProjection` — reuse verbatim for the new value record (it is load-bearing for STJ).
- **The `POD_NAME → HOSTNAME → MachineName → GUID` chain** — already written twice; D-04 hoists it once and reuses (no third copy, no new mechanism).

### Established Patterns
- **Single-source-of-truth, can't-desync** const/record types in the `Messaging.Contracts` leaf consumed by both writer and reader — the new key builders + value record + outcome consts follow this.
- **Additive-surface → later-teardown** within a breaking reshape (Phases 43, 50) — D-03 follows it; old builder/value deleted when the last caller moves (60/61).
- **Hermetic golden/shape tests** pin exact key strings and serialized JSON shapes (no real stack) — SC-5's "hermetic suite green" is satisfied by adding these.

### Integration Points
- New value record + key builders are **consumed by** the Phase-60 dual-loop writer and the Phase-61 WebAPI gate/probe — Phase 59 must expose them with the exact shape those phases need (per-schema `summary` derivable from Gate A; `status` derived from `summary`; per-instance key + index key; string `instanceId`).
- `LivenessProjection` is the **boundary not to cross** — the workflow-root liveness path shares it and is out of scope.

</code_context>

<specifics>
## Specific Ideas

- The value record SHOULD expose a static factory (smart-constructor) that takes the three per-schema outcomes (input/output/config, each nullable-skip) + timestamp + interval and returns the record with `status` already derived (any `Fail` ⇒ `Unhealthy`, null ⇒ `Success`). This is the single place the STATE-01/02 invariant is enforced, so neither the Phase-60 writer nor any future caller can desync `status` from `summary`.
- The SC-3 "definitions absent" test should assert against the **new** record's serialized JSON (it has no `inputDefinition`/`outputDefinition` keys by construction), not by mutating the legacy `ProcessorProjection`.
- Keep the `GUID` fallback rendering exactly as today (`Guid.NewGuid().ToString("N")`) when hoisting the instanceId chain, so behavior is byte-identical to the existing two copies.

</specifics>

<deferred>
## Deferred Ideas

- **Delete `L2ProjectionKeys.Processor(Guid)` + `ProcessorProjection`** — deferred to Phase 60 (writer swap) / Phase 61 (reader swap), when their last callers move to the new scheme (per D-03 additive posture).
- **Dual-loop writing / unhealthy-is-written / `SADD` index / per-key TTL / split startup+heartbeat intervals / in-memory L1 record** — Phase 60 (STATE-03, LOOP-01..04, L1-01).
- **`SMEMBERS`→`GET`-each ≥1-healthy gate / 422 + RFC 7807 / lazy-`SREM` / self-watchdog probe / summary in probe body** — Phase 61 (GATE-01..03, PROBE-01/02).
- **K8s liveness-probe wiring + restart policy** — explicitly future (milestone "Future Requirements").
- **Mid-life health re-validation (TOCTOU `healthy → unhealthy` within a process)** — out of scope; frozen-healthy this milestone.
- **Repointing the two existing observability instanceId copies to the new shared resolver** — if the planner judges it a separate sweep, it may land after 59; D-04's mandate is the SoT resolver exists and the liveness path uses it.

</deferred>

---

*Phase: 59-per-instance-l2-keyspace-two-state-liveness-value*
*Context gathered: 2026-06-13*
