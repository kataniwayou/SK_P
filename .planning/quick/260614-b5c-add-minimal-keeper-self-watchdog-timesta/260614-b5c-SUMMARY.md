---
phase: quick-260614-b5c
plan: 01
subsystem: Keeper / health
tags: [keeper, liveness, watchdog, health-check, bit-loop]
requires:
  - BitHealthLoop (Keeper.Health)
  - HealthCheckDescriptor seam (BaseConsole.Core.Health)
  - ProbeOptions (Keeper)
provides:
  - IKeeperLivenessState / KeeperLivenessState (timestamp-only L1 holder)
  - KeeperLivenessWatchdogHealthCheck ("live"-tagged, folded into /health/live)
  - per-tick unconditional liveness stamp inside BitHealthLoop
affects:
  - /health/live (now flips Unhealthy when the BIT loop goes silent)
tech-stack:
  added: []
  patterns:
    - lock-free long-ticks sentinel idiom (Interlocked.Exchange/Read; 0 = never stamped)
    - resolve-at-check-time watchdog over the OUTER IServiceProvider
key-files:
  created:
    - src/Keeper/Health/IKeeperLivenessState.cs
    - src/Keeper/Health/KeeperLivenessState.cs
    - src/Keeper/Health/KeeperLivenessWatchdogHealthCheck.cs
    - tests/BaseApi.Tests/Keeper/Health/KeeperLivenessWatchdogTests.cs
  modified:
    - src/Keeper/Health/BitHealthLoop.cs
    - src/Keeper/Program.cs
    - tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs
decisions:
  - timestamp-only record (NO ProcessorLivenessEntry reuse, NO per-schema Summary, NO Data payload)
  - DateTime? stored as long _ticks with Interlocked (a DateTime? cannot be volatile)
  - stamp is UNCONDITIONAL — outside the edge guard — so a hung probe OR hung Task.Delay goes stale
  - trailing Task.Delay left real-time; only the stamp reads clock.GetUtcNow()
metrics:
  duration: ~4m
  completed: 2026-06-14
---

# Phase quick-260614-b5c Plan 01: Minimal Keeper Self-Watchdog Summary

Timestamp-only keeper self-watchdog: a per-tick UTC stamp written unconditionally by `BitHealthLoop` plus a resolve-at-check-time `KeeperLivenessWatchdogHealthCheck` that flips `/health/live` Unhealthy when the BIT loop goes silent — making a wedged `ProbeOnceAsync` or a stuck trailing `Task.Delay` observable to a future K8s liveness probe.

## What was built

- **`IKeeperLivenessState` + `KeeperLivenessState`** — minimal timestamp-only L1 holder. `public sealed` so `AddSingleton<IKeeperLivenessState, KeeperLivenessState>` resolves across the assembly boundary. Lock-free `long _ticks` sentinel idiom (0 = never stamped) via `Interlocked.Exchange`/`Interlocked.Read`, reconstructed as `new DateTime(t, DateTimeKind.Utc)`. Deliberately NOT `ProcessorLivenessEntry`/`IProcessorLivenessState` (no status, no summary, no Data).
- **`KeeperLivenessWatchdogHealthCheck(IServiceProvider outer)`** — mirrors `LivenessWatchdogHealthCheck` in shape; resolves `IKeeperLivenessState` + `TimeProvider` + `IOptions<ProbeOptions>` AT CHECK TIME. Verdicts: `Current is null` → `Unhealthy("BIT loop not started")`; `now >= Current + DelaySeconds*2` (strict `>=`) → `Unhealthy("BIT loop stale")`; else → `Healthy("live")`. Description strings ONLY — NO `data:` argument on any branch.
- **`BitHealthLoop`** — two new primary-ctor params after `logger`: `TimeProvider clock, IKeeperLivenessState liveness`. One added line — `liveness.Update(clock.GetUtcNow().UtcDateTime)` — placed right after `probe.ProbeOnceAsync` returns and BEFORE the `if (prevHealthy != healthy)` edge guard, so it fires every tick unconditionally. All edge behavior (gate.Open/Close, endpoint Stop/Start, PauseAll/ResumeAll, prevHealthy advance, WR-01 catch) is byte-identical; the trailing `Task.Delay` stays real-time.
- **`Program.cs`** — added `using BaseConsole.Core.Health;` + `using Microsoft.Extensions.DependencyInjection.Extensions;`; registered `TryAddSingleton(TimeProvider.System)` (mirrors Orchestrator/Program.cs:91), `AddSingleton<IKeeperLivenessState, KeeperLivenessState>`, and `AddSingleton(new HealthCheckDescriptor("keeper-liveness-watchdog", new[]{"live"}, outer => new KeeperLivenessWatchdogHealthCheck(outer)))`. NO BaseConsole.Core change — `EmbeddedHealthEndpointService` auto-folds the "live"-tagged descriptor into `/health/live`.
- **Tests** — `BitHealthLoopTests.NewLoop` updated with OPTIONAL `clock`/`liveness` params (default `TimeProvider.System` + `new KeeperLivenessState()`) so the 7 existing edge facts compile + pass unchanged. Added Fact A `Stamp_Advances_Every_Tick_Including_Unhealthy`: a single-unhealthy-tick script with a pinned `FakeTimeProvider` asserts `Current == that instant` — proving the stamp fires on an unhealthy tick (edge-gated code would leave it null). New `KeeperLivenessWatchdogTests` mirrors the processor watchdog's `BuildProvider` stub exactly: live / BIT loop stale / BIT loop not started + exact-boundary + one-tick-before-boundary, all asserting `Assert.Empty(result.Data)`.

## Verification results

- `dotnet build SK_P.sln -c Debug --nologo` → Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet build SK_P.sln -c Release --nologo` → Build succeeded, 0 Warning(s), 0 Error(s).
- `BaseApi.Tests.exe --filter-namespace "BaseApi.Tests.Keeper.Health"` → Passed! total: 20, failed: 0 (7 pre-existing BitHealthLoop edge facts + Fact A + 5 watchdog verdict facts all green; RealStack/E2E excluded by the namespace filter — RabbitMQ down, expected).
- Scope greps: no `data:` argument in the watchdog (only the doc comment); no `ProcessorLivenessEntry`/`IProcessorLivenessState` reuse (the single `IProcessorLivenessState` hit is the doc note explaining it is deliberately NOT reused); no edits under src/BaseConsole.Core or src/BaseProcessor.Core (`git diff --stat HEAD~1` empty for both).

## Deviations from Plan

None - plan executed exactly as written.

## Commits

- `a3a4e9d` feat(quick-260614-b5c): add minimal keeper self-watchdog (state + watchdog + per-tick stamp + wiring)
- `d755df4` test(quick-260614-b5c): update BitHealthLoop harness + add keeper watchdog facts

## Self-Check: PASSED

- FOUND: src/Keeper/Health/IKeeperLivenessState.cs
- FOUND: src/Keeper/Health/KeeperLivenessState.cs
- FOUND: src/Keeper/Health/KeeperLivenessWatchdogHealthCheck.cs
- FOUND: tests/BaseApi.Tests/Keeper/Health/KeeperLivenessWatchdogTests.cs
- FOUND commit a3a4e9d
- FOUND commit d755df4
