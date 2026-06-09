---
phase: 49-live-proof-close-gate
plan: 03
subsystem: testing
tags: [realstack-e2e, xunit, redis-outage, docker, bit-health-gate, pause-resume, quartz, elasticsearch, masstransit]

# Dependency graph
requires:
  - phase: 45-keeper-bit-health-gate-global-pause-resume
    provides: "BitHealthLoop edge-triggered PauseAll/ResumeAll publish + IL2HealthGate; PauseAllConsumer/ResumeAllConsumer orchestrator seam logs"
  - phase: 49-live-proof-close-gate (plan 01)
    provides: "SampleRoundTripE2ETests RealStackWebAppFactory harness + PollForHealthyLivenessAsync + ElasticsearchTestClient.PollEsForLog seam-log precedent"
  - phase: 49-live-proof-close-gate (plan 02)
    provides: "SC2 sibling RealStack file establishing the docker shell-out (System.Diagnostics.Process) + factory-clone conventions"
provides:
  - "SC3PauseResumeOutageE2ETests.cs — the authored RealStack proof of BIT-gate global pause-all/resume-all across a TRUE transient L2 outage (docker stop/start sk-redis)"
  - "A dedicated non-parallel xUnit collection (RedisOutageSerial, DisableParallelization=true) isolating the outage so sibling RealStack tests are unaffected"
affects: [49-04-close-gate, 49-HUMAN-UAT]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Non-parallel xUnit collection ([CollectionDefinition(DisableParallelization=true)]) for a destructive-infra E2E"
    - "docker stop/start <container> shell-out via System.Diagnostics.Process to induce + heal a true transient L2 outage, with the heal in a finally"
    - "Live read of an out-of-process pause/resume effect via the orchestrator ES seam log (match_phrase on body + service.name term) — TriggerState idiom mirrored, not read in-process"
    - "Negative-proof observation window (no new skp:data:* output during the paused window) spanning at least one cron occurrence"

key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs
  modified: []

key-decisions:
  - "D-01: TRUE transient outage via docker stop sk-redis / docker start sk-redis (not pause/in-redis tricks) so the BIT probe genuinely throws RedisException across the down->up edge"
  - "D-02: isolated in its OWN non-parallel collection (RedisOutageSerial); blocks before returning on docker start -> liveness re-write + post-resume round-trip; heal in a try/finally"
  - "Live read mechanism = orchestrator ES seam log (Global PauseAll / Global ResumeAll); the TriggerState.Paused/Normal idiom is the assertion SHAPE mirrored (the orchestrator owns the scheduler out-of-process)"
  - "ScanExecutionDataKeys swallows RedisException during the outage window so the paused-window negative reads correctly when sk-redis is down"

patterns-established:
  - "Destructive-infra RealStack E2E isolated in a DisableParallelization collection with a finally-heal so the close-gate suite is never left degraded"
  - "ES seam-log poll as the out-of-process live read for a pause/resume effect the test process cannot observe in-memory"

requirements-completed: []  # TEST-02 stays UNTICKED per D-03 — definition-of-done is AUTHORED + hermetically green; the live N×GREEN run is operator-gated (49-HUMAN-UAT.md)

# Metrics
duration: ~30min
completed: 2026-06-09
---

# Phase 49 Plan 03: SC3 Pause/Resume-Outage RealStack E2E Proof Summary

**Authored SC3PauseResumeOutageE2ETests — a RealStack E2E (in its own non-parallel collection) proving the BIT-gate global pause-all/resume-all across a true transient L2 outage induced by `docker stop sk-redis` / `docker start sk-redis`, asserted via the orchestrator ES seam logs, healed in a finally; 0-warning Release+Debug, hermetic suite 507/507 GREEN.**

## Performance

- **Duration:** ~30 min
- **Started:** 2026-06-09T10:39:00Z
- **Completed:** 2026-06-09T11:08:37Z
- **Tasks:** 1
- **Files created:** 1 (no production code changed)

## Accomplishments
- Authored `SC3PauseResumeOutageE2ETests.cs` (589 lines) in a dedicated **non-parallel** xUnit collection (`RedisOutageSerial`, `DisableParallelization = true`) so stopping `sk-redis` cannot destabilize sibling RealStack tests (D-02).
- Drives a **true transient L2 outage** via `docker stop sk-redis` … `docker start sk-redis` (D-01) shelled out through `System.Diagnostics.Process` — the BIT probe throws `RedisException` (gate closes → `Publish(PauseAll)`), then succeeds on start (gate opens → `Publish(ResumeAll)` → per-job resume under the `TriggerState == Paused` guard).
- Asserts **pause** and **resume** via the orchestrator **ES seam logs** (`Global PauseAll` / `Global ResumeAll`) — the live read mechanism for an out-of-process scheduler — mirroring the `TriggerState.Paused`/`Normal` idiom from `ResumeAllConsumerTests`. Adds an additional observable negative proof: **no new `skp:data:*` output key appears during the paused window** (spanning ≥ one `* * * * *` occurrence).
- Proves **idempotency** (re-observing the resume seam is harmless — the per-job `TriggerState == Paused` guard makes a duplicate resume a no-op) and **blocks before returning** on steady-state re-establishment: `PollForHealthyLivenessAsync` (liveness `skp:{procId:D}` re-written) **and** a post-resume round-trip output key landing (gate re-opened).
- **Heals the outage in a `finally`** (`docker start sk-redis`) so even an assertion thrown mid-outage never leaves redis stopped for the sibling tests / the close gate (T-49-06). Net-zero L2 teardown identical to SC1.

## Task Commits

Each task was committed atomically:

1. **Task 1: Author SC3PauseResumeOutageE2ETests in a dedicated non-parallel collection with the docker-stop/start outage drive** — `55cef6c` (test)

**Plan metadata:** (this SUMMARY + STATE/ROADMAP) committed separately.

## Files Created/Modified
- `tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs` — the SC3 RealStack proof: non-parallel `RedisOutageSerial` collection; baseline round-trip → `docker stop sk-redis` → PauseAll ES seam + paused-window negative → `docker start sk-redis` → ResumeAll ES seam → idempotency re-observe → blocking liveness re-write + post-resume round-trip; finally-heal; cloned `RealStackWebAppFactory` + `PollForHealthyLivenessAsync` from SC1.

## Decisions Made
- **Seam-log query shape:** used `match_phrase` on `body` + a `term` on `resource.attributes.service.name = orchestrator` (the `PauseAllConsumer`/`ResumeAllConsumer` seam logs are structured templates `"Global PauseAll CorrelationId={CorrelationId}"` / `"Global ResumeAll ..."`), then re-confirm the distinct seam text on the returned hit in C# — the same precedent as SC1's orchestrator-advance proof.
- **`ScanExecutionDataKeys` tolerates the outage:** wrapped the SCAN in a `catch (RedisException)` so that while `sk-redis` is stopped the scan returns the partial/empty set (correct: no NEW keys during the outage). The positive output polls only run when redis is up, so this never masks a real signal.
- **Generous straddling budgets:** `OutageSettleMs` (20s) lets the BIT loop tick across `Probe:DelaySeconds` to observe each edge before asserting; `PausedQuietWindowMs` (90s) spans > one `* * * * *` cron occurrence so a still-firing cron would have minted a key; ES polls at 150s for otel→ES ingest + cadence latency.
- **TEST-02 stays UNTICKED (D-03):** definition-of-done is AUTHORED + hermetically green; the live outage run literally `docker stop sk-redis` is operator-gated (the rebuilt v4 stack is not up) and is tracked in `49-HUMAN-UAT.md`.

## Deviations from Plan

None - plan executed exactly as written. No production code changed; no auto-fixes required (Rules 1-3 not triggered); no architectural decisions (Rule 4 not triggered).

## Issues Encountered
- The Microsoft.Testing.Platform hermetic run emits heavy background `RabbitMQ`/`Redis` soft-dependency connection-failure log noise (the live stack is intentionally not up). Resolved by capturing the run to a file and reading the structured `total: 507 / failed: 0 / succeeded: 507 / skipped: 0` summary + exit code 0 rather than scrolling the inline stack-trace noise. This is expected hermetic-run behavior, not a test failure.

## Acceptance Criteria — Verified

- File exists: `tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs` (589 lines) — PASS
- `[CollectionDefinition("RedisOutageSerial", DisableParallelization = true)]` => 1 — PASS
- `[Collection("RedisOutageSerial")]` => 1; `[Collection("Observability")]` => 0 — PASS
- `[Trait("Phase", "49")]` => 1; `[Trait("Category", "RealStack")]` => 1 — PASS
- `docker stop sk-redis` / `docker start sk-redis` present (literal in doc + `DockerAsync(..,"stop"/"start", RedisContainer="sk-redis")`) — PASS
- `start sk-redis` heal inside a `finally` block (line 245 within the `finally` at line 239) — PASS
- `PollForHealthyLivenessAsync` present (blocking-teardown liveness re-write) — PASS
- `Global PauseAll` + `Global ResumeAll` ES seam asserts present — PASS
- `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` => 0 Warning / 0 Error — PASS
- `dotnet build ... -c Debug` => 0 Warning / 0 Error — PASS
- `dotnet run --project tests/BaseApi.Tests -- --filter-not-trait Category=RealStack` => **0 failed** (507/507 succeeded; the new RealStack fact EXCLUDED) — PASS
- File BOM-less UTF-8, no mojibake (BOM=False, mojibake=0); NO production-code file changed — PASS

## Threat Surface
No new threat surface beyond the plan's `<threat_model>` (T-49-06 mitigate — intentional transient outage healed in a finally + non-parallel isolation + blocking steady-state teardown; T-49-07 accept — local docker shell-out under the developer's existing context). No new network endpoints, auth paths, or schema changes.

## Known Stubs
None — the file is a complete authored E2E; its only "deferred" aspect is the operator-gated live run (D-03), which is by-design and tracked in `49-HUMAN-UAT.md`, not a stub.

## User Setup Required
None - no external service configuration required. The live run is operator-gated (rebuilt v4 stack + `docker stop sk-redis`), tracked in `49-HUMAN-UAT.md`.

## Next Phase Readiness
- SC3 is the third of the three sibling RealStack E2E proofs (SC1/SC2/SC3). With SC3 authored + hermetically green, the close-gate plan (49-04: `phase-49-close.ps1` + `49-HUMAN-UAT.md`) can proceed — the close gate runs all RealStack facts (SC3 serialized via its non-parallel collection) against the operator-gated live stack.
- **Blocker (by design):** TEST-02 ticks only on the operator's GREEN live N×GREEN run against the rebuilt v4 stack (the RealStack facts, incl. the `docker stop sk-redis` outage, cannot run in this hermetic environment).

## Self-Check: PASSED

- FOUND: `tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs`
- FOUND: `.planning/phases/49-live-proof-close-gate/49-03-SUMMARY.md`
- FOUND commit: `55cef6c` (test(49-03): author SC3 RealStack pause-resume-outage E2E proof)

---
*Phase: 49-live-proof-close-gate*
*Completed: 2026-06-09*
