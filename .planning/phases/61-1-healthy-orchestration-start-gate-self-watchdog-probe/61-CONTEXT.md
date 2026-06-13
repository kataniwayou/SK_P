# Phase 61: ≥1-Healthy Orchestration-Start Gate + Self-Watchdog Probe - Context

**Gathered:** 2026-06-13
**Status:** Ready for planning

<domain>
## Phase Boundary

The two **reader-side** consumers of the per-replica liveness keyspace that Phases 59–60 built, plus the legacy-contract teardown that the reader swap unblocks.

**(a) WebAPI ≥1-healthy orchestration-start gate (GATE-01/02/03)** — swap `ProcessorLivenessValidator` from the single last-write-wins read of `skp:{processorId}` (`ProcessorProjection`) to: `SMEMBERS skp:proc:{processorId}` (instance index) → `GET` each per-instance key `skp:proc:{processorId}:{instanceId}` → deserialize `ProcessorLivenessEntry` → admit the processor iff **≥1** replica is **present AND `status=healthy` AND non-stale** (`timestamp + interval×2 > now`, interval read from the entry, SECONDS). Present-but-`unhealthy` and present-but-stale each fail *that* replica (presence no longer implies live). When no replica qualifies → **422 + RFC 7807**. Genuine Redis faults still surface as **500** (unchanged).

**(b) Self-watchdog liveness probe (PROBE-01/02)** — a processor-scoped `IHealthCheck` that reads the in-memory `IProcessorLivenessState.Current` (`ProcessorLivenessEntry?`) and reports `Unhealthy` when the L1 timestamp is stale beyond the active-interval ×2 grace (a silently-crashed startup/heartbeat loop while the host stays up), returning the per-schema `summary` in its response body. It lands on `/health/live` for processors only.

**(c) Legacy teardown** — delete `L2ProjectionKeys.Processor(Guid)`, the `RedisProjectionKeys.Processor` forwarder, and the `ProcessorProjection` record once the validator (their last caller) swaps off them (Phase 59 D-03 / Phase 60 D-05 additive→teardown decomposition).

**In scope (reader + probe + teardown):**
- `ProcessorLivenessValidator` swap to `SMEMBERS`→`GET`-each ≥1-healthy logic, 422 + RFC 7807, lazy-`SREM` (GATE-01/02/03).
- New processor-scoped self-watchdog `IHealthCheck` reading L1, on `/health/live`, returning `summary` (PROBE-01/02).
- A **generic pluggable-check hook** in `BaseConsole.Core` so a processor-specific `live`-tagged check reaches the embedded inner-Kestrel listener (the only way given the dependency direction).
- Delete the legacy `Processor(Guid)` builder + `RedisProjectionKeys.Processor` forwarder + `ProcessorProjection` (D-05).

**Explicitly NOT in this phase (downstream / out of scope):**
- Live/RealStack proof + triple-SHA `psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues` net-zero close gate → **Phase 62** (TEST-01/02/03).
- K8s liveness/startupProbe wiring + pod-restart policy — explicitly **future** (milestone "Future Requirements"); this phase delivers the probe *semantics* only.
- Mid-life health re-validation (`healthy → unhealthy` within a process) — out of scope; frozen-healthy this milestone (the probe catches a *stale/crashed loop*, never a health flip).
- Workflow-root liveness (`LivenessProjection` with status `active`) — out of scope for the whole milestone; do NOT touch the shared `LivenessProjection`.
- The writer side (startup/heartbeat loops, `SADD`, TTL, L1 holder) — already shipped in **Phase 60**.

</domain>

<decisions>
## Implementation Decisions

### Self-watchdog probe — endpoint & semantics (PROBE-01, PROBE-02)
- **D-01 (endpoint — augment `/health/live`, processor-scoped):** The watchdog becomes part of the existing `/health/live` probe **for processors only**. `/health/live` is the K8s liveness probe, so it is the correct surface to trigger the future pod restart (the milestone intent). `Orchestrator`/`Keeper` `/health/live` stay **self-only, unchanged** — the watchdog is added only in the processor's composition. This deliberately extends `/health/live`'s historically self-only-never-fail contract (`EmbeddedHealthEndpointService.cs:76`), but only for the processor, and only on an *in-process* loop-liveness signal (not a dependency blip — Redis/RMQ are still never consulted by `/health/live`).
- **D-02 (boot/null verdict — `Current == null ⇒ Unhealthy`):** Before the first L1 write the holder's `Current` is null (the startup loop writes its first `unhealthy` entry only after identity resolves — Phase 60 D-02). The watchdog reports **`Unhealthy` ("liveness loop not started")** while `Current` is null — a loop that crashed before ever writing is caught. This is the safe liveness default; boot coverage is the future K8s `startupProbe`'s job (out of scope), so a null-during-boot Unhealthy is acceptable and correct for the probe semantics this phase delivers.
- **D-03 (verdict math — recorded-interval staleness):** Fresh ⇒ `Healthy`; `now > Current.Timestamp + Current.Interval × 2` ⇒ `Unhealthy`. The grace uses the **interval recorded on the entry** (Phase 60 D-12: heartbeat entries record 10s, startup `unhealthy` entries record the 30s `BackoffCap` anchor), so the probe adapts to whichever loop last wrote — identical math to the gate. Use the same `TimeProvider`/clock discipline the writer + validator use (`_clock.GetUtcNow().UtcDateTime`).
- **D-04 (summary in body — PROBE-02):** The probe returns the per-schema `summary` (`{inputSchema, outputSchema, configSchema}`) in its response body so the future K8s restart trigger has the diagnostic. Carry it via the `HealthCheckResult` `data` dictionary; the listener already uses `UIResponseWriter.WriteHealthCheckUIResponse`, which serializes per-check `data`. (Exact `data` key names = Claude's discretion; the `summary` fields must be present.)

### Self-watchdog probe — cross-library wiring (PROBE-01)
- **D-05 (generic pluggable-check hook in `BaseConsole.Core`):** `BaseConsole.Core` is a **leaf** that `BaseProcessor.Core` depends on (not the reverse), so `EmbeddedHealthEndpointService` physically cannot reference a `BaseProcessor` check type. Add a **generic seam** to `BaseConsole.Core`: a registered collection of additional health-check descriptors (name + tags + factory) in the OUTER container that `EmbeddedHealthEndpointService` enumerates and folds into its inner Kestrel container, bridging the OUTER `IServiceProvider` in — exactly the pattern `BusReadyHealthCheck` already uses to reach outer state (`EmbeddedHealthEndpointService.cs:73`, `BusReadyHealthCheck` ctor takes `_outer`). `BaseProcessor.Core` then registers `LivenessWatchdogHealthCheck` (reading `IProcessorLivenessState`) tagged `"live"`, and the listener picks it up automatically. Reusable by any console; one focused, additive change to the shared library — no second listener.

### WebAPI gate — discovery + verdict (GATE-01, GATE-02)
- **D-06 (replica discovery — `SMEMBERS` then `GET`-each):** The validator discovers replicas by `SMEMBERS skp:proc:{processorId}` (`L2ProjectionKeys.InstanceIndex(proc.Id)`) with **no prior knowledge of instanceIds**, then `GET`s each `L2ProjectionKeys.PerInstance(proc.Id, instanceId)`. An empty/missing index SET → zero replicas → fails the gate (equivalent to today's "absent"). Replaces the single-key `present ⟺ live` read.
- **D-07 (verdict — first-qualifier-wins, ≥1):** A processor PASSES iff **≥1** discovered replica is present AND `status == LivenessStatus.Healthy` AND non-stale (`liveness.Timestamp.AddSeconds(entry.Interval * 2) > now`). A single healthy-and-fresh replica admits the workflow even when siblings are unhealthy/stale/absent. Compare against the `LivenessStatus`/`SchemaOutcome` **consts**, never string literals (carried discipline). Deserialize each value to `ProcessorLivenessEntry` (the new liveness-only record), not `ProcessorProjection`.

### WebAPI gate — failure reporting & resilience (GATE-03)
- **D-08 (no-qualifier → 422 with aggregate reason; malformed = fail-that-replica):** When no replica qualifies, throw `OrchestrationValidationException.ProcessorNotLive(proc.Id, reason)` → **422 + RFC 7807** (same exception/handler contract). The `reason` is an **aggregate** with a replica-count breakdown (e.g. `"no healthy replica (3 checked: 1 absent, 1 unhealthy, 1 stale)"`). A present-but-**malformed** per-instance JSON value (bad shape / null liveness / `JsonException`) fails **that replica** (counted like `unhealthy`) — it is **never** a 500. This preserves the existing WR-01 reasoning (external self-registered data we don't own must not 500) while extending the single-reason string to a multi-replica aggregate.
- **D-09 (lazy-`SREM` — absent-only, fire-and-forget):** Only members whose per-instance `GET` returns null (TTL-expired) are lazily `SREM`'d from the index (self-healing). Present-but-stale and present-but-unhealthy keys are **NOT** pruned (the key still exists, TTL alive — the replica is still registered, just not admittable now). The `SREM` is **fire-and-forget**: a `SREM` failure never changes the gate verdict and never surfaces as a 500.
- **D-10 (genuine Redis fault → 500, unchanged):** A real connectivity/`RedisException` on `SMEMBERS`/`GET` still propagates as a **500** (the existing redisOp-catch behavior). Only the deterministic absent/unhealthy/stale/malformed *data* states map to the 422 gate. The 422-vs-500 split is the load-bearing contract from the shipped validator (`ProcessorLivenessValidator.cs:37-40` WR-01 note).

### Legacy teardown (D-05 of Phase 60 / D-03 of Phase 59)
- **D-11 (delete the old contract — the reader is its last caller):** With the validator swapped, delete `L2ProjectionKeys.Processor(Guid)`, the `RedisProjectionKeys.Processor` thin forwarder (`RedisProjectionKeys.cs:19`), and the `ProcessorProjection` record (`Messaging.Contracts/Projections/ProcessorProjection.cs`). Verified callers at discuss time: ONLY `ProcessorLivenessValidator` (reader) + the `RedisProjectionKeys` forwarder. **Do NOT** touch the SHARED `LivenessProjection` record (workflow-root path depends on it — Phase 59 D-01 boundary). Re-point/retire any hermetic tests that pinned the old key string or `ProcessorProjection` round-trip onto the new `PerInstance`/`InstanceIndex` + `ProcessorLivenessEntry` shapes.

### Claude's Discretion
- Exact type/file name for the watchdog check (`LivenessWatchdogHealthCheck` suggested) and the `BaseConsole.Core` hook seam (e.g. a `HealthCheckDescriptor` record + an `IEnumerable<...>`/options-registered collection the listener enumerates) — names consistent with the existing `BusReadyHealthCheck` / `ConsoleHealthServiceCollectionExtensions` conventions; the *shape* (outer-provider-bridged, `live`-tagged, additive) is locked (D-05).
- The exact `data`-dictionary key names the probe uses to carry the `summary` (D-04), as long as input/output/config outcomes are all present in the body.
- The precise wording/format of the aggregate 422 `reason` string (D-08) and whether the per-state counts are a formatted string vs a structured `ProcessorLivenessOffending` extension — keep the existing `ProcessorLivenessOffending(procId, reason)` shape unless a structured breakdown is trivially additive.
- `SREM` batching/pipelining mechanics for absent members (D-09) — as long as it is fire-and-forget and absent-only.
- Whether the gate reads per-instance values sequentially or pipelined/`Task.WhenAll` — behavior-equivalent; pick for clarity (the replica count per processor is small).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Milestone source of truth
- `.planning/ROADMAP.md` §"Phase 61" + the v7.0.0 milestone header (lines ~16–29, ~63–74) — locked goal, 5 success criteria, build order (59 → 60 → 61 → 62).
- `.planning/REQUIREMENTS.md` — GATE-01/02/03, PROBE-01/02 (this phase). The "Out of Scope" section (workflow-root liveness; no `HEXPIRE`; Gate A/B logic unchanged) is binding.

### Upstream contract this phase READS (shipped in 59/60 — read first)
- `src/Messaging.Contracts/Projections/ProcessorLivenessEntry.cs` — the value record the gate deserializes + the probe snapshots; `Create(...)` factory, nested `LivenessSummary {inputSchema, outputSchema, configSchema}`. `[property: JsonPropertyName]` is load-bearing (the gate's `JsonSerializer.Deserialize` depends on it).
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `PerInstance(Guid,string)` + `InstanceIndex(Guid)` builders the gate uses; `Processor(Guid)` is the builder being DELETED here (D-11).
- `src/Messaging.Contracts/Projections/LivenessStatus.cs` (`Healthy`/`Unhealthy`) + `SchemaOutcome.cs` (`Success`/`Fail`) — the consts the gate/probe compare against (never literals).
- `src/Messaging.Contracts/Identity/InstanceId.cs` — the instanceId SoT (the index members are these strings).
- `src/BaseProcessor.Core/Liveness/IProcessorLivenessState.cs` + `ProcessorLivenessState.cs` — the L1 holder the watchdog reads (`Current`, volatile-ref snapshot, null until first write — D-02).
- `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` — `IntervalSeconds`(10, heartbeat) / `StartupIntervalSeconds`(30) / `TtlSeconds`(30 floor); confirms the recorded-interval the staleness math reads (D-03/D-07).

### Reader swap target (the code this phase CHANGES)
- `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` — swap single-key `ProcessorProjection` read → `SMEMBERS`→`GET`-each `ProcessorLivenessEntry` ≥1-healthy gate (D-06/07/08/09/10). The existing absent/malformed/stale → 422 + 422-vs-500 split (WR-01 note, lines 37-57) is the contract to preserve/extend.
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs:19` — delete the `Processor` forwarder (D-11); the validator must call `L2ProjectionKeys.InstanceIndex`/`PerInstance` directly (or via new forwarders if the writer-side convention warrants).
- `src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs:76-81` — `ProcessorNotLive(procId, reason)` factory + `ProcessorLivenessOffending` — the aggregate-reason string (D-08) flows here.
- `src/BaseApi.Service/Features/Orchestration/OrchestrationValidationExceptionHandler.cs` — the RFC 7807 422 mapping (unchanged contract).
- `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs:60,80` + `OrchestrationServiceCollectionExtensions.cs:63,76` — where the validator is composed into the gate chain + its `AddScoped` registration (interval/clock unchanged).

### Probe wiring targets (the code this phase CHANGES / ADDS)
- `src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs` — add the generic pluggable-check enumeration (D-05); `/health/live` currently maps the self-only check (line 76, 82-86). The `_outer`-provider bridge pattern (line 73) is the template.
- `src/BaseConsole.Core/Health/BusReadyHealthCheck.cs` — the exact outer-provider-bridged `IHealthCheck` precedent the watchdog + hook mirror.
- `src/BaseConsole.Core/DependencyInjection/ConsoleHealthServiceCollectionExtensions.cs` — where outer `live`/`startup` checks are registered; the hook's registration seam likely lands here or adjacent.
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` — registers the new `LivenessWatchdogHealthCheck` + its descriptor into the hook (alongside the Phase-60 `IProcessorLivenessState` singleton at the documented registration sites).
- `src/Processor.Sample/Program.cs` — the Generic-Host composition that folds `AddBaseProcessor` (no per-app health wiring today; the watchdog must arrive transitively via `AddBaseProcessor`).

### Pattern precedent
- `.planning/phases/59-.../59-CONTEXT.md` + `.planning/phases/60-.../60-CONTEXT.md` — the locked upstream decisions (D-01 isolation, D-12 recorded-interval, D-05 additive→teardown). `60-PATTERNS.md` / `59-PATTERNS.md` — analog map + `[property: JsonPropertyName]` + golden-pin disciplines.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`BusReadyHealthCheck` (`BaseConsole.Core/Health/`)** — the exact outer-`IServiceProvider`-bridged `IHealthCheck` pattern the watchdog copies; constructed with `_outer`, resolves outer state at check time. The generic hook (D-05) generalizes how this single bridge is wired.
- **`ProcessorLivenessValidator`'s 422-vs-500 split + `JsonException`→422 WR-01 handling** — the swap preserves this; absent/malformed/stale stay 422, real `RedisException` stays 500.
- **`ProcessorLivenessEntry` + `LivenessStatus`/`SchemaOutcome` consts** (Phase 59) — the gate deserializes into the record and compares against the consts; the probe snapshots the same record from L1.
- **`OrchestrationValidationException.ProcessorNotLive` + `ProcessorLivenessOffending`** — the existing 422 factory the aggregate reason (D-08) reuses.
- **`UIResponseWriter.WriteHealthCheckUIResponse`** (already used by all three probes) — serializes per-check `data`, so the probe's `summary` (D-04) rides for free.

### Established Patterns
- **Outer-provider bridge into the inner Kestrel container** (`BusReadyHealthCheck` + `EmbeddedHealthEndpointService._outer`) — the watchdog reaches the OUTER `IProcessorLivenessState` singleton the same way; the generic hook (D-05) makes this a reusable seam instead of a one-off.
- **Additive-surface → later-teardown within a breaking reshape** (Phases 43, 50, 59, 60) — Phase 61 is the *teardown* step: the reader swaps and the old `Processor(Guid)`/`ProcessorProjection` delete (D-11).
- **String-const SoT comparison (never literals)** — gate/probe compare `status` against `LivenessStatus.Healthy`, outcomes against `SchemaOutcome.*`.
- **Hermetic mock-Redis validator tests + golden key-string pins** — the swap re-points existing `ProcessorLivenessFacts` / `RedisProjectionKeysTests` onto `SMEMBERS`/`GET`-each + `PerInstance`/`InstanceIndex`.

### Integration Points
- The gate reads the **L2 per-instance keys + index** the Phase-60 writer populates; the recorded-interval + derived-TTL the writer wrote (Phase 60 D-12/D-13) are exactly what the gate's `interval×2` staleness math + the lazy-`SREM` absent detection depend on — writer and reader must stay in lockstep on `interval`.
- The probe reads the **`IProcessorLivenessState` L1 holder** both Phase-60 loops update every iteration; `Current == null` is the genuine pre-first-write state (D-02).
- The generic check-hook (D-05) is a **new reusable `BaseConsole.Core` seam** — Orchestrator/Keeper don't register a watchdog, so their `/health/live` is unchanged; only `BaseProcessor.Core` populates it.
- This is the last phase before **Phase 62** (RealStack proof + close gate) — the gate + probe must be live-correct, not just hermetically green, because Phase 62 proves them against the real stack with two replicas.

</code_context>

<specifics>
## Specific Ideas

- The watchdog `IHealthCheck` should be a thin reader: snapshot `IProcessorLivenessState.Current`; null → `Unhealthy("liveness loop not started")`; else compare `now` to `Current.Timestamp + Current.Interval×2` using the same `TimeProvider` the writer uses; attach the `summary` fields to `HealthCheckResult.data`.
- Keep the gate's 422-vs-500 boundary identical to the shipped validator: deterministic *data* states (absent/unhealthy/stale/malformed-value) → 422; transport `RedisException` → 500. The only behavioral change is single-key → ≥1-of-N.
- Lazy-`SREM` is opportunistic index hygiene, not a correctness gate — fire-and-forget, absent-only, never blocks or fails the verdict.
- Delete the legacy contract in the SAME phase as the reader swap (the validator is its last caller) — do not leave `Processor(Guid)`/`ProcessorProjection` as dead code; but never touch the SHARED `LivenessProjection`.

</specifics>

<deferred>
## Deferred Ideas

- **RealStack/live proof (two replicas, restart-as-unhealthy, ≥1-healthy admit / none-qualify 422, stale-L1 probe fail) + triple-SHA net-zero close gate** — **Phase 62** (TEST-01/02/03).
- **K8s `livenessProbe` wiring to `/health/live` + `startupProbe` for boot coverage + pod-restart policy** — explicitly **future** (milestone "Future Requirements"); this phase delivers only the probe semantics + the restart-trigger signal.
- **Mid-life health re-validation (`healthy → unhealthy` TOCTOU within a process)** — out of scope; frozen-healthy this milestone.
- **Repointing the two existing observability `instanceId` copies to `InstanceId.Resolve()`** — optional sweep carried from Phase 59 D-04; not required for this phase.

</deferred>

---

*Phase: 61-1-healthy-orchestration-start-gate-self-watchdog-probe*
*Context gathered: 2026-06-13*
</content>
</invoke>
