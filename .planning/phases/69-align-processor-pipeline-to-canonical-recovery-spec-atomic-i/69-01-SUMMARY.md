---
phase: 69-align-processor-pipeline-to-canonical-recovery-spec-atomic-i
plan: 01
subsystem: infra
tags: [redis, lua, stackexchange-redis, processor-pipeline, keeper-recovery, atomicity, nsubstitute, xunit]

# Dependency graph
requires:
  - phase: 51-processor-forward-recovery-pipeline
    provides: "the A18 forward-Post 3-op shape (index HSET + index PEXPIRE + data SET) + the INFRA-01 drop / INFRA-02 inject split this plan collapses"
  - phase: 54-terminal-index-delete-atomic-keeper-gc
    provides: "the in-repo atomic-op-then-escalate precedent (DeleteTerminalAsync two-key DEL) the atomic forward write mirrors"
  - phase: 68-fault-injection-harness
    provides: "the TEST-06 index/data TTL desync guard the C#-computed ARGV TTLs preserve"
provides:
  - "Atomic forward-Post write: ONE Lua ScriptEvaluateAsync (index slot HSET + whole-hash PEXPIRE + data SET-with-PX) replacing the 3 separate ops (ATOMIC-01 / spec §4.3 step 3)"
  - "No-drop forward exhaust: the single atomic-write exhaustion routes to ONE SendKeeper(BuildInject) — the INFRA-01 silent DROP path is eliminated (NODROP-01 / spec §10 bullet 1)"
  - "AtomicForwardWrite const Lua script (compile-time, parameterized KEYS/ARGV — injection-safe)"
  - "Merged AtomicWriteFaultL2 test mux + AtomicWriteFault_Inject fact; forward/post facts inspect the single script's KEYS/ARGV"
affects: [69-02 gated forward cleanup, processor-keeper-recovery-spec alignment]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Atomic multi-key Redis write via a compile-time const Lua ScriptEvaluateAsync wrapped in RetryLoop (TTLs computed in C#, passed as ARGV ms — no RNG in Lua)"
    - "Fault mux stubs the ScriptEvaluateAsync binding to a success result FIRST, then layers a When/Do throw (no unstubbed-Task false-green)"
    - "Facts inspect the single script call's KEYS[]/ARGV[] (single-quoted Lua token ordering for HSET-before-SET) instead of separate per-op StringSet/HashSet/KeyExpire calls"

key-files:
  created: []
  modified:
    - "src/BaseProcessor.Core/Processing/ProcessorPipeline.cs - AtomicForwardWrite const + atomic forward-Post write + single-INJECT no-drop; class doc summary updated"
    - "tests/BaseApi.Tests/Processor/DispatchTestKit.cs - ForwardOkL2 stubs the script; AtomicWriteFaultL2 merges the two old forward write-fault muxes"
    - "tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs - AtomicWriteFault_Inject + reworked Completed_AllocationBeforeData + IndexTtl_* against script ARGV"
    - "tests/BaseApi.Tests/Processor/PipelinePostFacts.cs - PostCompleted_WritesWithTtl + WriteFault_Inject retargeted to the atomic script (in-scope Rule 1 fix)"

key-decisions:
  - "Index TTL (SlotTtl random[ttl,2×ttl]) and data TTL (ExecutionDataTtl) stay computed in C# and pass as ARGV ms — Lua never randomizes, preserving the Phase-68 TEST-06 desync guard"
  - "PEXPIRE is a WHOLE-HASH expire (byte-for-byte matching the former KeyExpireAsync(MessageIndex, SlotTtl())) — NOT a per-field HEXPIRE, so Redis 7.4 is not required"
  - "AtomicWriteFault_Inject subsumes both the former SlotWriteFault_Drop and DataWriteFault_Inject_WithIdSet — both index- and data-failure modes are now the single atomic-write exhaust"
  - "PipelinePostFacts (WriteFault_Inject, PostCompleted_WritesWithTtl) touch the same forward write path my Task-2 change altered → fixed in-scope (Rule 1) to keep the plan's *PipelinePost* verification gate green"

patterns-established:
  - "Atomic forward L2 write: const Lua HSET+PEXPIRE+SET-PX, one ScriptEvaluateAsync in RetryLoop, exhaust → one KeeperInject (no drop)"

requirements-completed:
  - "Spec §4.3 atomic write (ATOMIC-01)"
  - "Spec §10 INFRA-01 no-drop → single INJECT (NODROP-01)"

# Metrics
duration: 25min
completed: 2026-06-15
---

# Phase 69 Plan 01: Atomic Forward-Post Write + Single-INJECT No-Drop Summary

**The forward-Post L2 write is now ONE atomic Lua `ScriptEvaluateAsync` (index slot HSET + whole-hash PEXPIRE + data SET-with-PX); its exhaustion routes to a single `KeeperInject`, eliminating the INFRA-01 silent DROP path.**

## Performance

- **Duration:** ~25 min
- **Started:** 2026-06-15T21:22Z (approx)
- **Completed:** 2026-06-15T21:47Z
- **Tasks:** 3
- **Files modified:** 4

## Accomplishments
- Collapsed the three separate forward-Post ops (`HashSetAsync` index slot + `KeyExpireAsync` index TTL + `StringSetAsync` data) into ONE atomic server-side Lua write — a concurrent reader/Recovery can no longer observe a partial index-without-data (or data-without-index) state (spec §4.3 step 3 / ATOMIC-01).
- Eliminated the INFRA-01 silent DROP: an exhausted atomic write (former index- AND data-failure modes) escalates as a single `SendKeeper(BuildInject(d, item, entryId))` — no drop path remains (spec §10 bullet 1 / NODROP-01).
- Preserved the index/data TTL relationship as C#-computed ARGV ms (index = `SlotTtl()` random[ExecutionDataTtl, 2×], data = ExecutionDataTtl) so the Phase-68 TEST-06 desync guard stays green; Lua never randomizes.
- Migrated the forward test harness + facts to the single-script shape (`AtomicWriteFaultL2` mux, `AtomicWriteFault_Inject` fact, script-ARGV TTL/ordering inspection).

## Task Commits

Each task was committed atomically (TDD RED → GREEN → fact rework):

1. **Task 1: Migrate forward muxes to single `ScriptEvaluateAsync` shape (Wave-0 RED)** - `afb8a8d` (test)
2. **Task 2: Atomic Lua write + single-INJECT no-drop (GREEN — production)** - `566034b` (feat)
3. **Task 3: Invert drop→inject; rework forward+post facts to script ARGV** - `d344002` (test)

_Note: TDD plan — the RED harness change (Task 1) deliberately broke compile until Task 3 retargeted the renamed muxes; GREEN production (Task 2) built clean in between against `BaseProcessor.Core.csproj`._

## Files Created/Modified
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` - Added `AtomicForwardWrite` const Lua; replaced the 3 forward-Post ops with one `ScriptEvaluateAsync` in `RetryLoop`; exhaust → one `SendKeeper(BuildInject)`; updated the class XML doc summary to the atomic-write + no-drop shape.
- `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` - `ForwardOkL2` now stubs the script to `RedisResult.Create(1)`; merged `ForwardSlotFaultL2` + `ForwardDataFaultL2` into `AtomicWriteFaultL2` (default-stub then `When/Do` throw on the script binding).
- `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs` - Replaced `SlotWriteFault_Drop` + `DataWriteFault_Inject_WithIdSet` with one `AtomicWriteFault_Inject`; reworked `Completed_AllocationBeforeData` (HSET-before-SET inside the script body, KEYS inspection) and `IndexTtl_*` (TTLs from ARGV[4]/[5]); updated the class doc bullets.
- `tests/BaseApi.Tests/Processor/PipelinePostFacts.cs` - `PostCompleted_WritesWithTtl` reads the data TTL from script ARGV; `WriteFault_Inject` retargeted to `AtomicWriteFaultL2`.

## Decisions Made
See `key-decisions` frontmatter. Headline: TTLs stay C#-computed ARGV (no Lua RNG); PEXPIRE stays a whole-hash expire (no Redis 7.4 requirement); `AtomicWriteFault_Inject` subsumes both former forward write-fault facts.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Retargeted two PipelinePostFacts to the atomic script write**
- **Found during:** Task 3 (verification sweep — the plan lists `*PipelinePost*` as a required green gate)
- **Issue:** `PipelinePostFacts.PostCompleted_WritesWithTtl_AndSendsStepCompleted` asserted `StringSetAsync` carried the TTL, and `WriteFault_Inject` used the `StringSetAsync`-throwing `PresentReadWriteFaultL2` mux. Both directly exercise the forward-Post write path my Task-2 change converted to a single `ScriptEvaluateAsync`, so both failed (no `StringSetAsync` call any more; the write-fault mux no longer faults the script).
- **Fix:** `PostCompleted_WritesWithTtl` now reads the data TTL ms from script `ARGV[5]` (== 300_000); `WriteFault_Inject` now builds with `AtomicWriteFaultL2` and asserts the single `KeeperInject`.
- **Files modified:** `tests/BaseApi.Tests/Processor/PipelinePostFacts.cs`
- **Verification:** `dotnet test ... --filter-method "*PipelinePost*"` → 5/5 green.
- **Committed in:** `d344002` (part of the Task 3 commit)

---

**Total deviations:** 1 auto-fixed (1 bug — directly caused by the in-scope Task-2 production change to the shared forward-Post write path).
**Impact on plan:** Necessary to satisfy the plan's own `*PipelinePost*` verification gate. No scope creep — only facts exercising the changed write path were touched.

## Issues Encountered
- The Task-1 source-text verify initially failed because the new mux's XML doc comment named the two old muxes (`ForwardSlotFaultL2`/`ForwardDataFaultL2`), which the negated `Select-String` guard caught. Reworded the comment to drop the literal old names; verify passed.
- `DispatchTestKit.PresentReadWriteFaultL2` is now orphaned (no fact references it after `WriteFault_Inject` was retargeted). Left in place (internal static, builds clean) and logged to `deferred-items.md` rather than expand plan scope.

## Verification Results
- `dotnet build SK_P.sln -c Release` — succeeded, **0 warnings / 0 errors**.
- `dotnet test ... --filter-method "*PipelineForward*"` — **7/7 green**.
- `dotnet test ... --filter-method "*PipelinePost*"` — **5/5 green**.
- `dotnet test ... --filter-method "*InjectConsumer*"` — **3/3 green** (D-2 honored — INJECT contract unchanged).
- `dotnet test ... --filter-method "*Pipeline*"` — 34/36; the 2 failures are `UseBaseApiPipelineFacts.Probe_ApiV1Tests_*`, a real-stack WebAPI test needing Postgres on :5433 (not running) — pre-existing infra-dependent, unrelated to this plan's changes.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- The atomic forward write + single-INJECT no-drop is in place for Plan 69-02 (gated forward cleanup / T-69-02 processor↔keeper index-key race). D-3 (forward slot retirement model) and the In-Process contract were correctly left untouched per the plan's OUT-OF-SCOPE flags.

---
*Phase: 69-align-processor-pipeline-to-canonical-recovery-spec-atomic-i*
*Completed: 2026-06-15*

## Self-Check: PASSED

- Commits verified present: `afb8a8d`, `566034b`, `d344002`.
- Files verified present: `ProcessorPipeline.cs`, `DispatchTestKit.cs`, `PipelineForwardFacts.cs`, `PipelinePostFacts.cs`, `69-01-SUMMARY.md`.
