# Phase 60: Dual-Loop Writer + In-Memory L1 Liveness Record - Research

**Researched:** 2026-06-13
**Domain:** .NET 8 / C# distributed-systems — the WRITING side of a per-replica Redis liveness contract (StackExchange.Redis, BackgroundService loops, lock-free in-memory publication, IOptions binding)
**Confidence:** HIGH — every claim is grounded in code read this session; no external/training-only assertions.

## Summary

This phase wires the WRITING half of the v7.0.0 per-replica liveness contract whose value/key/identity SoTs Phase 59 already shipped (`ProcessorLivenessEntry`, `L2ProjectionKeys.PerInstance/InstanceIndex`, `InstanceId.Resolve()`, `LivenessStatus.Unhealthy`, `SchemaOutcome`). Two existing `BackgroundService`s become the two writers: `ProcessorStartupOrchestrator` writes `unhealthy` per resolution iteration (inline, single-threaded, reading partial schema-resolution state — safe only because it owns that state), and `ProcessorLivenessHeartbeat` keeps writing the post-Healthy `healthy` refresh but swapped onto the new per-instance key + `ProcessorLivenessEntry` value (dropping the old `ProcessorProjection`/`skp:{id}` write entirely). A new dedicated singleton L1 holder stores the same immutable `ProcessorLivenessEntry` both loops write, published via a `volatile` reference swap for the Phase-61 probe-thread reader. [VERIFIED: codebase — all six source files + Phase-59 contract read this session]

The work is almost entirely "swap + extend existing disciplines," not new invention. The heartbeat already embodies every write discipline the startup writer must mirror: `_clock.GetUtcNow().UtcDateTime`, blind whole-value `StringSetAsync(..., expiry)` last-write-wins, log-and-continue on Redis fault, key via `L2ProjectionKeys` never a literal. `SetAddAsync` (the SADD) already has an in-repo precedent in `RedisProjectionWriter` (parent-index discipline). The cleanest shape is a single shared internal writer both loops call so the SADD/TTL/L1-update disciplines cannot drift (CONTEXT Claude's-discretion encourages, does not mandate, this). [VERIFIED: codebase]

The single biggest blast-radius item: **adding constructor dependencies to `ProcessorStartupOrchestrator`** (it needs Redis, the L1 holder, and an instanceId) breaks all three test call sites that `new` it directly (`IdentityResolutionFacts`, `SchemaResolutionFacts`, `DispatchBindSequenceFacts`) plus the DI registration. This is the main "don't forget" for the planner. [VERIFIED: grep — 3 `new ProcessorStartupOrchestrator(` call sites]

**Primary recommendation:** Extract one internal shared writer (`L2 StringSetAsync(perInstanceKey, json, expiry: derivedTtl)` + `SetAddAsync(indexKey, instanceId)` + `l1Holder.Update(entry)`), inject it (or the Redis multiplexer + L1 holder + instanceId) into both the orchestrator and the heartbeat, build the value through `ProcessorLivenessEntry.Create(...)`, and write the L1 holder from the *same* constructed `entry` so L1 and L2 are provably identical. Split `Interval` (heartbeat, 10s) / new `StartupInterval` (anchored to `BackoffCap`, 30s); TTL = `max(activeInterval×2, Ttl)` rounded to whole seconds.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Startup `unhealthy` write (per resolution iteration) | Processor / `ProcessorStartupOrchestrator` (BackgroundService) | — | Single-threaded over its own resolution progress; only it can safely read partial schema state for the `summary` (WR-03 — `IProcessorContext` definition props are NOT barrier-safe) |
| Heartbeat `healthy` refresh | Processor / `ProcessorLivenessHeartbeat` (BackgroundService) | — | Already the post-Healthy writer; gated by `IsHealthy`; owns timestamp-only refresh |
| Shared write path (L2 SET + index SADD + L1 update) | Processor / new internal writer collaborator | both loops call it | Prevents two divergent write paths (CONTEXT Specifics) |
| In-memory L1 record | Processor / new singleton holder (`IProcessorLivenessState`) | — | Read DURING startup (`unhealthy`) — different access discipline than `IProcessorContext`'s read-after-Healthy, so NOT on that holder (D-08) |
| L2 (Redis) persistence | Database / StackExchange.Redis | — | Per-instance key + index SET; TTL is liveness SoT |
| L1→probe read | Processor / Phase-61 probe (OUT OF SCOPE this phase) | — | Phase 60 only exposes the snapshot read; the probe consumes it in 61 |

## User Constraints (from CONTEXT.md)

> Phase 60 has a full CONTEXT.md with 15 locked decisions (D-01..D-15). These are BINDING. Reproduced faithfully below; the binding spec is `60-CONTEXT.md`.

### Locked Decisions

- **D-01 — Writer topology:** `ProcessorStartupOrchestrator` writes the `unhealthy` entry INLINE at each resolution iteration (Loop A identity retries, Loop B per-definition retries, after Gate A). It is single-threaded over its own resolution progress, so it can safely read partial schema-resolution state to build the `summary`; a separate concurrent loop would violate WR-03. `ProcessorLivenessHeartbeat` continues to own the `healthy` write post-Healthy. Clean handoff at `MarkHealthy`.
- **D-02 — First-write timing:** Per-instance key is `skp:proc:{processorId}:{instanceId}`; `processorId` only exists AFTER Loop A resolves identity. Earliest a per-instance key (and the index SADD) can be written is the first iteration after identity resolves. "Never absent" means from the first post-identity iteration. Before identity resolves there is no `processorId`, hence no key — unavoidable and accepted. `instanceId` (`InstanceId.Resolve()`) is available from boot.
- **D-03 — Startup cadence:** The `unhealthy` writes piggyback on the existing backoff-retry iterations (1s → `BackoffCap`); no new startup timer. The entry records `startup_interval` as its staleness anchor; TTL sized off it.
- **D-04 — Startup summary granularity:** The `unhealthy` `summary` reflects real per-schema progress — each field flips `FAIL → SUCCESS` as that definition resolves (null schema id ⇒ SUCCESS / null-is-skip); `configSchema` = the v6.0.0 Gate A outcome (never recomputed). `status` stays `Unhealthy` until all non-null schemas resolve + `MarkHealthy`. Feed outcomes straight into `ProcessorLivenessEntry.Create(...)`.
- **D-05 — Hard-swap the writer:** Phase 60 stops writing the old `skp:{id}` / `ProcessorProjection` entirely; only the new per-instance key + index SET are written. The old contract types (`L2ProjectionKeys.Processor(Guid)`, `ProcessorProjection`) are left in place (the reader still compiles against them) — deleted in Phase 61.
- **D-06 — Knowingly-stale window, accepted:** Between Phase 60 and 61 the orchestration-start reader reads a key nobody writes → `absent` → would 422. Accepted; nothing live depends on it mid-milestone; hermetic suite stays green (validator tests mock Redis).
- **D-07 — Reader strictly Phase 61:** Phase 60 does NOT touch `ProcessorLivenessValidator`. The SMEMBERS→GET-each ≥1-healthy gate is GATE-01/02/03 — Phase 61.
- **D-08 — New dedicated singleton holder:** The L1 record lives in a NEW dedicated singleton (e.g. `IProcessorLivenessState` / `LivenessRecordHolder`), NOT on `IProcessorContext`. Both loops call `Update(...)`; the Phase-61 probe reads a snapshot. Must be readable during startup (`unhealthy`) — a different discipline than `IProcessorContext`'s read-after-Healthy (WR-03).
- **D-09 — Reuse `ProcessorLivenessEntry` as the snapshot:** L1's fields (`timestamp`, `interval`, `status`, `summary`) are exactly `ProcessorLivenessEntry`. Store the same immutable record written to L2 — one type, L1 and L2 cannot desync.
- **D-10 — Volatile immutable-reference swap:** The holder stores a `volatile ProcessorLivenessEntry?`; each loop builds the immutable entry and swaps the reference (atomic ref assignment + `volatile` = safe publication across startup-thread / heartbeat-thread writers and probe-thread reader). Lock-free, mirroring `IsHealthy`'s `Interlocked`/`volatile` discipline.
- **D-11 — Config-options shape:** Keep `Interval` (default 10s) = heartbeat cadence (no appsettings churn); add new `StartupInterval`; retain `Ttl`. Property split lands on `ProcessorLivenessOptions`.
- **D-12 — Cadence defaults:** `heartbeat_interval` = 10s (today's `Interval`). Recorded startup `interval` anchor = `BackoffCap` (default 30s) — worst-case backoff gap — so downstream `interval×2` staleness math and TTL cover the slowest startup write. Each entry records its active interval.
- **D-13 — TTL derived from active interval, `Ttl` as floor:** Written per-instance key TTL = `max(activeInterval × 2, Ttl)`. Heartbeat (interval 10) ⇒ floor 30 (max(20,30)); startup (interval 30) ⇒ 60 (max(60,30)). `Ttl` (30s) retained as the floor.
- **D-14 — Heartbeat swap:** Heartbeat keeps its `IsHealthy` gate but writes the new per-instance key (`L2ProjectionKeys.PerInstance(id, instanceId)`) with a `ProcessorLivenessEntry` (frozen `healthy`) instead of `ProcessorProjection`. Old write removed (D-05). Health frozen `healthy` once heartbeat starts — timestamp-only refresh, monotonic within a process, reset on restart.
- **D-15 — Heartbeat SADDs too:** Heartbeat also `SADD`s `instanceId` on its first write iff the orchestrator did not already (idempotent SADD — set semantics make double-add harmless). Both loops update the L1 holder every iteration.

### Claude's Discretion

- Exact type/member names for the L1 holder (`IProcessorLivenessState`, `LivenessRecordHolder`, `Update`/`Current`) and its DI registration site — pick names consistent with `IProcessorContext` conventions; shape + publication semantics (D-08/09/10) are locked.
- Exact `ConfigurationKeyName` string for `StartupInterval` and its baked default (anchor to `BackoffCap`'s 30s per D-12; pin in a binding test like `ProcessorOptionsBindingFacts`).
- Whether the orchestrator's inline `unhealthy` write is a small private helper vs an injected writer collaborator — as long as it shares L2-write + L1-update + SADD discipline with the heartbeat (a shared internal writer is encouraged, not mandated).
- Redis-fault resilience for the startup `unhealthy` write mirrors the heartbeat's log-and-continue — a Redis fault must NOT crash the host or abort resolution.
- TTL rounding/units mechanics for `max(activeInterval×2, Ttl)`.

### Deferred Ideas (OUT OF SCOPE)

- WebAPI ≥1-healthy orchestration-start gate (SMEMBERS→GET-each, 422 + RFC 7807, lazy-SREM) + self-watchdog probe — **Phase 61** (GATE-01/02/03, PROBE-01/02). Phase 60 does NOT touch `ProcessorLivenessValidator`.
- Delete `L2ProjectionKeys.Processor(Guid)` + `ProcessorProjection` — **Phase 61** (when the reader, the last caller, swaps).
- RealStack/live proof + triple-SHA net-zero close gate — **Phase 62** (TEST-01/02/03).
- K8s liveness/startup probe wiring + pod-restart policy — explicitly future.
- Mid-life health re-validation (`healthy → unhealthy` within a process) — out of scope; frozen-healthy this milestone.
- Repointing the two observability `instanceId` copies to `InstanceId.Resolve()` — optional sweep carried from Phase 59 D-04; NOT required this phase.

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| STATE-03 | A starting/failed replica WRITES its `unhealthy` entry — never absent (removes "only Healthy writes") | The orchestrator gains an inline write at each resolution iteration (Loop A/B/Gate A), building the value via `ProcessorLivenessEntry.Create(...)`. See Pattern 1 + Code Examples. |
| LOOP-01 | Startup loop writes the entry (L2 + L1) every iteration, `status`/`summary` reflecting progress | The orchestrator reads its own `context.Input/Output/ConfigSchemaId` + `context.ConfigDefinition`/Gate A outcome single-threaded (WR-03 safe) to build per-schema outcomes. See Pattern 1. |
| LOOP-02 | On success heartbeat starts; each beat refreshes timestamp (L2 + L1), frozen `healthy` | Heartbeat already gated by `IsHealthy`; swap value/key per D-14, add L1 `Update`. See Code Example "Heartbeat swap." |
| LOOP-03 | Split `startup_interval`/`heartbeat_interval`; each entry records active interval; `Ttl` retained | Add `StartupInterval` to `ProcessorLivenessOptions`; pass active interval into `Create(...)`. See Pattern 3 + binding test. |
| LOOP-04 | Each per-instance key written with a TTL; first write SADDs instanceId | `StringSetAsync(..., expiry: derivedTtl)` + `SetAddAsync(InstanceIndex(id), instanceId)` (idempotent). See Code Examples. |
| L1-01 | In-memory L1 record (timestamp, interval, status, summary) updated by BOTH loops; single probe source | New `volatile ProcessorLivenessEntry?` singleton holder (D-08/09/10). See Pattern 2. |

## Standard Stack

> All-internal phase — no new packages. The "stack" is what's already referenced and how it's used.

### Core (already referenced, no version churn)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| StackExchange.Redis (`IConnectionMultiplexer`) | as-pinned in repo | L2 string SET (with expiry) + set SADD | Soft-dep multiplexer (`abortConnect=false`) already injected into the heartbeat; `SetAddAsync` precedent in `RedisProjectionWriter` [VERIFIED: codebase] |
| `Microsoft.Extensions.Hosting` (`BackgroundService`) | net8.0 | Both loops are already `BackgroundService`s | No change to hosting model |
| `System.Text.Json` | net8.0 BCL | Serialize `ProcessorLivenessEntry` to the L2 value | Same `JsonSerializer.Serialize(...)` the heartbeat already uses; default options (the `[property: JsonPropertyName]` pins carry the wire shape) |
| `Microsoft.Extensions.Options` (`IOptions<T>` + `[ConfigurationKeyName]`) | net8.0 | Bind `Interval`/`StartupInterval`/`Ttl` from `"Processor"` section | Exact pattern already in `ProcessorLivenessOptions` |
| `TimeProvider` (`clock.GetUtcNow().UtcDateTime`) | net8.0 BCL | Same clock the reader uses; `FakeTimeProvider`-drivable in tests | Heartbeat + validator + orchestrator backoff all use it [VERIFIED] |

### Supporting (the new in-phase symbols)
| Symbol | Purpose | When to Use |
|--------|---------|-------------|
| `IProcessorLivenessState` / `LivenessRecordHolder` (NEW singleton, name = discretion) | Holds the `volatile ProcessorLivenessEntry?`; `Update(entry)` + `Current` snapshot | Both loops `Update`; Phase-61 probe reads `Current` |
| Shared internal writer (NEW, name/shape = discretion) | `Write(processorId, instanceId, entry, ttl)` → L2 SET + index SADD + L1 Update | Both loops call it to avoid divergence (CONTEXT Specifics) |
| `ProcessorLivenessOptions.StartupIntervalSeconds` (NEW property) | Startup-loop staleness anchor (default 30, = BackoffCap) | Recorded as the `interval` on `unhealthy` entries |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `volatile` reference swap (D-10) | `Interlocked.Exchange<T>(ref field, value)` for the L1 ref | `Interlocked.Exchange<T>` gives a full barrier and is also valid, but D-10 LOCKS `volatile` + plain assignment (atomic for reference types) mirroring the IsHealthy idiom — do NOT introduce a lock or a `lock{}` block |
| Shared internal writer | Two private helpers (one per loop) | Allowed by discretion, but risks SADD/TTL/L1 drift; the shared writer is recommended |
| New startup timer | Ride existing `BackoffAsync` (D-03) | LOCKED — no new timer; the `unhealthy` write lands on each backoff iteration |

**Installation:** none — no `dotnet add package`.

**Version verification:** N/A — no new packages. The StackExchange.Redis / Hosting / Options / STJ versions are inherited unchanged from the existing project references. [VERIFIED: no package additions required]

## Architecture Patterns

### System Architecture Diagram

```
                          processor host process (one per replica)
  ┌─────────────────────────────────────────────────────────────────────────────────┐
  │                                                                                   │
  │   ProcessorStartupOrchestrator (BackgroundService)        ProcessorLivenessHeartbeat │
  │   ── single-threaded over its OWN resolution ──           ── post-Healthy beat loop ──│
  │                                                                                   │
  │   Loop A: identity-by-SourceHash (backoff 1s→cap)                                 │
  │     └─(no key yet: processorId unknown — D-02 accepted gap)                       │
  │   ── identity resolves → context.SetIdentity ──                                   │
  │   Loop A→B→GateA iterations (each backoff tick):                                  │
  │     build outcomes from context.Input/Output/ConfigSchemaId + ConfigDefinition    │
  │     entry = ProcessorLivenessEntry.Create(in,out,cfg, now, StartupInterval)       │
  │         status = Unhealthy (some FAIL) ───────┐                                   │
  │   MarkHealthy() ──────────────────────────────┼──── IsHealthy flips ──────────┐   │
  │                                               │                               │   │
  │                          (every IsHealthy beat, interval = Interval/10s)       ▼   │
  │                          entry = Create(allSUCCESS, now, Interval) → Healthy        │
  │            ┌──────────────────────────────────┼───────────────────────────────┐   │
  │            ▼  SHARED INTERNAL WRITER (both loops call) ▼                        │   │
  │   ┌────────────────────────────────────────────────────────────────────────┐  │   │
  │   │ ttl = max(entry.Interval*2, Ttl)                                         │  │   │
  │   │ (1) L2 StringSetAsync(PerInstance(procId,instId), json, expiry: ttl)     │──┼───┼──▶ Redis L2
  │   │ (2) L2 SetAddAsync(InstanceIndex(procId), instId)   [idempotent SADD]    │──┼───┼──▶ skp:proc:{id} SET
  │   │ (3) L1 holder.Update(entry)   [volatile ref swap]                        │  │   │
  │   │ catch → log-and-continue (NEVER crash host / abort resolution)           │  │   │
  │   └────────────────────────────────────────────────────────────────────────┘  │   │
  │                              │                                                  │   │
  │                              ▼  volatile ProcessorLivenessEntry?                │   │
  │                   ┌───────────────────────────┐                                │   │
  │                   │  L1 holder (singleton)     │◀── Phase-61 probe reads (OUT OF SCOPE) │
  │                   │  Current (snapshot read)   │                                │   │
  │                   └───────────────────────────┘                                │   │
  └─────────────────────────────────────────────────────────────────────────────────┘
                                       │
                                       ▼
        Phase-61 WebAPI gate (OUT OF SCOPE): SMEMBERS skp:proc:{id} → GET each per-instance key
```

### Recommended Project Structure
```
src/BaseProcessor.Core/
├── Liveness/
│   ├── ProcessorLivenessHeartbeat.cs        # MODIFY (D-14): swap key+value, add L1 update + SADD
│   ├── IProcessorLivenessState.cs           # NEW (D-08): L1 holder interface (name = discretion)
│   ├── ProcessorLivenessState.cs            # NEW (D-08/10): volatile-ref-swap impl
│   └── ProcessorLivenessWriter.cs           # NEW (recommended): shared internal writer
├── Startup/
│   └── ProcessorStartupOrchestrator.cs      # MODIFY (D-01/02/03/04): inline unhealthy write per iteration
├── Configuration/
│   └── ProcessorLivenessOptions.cs          # MODIFY (D-11): add StartupInterval
└── DependencyInjection/
    └── BaseProcessorServiceCollectionExtensions.cs  # MODIFY: register L1 holder + writer (near line 136)
```

### Pattern 1: Inline `unhealthy` write per resolution iteration (D-01/02/04, LOOP-01, STATE-03)

**What:** At each backoff iteration of Loop A/Loop B and after Gate A, once `context.Id` is non-null, the orchestrator builds the per-schema outcomes from its OWN single-threaded resolution state and writes an `unhealthy` entry through the shared writer.

**When to use:** Every place the orchestrator currently logs "retrying in {Delay}" and after Gate A's `coverage` evaluation. Building outcomes is safe HERE (and only here) because the orchestrator owns the resolution state — WR-03 forbids reading `context`'s definition props from another thread before Healthy.

**Per-schema outcome rule (mirrors `Create`'s null-is-skip):**
- A schema with a **null** id ⇒ `SchemaOutcome.Success` (null-is-skip).
- A schema with a non-null id but **not yet resolved** (definition still null) ⇒ `SchemaOutcome.Fail`.
- A resolved definition ⇒ `SchemaOutcome.Success`.
- `configOutcome` = the Gate A outcome — but Gate A runs AFTER Loop B; before Gate A, treat config as `Fail` if `ConfigSchemaId` is non-null and unresolved/un-evaluated, `Success` if null. (Planner: confirm the exact pre-Gate-A config outcome convention — see Open Q1.)

**Example (skeleton — the orchestrator builds outcomes then calls the shared writer):**
```csharp
// Source: derived from ProcessorStartupOrchestrator.cs (read this session) + ProcessorLivenessEntry.Create
// Called at each Loop A/B backoff iteration AND after Gate A, only when context.Id is non-null (D-02).
private async Task WriteUnhealthyAsync(/* writer, instanceId, opts.StartupIntervalSeconds */)
{
    if (context.Id is not { } procId) return; // D-02: no processorId before Loop A resolves identity

    static string Outcome(Guid? id, string? def) =>
        id is null ? SchemaOutcome.Success            // null-is-skip
                   : def is null ? SchemaOutcome.Fail // not-yet-resolved
                                 : SchemaOutcome.Success;

    var now = clock.GetUtcNow().UtcDateTime;          // SAME clock the reader uses
    var entry = ProcessorLivenessEntry.Create(
        inputOutcome:  Outcome(context.InputSchemaId,  context.InputDefinition),
        outputOutcome: Outcome(context.OutputSchemaId, context.OutputDefinition),
        configOutcome: /* Gate A outcome — Open Q1 */ Outcome(context.ConfigSchemaId, context.ConfigDefinition),
        timestamp: now,
        interval:  opts.StartupIntervalSeconds);      // D-12: startup anchor = BackoffCap (30s)
    // entry.Status will be Unhealthy until ALL non-null schemas resolve (Create's invariant).

    await writer.WriteAsync(procId, instanceId, entry); // shared writer: L2 SET + SADD + L1 Update + log-and-continue
}
```

### Pattern 2: Lock-free L1 holder (D-08/09/10, L1-01)

**What:** A new dedicated singleton storing `volatile ProcessorLivenessEntry?`. `Update(entry)` does a plain reference assignment (atomic for reference types) to the `volatile` field; `Current` reads it. No lock — mirrors `ProcessorContext.IsHealthy`'s `Volatile.Read`/`Interlocked` discipline.

**Example:**
```csharp
// Source: pattern mirrors ProcessorContext.cs (read this session). Names are Claude's discretion.
namespace BaseProcessor.Core.Liveness;

public interface IProcessorLivenessState
{
    void Update(ProcessorLivenessEntry entry);
    ProcessorLivenessEntry? Current { get; }
}

public sealed class ProcessorLivenessState : IProcessorLivenessState
{
    // volatile reference: atomic assignment + safe publication across the startup-thread / heartbeat-thread
    // writers and the Phase-61 probe-thread reader (D-10). Reference-type assignment is atomic in the CLR.
    private volatile ProcessorLivenessEntry? _current;

    public void Update(ProcessorLivenessEntry entry) => _current = entry; // swap the immutable reference
    public ProcessorLivenessEntry? Current => _current;                   // snapshot read for the probe
}
```
DI: `services.AddSingleton<IProcessorLivenessState, ProcessorLivenessState>();` alongside line 136 (the `IProcessorContext` registration). [VERIFIED: registration site]

### Pattern 3: Interval split + TTL derivation (D-11/12/13, LOOP-03/04)

**What:** `Interval` stays = heartbeat (10s). Add `StartupInterval` (default 30, = BackoffCap). Each entry records its active interval (heartbeat entries → `Interval`; startup entries → `StartupInterval`). The written TTL = `max(activeInterval × 2, Ttl)`.

```csharp
// Source: derived from ProcessorLivenessOptions.cs (read this session)
[ConfigurationKeyName("StartupInterval")]
public int StartupIntervalSeconds { get; set; } = 30;   // D-12: anchor to BackoffCap's 30s default

// TTL derivation (whole seconds — Open Q2 on rounding; activeInterval is already an int seconds value):
var ttlSeconds = Math.Max(entry.Interval * 2, opts.TtlSeconds);
await db.StringSetAsync(key, json, expiry: TimeSpan.FromSeconds(ttlSeconds));
```
Note `entry.Interval` IS the active interval already baked into the value by `Create(...)`, so the writer derives TTL from the entry it just built — no separate "which loop am I" branch needed in the shared writer. [VERIFIED: `ProcessorLivenessEntry.Interval` is the recorded interval]

### Anti-Patterns to Avoid
- **Reading `context` definition props from the heartbeat (or any non-orchestrator thread) before Healthy:** WR-03 — they are plain auto-properties with no barrier; may return stale nulls. The heartbeat only writes when `IsHealthy` (full barrier already published the writes). The `unhealthy` summary is built ONLY inside the orchestrator (D-01). [VERIFIED: IProcessorContext WR-03 doc]
- **A literal status/key string:** always `LivenessStatus.*` / `SchemaOutcome.*` / `L2ProjectionKeys.*` consts (SoT can't-desync discipline). [VERIFIED]
- **Threading `stoppingToken` into `StringSetAsync`:** the heartbeat deliberately does NOT (no CT overload; command timeout is the bound). Mirror this in the startup write. [VERIFIED: heartbeat comment lines 91-96]
- **A `lock{}` or `Interlocked.Exchange` on the L1 ref:** D-10 LOCKS plain `volatile` reference assignment. Don't over-synchronize.
- **Letting a Redis fault on the startup write crash the host or abort resolution:** log-and-continue, identical to the heartbeat catch. [VERIFIED: heartbeat lines 102-110]
- **Forgetting the 3 hermetic `new ProcessorStartupOrchestrator(...)` call sites + DI when the ctor grows:** see Common Pitfalls.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Per-replica instance id | A new env-precedence resolver | `InstanceId.Resolve()` (Phase 59 SoT) | KEY-03 "reused, no new mechanism"; one SoT [VERIFIED] |
| Per-instance / index key strings | String interpolation in the writer | `L2ProjectionKeys.PerInstance(id, instId)` / `.InstanceIndex(id)` | SoT can't-desync; golden-pinned in Phase 59 [VERIFIED] |
| Status derivation from summary | `if (anyFail) status = "unhealthy"` in the writer | `ProcessorLivenessEntry.Create(...)` | Single STATE-01/02 invariant point; writer "CANNOT produce a status that contradicts the summary" [VERIFIED: ProcessorLivenessEntry doc] |
| SADD / parent-index discipline | A "have I added yet?" flag + custom SET op | `db.SetAddAsync(indexKey, instanceId)` (idempotent) | Set semantics make double-add harmless (D-15); precedent in `RedisProjectionWriter.cs:84` [VERIFIED] |
| Whole-value last-write-wins L2 write | Read-modify-write / WATCH/MULTI | blind `StringSetAsync(key, json, expiry)` | Heartbeat already does exactly this (LIVE-06) [VERIFIED] |
| Cross-thread snapshot publication | A `ConcurrentDictionary` / `lock` | `volatile` reference swap (D-10) | Atomic ref assignment; mirrors `IsHealthy` [VERIFIED: ProcessorContext.cs] |

**Key insight:** Phase 59 already built every value/key/identity primitive. Phase 60 is a wiring phase — the only genuinely new construct is the lock-free L1 holder, and even that mirrors `ProcessorContext.IsHealthy`'s existing `volatile`/`Interlocked` idiom.

## Common Pitfalls

### Pitfall 1: Constructor blast radius on `ProcessorStartupOrchestrator`
**What goes wrong:** Adding Redis (`IConnectionMultiplexer`), the L1 holder, the instanceId, and/or the shared writer to the orchestrator's primary constructor breaks every direct `new ProcessorStartupOrchestrator(...)` and the DI registration.
**Why it happens:** The orchestrator is `new`'d directly in three hermetic facts, not resolved from DI.
**How to avoid:** Update ALL of:
- `tests/BaseApi.Tests/Processor/IdentityResolutionFacts.cs` (`new ProcessorStartupOrchestrator(...)` ~line 72)
- `tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs`
- `tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs`
- `BaseProcessorServiceCollectionExtensions.cs` (DI; `IConnectionMultiplexer` is already registered by `AddBaseConsole`, so it's resolvable — the orchestrator just needs to declare it).
**Warning signs:** Compile errors in the three facts; a green build that didn't touch them means the ctor didn't actually change. [VERIFIED: grep — exactly 3 call sites]

### Pitfall 2: The `[property: JsonPropertyName]` is load-bearing on the value
**What goes wrong:** Serializing `ProcessorLivenessEntry` with wrong/missing attribute targeting produces a wire shape the Phase-61 reader can't parse.
**Why it happens:** This is already correct in `ProcessorLivenessEntry` (Phase 59) — the pitfall is only if a task "helpfully" reshapes it. DON'T. Phase 60 writes the existing record as-is.
**How to avoid:** Serialize with DEFAULT `JsonSerializer` options (the pins carry the shape) exactly as the heartbeat does today. [VERIFIED]

### Pitfall 3: First-write timing vs "never absent"
**What goes wrong:** Trying to write a key before identity resolves (no `processorId`) → impossible key.
**Why it happens:** Misreading STATE-03 "never absent" as "from boot."
**How to avoid:** D-02 — earliest write is the first iteration AFTER Loop A resolves identity. The pre-identity window legitimately has no key. Guard with `if (context.Id is not { } procId) return;`. [VERIFIED: D-02]

### Pitfall 4: TTL units / the `Ttl`-as-floor semantics
**What goes wrong:** Writing TTL = `Ttl` (flat 30s) for startup entries, or `activeInterval×2` without the floor — a slow backoff (30s gap) with a 20s TTL would let the key lapse between writes → "absent" → violates STATE-03.
**Why it happens:** Missing that startup's interval (30) × 2 = 60 must beat the worst-case write gap.
**How to avoid:** `max(activeInterval × 2, Ttl)`. Heartbeat: `max(20, 30) = 30`. Startup: `max(60, 30) = 60`. [VERIFIED: D-13 arithmetic]

### Pitfall 5: Old `ProcessorProjection` write must be fully removed, not left dual-writing
**What goes wrong:** Leaving the heartbeat's old `skp:{id}` / `ProcessorProjection` write in place "for safety" → two L2 writes, breaks net-zero close-gate accounting and the D-05 hard-swap.
**Why it happens:** Reluctance to delete during a swap.
**How to avoid:** D-05 — Phase 60 stops writing the old key/value ENTIRELY. The TYPES stay (reader compiles against them, deleted in 61); the WRITE is removed. Update `LivenessHeartbeatFacts` accordingly (it currently asserts `L2ProjectionKeys.Processor` + `ProcessorProjection`). [VERIFIED: heartbeat + facts read]

### Pitfall 6: Heartbeat now needs the instanceId + L1 holder + writer too
**What goes wrong:** Swapping only the key/value in the heartbeat but forgetting the L1 `Update` (D-15) and the SADD-on-first-write means LOOP-02/L1-01 are half-done.
**How to avoid:** Route the heartbeat through the SAME shared writer (recommended). Then L1 update + SADD + TTL come for free and provably match the startup path. The heartbeat's ctor also grows (instanceId + L1 holder or the writer) — but it's resolved from DI (`AddHostedService`), so only `LivenessHeartbeatFacts` (which `new`s it) needs updating, not a DI signature. [VERIFIED: LivenessHeartbeatFacts news it directly]

## Code Examples

### Heartbeat swap (D-14, LOOP-02 — the post-Healthy `healthy` write)
```csharp
// Source: ProcessorLivenessHeartbeat.cs (read this session) — the swap inside the IsHealthy gate.
// BEFORE (to be removed): ProcessorProjection + L2ProjectionKeys.Processor(id) + flat Ttl.
// AFTER:
if (_context.IsHealthy && _context.Id is { } id)
{
    var now = _clock.GetUtcNow().UtcDateTime;
    // Frozen healthy: all outcomes SUCCESS (Create derives Healthy). Active interval = heartbeat Interval.
    var entry = ProcessorLivenessEntry.Create(
        inputOutcome: SchemaOutcome.Success, outputOutcome: SchemaOutcome.Success,
        configOutcome: SchemaOutcome.Success, timestamp: now, interval: opts.IntervalSeconds);
    await _writer.WriteAsync(id, _instanceId, entry); // shared writer: SET(perInstance, ttl) + SADD + L1 Update
}
```
> Note: passing all-`SUCCESS` to `Create` yields `Healthy` (frozen). The heartbeat does NOT re-read `context` definition props for the summary — frozen-healthy means a fixed all-SUCCESS summary (no mid-life re-validation, D-14). [VERIFIED: Create invariant]

### Shared internal writer (recommended — single L2+SADD+L1 path)
```csharp
// Source: composed from ProcessorLivenessHeartbeat write disciplines + RedisProjectionWriter SADD precedent.
internal sealed class ProcessorLivenessWriter // name = discretion
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IProcessorLivenessState _l1;
    private readonly ProcessorLivenessOptions _opts;
    private readonly ILogger _logger;
    // ctor injects the above (all DI-registered) ...

    public async Task WriteAsync(Guid processorId, string instanceId, ProcessorLivenessEntry entry)
    {
        // L1 first or last is fine; do it OUTSIDE the try if it must never be skipped on a Redis fault,
        // OR inside if L1 should mirror L2 success. RECOMMEND: update L1 even on Redis fault (the probe
        // is the watchdog — it should see the latest in-process truth). Decide per Open Q3.
        _l1.Update(entry);
        try
        {
            var db = _redis.GetDatabase();
            var json = JsonSerializer.Serialize(entry);
            var ttl = Math.Max(entry.Interval * 2, _opts.TtlSeconds); // D-13
            await db.StringSetAsync(L2ProjectionKeys.PerInstance(processorId, instanceId),
                                    json, expiry: TimeSpan.FromSeconds(ttl)); // blind whole-value LWW
            await db.SetAddAsync(L2ProjectionKeys.InstanceIndex(processorId), instanceId); // idempotent SADD (D-15)
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Liveness write failed for processor {ProcessorId}; will retry next iteration", processorId);
        }
    }
}
```

### Config binding test (mirror `ProcessorOptionsBindingFacts`)
```csharp
// Source: tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs (read this session) — add StartupInterval.
[Fact]
public void Binds_StartupInterval_From_Processor_Section()
{
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["Processor:StartupInterval"] = "30" }).Build();
    var opts = cfg.GetSection("Processor").Get<ProcessorLivenessOptions>();
    Assert.Equal(30, opts!.StartupIntervalSeconds);
}
[Fact]
public void Empty_Config_Yields_StartupInterval_Default_30()
{
    var opts = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>())
        .Build().GetSection("Processor").Get<ProcessorLivenessOptions>() ?? new ProcessorLivenessOptions();
    Assert.Equal(30, opts.StartupIntervalSeconds);
}
```
> Also extend the existing `Binds_Five_Independent_Seconds_Knobs...` fact (it becomes six knobs) and the `Empty_Config_Yields_Baked_Defaults` fact. [VERIFIED: existing fact shape]

### SADD precedent (already in repo)
```csharp
// Source: src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs:84
tasks.Add(batch.SetAddAsync(RedisProjectionKeys.ParentIndex(), wf.Id.ToString("D")));
// Phase 60 analog: db.SetAddAsync(L2ProjectionKeys.InstanceIndex(processorId), instanceId);
// instanceId is already a string (InstanceId.Resolve()) — no ToString needed.
```

## State of the Art

| Old Approach (pre-Phase-60) | Current Approach (Phase 60) | Impact |
|--------------|------------------|--------|
| Only a Healthy replica writes; `skp:{id}` single key; `ProcessorProjection` value | Both loops write; per-instance key `skp:proc:{id}:{instId}`; `ProcessorLivenessEntry` value; startup writes `unhealthy` | A restarting replica is visible as `unhealthy`, never absent (STATE-03) |
| Flat `Ttl` (30s) on every write | `max(activeInterval×2, Ttl)` per-entry | Slow-backoff startup key never lapses between writes |
| Single `Interval` knob | `Interval` (heartbeat 10s) + `StartupInterval` (30s) | Downstream staleness math adapts to which loop wrote it |
| No in-memory liveness record | `volatile ProcessorLivenessEntry?` holder updated by both loops | Phase-61 self-watchdog probe has a single in-process source |

**Deprecated/outdated (this phase):** the heartbeat's `ProcessorProjection` + `L2ProjectionKeys.Processor(id)` WRITE is removed (the TYPES survive until Phase 61). [VERIFIED: D-05]

## Runtime State Inventory

> This is a writer-swap phase touching the LIVE Redis keyspace. Categories below answered explicitly.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | The OLD `skp:{processorId}` flat key (written today by the heartbeat). After Phase 60 nobody writes it; existing keys TTL-expire (30s) on their own. The NEW `skp:proc:{id}:{instId}` keys + `skp:proc:{id}` index SETs are written fresh. | Code change only — no data migration. Old keys self-expire; no manual purge needed (and the close-gate net-zero sweep is a Phase-62 concern). |
| Live service config | None — no external service (n8n/Datadog/Tailscale/etc.) holds this string. Liveness keys are written by the processor itself at runtime. | None — verified by absence of any such integration in the read files. |
| OS-registered state | None — no Task Scheduler / pm2 / systemd registration embeds liveness keys. | None — verified. |
| Secrets/env vars | `POD_NAME`/`HOSTNAME` are READ by `InstanceId.Resolve()` (unchanged contract); no secret key renamed. | None — read-only consumption of existing env precedence. |
| Build artifacts | None — no package rename; this is in-place edits to existing `BaseProcessor.Core` files + new files in the same assembly. | None — normal rebuild. |

**Hermetic-suite keyspace note:** `RedisFixture` tracks specific keys for net-zero teardown (no `skp:*` wildcard SCAN). Any new fact that writes a real per-instance key / index SET MUST `_redis.Track(...)` both the `PerInstance` key AND `SREM`/track the `InstanceIndex` SET member (the index SET is the one shared contention point — mirror the Phase-22 parent-index `SREM`-your-own-id discipline, run in a non-parallel collection if it touches a shared SET). [VERIFIED: RedisFixture + ParentIndex collection discipline]

## Validation Architecture

> Nyquist is ENABLED (`workflow.nyquist_validation: true` in config.json). [VERIFIED: config.json]

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit v3 (`TestContext.Current.CancellationToken`, `IClassFixture<T>`) [VERIFIED: existing facts] |
| Config file | none custom — standard `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "Phase=60"` (after tagging new classes `[Trait("Phase","60")]`) |
| Full suite command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |

Two test families exist and both are reused:
- **Pure-hermetic (no stack):** options binding (`ProcessorOptionsBindingFacts`), orchestrator-driven facts using the in-memory MassTransit harness + `FakeTimeProvider` + NSubstitute stubs (`IdentityResolutionFacts`, `SchemaResolutionFacts`, `DispatchBindSequenceFacts`).
- **Real-Redis-on-localhost:6380 (`RedisFixture`):** liveness write-behavior facts (`LivenessHeartbeatFacts`) with `FakeTimeProvider` driving the beat loop and net-zero key tracking.

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command (filter) | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| LOOP-03 | `StartupInterval` binds (default 30) | unit (binding) | `dotnet test --filter "FullyQualifiedName~ProcessorOptionsBindingFacts"` | ✅ extend existing |
| L1-01 | L1 holder `Update`/`Current` publishes the last entry; both-loops-update | unit | `--filter "FullyQualifiedName~ProcessorLivenessStateFacts"` | ❌ Wave 0 (NEW) |
| STATE-03 / LOOP-01 | Startup loop writes `unhealthy` to L2+L1 from first post-identity iteration, summary reflects progress, never absent | integration (Redis + harness or stubbed Redis) | `--filter "FullyQualifiedName~StartupUnhealthyWriteFacts"` | ❌ Wave 0 (NEW) |
| LOOP-04 | First write SADDs instanceId to index; per-instance key has a TTL | integration (RedisFixture) | `--filter "FullyQualifiedName~ProcessorLivenessWriterFacts"` | ❌ Wave 0 (NEW) |
| LOOP-02 / LOOP-04 | Healthy heartbeat refreshes per-instance key (frozen healthy) + L1, TTL = max(20,30)=30 | integration (RedisFixture) | `--filter "FullyQualifiedName~LivenessHeartbeatFacts"` | ✅ MODIFY existing (swap assertions to PerInstance/ProcessorLivenessEntry) |
| LOOP-03 / LOOP-04 | TTL = max(activeInterval×2, Ttl): startup→60, heartbeat→30; recorded interval matches the loop | unit (TTL math) + integration (`KeyTimeToLiveAsync`) | `--filter "FullyQualifiedName~ProcessorLivenessWriterFacts"` | ❌ Wave 0 (NEW) |
| LOOP-01 (resilience) | Redis fault on startup write logs-and-continues; resolution still reaches Healthy | integration | covered in `StartupUnhealthyWriteFacts` (point at a dead Redis port) | ❌ Wave 0 (NEW) |

### Observable behaviors (Nyquist sampling — what a VALIDATION.md asserts)
1. **Per-instance key present + `status=Unhealthy`** during the post-identity resolution window (GET `PerInstance(id, instId)` → deserialize `ProcessorLivenessEntry`, assert `Status == LivenessStatus.Unhealthy`). [STATE-03/LOOP-01]
2. **Summary reflects progress:** with one schema unresolved, that summary field = `FAIL`; once resolved, `SUCCESS`. [LOOP-01/D-04]
3. **Index SADD:** `SMEMBERS InstanceIndex(id)` contains `instanceId` after the first write; idempotent across both loops (count stays 1). [LOOP-04/D-15]
4. **TTL banding:** `KeyTimeToLiveAsync(PerInstance) ∈ (ttl-grace, ttl]` where ttl=30 (heartbeat) / 60 (startup). [LOOP-04/D-13]
5. **Recorded interval:** `entry.Interval == StartupIntervalSeconds` (startup) / `== IntervalSeconds` (heartbeat). [LOOP-03/D-12]
6. **Frozen healthy:** post-Healthy beats write `status=Healthy` with all-SUCCESS summary; timestamp advances on each beat (monotonic via `FakeTimeProvider.Advance`). [LOOP-02/D-14]
7. **L1 == L2:** the `ProcessorLivenessEntry` in the L1 holder equals the value just SET to L2 (same instance/value). [L1-01/D-09]
8. **L1 publication across threads:** `IProcessorLivenessState.Current` returns the last `Update`d entry (volatile read). [L1-01/D-10]
9. **Old key NOT written:** no `skp:{id}` (flat `Processor` key) is created by either loop after the swap. [D-05]
10. **Resilience:** a startup/heartbeat write against a dead Redis logs-and-continues; the host does not crash and resolution still reaches Healthy. [Discretion/heartbeat-precedent]

### Sampling Rate
- **Per task commit:** `dotnet test --filter "Phase=60"` (the new + modified facts; Redis facts need localhost:6380 compose up).
- **Per wave merge:** full `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj`.
- **Phase gate:** full suite green + 0-warning Release AND Debug (`-warnaserror`) before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Processor/ProcessorLivenessStateFacts.cs` — L1 holder Update/Current (L1-01) — NEW, pure-hermetic.
- [ ] `tests/BaseApi.Tests/Processor/ProcessorLivenessWriterFacts.cs` — shared writer: PerInstance SET + TTL banding + index SADD idempotency (LOOP-03/04) — NEW, `RedisFixture` (track both keys; SREM/track the index SET member).
- [ ] `tests/BaseApi.Tests/Processor/StartupUnhealthyWriteFacts.cs` — orchestrator inline `unhealthy` write + summary-progress + resilience (STATE-03/LOOP-01) — NEW, harness + Redis (or stubbed `IConnectionMultiplexer`).
- [ ] MODIFY `tests/BaseApi.Tests/Processor/LivenessHeartbeatFacts.cs` — swap assertions to `PerInstance` key + `ProcessorLivenessEntry` (frozen Healthy) + L1 update + index SADD.
- [ ] MODIFY `tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs` — add `StartupInterval` (six knobs).
- [ ] MODIFY the 3 `new ProcessorStartupOrchestrator(...)` facts for the grown ctor (`IdentityResolutionFacts`, `SchemaResolutionFacts`, `DispatchBindSequenceFacts`).
- [ ] MODIFY `tests/BaseApi.Tests/Processor/AddBaseProcessorFacts.cs` — assert the new L1 holder (+ writer) registration descriptors.
- [ ] `FakeProcessorContext` already sufficient for heartbeat facts (no change needed unless the writer reads more from context).

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | build + test | ✓ (project is net8.0) | net8.0 | — |
| Redis on localhost:6380 | `RedisFixture`-based facts (heartbeat/writer) | runtime (compose) | docker compose | Stub `IConnectionMultiplexer` (NSubstitute) for pure-hermetic write-shape assertions, as some facts already do for descriptor tests |
| RabbitMQ (in-memory harness) | orchestrator-driven facts | ✓ (MassTransit in-memory test harness — no broker) | in-process | — |

**Missing dependencies with no fallback:** none.
**Missing dependencies with fallback:** the real Redis facts need the compose stack up (`localhost:6380`); if unavailable, the writer's shape can still be asserted against a stubbed multiplexer (the L1-holder and binding facts need no Redis at all).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Pre-Gate-A `configOutcome` should be `Fail` when `ConfigSchemaId` is non-null and unresolved, `Success` when null — i.e. config is treated like input/output during the resolution window, and only "finalized" by Gate A after Loop B | Pattern 1 / Open Q1 | LOW — affects only the transient `unhealthy` summary's `configSchema` field during startup; status is `Unhealthy` either way (anyFail). Confirm the exact convention with the planner. |
| A2 | TTL is computed in whole seconds (`activeInterval` is already an int-seconds value; `entry.Interval` is int) so `max(interval*2, Ttl)` needs no fractional rounding | Pattern 3 / Open Q2 | LOW — `ProcessorLivenessEntry.Interval` and all options are int seconds [VERIFIED: types]; no rounding decision actually arises. |
| A3 | The L1 holder should be `Update`d even when the L2 write faults (the in-process watchdog should reflect latest truth) | Code Examples / Open Q3 | MEDIUM — affects what the Phase-61 probe sees on a Redis outage; a reasonable alternative is L1-mirrors-L2-success. Planner/discuss should confirm. |
| A4 | The shared internal writer is preferred over two private helpers | multiple | LOW — CONTEXT explicitly leaves this to discretion ("encouraged but not mandated"); either is acceptable. |

## Open Questions (RESOLVED)

> All three resolved during planning (2026-06-13); resolutions encoded in the plans as noted.

1. **Pre-Gate-A `configSchema` outcome convention (A1).** — **RESOLVED** (60-04-PLAN.md objective + `WriteUnhealthyAsync` action)
   - What we know: `configSchema` = Gate A outcome (D-04), but Gate A runs after Loop B; `Create` treats null-config as Success. During the resolution window before Gate A, `ConfigDefinition` may be null (unresolved) or resolved-but-not-yet-Gate-A-checked.
   - What's unclear: whether a non-null-but-unresolved config id should surface as `FAIL` (treated like input/output) or be deferred.
   - **Resolution:** treat config like input/output during startup (`Fail` if non-null & unresolved, `Success` if null), and after Gate A use the actual Gate A outcome. Status is `Unhealthy` throughout startup regardless, so this only affects the diagnostic field.

2. **L1 update ordering on Redis fault (A3).** — **RESOLVED** (60-02-PLAN.md objective + `key_links`)
   - What we know: the probe (Phase 61) reads L1 to detect a silently-crashed loop.
   - What's unclear: should L1 reflect the latest *attempted* write (update L1 unconditionally) or the latest *successful* L2 write (update L1 only after SET succeeds)?
   - **Resolution:** update L1 **unconditionally** (before the L2 try block) — the watchdog wants in-process liveness, not Redis reachability.

3. **Heartbeat ctor growth vs shared writer injection.** — **RESOLVED** (60-02/03/04 — single injected `ProcessorLivenessWriter`)
   - What we know: routing both loops through one injected writer is cleanest (Pitfall 6).
   - **Resolution:** inject the shared `ProcessorLivenessWriter` + instanceId into both `BackgroundService`s; the writer owns Redis + L1 holder + options. Minimizes per-loop ctor churn and guarantees identical disciplines.

## Sources

### Primary (HIGH confidence — read this session)
- `60-CONTEXT.md` — 15 locked decisions (binding spec)
- `src/Messaging.Contracts/Projections/ProcessorLivenessEntry.cs`, `L2ProjectionKeys.cs`, `LivenessStatus.cs`, `SchemaOutcome.cs`, `ProcessorProjection.cs`
- `src/Messaging.Contracts/Identity/InstanceId.cs`
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs`
- `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs`
- `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs`
- `src/BaseProcessor.Core/Identity/IProcessorContext.cs` + `ProcessorContext.cs`
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs`
- `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` (reader — DO NOT touch; shows the contract)
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs` (SADD precedent)
- `tests/BaseApi.Tests/Processor/{ProcessorOptionsBindingFacts,LivenessHeartbeatFacts,IdentityResolutionFacts,SchemaResolutionFacts,AddBaseProcessorFacts,FakeProcessorContext,ProcessorTestHarness}.cs`
- `tests/BaseApi.Tests/Composition/RedisFixture.cs`
- `.planning/REQUIREMENTS.md`, `.planning/ROADMAP.md` (Phase 60 section), `.planning/config.json`
- `.planning/phases/59-.../59-PATTERNS.md`

### Secondary / Tertiary
- None — no external lookup was needed; the phase is entirely internal to the repo and the Phase-59 contract.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new packages; all primitives read in source.
- Architecture / writer topology: HIGH — derived directly from CONTEXT decisions + the existing heartbeat/orchestrator code.
- Pitfalls: HIGH — the 3 ctor call sites and the existing test assertions were grep'd/read directly.
- Open questions (A1/A3): MEDIUM — diagnostic-field convention + L1-on-fault ordering are genuine small judgment calls, flagged for the planner/discuss.

**Research date:** 2026-06-13
**Valid until:** 2026-07-13 (stable — internal contract, no fast-moving external deps)

## RESEARCH COMPLETE
