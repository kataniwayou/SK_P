---
phase: 40-keeper-recovery-hardening
plan: 02
subsystem: keeper
tags: [keeper, recovery, dos-hardening, redis, attempt-cap, single-winner, kharden]

requires:
  - phase: 40-01
    provides: "KeeperRecoveryHandler — the single shared generic recovery body the cap lands inside; IExecutionCorrelated.H (inner.H read through the bound)"
  - phase: 39
    provides: "KeeperMetrics.DlqPushed + KeeperMetricTags.Reason closed-enum; L2ProbeRecovery redis+IOptions ctor pattern"
provides:
  - "RecoverAttemptCap (ProbeOptions, default 3) — the OUTER recover→reinject per-H cap, distinct from MaxAttempts (inner probe-loop)"
  - "L2ProjectionKeys.KeeperRecoverAttempts(h) => skp:keeper:attempts:{h} — the atomic per-H recover-attempt counter key"
  - "KeeperMetricTags.ReasonRecoverCap = recover_cap — give-up tag distinct from probe_exhausted"
  - "The single-winner cap check inside KeeperRecoveryHandler.HandleAsync (n==cap+1 parks once + DELs the counter)"
  - "FakeRedis counter ops (StringIncrementAsync/KeyExpireAsync, KeyDeleteAsync removes counter, CounterKeyExists accessor)"
  - "KeeperRecoverCapTests — hermetic proof: exactly cap reinjects then one idempotent park, counter DEL'd, single-winner crossing"
affects: [40-03 (keeper-dlq drain — the cap-park is now a second give-up source into keeper-dlq), 42 (close gate / milestone reconciliation)]

tech-stack:
  added: []
  patterns:
    - "Single-winner park gated on the atomic INCR crossing increment (n == cap+1) — mirrors the flag[H] first-writer-wins dedup; race-free across the 2 keeper replicas"
    - "TTL set without clobber via ExpireWhen.HasNoExpiry (set-once-on-create) + DEL-on-terminal — the keepTtl landmine countermeasure for net-zero skp: growth"
    - "Hermetic-only DoS proof: the single-winner invariant proven on the atomic-INCR contract directly (concurrent INCR race) rather than via harness re-fault (a live cap test is FORBIDDEN — flood landmine)"

key-files:
  created:
    - "tests/BaseApi.Tests/Keeper/KeeperRecoverCapTests.cs"
  modified:
    - "src/Keeper/ProbeOptions.cs"
    - "src/Keeper/appsettings.json"
    - "src/Messaging.Contracts/Projections/L2ProjectionKeys.cs"
    - "src/Keeper/Observability/KeeperMetrics.cs"
    - "src/Keeper/Recovery/KeeperRecoveryHandler.cs"
    - "tests/BaseApi.Tests/Keeper/FakeRedis.cs"
    - "tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs"
    - "tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs"

key-decisions:
  - "D-A1: gate the park on the atomic crossing increment n==cap+1 ONLY (single-winner) — n<=cap reinjects, n==cap+1 parks once+DELs, n>cap+1 does nothing"
  - "D-A2: RecoverAttemptCap added to existing ProbeOptions (default 3), bound from the existing Probe section — no new Configure<T>"
  - "D-A3: counter TTL=300s set via ExpireWhen.HasNoExpiry (no clobber on later INCRs) + DEL on park; missed DEL self-cleans"
  - "D-A4: reason=recover_cap tag distinct from probe_exhausted so operators distinguish flood-cap from L2-never-returned"
  - "Test-safety: cap proven HERMETICALLY only; the idempotent single-winner fact exercises the atomic-INCR contract directly (concurrent race) — a RealStack cap test is FORBIDDEN (flood landmine)"

patterns-established:
  - "Single-winner-on-INCR-crossing: park exactly once on n==cap+1, DEL the key, ignore n>cap+1 — the cross-replica convergence primitive"
  - "Set-once TTL (ExpireWhen.HasNoExpiry) + DEL-on-terminal as the net-zero counter-key lifecycle"

requirements-completed: [KHARD-01]

duration: 13min
completed: 2026-06-06
---

# Phase 40 Plan 02: Per-H Recover-Attempt Cap (KHARD-01) Summary

**Bounded the OUTER recover→reinject cycle with a configurable per-`H` Redis attempt cap (`RecoverAttemptCap`, default 3) inside the single `KeeperRecoveryHandler`: a persistent fault now converges to exactly ONE park (reason=`recover_cap`) gated on the atomic INCR crossing `n == cap+1`, eliminating the unbounded ~67 cyc/s/replica reinject self-DoS.**

## Performance

- **Duration:** ~13 min
- **Started:** 2026-06-06T20:10:02Z
- **Completed:** 2026-06-06T20:23:10Z
- **Tasks:** 3
- **Files modified:** 8 (1 created, 7 modified)

## Accomplishments
- The reinject self-DoS landmine (MEMORY-tracked) is fixed: a non-transient fault for a given `H` parks once instead of looping forever.
- The cap lives in **exactly one place** — the single shared handler extracted in Plan 01 — keyed off an **atomic, race-free** per-`H` Redis counter (`skp:keeper:attempts:{H}`).
- Net-zero Redis footprint: counter DEL'd on park + 300s TTL set without clobber (a missed DEL self-cleans → close-gate triple-SHA stays net-zero).
- Hermetic proof (2 facts, both GREEN): exactly `cap` reinjects then one idempotent park with the counter DEL'd; and a concurrent-INCR race proving exactly one crossing winner (`n == cap+1`) regardless of how far past `cap` the counter is driven.

## Task Commits

Each task committed atomically (scoped `git add` — pre-existing untracked items `.claude/` / `27-PATTERNS.md` / `psql-*.txt` / `launchSettings.json` left UNtouched; no file deletions):

1. **Task 1 (Wave-0): config key + counter key factory + recover_cap reason + FakeRedis counter ops** — `5f62fb0` (feat)
2. **Task 2: land the per-H recover-attempt cap in the shared handler** — `22b8388` (feat)
3. **Task 3: hermetic cap proof + direct-construction ctor fixes** — `b2ef45f` (test)

**Plan metadata:** (this SUMMARY + STATE/ROADMAP/REQUIREMENTS) — see final docs commit.

## Files Created/Modified
- `src/Keeper/ProbeOptions.cs` — `RecoverAttemptCap` (default 3), the OUTER cap distinct from inner `MaxAttempts`.
- `src/Keeper/appsettings.json` — `Probe.RecoverAttemptCap: 3` alongside DelaySeconds/MaxAttempts.
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `KeeperRecoverAttempts(h) => skp:keeper:attempts:{h}` beside `KeeperProbe`.
- `src/Keeper/Observability/KeeperMetrics.cs` — `KeeperMetricTags.ReasonRecoverCap = "recover_cap"`; `Reason` doc extended to both closed values.
- `src/Keeper/Recovery/KeeperRecoveryHandler.cs` — ctor injects `IConnectionMultiplexer` + `IOptions<ProbeOptions>`; the cap check (atomic INCR → `n==cap+1` single-winner park with reason=recover_cap + RecoveryDuration{gave_up} + DEL; `n>cap+1` no-op) sits at the top of the Recovered branch before the reinject Send. GaveUp/probe_exhausted path untouched.
- `tests/BaseApi.Tests/Keeper/FakeRedis.cs` — `_counters` dict + `StringIncrementAsync`/`KeyExpireAsync` ops + `CounterKeyExists` accessor; `KeyDeleteAsync` now removes the counter (proving DEL-on-park).
- `tests/BaseApi.Tests/Keeper/KeeperRecoverCapTests.cs` — **created**, 2 hermetic facts (no RealStack trait).
- `tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs`, `tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs` — direct-`new` construction sites updated for the new cap ctor deps (in-scope ripple fix).

## Decisions Made
- **D-A1 (single-winner):** park gated on the atomic crossing increment `n == cap+1` only — mirrors the existing `flag[H]` first-writer-wins dedup; race-free across the 2 keeper replicas.
- **D-A2 (config):** `RecoverAttemptCap` added to existing `ProbeOptions` (default 3), bound from the existing `Probe` section — no new `Configure<T>`. Matches the `Retry.Limit=3` budget.
- **D-A3 (TTL):** counter TTL = 300s via `ExpireWhen.HasNoExpiry` (set-once, no clobber on later INCRs) + DEL on park; a missed DEL self-cleans → net-zero `skp:` growth.
- **D-A4 (reason):** `recover_cap` tag distinct from `probe_exhausted` so operators distinguish a flood-cap park from an L2-never-returned park.
- **Test-safety:** the cap is proven hermetically only; the single-winner idempotency fact exercises the atomic-INCR contract directly (concurrent race) — a RealStack cap test is FORBIDDEN (would flood the live stack; MEMORY landmine).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Updated the two direct-`new` KeeperRecoveryHandler construction sites for the new cap ctor deps**
- **Found during:** Task 3 (test project Release build)
- **Issue:** The Task-2 ctor change (added `IConnectionMultiplexer redis, IOptions<ProbeOptions> opts`) broke the two non-DI direct-construction sites (`KeeperPausePublishTests` ×2, `KeeperMetricsFacts` ×2) with CS7036 — exactly the "direct-construction test fixes" class the prior wave (40-01) also handled.
- **Fix:** Each site now builds its `FakeRedis` multiplexer once and passes it + `Opts()` to the handler ctor. Recovered-path sites use an Up FakeRedis (cap counter reachable; `n=1 <= cap` → reinject + recovered metrics, no dlq — assertions unchanged); the GaveUp site uses a Down FakeRedis (the cap check is never reached). Behavior of every existing assertion is preserved.
- **Files modified:** `tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs`, `tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs`
- **Verification:** Both files build 0-warning Release; their facts GREEN in the full 502-test run.
- **Committed in:** `b2ef45f` (Task 3 commit)

**2. [Rule 3 - Blocking] Used `Assert.Single` instead of the plan's literal `Assert.Equal(1, …Count())` for the park-count assertion**
- **Found during:** Task 3 (test build under TreatWarningsAsErrors)
- **Issue:** The plan's acceptance grep wanted `Assert.Equal(1, harness.Sent.Select<Fault<…>>().Count())`, but the xUnit2013 analyzer (warnings-as-errors) FORBIDS `Assert.Equal(1, collection.Count())` and mandates `Assert.Single`. The parameterless `Select<T>()` also trips xUnit1051 (must pass the CancellationToken).
- **Fix:** Park-count proven with `Assert.Single(harness.Sent.Select<Fault<…>>(ct))` (proves exactly one even more precisely); the `cap`-count check retains `Assert.Equal(cap, …Select<…>(ct).Count())` (variable count, analyzer-clean). The single-winner crossing fact uses `Assert.Single(results, n => n == cap + 1)`.
- **Files modified:** `tests/BaseApi.Tests/Keeper/KeeperRecoverCapTests.cs`
- **Verification:** 0-warning Release; both cap facts GREEN.
- **Committed in:** `b2ef45f` (Task 3 commit)

**3. [Rule 1 - Plan-test-fidelity] Fact 2 proves the single-winner invariant on the atomic-INCR contract instead of harness re-drive**
- **Found during:** Task 3 (designing `Cap_Idempotent_*`)
- **Issue:** The plan sketched Fact 2 as "publish cap+N times → still one park, exactly cap reinjects." But DEL-on-park (D-A3) resets the counter at `n==cap+1`, and the in-memory harness consumes serially — so sequential re-drives after a completed DEL legitimately restart a fresh cap window (more reinjects, more parks), which is correct production behavior, NOT one park. The `n > cap+1` (race-loser) branch is only reachable when many INCRs land before the winner's DEL — i.e., a concurrent 2-replica race, which a serialized harness cannot model.
- **Fix:** `Cap_Idempotent_RaceCrossesCap_StillOnePark` pre-arms the counter to `cap`, then fires 8 concurrent `StringIncrementAsync` and asserts exactly ONE returns `cap+1` (`Assert.Single(results, n => n == cap+1)`) while all are `> cap` (none reinjects) — proving the exact single-winner gate the handler keys its single Send off, deterministically and truthfully. Same intent (idempotent convergence to one park, no flood past cap), correct mechanism.
- **Files modified:** `tests/BaseApi.Tests/Keeper/KeeperRecoverCapTests.cs`
- **Verification:** Both cap facts GREEN; full hermetic suite GREEN.
- **Committed in:** `b2ef45f` (Task 3 commit)

---

**Total deviations:** 3 auto-fixed (2 blocking, 1 plan-test-fidelity). No architectural changes, no auth gates, no scope creep.
**Impact on plan:** All three are mechanical/correctness adjustments to satisfy the repo's 0-warning analyzer gate and to make the idempotency proof deterministic and honest. The production cap behavior (Tasks 1–2) is byte-for-byte the plan's spec.

## Issues Encountered
- The MTP runner ignores the VSTest `--filter` (`MTP0001`), so `dotnet test --filter` ran the full default suite (502 tests) — this conveniently satisfied BOTH the filtered-cap-test gate and the full-hermetic-suite gate in one pass. A follow-up `-- --filter-class "BaseApi.Tests.Keeper.KeeperRecoverCapTests"` run confirmed the 2 cap facts in isolation (2 passed).

## Threat Surface
No new external surface. The only new boundary crossing is the per-`H` counter key into shared Redis — covered by the plan's threat register (T-40-04 self-DoS / T-40-05 Redis growth / T-40-06 double-park race / T-40-07 live-flood-from-test), all `mitigate`, all addressed by Tasks 1–3. No threat flags beyond the plan's model.

## Verification
- `dotnet build src/Keeper/Keeper.csproj -c Release` + `tests/BaseApi.Tests` Release — **0 Warning / 0 Error** (TreatWarningsAsErrors).
- **Full hermetic suite** (MTP default, excludes RealStack) = **502 passed / 0 failed / 0 skipped** (491 Plan-01 baseline + growth + 2 new cap facts; no sibling regressed).
- Isolated cap class via `-- --filter-class` = **2 passed / 0 failed**.
- Acceptance greps clean: 1 `RecoverAttemptCap` in ProbeOptions (default 3) + 1 in appsettings; 1 `keeper:attempts:` factory; 1 `ReasonRecoverCap = "recover_cap"`; 1 `StringIncrementAsync` + 1 `CounterKeyExists` in FakeRedis; handler has 1 each of `RecoverAttemptCap`/`ReasonRecoverCap`/`n == cap + 1`/`KeyDeleteAsync(key)`/`ExpireWhen.HasNoExpiry`/`ReasonProbeExhausted` (GaveUp path intact); test class present, `CounterKeyExists` asserted, no `[Trait("Category","RealStack")]`.

## Known Stubs
None. The cap is fully wired (config → counter key → handler check → metrics → DEL); the hermetic facts exercise the real path.

## Next Phase Readiness
- KHARD-01 satisfied; the cap-park is now a SECOND give-up source into `keeper-dlq` (reason=recover_cap, alongside probe_exhausted) — 40-03's poll-until-stably-empty `keeper-dlq` drain must account for both park reasons reaching depth==0.
- Wave 2 of Phase 40 = 1/2 plans (40-02 done; 40-03 KHARD-02 drain next).

## Self-Check: PASSED

- `tests/BaseApi.Tests/Keeper/KeeperRecoverCapTests.cs` — FOUND
- `src/Keeper/Recovery/KeeperRecoveryHandler.cs` (cap check) — FOUND (RecoverAttemptCap + n == cap + 1 present)
- Commit `5f62fb0` (Task 1) — FOUND
- Commit `22b8388` (Task 2) — FOUND
- Commit `b2ef45f` (Task 3) — FOUND

---
*Phase: 40-keeper-recovery-hardening*
*Completed: 2026-06-06*
