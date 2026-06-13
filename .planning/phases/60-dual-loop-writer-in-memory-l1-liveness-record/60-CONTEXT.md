# Phase 60: Dual-Loop Writer + In-Memory L1 Liveness Record - Context

**Gathered:** 2026-06-13
**Status:** Ready for planning

<domain>
## Phase Boundary

Wire the **writing** side of the v7.0.0 per-replica liveness contract that Phase 59 shaped. The processor's **startup loop** and **heartbeat loop** both write the per-instance liveness entry to **L2** (Redis) and to a new **in-memory L1 record** on every iteration. The startup loop writes `unhealthy` (so a starting/restarting replica is visible in L2, never absent); the heartbeat loop refreshes timestamp-only with health **frozen `healthy`**. Each replica `SADD`s its own `instanceId` to the instance-index SET on its first write; each per-instance key carries a TTL (the liveness source of truth). Liveness intervals split into `startup_interval` / `heartbeat_interval`; each entry records its active interval.

**In scope (writer + L1 only):**
- Startup orchestrator writes `unhealthy` per resolution iteration → L2 + L1 (STATE-03, LOOP-01).
- Heartbeat writes `healthy` (frozen) per beat → L2 + L1; swapped onto the new per-instance key/value (LOOP-02).
- First per-instance write `SADD`s `instanceId` to the index SET; per-instance key written with a TTL (LOOP-04).
- Split `startup_interval` / `heartbeat_interval`; entry records active interval; `Ttl` knob retained (LOOP-03).
- New dedicated in-memory L1 holder updated by BOTH loops (L1-01).

**Explicitly NOT in this phase (downstream / out of scope):**
- The WebAPI `SMEMBERS`→`GET`-each **≥1-healthy gate**, 422 + RFC 7807, lazy-`SREM`, and the **self-watchdog probe** → **Phase 61** (GATE-01/02/03, PROBE-01/02). Phase 60 does NOT touch `ProcessorLivenessValidator`.
- Live/RealStack proof + the triple-SHA close gate → **Phase 62**.
- Workflow-root liveness (`LivenessProjection` with status `active`) — out of scope for the whole milestone.
- Mid-life health re-validation (`healthy → unhealthy` within a process) — frozen-healthy this milestone.
- K8s probe wiring / restart policy — future.

</domain>

<decisions>
## Implementation Decisions

### Writer topology — who writes what, where (LOOP-01, LOOP-02, STATE-03)
- **D-01:** The **`ProcessorStartupOrchestrator` writes the `unhealthy` entry INLINE** at each of its resolution iterations (Loop A identity retries, Loop B per-definition retries, and after Gate A). Rationale: the orchestrator is single-threaded over its own resolution progress, so it can safely read partial schema-resolution state to build the `summary` — a *separate* concurrent loop would violate the **WR-03 memory-visibility invariant** (`IProcessorContext`'s definition properties are NOT barrier-safe and are documented as "read only after `IsHealthy`"). The `ProcessorLivenessHeartbeat` continues to own the **`healthy`** write post-Healthy. Two existing background services, each owning its phase's write, with a clean handoff at `MarkHealthy`. This is the literal reading of "BOTH the startup loop and the heartbeat loop write."
- **D-02 (first-write timing — precise STATE-03 semantics):** The per-instance key is `skp:proc:{processorId}:{instanceId}` and `processorId` only exists **after Loop A resolves identity**. Therefore the earliest a per-instance key (and the index `SADD`) can be written is the **first iteration after identity resolves**. "Writes `unhealthy` from its first iteration, never absent" means *from the first post-identity iteration* — covering the definition-resolution + Gate A window where today the replica writes nothing (it is absent). Before identity resolves there is no `processorId`, hence no key — this is unavoidable and accepted. `instanceId` (`InstanceId.Resolve()`) is available from boot.
- **D-03 (startup cadence — ride the backoff):** The `unhealthy` writes **piggyback on the existing backoff-retry iterations** (1s → `BackoffCap`); no new startup timer is introduced. The entry **records `startup_interval` as its staleness anchor** (see D-12) and the TTL is sized off it. During startup the gate/probe staleness precision is low-stakes (the Phase-61 gate fails an `unhealthy` replica on `status` first; K8s startupProbe — not the liveness probe — covers a pod that is still starting).
- **D-04 (startup summary granularity — per-schema progress):** The `unhealthy` `summary` reflects **real per-schema progress**: each field flips `FAIL → SUCCESS` as that definition resolves (a **null** schema id ⇒ `SUCCESS` / null-is-skip); `configSchema` = the v6.0.0 Gate A outcome (never recomputed). `status` stays `Unhealthy` until all non-null schemas resolve + `MarkHealthy` flips. The orchestrator feeds these per-schema outcomes straight into `ProcessorLivenessEntry.Create(...)` — the single STATE-01/02 invariant point built in Phase 59.

### Key transition — the 60→61 writer/reader skew (RETIRE-adjacent, D-03 of Phase 59)
- **D-05 (hard-swap the writer):** Phase 60 **stops writing the old `skp:{id}` / `ProcessorProjection`** entirely; only the **new per-instance key + index SET** are written. The old contract types (`L2ProjectionKeys.Processor(Guid)`, `ProcessorProjection`) are **left in place** because the reader (`ProcessorLivenessValidator`) still compiles against them — they are **deleted in Phase 61** when the reader swaps (per Phase-59 D-03: a type is deleted only when its *last* caller moves; the writer is one caller, the reader the other).
- **D-06 (knowingly-stale window, accepted):** Between Phase 60 and Phase 61 the orchestration-start liveness reader reads a key nobody writes → sees `absent` → would 422. This is **accepted**: nothing live depends on orchestration-start liveness mid-milestone (no RealStack proof until Phase 62), the reader swaps in Phase 61, and the **hermetic suite stays green** (the validator's tests mock Redis). Matches the locked additive-surface → teardown decomposition (Phases 43, 50, 59).
- **D-07 (reader strictly Phase 61):** Phase 60 does **not** touch `ProcessorLivenessValidator`. The `SMEMBERS`→`GET`-each ≥1-healthy gate logic is GATE-01/02/03 — explicitly Phase 61. Pulling it forward would overlap the locked phase boundary.

### In-memory L1 record (L1-01)
- **D-08 (new dedicated singleton holder):** The L1 record lives in a **new dedicated singleton** (e.g. `IProcessorLivenessState` / `LivenessRecordHolder`), NOT bolted onto `IProcessorContext`. Both loops call an `Update(...)`; the Phase-61 self-watchdog probe reads a snapshot. Mirrors Phase 59's D-01 isolation discipline; the L1 record must be readable **during startup** (`unhealthy`), which is a different access discipline than `IProcessorContext`'s "read only after Healthy" (WR-03) — so it does not belong on that holder.
- **D-09 (reuse `ProcessorLivenessEntry` as the snapshot):** L1-01's fields (`timestamp`, `interval`, `status`, `summary`) are **exactly** `ProcessorLivenessEntry`. The holder stores the same immutable record written to L2 — one type, L1 and L2 cannot desync, no new contract. The probe reads it and compares `timestamp` staleness to `now`.
- **D-10 (volatile immutable-reference swap):** The holder stores a `volatile ProcessorLivenessEntry?`; each loop builds the immutable entry and **swaps the reference** (atomic reference assignment + `volatile` = safe publication across the startup-thread / heartbeat-thread writers and the probe-thread reader). Lock-free, matching the `Interlocked`/`volatile` discipline already used for `IsHealthy` in `IProcessorContext`.

### Intervals & TTL (LOOP-03, LOOP-04)
- **D-11 (config-options shape — minimal churn):** Keep the existing `Interval` config key (default **10s**) meaning the **heartbeat** cadence (no appsettings churn for it); add a **new `StartupInterval`** key. Retain the existing `Ttl` knob. The property split lands on `ProcessorLivenessOptions`.
- **D-12 (cadence defaults):** `heartbeat_interval` = **10s** (today's `Interval`). The recorded **startup `interval` anchor = `BackoffCap` (default 30s)** — the worst-case backoff gap — so downstream `interval×2` staleness math and the TTL both cover the slowest startup write. Each entry records its **active** interval (heartbeat entries record `heartbeat_interval`; startup entries record the startup anchor) so the Phase-61 reader/probe staleness math adapts to which loop wrote it.
- **D-13 (TTL derived from active interval, `Ttl` as floor):** The written per-instance key TTL = **`max(activeInterval × 2, Ttl)`**. Auto-adapts per loop — heartbeat (interval 10) ⇒ ~20–30s; startup (interval 30) ⇒ ~60s — so a live replica's key always comfortably outlives that loop's inter-write gap and never lapses (preserving STATE-03 "never absent" even under a slow backoff). The existing `Ttl` (30s) is **retained as the floor** (satisfies LOOP-03's "Ttl knob retained" + LOOP-04's per-key TTL).

### Heartbeat swap details (carried, not re-litigated)
- **D-14:** The heartbeat keeps its `IsHealthy` gate as the "I am the *healthy* writer" signal, but now writes the **new per-instance key** (`L2ProjectionKeys.PerInstance(id, instanceId)`) with a **`ProcessorLivenessEntry` (frozen `healthy`)** value instead of the old `ProcessorProjection`. The old IsHealthy-gated `ProcessorProjection` write is removed (D-05). Health is frozen `healthy` once the heartbeat starts — timestamp-only refresh, no mid-life re-validation, monotonic within a process, reset on restart.
- **D-15:** The heartbeat also `SADD`s `instanceId` to the index on its first write **iff** the startup orchestrator did not already (idempotent `SADD` — set semantics make a double-add harmless). Both loops update the L1 holder every iteration.

### Claude's Discretion
- Exact type/member names for the new L1 holder (`IProcessorLivenessState`, `LivenessRecordHolder`, `Update`/`Current`) and its DI registration site — planner/executor pick names consistent with `IProcessorContext`'s conventions; the *shape and publication semantics* (D-08/09/10) are locked.
- Exact `ConfigurationKeyName` string for the new `StartupInterval` property and its baked default value (anchor it to `BackoffCap`'s 30s per D-12; pin in a binding test like the existing `ProcessorOptionsBindingFacts`).
- Whether the orchestrator's inline `unhealthy` write is a small private helper vs an injected writer collaborator — as long as it shares the L2-write + L1-update + `SADD` discipline with the heartbeat (avoid two divergent write paths; a shared internal writer is encouraged but not mandated).
- Redis-fault resilience for the startup `unhealthy` write should mirror the heartbeat's **log-and-continue** (D-11/T-26-10 in `ProcessorLivenessHeartbeat`): a Redis fault on a startup write must NOT crash the host or abort resolution.
- TTL rounding/units mechanics for `max(activeInterval×2, Ttl)`.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Milestone source of truth
- `.planning/ROADMAP.md` §"Phase 60" + the v7.0.0 milestone header (lines ~16–29) — locked goal, 5 success criteria, build order (59 → 60 → 61 → 62).
- `.planning/REQUIREMENTS.md` — STATE-03, LOOP-01/02/03/04, L1-01 (this phase). The "Out of Scope" section (workflow-root liveness; no `HEXPIRE`; Gate A/B logic unchanged) is binding.

### Phase 59 foundation (the contract this phase WRITES — read first)
- `.planning/phases/59-per-instance-l2-keyspace-two-state-liveness-value/59-CONTEXT.md` — the locked key/value/resolver decisions (D-01 isolation, D-02/02a summary+status, D-03 additive→teardown, D-04 instanceId SoT). The Deferred section explicitly hands this phase the writer swap + delete-old-contract task.
- `.planning/phases/59-per-instance-l2-keyspace-two-state-liveness-value/59-PATTERNS.md` — analog map for every new symbol; the load-bearing `[property: JsonPropertyName]` + golden-pin disciplines.
- `src/Messaging.Contracts/Projections/ProcessorLivenessEntry.cs` — the value record + `Create(input,output,config,ts,interval)` factory (the STATE-01/02 invariant point the writer feeds) + nested `LivenessSummary`.
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `PerInstance(Guid,string)` + `InstanceIndex(Guid)` builders (+ the old `Processor(Guid)` retired here at the writer, deleted in 61).
- `src/Messaging.Contracts/Projections/LivenessStatus.cs` + `SchemaOutcome.cs` — the `Healthy`/`Unhealthy` + `Success`/`Fail` consts the writer compares against (never literals).
- `src/Messaging.Contracts/Identity/InstanceId.cs` — `InstanceId.Resolve()`; the writer injects/calls it for the `{instanceId}` key segment + the index `SADD` member.

### Writer swap targets (the code this phase changes)
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` — gains the inline `unhealthy` write per resolution iteration (D-01/02/03/04). Loop A (identity), Loop B (per-definition), Gate A are the iteration points; the existing backoff (`BackoffAsync`, cap `BackoffCapSeconds`) is the cadence (D-03).
- `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` — swapped to write the new per-instance key + `ProcessorLivenessEntry` (frozen `healthy`); old `ProcessorProjection` write removed (D-05/14). Keep the log-and-continue resilience + `IsHealthy` gate.
- `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` — add `StartupInterval`; `Interval`=heartbeat, `Ttl` retained as floor (D-11/12/13). Mirror its `ConfigurationKeyName` + binding-test discipline.
- `src/BaseProcessor.Core/Identity/IProcessorContext.cs` + `ProcessorContext.cs` — source of identity (`Id`), the per-schema definition resolution state the `summary` reads (single-threaded inside the orchestrator only — WR-03), and the Gate A `configSchema` outcome.
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:136,178,182` — registers `IProcessorContext`, `ProcessorStartupOrchestrator`, `ProcessorLivenessHeartbeat`; the new L1 holder singleton registers alongside.

### Reader (DO NOT touch in Phase 60 — Phase-61 swap target; shows the contract the writer must satisfy)
- `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` — the current `skp:{id}` reader; its `timestamp + interval×2 > now` staleness math is what D-12/D-13's recorded-interval + TTL must keep honest once the reader swaps in 61.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`ProcessorLivenessEntry.Create(...)`** (Phase 59) — the single STATE-01/02 invariant point; both the startup `unhealthy` write and the heartbeat `healthy` write build their value through it (heartbeat passes all-`SUCCESS`/resolved outcomes ⇒ `Healthy`).
- **`InstanceId.Resolve()`** (Phase 59) — the `{instanceId}` segment + index `SADD` member; available from boot (no identity dependency).
- **`ProcessorLivenessHeartbeat`'s write disciplines** — `_clock.GetUtcNow().UtcDateTime` (same clock the reader uses), blind whole-value `StringSetAsync(..., expiry)` last-write-wins (no RMW), log-and-continue on Redis fault, key built via `L2ProjectionKeys` never a literal. The startup inline write reuses ALL of these.
- **`ProcessorStartupOrchestrator.BackoffAsync` / `BackoffCapSeconds`** — the existing startup cadence the `unhealthy` writes ride (D-03); the cap is the recorded startup-interval anchor (D-12).
- **`ProcessorOptionsBindingFacts`** (precedent from Phase 51 `SlotArrayOptions`) — the binding-test pattern for the new `StartupInterval` knob.

### Established Patterns
- **Single mutable singleton shared between startup-writer and heartbeat-reader** (`IProcessorContext`, registered `AddSingleton`) — the new L1 holder follows the same DI shape; `volatile`/`Interlocked` publication for the one cross-thread-read field (D-10) mirrors `IsHealthy`/`WhenHealthy`.
- **Additive-surface → later-teardown within a breaking reshape** (Phases 43, 50, 59) — the writer hard-swaps now (D-05); the old contract types delete in Phase 61 when the reader moves.
- **A shared internal write path** so the two loops cannot diverge on L2-write + L1-update + `SADD` (Claude's discretion encourages, does not mandate, extracting one).

### Integration Points
- The new per-instance key + index + L1 holder are **consumed by Phase 61** (the `SMEMBERS`→`GET`-each gate reads the L2 keys/index; the self-watchdog probe reads the L1 holder). Phase 60 must expose the L1 holder with a snapshot read the probe can depend on, and write the L2 entries with the exact recorded-interval + TTL the gate's staleness math expects.
- The heartbeat↔startup handoff is at `MarkHealthy`: the last `unhealthy` write (orchestrator) precedes the first `healthy` write (heartbeat) on the same per-instance key — last-write-wins makes the transition clean (no two-writer race because the orchestrator stops after `MarkHealthy` and the heartbeat only writes once `IsHealthy`).

</code_context>

<specifics>
## Specific Ideas

- Prefer a **single shared internal writer** (L2 `StringSetAsync` with the derived TTL + index `SADD` + L1 holder `Update`) that both the orchestrator's inline `unhealthy` write and the heartbeat's `healthy` write call, so the `SADD`/TTL/L1 disciplines cannot drift between the two loops.
- The L1 in-memory record **is** the same `ProcessorLivenessEntry` instance written to L2 on that iteration — write L2 and `Update` L1 from one constructed value, so they are provably identical.
- Keep the startup `unhealthy` write's Redis-fault handling **identical** to the heartbeat's log-and-continue — a dead Redis must not crash startup or abort identity/definition resolution.
- `SADD` is idempotent by set semantics — both loops may `SADD` on their first write without coordination; no "have I added yet?" flag needed.

</specifics>

<deferred>
## Deferred Ideas

- **WebAPI ≥1-healthy orchestration-start gate** (`SMEMBERS`→`GET`-each, 422 + RFC 7807, lazy-`SREM`) + **self-watchdog liveness probe** (reads L1, fails on stale, returns `summary`) — **Phase 61** (GATE-01/02/03, PROBE-01/02).
- **Delete `L2ProjectionKeys.Processor(Guid)` + `ProcessorProjection`** — **Phase 61**, when the reader (last caller) swaps (D-05/06).
- **RealStack/live proof + triple-SHA net-zero close gate** — **Phase 62** (TEST-01/02/03).
- **K8s liveness/startup probe wiring + pod-restart policy** — explicitly future (milestone "Future Requirements").
- **Mid-life health re-validation (`healthy → unhealthy` TOCTOU within a process)** — out of scope; frozen-healthy this milestone.
- **Repointing the two existing observability `instanceId` copies to `InstanceId.Resolve()`** — optional sweep carried from Phase 59 D-04; not required for this phase's writer.

</deferred>

---

*Phase: 60-dual-loop-writer-in-memory-l1-liveness-record*
*Context gathered: 2026-06-13*
