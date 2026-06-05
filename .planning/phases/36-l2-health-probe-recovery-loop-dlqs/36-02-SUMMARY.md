---
phase: 36-l2-health-probe-recovery-loop-dlqs
plan: 02
subsystem: Keeper / L2 probe-recovery engine (probe loop + consumer re-inject/park)
tags: [keeper, probe-loop, dlq, recovery, re-inject, tdd, wave-2]
requires:
  - "Keeper.ProbeOptions (DelaySeconds/MaxAttempts, Plan 01)"
  - "Messaging.Contracts.Projections.L2ProjectionKeys.ExecutionData(string) + KeeperProbe(string) (Plan 01)"
  - "Messaging.Contracts.KeeperQueues.DeadLetter = keeper-dlq (Plan 01)"
  - "Messaging.Contracts.OrchestratorQueues.Result = orchestrator-result (existing)"
  - "Keeper.Consumers.FaultEntryStepDispatchConsumer + FaultExecutionResultConsumer (Phase 35 observe-and-ack bodies)"
  - "tests/BaseApi.Tests/Keeper/FakeRedis.cs (down/half-open/up double, Plan 01)"
  - "IConnectionMultiplexer singleton via AddBaseConsole (no second registration)"
provides:
  - "Keeper.Recovery.L2ProbeRecovery — shared bounded read+write-then-delete probe loop (RedisException = keep looping; both-ops-clean = Recovered; MaxAttempts = GaveUp)"
  - "Keeper.Recovery.ProbeOutcome { Recovered, GaveUp }"
  - "FaultEntryStepDispatchConsumer recovery body: probe → re-inject verbatim inner to queue:{ProcessorId:D} | park original Fault<EntryStepDispatch> to keeper-dlq"
  - "FaultExecutionResultConsumer recovery body: probe → re-inject verbatim inner to queue:orchestrator-result | park original Fault<ExecutionResult> to keeper-dlq"
  - "tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs (PROBE-02..05 hermetic proof — 6 facts)"
affects:
  - "Plan 03 (probe metrics — instruments the L2ProbeRecovery outcomes / consumer re-inject+park paths)"
  - "Plan 04 (RealStack E2E — drives the live trip → probe → re-inject | keeper-dlq park topology)"
tech-stack:
  added: []
  patterns:
    - "Shared stateless DI-injected recovery helper (L2ProbeRecovery) ctor-injecting the singleton IConnectionMultiplexer + IOptions<ProbeOptions>"
    - "Bounded RedisException-only retry loop awaited inside Consume (ack-after-loop)"
    - "Per-consumer concrete-type origin endpoint (NOT a runtime type switch on a statically-known inner)"
    - "Re-inject via GetSendEndpoint + Send (NOT Publish); park the original Fault<T> envelope via Send(context.Message)"
key-files:
  created:
    - "src/Keeper/Recovery/L2ProbeRecovery.cs"
    - "tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs"
  modified:
    - "src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs"
    - "src/Keeper/Consumers/FaultExecutionResultConsumer.cs"
    - "src/Keeper/Program.cs"
    - "tests/BaseApi.Tests/Keeper/KeeperFaultConsumerScopeTests.cs"
    - "tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs"
    - "tests/BaseApi.Tests/Keeper/KeeperHostBootFixture.cs"
decisions:
  - "L2ProbeRecovery.RunAsync takes (entryId, h) explicitly, NOT an IExecutionCorrelated — H is a per-record property on EntryStepDispatch/ExecutionResult, NOT on IExecutionCorrelated/ICorrelated; the plan's interface signature would not compile"
  - "Each consumer hardcodes its own origin endpoint (dispatch → queue:{inner.ProcessorId:D}; result → queue:orchestrator-result) instead of the plan's shared inner switch — a pattern switch on a statically-known concrete inner type is a CS8121/CS0104 compile error"
  - "The 3 standing Keeper harnesses (ScopeTests/RoundRobin/HostBootFixture) get a healthy FakeRedis + ProbeOptions + L2ProbeRecovery threaded in so the new consumer ctor-dep resolves and they stay GREEN"
metrics:
  duration: "~18 min"
  completed: 2026-06-05
  tasks: 2
  files: 8
  commits: 2
---

# Phase 36 Plan 02: L2 Probe-Recovery Loop + Consumer Re-inject/Park Summary

Turned the two Phase-35 observe-and-ack Keeper consumers into the recovery engine. Added the shared bounded `L2ProbeRecovery` loop (read `skp:data:{entryId}` AND write-then-delete `skp:keeper:probe:{h}`, looping on `RedisException`, bounded by `ProbeOptions`), then wired it into both consumers: on success re-inject the verbatim inner to its origin endpoint, on give-up park the original `Fault<T>` envelope to `keeper-dlq`. The loop is awaited inside `Consume`, so ack-after-loop (PROBE-05) is automatic. PROBE-01..05 + DLQ-03 (keeper-dlq park) land hermetically; the live trip→recover topology is Plan 04.

## What Was Built

### Task 1 — `L2ProbeRecovery` shared bounded probe-loop helper (commit `399570f`, tdd=true)
- `src/Keeper/Recovery/L2ProbeRecovery.cs` — `public enum ProbeOutcome { Recovered, GaveUp }` + a stateless `sealed class L2ProbeRecovery(IConnectionMultiplexer redis, IOptions<ProbeOptions> opts)`. `RunAsync(string entryId, string h, CancellationToken ct)`: a bounded `for (attempt < MaxAttempts)` loop that each iteration READs `L2ProjectionKeys.ExecutionData(entryId)` (value need NOT exist — a Null read on an up Redis still counts) AND WRITEs-then-deletes the scratch key `L2ProjectionKeys.KeeperProbe(h)` (plain TTL'd 30s scratch write, then `KeyDeleteAsync` → net-zero). Returns `Recovered` only if BOTH ops complete with no Redis exception. `catch (RedisException)` ONLY (the `RedisConnectionException`+`RedisTimeoutException` superset) → `Task.Delay(DelaySeconds)` and keep looping; loop exhaustion → `GaveUp`. NO `catch (Exception)` (a genuine bug must propagate, T-36-06). NO `When.Exists`/`keepTtl` flag-flip semantics (PATTERNS warning — this is a scratch write, not ResultConsumer's idempotent flag flip).
- `tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs` (helper layer) — three facts driving the helper DIRECTLY against the Plan-01 `FakeRedis` with `DelaySeconds=0`: `Probe_RequiresReadAndWrite` (HalfOpen = read OK / write throws → GaveUp, PROBE-02), `Probe_FailThenSucceed` (`SetFailuresBeforeUp(2)` → Recovered within budget, did NOT exhaust), `Probe_FailToMax` (Down all attempts → GaveUp).
- Verify: `--filter-method "*Probe_RequiresReadAndWrite*|*Probe_FailThenSucceed*|*Probe_FailToMax*"` → 3 passed / 0 failed. Greps: `catch (RedisException)`==1, `catch (Exception`==0, `StringGetAsync`/`StringSetAsync`/`KeyDeleteAsync`==1 each, `When.Exists|keepTtl`==0.

### Task 2 — Wire the loop into both consumers + harness facts (commit `cf627b9`, tdd=true)
- Both `FaultEntryStepDispatchConsumer` + `FaultExecutionResultConsumer`: ctor now `(ILogger<…> logger, L2ProbeRecovery recovery)`; `Consume` is `async Task`. The Phase-35 double-unwrap + `ex` extraction + manual `CorrelationKeys.LogScope` scope + `ExecutionLogScope.BuildState` scope + the ONE Information intake log are KEPT VERBATIM (lines 33-42, load-bearing T-35-06). The recovery slot REPLACES `return Task.CompletedTask;` with: `await recovery.RunAsync(inner.EntryId, inner.H, ct)` → `Recovered` ⇒ `GetSendEndpoint` + `Send(inner)` to the origin (dispatch → `queue:{inner.ProcessorId:D}`, result → `queue:{OrchestratorQueues.Result}`); `GaveUp` ⇒ `GetSendEndpoint("queue:keeper-dlq")` + `Send(context.Message)` (the ORIGINAL `Fault<T>` envelope, carrying `Exceptions[]` for triage — D-10).
- `src/Keeper/Program.cs`: `builder.Services.AddSingleton<Keeper.Recovery.L2ProbeRecovery>();` after the Plan-01 `Configure<ProbeOptions>` line. No second Redis registration (singleton already via AddBaseConsole).
- `KeeperProbeLoopTests.cs` (harness layer): `Probe_Success_Reinjects` (Fault<EntryStepDispatch> + healthy FakeRedis → harness `Sent.Any<EntryStepDispatch>`; repeat for Fault<ExecutionResult> → `Sent.Any<ExecutionResult>`), `Probe_GiveUp_ParksToDlq` (Down all attempts → `Sent.Any<Fault<EntryStepDispatch>>` AND NOT the bare inner), `Probe_AcksOnlyAfterLoop` (fail-twice-then-up → `Consumed.Any` completes only after the awaited loop, re-inject present).
- Verify: `--filter-method "*Probe_Success_Reinjects*|*Probe_GiveUp_ParksToDlq*|*Probe_AcksOnlyAfterLoop*"` → 3 passed / 0 failed. Greps: both consumers `async Task Consume`==1, `GetSendEndpoint`==2, `Send(context.Message`==1, `.Publish`==0; Program.cs `L2ProbeRecovery`==1. (`CorrelationKeys.LogScope`/`ExecutionLogScope.BuildState` grep==2 each because the KEPT Phase-35 `<see cref>` doc comments mention them too — the actual code usage is exactly 1 each, verified by line inspection at L39/L40.)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] `RunAsync` signature corrected — `H` is not on `IExecutionCorrelated`**
- **Found during:** Task 1 (first Keeper build).
- **Issue:** The plan's `RunAsync(IExecutionCorrelated inner, …)` reads `inner.H`, but `H` is a per-record property on `EntryStepDispatch`/`ExecutionResult`, NOT a member of `IExecutionCorrelated` or its base `ICorrelated` (which only carries `CorrelationId`). CS1061: `'IExecutionCorrelated' does not contain a definition for 'H'`. The plan's `<interfaces>` comment "`+ H from ICorrelated`" was inaccurate.
- **Fix:** Changed the signature to `RunAsync(string entryId, string h, CancellationToken ct)` and pass `inner.EntryId, inner.H` from each consumer (both have the concrete record where `H` lives). Keeps the helper self-contained; avoids a shared-contract change (which would have been out of this plan's scope / possibly Rule 4). Trimmed the now-unused `MassTransit` + `Messaging.Contracts` usings to preserve 0-warning.
- **Files modified:** src/Keeper/Recovery/L2ProbeRecovery.cs (in scope). **Commit:** `399570f`.

**2. [Rule 3 - Blocking] Per-consumer concrete-type endpoint instead of the shared `inner switch`**
- **Found during:** Task 2 (solution build).
- **Issue:** The plan's identical `inner switch { EntryStepDispatch d => …, ExecutionResult => … }` does not compile in either consumer: in `FaultExecutionResultConsumer`, `inner` is statically `Messaging.Contracts.ExecutionResult`, so the `EntryStepDispatch` arm is CS8121 (an expression of type `ExecutionResult` cannot be matched by a pattern of type `EntryStepDispatch`); in `FaultEntryStepDispatchConsumer` the bare `ExecutionResult` arm is CS0104 (ambiguous with `MassTransit.ExecutionResult`, no alias in that file). A type switch on a statically-known concrete inner is a compile error, not "type-safe in both".
- **Fix:** Each consumer hardcodes its own origin endpoint directly: dispatch → `new Uri($"queue:{inner.ProcessorId:D}")`; result → `new Uri($"queue:{OrchestratorQueues.Result}")`. Same verbatim-`Send(inner)` re-inject semantics (spike-proven), no behavioral change vs intent — just type-correct.
- **Files modified:** src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs, src/Keeper/Consumers/FaultExecutionResultConsumer.cs (both in scope). **Commit:** `cf627b9`.

**3. [Rule 3 - Blocking] Threaded the new ctor-dep through 3 standing Keeper harnesses**
- **Found during:** Task 2 (the plan's `<verification>` explicitly anticipated this).
- **Issue:** The consumer ctor now requires `L2ProbeRecovery` (→ `IConnectionMultiplexer` + `IOptions<ProbeOptions>`). The standing `KeeperFaultConsumerScopeTests` (BuildHarness), `KeeperRoundRobinTests`, and `KeeperHostBootFixture` register the consumers but did not provide those deps, so consume-time resolution would fail.
- **Fix:** Added a healthy `FakeRedis(Up)` as `IConnectionMultiplexer` + `ProbeOptions(DelaySeconds=0)` + `AddSingleton<L2ProbeRecovery>()` to ScopeTests' BuildHarness and RoundRobinTests; for HostBootFixture (boots against dead Redis, IConnectionMultiplexer already via AddBaseConsole) added the `Probe` config section bind + the helper singleton, mirroring Program.cs. The scope/round-robin assertions are unaffected (the scope is opened before the loop; round-robin only counts consumed==1).
- **Files modified:** tests/BaseApi.Tests/Keeper/{KeeperFaultConsumerScopeTests,KeeperRoundRobinTests,KeeperHostBootFixture}.cs (all in scope as standing-test repair). **Commit:** `cf627b9`.

No bugs in shipped logic, no missing-critical-functionality gaps, no architectural decisions, no auth gates, no scope creep.

## TDD Gate Compliance

Both tasks are `tdd="true"`. There is no compiling RED state possible for either: the SUT types (`L2ProbeRecovery`, the modified consumers) must compile before the tests can reference them, so each helper/consumer landed in its `feat(...)` GREEN commit alongside the behavior-driving facts (the same RED-collapse precedent recorded across Phases 33/35 and 36-01). The `<behavior>` blocks drove the assertions; all 6 facts were verified GREEN before each commit. Plan frontmatter `type: tdd` — the gate sequence here is feat+facts-per-task (RED would require a non-compiling test against a not-yet-existing SUT, which the harness rejects). No GREEN-skipping-RED bug: each fact exercises real loop/re-inject/park behavior and would fail if the logic were wrong (e.g. HalfOpen→GaveUp catches a read-only-success regression; give-up→park-envelope catches a bare-inner-park regression).

## Threat Model Compliance

- **T-36-05 (half-open Redis swallowed as up) — mitigate:** the loop requires BOTH a read AND a write-then-delete; `Probe_RequiresReadAndWrite` proves HalfOpen (write-failing) → GaveUp. ✓
- **T-36-06 (`catch (Exception)` masking a bug) — mitigate:** `catch (RedisException)` only; `catch (Exception`==0 grep holds; a genuine bug propagates → Immediate(N) → DLQ-1. ✓
- **T-36-04 (re-inject spoofing) — accept:** verbatim inner, same H; receiver's Phase-31 flag[H] gate collapses duplicates (PROBE-06, no Keeper-side dedup). ✓
- **T-36-07 (parked envelope carries Exceptions[]/payload) — accept:** the Information log surfaces only ExceptionType+Message (no stack frames, T-35-05 kept); keeper-dlq drainers already have broker access. ✓
- **T-36-08 (poison re-inject loop) — accept:** pre-existing milestone limitation; a non-L2 fault re-faults → DLQ-1. ✓
No new security-relevant surface beyond the planned re-inject + keeper-dlq park.

## Git Hygiene

2 task commits, each with explicit per-file `git add` (no `git add -A`/`.`). No file deletions in either commit (the "6 deletions" in commit `cf627b9` are line-level edits, not file removals — `git diff --diff-filter=D HEAD~1 HEAD` empty for both commits). The ~242 pre-existing `.planning/` archive deletions were left UNtouched (NOT staged, NOT reverted) — verified `git status` still shows 242 ` D .planning/...` entries after both commits.

## Verification Summary

| Check | Result |
|-------|--------|
| `dotnet build SK_P.sln -c Release` | 0 Warning / 0 Error |
| Task 1 facts (`Probe_RequiresReadAndWrite`/`FailThenSucceed`/`FailToMax`) | 3 passed / 0 failed |
| Task 2 facts (`Probe_Success_Reinjects`/`GiveUp_ParksToDlq`/`AcksOnlyAfterLoop`) | 3 passed / 0 failed |
| Keeper namespace hermetic suite | 13 passed / 0 failed (deterministic) |
| Full hermetic suite (`Category!=RealStack`) | 464 passed / 0 failed on a clean run (was 458 + 6 new) |
| Task 1/2 acceptance greps | all met (code-line usage exact; comment `<see cref>` accounts for the 2× scope greps) |

**Flake note:** Two of the four full-suite runs reported 1/464 failed; the Keeper namespace (13/13) and all 6 new facts pass deterministically, and a clean re-run passed 464/464. This is the documented run-to-run cross-namespace in-memory-MassTransit harness flake (recorded in the 35-02 SUMMARY — "cleared on re-run; not in any touched file"), NOT a regression from this plan's changes (no touched test fails in isolation). The Phase-39 close gate's 3×GREEN loop is the authoritative signal.

## Requirements

PROBE-01 (bounded probe-loop awaited inside Consume), PROBE-02 (read AND write both required — half-open = fault), PROBE-03 (verbatim re-inject by origin), PROBE-04 (give-up parks original Fault<T> to keeper-dlq), PROBE-05 (ack only after loop), DLQ-03 (keeper-dlq park) — all land hermetically this plan. The LIVE trip→probe→re-inject | keeper-dlq topology proof is Plan 04 (RealStack E2E).

## Self-Check: PASSED

`src/Keeper/Recovery/L2ProbeRecovery.cs` + `tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs` exist on disk; the 3 modified src files + 3 modified test files present. Both task commits (`399570f`, `cf627b9`) in git history; no file deletions in either; 242 `.planning/` deletions still uncommitted.
