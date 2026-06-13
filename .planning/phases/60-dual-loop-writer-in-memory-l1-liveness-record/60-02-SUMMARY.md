---
phase: 60-dual-loop-writer-in-memory-l1-liveness-record
plan: 02
subsystem: infra
tags: [liveness, processor, redis, ttl, sadd, l1, dotnet, stackexchange-redis]

# Dependency graph
requires:
  - phase: 60-01
    provides: "IProcessorLivenessState (L1 holder) + ProcessorLivenessOptions.{StartupInterval,Interval,Ttl} knobs"
  - phase: 59-per-instance-l2-keyspace-two-state-liveness-value
    provides: "ProcessorLivenessEntry.Create + L2ProjectionKeys.{PerInstance,InstanceIndex} + LivenessStatus/SchemaOutcome consts"
provides:
  - "ProcessorLivenessWriter: single shared write path (L2 SET(perInstance, derived TTL) + idempotent index SADD + unconditional L1 Update + log-and-continue) both loops call"
  - "Derived TTL discipline: max(entry.Interval*2, Ttl-floor) — startup(30)->60s, heartbeat(10)->30s"
affects: [60-03-startup-loop, 60-04-heartbeat-loop, 61-gate-and-self-watchdog-probe]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Single shared internal writer collaborator both background loops call so L2-SET + index-SADD + L1-Update + TTL disciplines cannot drift"
    - "Caller-baked active interval (entry.Interval) drives TTL math — writer never branches on which loop"
    - "Per-test unique processorId makes the per-instance key AND its index SET key unique → Track-the-whole-key net-zero without shared-SET non-parallel collection"

key-files:
  created:
    - src/BaseProcessor.Core/Liveness/ProcessorLivenessWriter.cs
    - tests/BaseApi.Tests/Processor/ProcessorLivenessWriterFacts.cs
  modified: []

key-decisions:
  - "L1 holder Updated UNCONDITIONALLY before/independent of the Redis attempt (Open Q3 RESOLVED) — the watchdog wants latest in-process truth, not Redis reachability"
  - "ProcessorLivenessWriter is public sealed (not internal) so AddSingleton<ProcessorLivenessWriter>() + AddBaseProcessorFacts descriptor assert work cross-assembly without InternalsVisibleTo (mirrors ProcessorContext / ProcessorLivenessState)"
  - "Net-zero teardown tracks both the per-instance key and the index SET key; deleting the whole (per-test-unique) index SET key removes the SADD'd member — no SREM needed and no shared non-parallel collection required"

patterns-established:
  - "Shared single-write-path collaborator unifying L2 SET + index SADD + L1 Update + derived-TTL + log-and-continue resilience"

requirements-completed: [LOOP-03, LOOP-04]

# Metrics
duration: 3min
completed: 2026-06-13
---

# Phase 60 Plan 02: Shared Dual-Loop Liveness Writer Summary

**Built `ProcessorLivenessWriter` — the single shared write path both the startup and heartbeat loops call: L2 SET(per-instance key, TTL=max(entry.Interval*2, Ttl-floor)) + idempotent index SADD + unconditional L1 Update + Redis-fault log-and-continue, with a 5-fact RedisFixture suite proving TTL banding, SADD idempotency, L1==L2, and resilience.**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-06-13T11:17:33Z
- **Completed:** 2026-06-13T11:20:35Z
- **Tasks:** 2
- **Files created:** 2

## Accomplishments
- New `ProcessorLivenessWriter` (`public sealed`): `WriteAsync(processorId, instanceId, entry)` does L1.Update (unconditional, D-09/Open-Q3) → L2 `StringSetAsync(PerInstance, json, expiry=max(Interval*2, TtlSeconds))` → idempotent `SetAddAsync(InstanceIndex, instanceId)` → `catch → LogWarning → continue` (D-11/T-26-10).
- TTL math derives from `entry.Interval` (caller-baked active interval): startup entry (interval 30) → 60s, heartbeat entry (interval 10) → 30s (Ttl-floor wins). Writer never branches on "which loop".
- New `ProcessorLivenessWriterFacts` (5 facts, `[Trait("Phase","60")]`, `IClassFixture<RedisFixture>`): startup→Unhealthy+TTL(55,60], heartbeat→Healthy+TTL(25,30], SADD idempotency (count stays 1), L1==L2 `Assert.Same` (D-09), dead-Redis (NSubstitute stub) log-and-continue still Updates L1.
- All 12 Phase=60 trait facts green (7 from Plan 01 + 5 new); Release AND Debug builds 0-warning under `-warnaserror`.

## Task Commits

Each task was committed atomically:

1. **Task 1: Create ProcessorLivenessWriter** - `70d0ff0` (feat)
2. **Task 2: ProcessorLivenessWriterFacts** - `9d8cdb8` (test)

_Note: Task 1 (`tdd="true"`) is the implementation; its behaviors are asserted by Task 2's RedisFixture facts per the plan's explicit task split. No separate RED commit — the plan orders writer-then-facts._

## Files Created/Modified
- `src/BaseProcessor.Core/Liveness/ProcessorLivenessWriter.cs` - Shared single write path: L2 SET(perInstance, derived TTL) + idempotent index SADD + unconditional L1 Update + log-and-continue. Keys via `L2ProjectionKeys`, status via `Create`/`LivenessStatus` consts — never literals.
- `tests/BaseApi.Tests/Processor/ProcessorLivenessWriterFacts.cs` - RedisFixture facts: TTL banding (startup 60 / heartbeat 30), SADD idempotency (D-15), L1==L2 (D-09), dead-Redis resilience (D-11); net-zero Track of per-instance key + index SET key.

## Decisions Made
None beyond the plan's locked decisions (D-09/D-11/D-13/D-15 + Open Q3 resolved to unconditional L1 update). The plan supplied the writer body verbatim; the test fact names and the index-SET net-zero approach (Track the whole per-test-unique index key rather than SREM the member) were the executor's discretion within the plan's net-zero requirement.

## Deviations from Plan

None - plan executed exactly as written. (No Rule 1-4 deviations; the writer compiled 0-warning on the first build and all 5 facts passed on the first run against the live localhost:6380 Redis.)

## Issues Encountered

**Tooling — test runner invocation (no code impact, carried from Plan 01).** The repo's `tests/BaseApi.Tests` is xUnit v3 on Microsoft.Testing.Platform (MTP), not VSTest. The plan's `<verify>` command uses the VSTest `--filter "FullyQualifiedName~..."` form that the MTP runner rejects. Ran the same facts via the MTP-native flags: `dotnet test ... -- --filter-class "*ProcessorLivenessWriterFacts"` (5/5 passed) and `-- --filter-trait "Phase=60"` (12/12 passed). No production-code consequence.

## User Setup Required
None - no external service configuration required (uses the existing compose Redis at localhost:6380 for integration facts).

## Verification
- `dotnet test ... -- --filter-class "*ProcessorLivenessWriterFacts"` → Passed! Failed: 0, Passed: 5, Total: 5.
- `dotnet test ... -- --filter-trait "Phase=60"` → Passed! Failed: 0, Passed: 12, Total: 12.
- `dotnet build src/BaseProcessor.Core -c Release -warnaserror` → Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet build src/BaseProcessor.Core -c Debug -warnaserror` → Build succeeded, 0 Warning(s).
- `dotnet build tests/BaseApi.Tests -warnaserror` → Build succeeded, 0 Warning(s).
- Acceptance greps — Task 1: `Math.Max(entry.Interval * 2, _options.TtlSeconds)`==1, `L2ProjectionKeys.PerInstance`==1, `SetAddAsync(L2ProjectionKeys.InstanceIndex`==1, `_l1.Update(entry)`==1, `catch (Exception`==1. Task 2: `Assert.InRange`==2, `SetLengthAsync|SetMembersAsync`==2, `Assert.Same`==2, `Track`==8.

## Next Phase Readiness
- Plan 03 (startup loop) and Plan 04 (heartbeat loop) can inject `ProcessorLivenessWriter` and call `WriteAsync(id, instanceId, entry)` — the L2 SET + index SADD + L1 Update + derived TTL + resilience all come for free, so the two loops cannot diverge.
- DI registration of `ProcessorLivenessWriter` and `IProcessorLivenessState` is deferred to Plan 04 (per plan; not touched here).
- No blockers.

## Known Stubs
None - the writer is fully implemented; the only stub is the NSubstitute dead-`IConnectionMultiplexer` used INSIDE the resilience fact (intentional test double, not production code).

## Threat Flags
None - no new security surface beyond the plan's `<threat_model>` (T-60-04/05/06/07). Keys are built only via `L2ProjectionKeys` SoT builders, `instanceId` is the caller-supplied resolved string, the entry carries no secrets, and the Redis fault is contained by log-and-continue.

## Self-Check: PASSED

Both created files exist on disk; both task commits (`70d0ff0`, `9d8cdb8`) present in git history.

---
*Phase: 60-dual-loop-writer-in-memory-l1-liveness-record*
*Completed: 2026-06-13*
