---
phase: 60-dual-loop-writer-in-memory-l1-liveness-record
verified: 2026-06-13T13:30:00Z
status: passed
score: 5/5 must-haves verified
overrides_applied: 0
---

# Phase 60: Dual-Loop Writer + In-Memory L1 Liveness Record — Verification Report

**Phase Goal:** The processor's startup loop and heartbeat loop BOTH write the per-instance liveness entry to L2 AND update an in-memory L1 liveness record on every iteration, and the startup loop writes its entry as `unhealthy` from its first iteration. Each replica `SADD`s its own `instanceId` to the instance-index SET on its first liveness write; each per-instance key carries a TTL. Liveness intervals split into `startup_interval` and `heartbeat_interval`, each entry records its active interval, and health is frozen `healthy` once the heartbeat loop starts.
**Verified:** 2026-06-13T13:30:00Z
**Status:** PASSED
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Startup loop writes replica's liveness entry to BOTH L2 (per-instance key) and in-memory L1 on every iteration, with status=unhealthy and a summary reflecting current schema-resolution progress; restarting replica observable in L2 as unhealthy, never absent | VERIFIED | `ProcessorStartupOrchestrator.WriteUnhealthyAsync()` called at Loop A post-identity, each Loop-B iteration, and Gate-A clash path. `ProcessorLivenessWriter._l1.Update(entry)` unconditionally before the Redis attempt. `StartupUnhealthyWriteFacts.Startup_Writes_Unhealthy_PerInstance_With_Summary_Progress_Sadd_And_No_Old_Key` asserts Status=Unhealthy, Interval=30, per-schema summary (Fail/Success), and L1 mirrors L2. |
| 2 | On first liveness write each replica SADDs its own instanceId to skp:proc:{processorId}, and each per-instance key is written with a TTL | VERIFIED | `ProcessorLivenessWriter.WriteAsync` calls `db.SetAddAsync(L2ProjectionKeys.InstanceIndex(processorId), instanceId)` and `db.StringSetAsync(L2ProjectionKeys.PerInstance(...), json, expiry: TimeSpan.FromSeconds(ttl))`. TTL = `Math.Max(entry.Interval * 2, _options.TtlSeconds)`. `ProcessorLivenessWriterFacts` asserts SADD idempotency (count stays 1) and TTL banding (startup→60s, heartbeat→30s). `StartupUnhealthyWriteFacts` fact 3 asserts SMEMBERS contains instanceId. |
| 3 | On startup success the heartbeat loop starts; each heartbeat iteration refreshes the entry timestamp in BOTH L2 and L1, with health frozen healthy | VERIFIED | `ProcessorLivenessHeartbeat` writes inside `if (_context.IsHealthy && _context.Id is { } id)` gate with `ProcessorLivenessEntry.Create(Success, Success, Success, now, IntervalSeconds)` — frozen healthy, all-SUCCESS, no re-read of context definition props. `LivenessHeartbeatFacts.Healthy_Writes_FrozenHealthy_PerInstance_With_Index_And_L1` asserts Status=Healthy, Interval=10, multi-beat timestamp advance via L1 holder. |
| 4 | Liveness intervals split into startup_interval and heartbeat_interval (Ttl knob retained), and each entry records its active interval | VERIFIED | `ProcessorLivenessOptions` has `StartupIntervalSeconds = 30` (`[ConfigurationKeyName("StartupInterval")]`) alongside `IntervalSeconds = 10` and `TtlSeconds = 30` (all three unchanged). Startup entries use `options.Value.StartupIntervalSeconds` (30); heartbeat entries use `_options.IntervalSeconds` (10). Both intervals are baked into the entry via `ProcessorLivenessEntry.Create(..., interval: ...)` so the stored `Interval` field records the active interval. `ProcessorOptionsBindingFacts` verifies the six-knob binding including StartupInterval. |
| 5 | The in-memory L1 liveness record is updated by BOTH loops on every iteration and is the single source the Phase-61 self-watchdog probe reads | VERIFIED | `IProcessorLivenessState.Update(entry)` is called unconditionally in `ProcessorLivenessWriter.WriteAsync` (before the Redis try/catch) — so BOTH loops update it via the shared writer on every iteration. `ProcessorLivenessState` is registered as `AddSingleton<IProcessorLivenessState, ProcessorLivenessState>()`. `IProcessorLivenessState.Current` is exposed for Phase-61 probe reads. The L1 probe API is intentionally unimplemented in Phase 60 (Phase 61 scope — accepted). |

**Score:** 5/5 truths verified

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` | StartupIntervalSeconds property, default 30, ConfigurationKeyName("StartupInterval") | VERIFIED | Present at line 27-28. `IntervalSeconds = 10` and `TtlSeconds = 30` retained unchanged. Six-knob XML doc updated. |
| `src/BaseProcessor.Core/Liveness/IProcessorLivenessState.cs` | L1 holder contract: Update(entry) + Current snapshot | VERIFIED | `public interface IProcessorLivenessState` with `void Update(ProcessorLivenessEntry entry)` and `ProcessorLivenessEntry? Current { get; }`. 21 lines, substantive. |
| `src/BaseProcessor.Core/Liveness/ProcessorLivenessState.cs` | Lock-free volatile-ref-swap implementation | VERIFIED | `private volatile ProcessorLivenessEntry? _current`; `Update` = plain assignment; `Current` = volatile read. No lock, no Interlocked.Exchange. `public sealed`. |
| `src/BaseProcessor.Core/Liveness/ProcessorLivenessWriter.cs` | WriteAsync: L2 SET(perInstance, ttl) + index SADD + L1 Update + log-and-continue | VERIFIED | All four operations present in correct order. TTL = `Math.Max(entry.Interval * 2, _options.TtlSeconds)`. Keys via `L2ProjectionKeys` consts, never literals. `public sealed`. |
| `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` | Post-Healthy beat builds frozen-healthy entry and calls shared writer; old flat write removed | VERIFIED | `_writer.WriteAsync(id, _instanceId, entry)` called inside `IsHealthy && Id is { } id` gate. No `L2ProjectionKeys.Processor(` reference, no `ProcessorProjection`. `string instanceId` ctor param. |
| `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` | Inline WriteUnhealthyAsync at each resolution iteration; D-02 guard; interval=30 | VERIFIED | `WriteUnhealthyAsync` defined at line 283; called at lines 125 (Loop A), 163 (Loop B), 211 (Gate-A clash with configOutcomeOverride=Fail). `if (context.Id is not { } procId) return` D-02 guard at line 285. `options.Value.StartupIntervalSeconds` used at line 298. |
| `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` | AddSingleton<IProcessorLivenessState, ProcessorLivenessState> + AddSingleton<ProcessorLivenessWriter>; instanceId resolved once for both loops | VERIFIED | Lines 142 and 149. `InstanceId.Resolve()` at line 199, same value passed to both `ActivatorUtilities.CreateInstance` calls (lines 205, 213). Concrete-singleton + hosted-service pattern. |
| `tests/BaseApi.Tests/Processor/ProcessorLivenessStateFacts.cs` | L1 holder null-before-update, Assert.Same, last-write-wins with const-status | VERIFIED | Three facts tagged `[Trait("Phase","60")]`. `Assert.Null`, `Assert.Same(entry, state.Current)`, `Assert.Equal(LivenessStatus.Healthy, ...)` — const, not literal. |
| `tests/BaseApi.Tests/Processor/ProcessorLivenessWriterFacts.cs` | TTL banding, SADD idempotency, L1==L2 Assert.Same, dead-Redis resilience; net-zero | VERIFIED | 5 facts, `[Trait("Phase","60")]`, `IClassFixture<RedisFixture>`. `Assert.InRange(55,60)` and `(25,30)`. `SetMembersAsync` + `SetLengthAsync` (idempotency). `Assert.Same(entry, l1.Current)`. NSubstitute dead-Redis. `_redis.Track(...)` on both per-instance and index keys. |
| `tests/BaseApi.Tests/Processor/LivenessHeartbeatFacts.cs` | Re-pointed to PerInstance + ProcessorLivenessEntry + L1 + SADD; old flat key gone | VERIFIED | `[Trait("Phase","60")]`. `L2ProjectionKeys.PerInstance(...)` used, zero references to `L2ProjectionKeys.Processor(` or `ProcessorProjection`. `Deserialize<ProcessorLivenessEntry>`, Status=Healthy, Interval=10, TTL (25,30], index SADD, L1 mirror, multi-beat timestamp advance. |
| `tests/BaseApi.Tests/Processor/StartupUnhealthyWriteFacts.cs` | Unhealthy write + summary progress + SADD + old-key-absent + dead-Redis-still-Healthy | VERIFIED | 2 facts, `[Trait("Phase","60")]`, `IClassFixture<RedisFixture>`. Fact 1: Status=Unhealthy, Interval=30, Summary.InputSchema=Fail, SADD, old Processor key absent, L1 mirrors. Fact 5: dead Redis, resolution still reaches `context.IsHealthy == true`. Net-zero with `_redis.Track`. |
| `tests/BaseApi.Tests/Processor/AddBaseProcessorFacts.cs` | Asserts IProcessorLivenessState + ProcessorLivenessWriter singleton descriptors; both concrete hosted-service singletons | VERIFIED | `typeof(IProcessorLivenessState)` + `ProcessorLivenessState` Singleton at line 67-70. `typeof(ProcessorLivenessWriter)` Singleton at line 73-75. `typeof(ProcessorStartupOrchestrator)` Singleton and `typeof(ProcessorLivenessHeartbeat)` Singleton at lines 92-96. |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|-----|-----|--------|---------|
| `ProcessorLivenessWriter.WriteAsync` | L2 per-instance key | `db.StringSetAsync(L2ProjectionKeys.PerInstance(processorId, instanceId), json, expiry: ttl)` | WIRED | Line 73-76 of ProcessorLivenessWriter.cs. Key via `L2ProjectionKeys` const, never a literal. |
| `ProcessorLivenessWriter.WriteAsync` | instance-index SET | `db.SetAddAsync(L2ProjectionKeys.InstanceIndex(processorId), instanceId)` | WIRED | Line 80 of ProcessorLivenessWriter.cs. Idempotent SADD. |
| `ProcessorLivenessWriter.WriteAsync` | L1 holder | `_l1.Update(entry)` unconditionally before Redis try/catch | WIRED | Line 65 of ProcessorLivenessWriter.cs. L1 updated even on Redis fault. |
| `ProcessorLivenessState.Update` | `_current` volatile field | plain reference assignment `_current = entry` | WIRED | Line 17 of ProcessorLivenessState.cs. |
| `ProcessorLivenessHeartbeat (IsHealthy gate)` | shared ProcessorLivenessWriter | `_writer.WriteAsync(id, _instanceId, entry)` at line 94 | WIRED | Inside `if (_context.IsHealthy && _context.Id is { } id)`. No old flat key write. |
| `ProcessorStartupOrchestrator (each resolution iteration)` | shared ProcessorLivenessWriter | `writer.WriteAsync(procId, instanceId, entry)` via `WriteUnhealthyAsync()` at line 301 | WIRED | Called at Loop A (125), Loop B (163), Gate-A clash (211). D-02 guard at line 285. |
| `DI composition root` | L1 holder + writer singletons | `AddSingleton<IProcessorLivenessState, ProcessorLivenessState>()` + `AddSingleton<ProcessorLivenessWriter>()` | WIRED | Lines 142 and 149 of BaseProcessorServiceCollectionExtensions.cs. |
| `DI composition root` | both loops share same instanceId | `InstanceId.Resolve()` once at line 199; passed to both ActivatorUtilities calls | WIRED | Lines 205 and 213 of BaseProcessorServiceCollectionExtensions.cs. One replica identity, one per-instance key. |

---

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|--------------------|--------|
| `ProcessorLivenessWriter.WriteAsync` | `entry` (ProcessorLivenessEntry) | Caller-supplied (startup: `ProcessorLivenessEntry.Create(...)` with real context outcomes; heartbeat: all-SUCCESS frozen) | Yes — entry is built from real schema resolution state in startup, fixed frozen-healthy in heartbeat | FLOWING |
| `ProcessorLivenessState.Current` | `_current` volatile field | Written by `Update(entry)` unconditionally on every `WriteAsync` call | Yes — updated by both loops; Phase-61 probe reads this | FLOWING |
| TTL on per-instance key | `ttl = Math.Max(entry.Interval * 2, _options.TtlSeconds)` | `entry.Interval` is baked by caller (30 for startup, 10 for heartbeat); `TtlSeconds` from bound config | Yes — math produces 60 for startup, 30 for heartbeat | FLOWING |

---

### Behavioral Spot-Checks

Step 7b: SKIPPED — no standalone runnable entry point available without full compose stack. The hermetic test suite serves as the behavioral verification. Phase-61 E2E poller failures are in-scope accepted (D-06, see locked scope boundary).

---

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| STATE-03 | 60-04 | Starting/failed replica writes `unhealthy` — L2 never absent | SATISFIED | `WriteUnhealthyAsync` at Loop A first post-identity iteration; StartupUnhealthyWriteFacts fact 1 proves presence; fact 4 proves old key absent. |
| LOOP-01 | 60-04 | Startup loop writes entry (L2 + L1) on every iteration, status/summary = current resolution progress | SATISFIED | WriteUnhealthyAsync called at every Loop A/B iteration + Gate-A clash. Summary uses `Outcome(id, def)` helper. L1 updated unconditionally. |
| LOOP-02 | 60-03 | Heartbeat loop refreshes timestamp (L2 + L1); health frozen healthy once started | SATISFIED | `ProcessorLivenessHeartbeat` writes frozen-healthy entry on each beat; `LivenessHeartbeatFacts` multi-beat timestamp-advance assertion via L1. |
| LOOP-03 | 60-01 | `startup_interval` and `heartbeat_interval` split; each entry records active interval; Ttl retained | SATISFIED | `StartupIntervalSeconds=30`, `IntervalSeconds=10`, `TtlSeconds=30` on `ProcessorLivenessOptions`. Each `Create(...)` call passes the correct interval; `entry.Interval` is recorded on L2 wire. |
| LOOP-04 | 60-02, 60-03 | Per-instance key written with TTL; index SET as discovery hint; TTL is source of truth | SATISFIED | `Math.Max(entry.Interval * 2, TtlSeconds)` TTL applied on every SET. SADD to InstanceIndex on every write. `ProcessorLivenessWriterFacts` TTL banding + SADD idempotency. |
| L1-01 | 60-01, 60-02 | In-memory L1 liveness record updated by BOTH loops on every iteration; source for Phase-61 probe | SATISFIED | `IProcessorLivenessState.Update(entry)` unconditionally in `ProcessorLivenessWriter.WriteAsync` — single code path both loops call. Singleton registered for Phase-61 probe injection. `ProcessorLivenessStateFacts` + L1 assertions in writer/heartbeat/startup facts. |

All 6 requirements (STATE-03, LOOP-01, LOOP-02, LOOP-03, LOOP-04, L1-01) are SATISFIED. No orphaned requirements.

---

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| None found | — | — | — | All artifacts pass stub/anti-pattern checks. No TODO/FIXME/placeholder comments in Phase-60 source files. No empty returns or hardcoded stubs in production code paths. Test doubles (NSubstitute dead-Redis, `StubLivenessWriter`) are intentional test seams, not production stubs. |

Spot-checks run on all Phase-60 source files:
- `grep -E "TODO|FIXME|XXX|HACK|PLACEHOLDER"` in Liveness/ and Startup/ files: zero matches in Phase-60 code
- `return null / return {} / return []` checks: no hollow returns in production paths
- Hardcoded empty state: no `= []` or `= {}` in paths that flow to L2/L1 writes
- The pass/skip pre-bind write was deliberately omitted (Deviation #1 in 60-04 — correct behavior to prevent premature-Healthy EXEC-01 violation)
- `LivenessReaderRoundTripFacts` deletion is correct (D-05 severs the old loop; Phase-61 re-establishes the round-trip)

---

### Human Verification Required

None. All success criteria are verifiable programmatically. The hermetic test suite (582/582 non-E2E + 17/17 Phase=60 trait facts) is the relevant signal per the locked scope boundary. The 7 E2E failures are the accepted D-06 stale window (Phase-61 scope).

---

### Gaps Summary

No gaps. All 5 observable truths are verified, all artifacts are substantive and wired, all 6 requirements are satisfied, no blocking anti-patterns found.

**Accepted scope boundary (not gaps):**
- Phase-61 L2 reader/validator, WebAPI gate, and self-watchdog probe are intentionally absent — correct per scope.
- 7 `*E2ETests` fail because they poll the retired flat `skp:{processorId}` key (D-06 accepted stale window) — these are Phase-61 scope.
- The pass/skip pre-bind unhealthy write was intentionally omitted (Deviation #1) to prevent a premature-Healthy EXEC-01 violation — this is a correctness improvement, not a gap.

---

_Verified: 2026-06-13T13:30:00Z_
_Verifier: Claude (gsd-verifier)_
