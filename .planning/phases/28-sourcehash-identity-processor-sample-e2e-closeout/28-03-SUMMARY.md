---
phase: 28-sourcehash-identity-processor-sample-e2e-closeout
plan: 03
subsystem: testing
tags: [e2e, realstack, sourcehash, identity, liveness-gate, round-trip, processor-sample, otel-es]

# Dependency graph
requires:
  - phase: 28-sourcehash-identity-processor-sample-e2e-closeout
    plan: 01
    provides: "Processor.Sample concrete + SourceHash.targets embedding the reproducible 64-hex via reflection-read assembly metadata"
  - phase: 28-sourcehash-identity-processor-sample-e2e-closeout
    plan: 02
    provides: "processor-sample Dockerfile + compose tier; PROVEN cross-OS SourceHash reproducibility (host == Docker), the property this E2E depends on"
provides:
  - "tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs — the only real-stack proof of the whole milestone: live orchestrator -> Processor.Sample -> orchestrator round-trip + truthful liveness-gated Start, identity closed on the GENUINE embedded SourceHash"
affects: [28-04-closeout]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Real-stack E2E REUSES RealStackWebAppFactory WHOLESALE (env-var-in-ctor host overrides RMQ 5673 / Redis 6380 / Postgres 5433 / otel 4317 + L2KeysToCleanup / ParentIndexMembersToSrem net-zero teardown) but OMITS the synthetic SeedHostProcessorLive seed (Pitfall 3 / D-07) — polls the REAL container's skp:{id:D} heartbeat instead"
    - "Genuine embedded-hash identity (D-08): typeof(SampleProcessor).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == \"SourceHash\").Value — reflected, never RandomSha256Hex, never recomputed"
    - "GET-or-create idempotency on /api/v1/processors/by-source-hash/{hash}: a FIXED genuine hash collides on the unique uq_processor_source_hash constraint across runs; resolve the existing row (the one the live container already heartbeats against) and reuse its id, create only on a fresh DB"
    - "Round-trip needs a fired dispatch: the workflow is seeded WITH a '* * * * *' cron so the orchestrator's self-rescheduling one-shot Quartz job actually fires (a null-cron workflow is a business-skip in WorkflowLifecycle.HydrateAndScheduleAsync and would never dispatch)"
    - "Two-clause SC#4 assertion: (a) a fresh skp:data:* output key appears post-Start via host-Redis SCAN of the execution-data family (server-minted entryId is unknowable a priori); (b) the orchestrator-advance log 'Start reload for WorkflowId={wfId}' read back from Elasticsearch via the proven otel->ES PollEsForLog precedent"

key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs
  modified: []

key-decisions:
  - "The E2E proves the truthful liveness gate (SC#4 / D-07): it does NOT seed skp:{procId:D}; it POLLS host Redis for the REAL processor-sample container's fresh Healthy heartbeat (written only after the container resolves identity, binds queue:{id:D}, and MarkHealthy) before POSTing Start. A false-green with the container stopped is therefore impossible — the grep acceptance criterion (no SeedHostProcessorLive in the file) enforces the omission."
  - "Identity closes on the GENUINE embedded SourceHash (D-08): the E2E reflects the host-built Processor.Sample.dll hash and registers it as the DB row; the live container runs the Linux-Docker-built hash. Plan 02 PROVED these are byte-identical, so the container resolves THIS procId by GetProcessorBySourceHash(hash) and the identity loop closes. No synthetic/random hash, no recompute."
  - "Rule 1 fix — GET-or-create on by-source-hash. A blind POST of the FIXED genuine hash violates uq_processor_source_hash (23505) on every run after the first (the row persists in host Postgres and is exactly the row the live container heartbeats against). The seed now resolves the existing row by hash and reuses its id; it creates only when no row exists yet. This is correctness (idempotent re-runs against a stable identity), not a plan deviation in intent."
  - "Rule 3 fix — seed the workflow WITH a '* * * * *' cron. The verbatim CorrelationPropagationE2ETests SeedWorkflowAsync passes CronExpression: null, but a null-cron workflow is a business-skip in HydrateAndScheduleAsync (it cannot be scheduled) — the orchestrator would never fire the dispatch and no round-trip output would ever land. A minute-granularity cron makes the one-shot Quartz job fire so the live round-trip actually runs."
  - "Rule 3 fix — rebuild the stale compose containers before the live verify. The running orchestrator/baseapi-service images predated the Phase 23/24 Start-reload refactor (the orchestrator still logged the OLD 'Scheduler job start (seam)' and never fired the cron dispatch). docker compose up -d --build orchestrator baseapi-service (and a first build of the new processor-sample service) was required so the live stack runs current code; only then does the round-trip complete and the 'Start reload for WorkflowId=' advance log emit."

requirements-completed: [TEST-01]

# Metrics
duration: ~50min
completed: 2026-06-02
---

# Phase 28 Plan 03: Real-Stack Sample Round-Trip + Truthful Liveness-Gated Start Summary

**`SampleRoundTripE2ETests` is the milestone's single end-to-end proof against real containers: it registers the Processor DB row with the GENUINE reflected embedded SourceHash, OMITS the synthetic liveness seed and instead polls the live processor-sample container's real `skp:{id:D}` Healthy heartbeat, drives `POST /orchestration/start` to 204 (the liveness gate passes truthfully), then proves the round-trip on two clauses — a fresh `skp:data:*` output key lands in L2 and the orchestrator's `Start reload for WorkflowId=` advance log surfaces in Elasticsearch via the otel pipeline — all with net-zero teardown. It passes live in ~45s; the full hermetic suite (392/0) and the zero-warning Release build confirm no regression.**

## Performance

- **Duration:** ~50 min (most of it live-stack rebuild + multi-minute real-stack test runs + diagnosing two blocking issues)
- **Tasks:** 2 (Task 1 auto — the E2E; Task 2 auto — the per-wave merge gate)
- **Files:** 1 created (the E2E test); 0 source files modified

## Accomplishments

- **`tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs`** — a `[Trait("Category","E2E")] [Trait("Category","RealStack")] [Collection("Observability")]` real-stack E2E that:
  - **Reuses `RealStackWebAppFactory` wholesale** (the env-var-in-ctor host overrides + the `L2KeysToCleanup` / `ParentIndexMembersToSrem` net-zero teardown), MINUS the synthetic `SeedHostProcessorLive` seed.
  - **Reads the GENUINE embedded hash (D-08)** off the built `Processor.Sample.dll` via `GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "SourceHash")` — the same way the runtime reader and `SourceHashEmbedFacts` read it. No `RandomSha256Hex`, no `SHA256`/`ComputeHash` (grep-enforced).
  - **Registers the Processor DB row** with that genuine hash + null schema Ids (D-05) via CRUD, GET-or-create so the fixed hash is idempotent across runs; then seeds Step -> Workflow (with a `* * * * *` cron so the dispatch fires).
  - **Polls the REAL container's `skp:{procId:D}` Healthy heartbeat (Pitfall 3 / D-07)** with a 90s budget + freshness check before driving Start — so the liveness gate passes ONLY because the live container is genuinely heartbeating.
  - **Drives `POST /api/v1/orchestration/start`** and asserts `204 NoContent`.
  - **Asserts the round-trip on two clauses (SC#4):** (a) polls host Redis (120s) for a NEW `skp:data:*` execution-data key that appeared after Start (the processor consumed the dispatch, ran `ProcessAsync`, wrote output); (b) polls Elasticsearch (120s) for the orchestrator's `Start reload for WorkflowId={wfId}` seam log (term on the seeded WorkflowId, scoped to the orchestrator service) via the proven `ElasticsearchTestClient.PollEsForLog` otel->ES precedent.
  - **Net-zero teardown (Pitfall 4):** registers the run's new `skp:data:*` key + the L2 root/step keys into `L2KeysToCleanup` (drained in `DisposeAsync`) and SREMs the parent-index member; LEAVES the steady-state `skp:{procId:D}` liveness key (the live container keeps refreshing it — it is in both close-gate snapshots). No `FLUSHDB`.

## Live Verification (Task 1)

```
dotnet test ... --filter-class "BaseApi.Tests.Orchestrator.SampleRoundTripE2ETests"
  Passed! - Failed: 0, Passed: 1, Total: 1, Duration: 44s 890ms
```

Post-run `redis-cli --scan 'skp:*'` returned ONLY `skp:4315324c-...` (the steady-state liveness key) — net-zero held, no leaked `skp:data:*`.

## Per-Wave Merge Gate (Task 2)

- **Full hermetic suite** (`--filter-not-trait "Category=RealStack"`, Release): **Passed 392 / Failed 0** (the 2 RealStack E2Es correctly excluded from 394) — no regression from the E2E addition.
- **Zero-warning Release build** (`dotnet build SK_P.sln -c Release`): **0 Warning(s) / 0 Error(s)** — the new test file introduced no TreatWarningsAsErrors break.

## Task Commits

1. **Task 1: SampleRoundTripE2ETests — genuine-hash registration, no liveness seed, poll real heartbeat, Start, assert round-trip, net-zero teardown** — `9320128` (test). Verified: builds 0/0; passes live (44.9s); redis net-zero confirmed.
2. **Task 2: full hermetic suite + Release regression gate** — no new code (gate-only). Hermetic 392/0; Release 0 Warning / 0 Error.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] GET-or-create on the Processor by-source-hash to make the genuine-hash registration idempotent**
- **Found during:** Task 1 (first live run)
- **Issue:** The plan's seed adapts `SeedProcessorAsync` to POST the genuine hash. But the genuine embedded hash is FIXED and the `processors` table has a unique `uq_processor_source_hash` constraint that persists in host Postgres across runs — so a blind POST throws `23505 duplicate key` on every run after the first (the row is already there, and it is exactly the row the live container heartbeats against).
- **Fix:** `SeedProcessorAsync` first GETs `/api/v1/processors/by-source-hash/{hash}`; on 200 it reuses the existing row's id, on 404 it POSTs a create. Idempotent against the stable identity.
- **Files modified:** `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs`
- **Commit:** `9320128`

**2. [Rule 3 - Blocking] Seed the workflow WITH a cron so the orchestrator fires the dispatch**
- **Found during:** Task 1 (second live run — liveness passed, Start 204, but no `skp:data:*` ever appeared)
- **Issue:** The verbatim `SeedWorkflowAsync` passes `CronExpression: null`. A null-cron workflow is a business-skip in `WorkflowLifecycle.HydrateAndScheduleAsync` ("has no cron — skipping hydration") — it is never scheduled, so `WorkflowFireJob` never fires and no dispatch is sent. The round-trip cannot complete.
- **Fix:** Seed the workflow with `* * * * *` (every-minute, 5-field Cronos Standard) so the one-shot Quartz job fires at the next minute and dispatches. Poll budgets widened to cover the up-to-60s fire + round-trip + ingest.
- **Files modified:** `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs`
- **Commit:** `9320128`

**3. [Rule 3 - Blocking] Rebuild the stale compose containers before the live verify (environment, not a code change)**
- **Found during:** Task 1 (the no-`skp:data:*` run)
- **Issue:** The running `sk-orchestrator` / `sk_p-baseapi-service-1` images were ~2h old and predated the Phase 23/24 Start-reload refactor — the orchestrator still logged the OLD `Scheduler job start (seam)` and did not fire the cron dispatch; the new `processor-sample` service was not yet running at all.
- **Fix:** `docker compose up -d --build processor-sample` (first build of the new tier) then `docker compose up -d --build orchestrator baseapi-service` so the live stack runs current source. After this the round-trip completes and the `Start reload for WorkflowId=` advance log emits. (No repo files changed — operational prerequisite.)
- **Commit:** n/a (environment)

## Threat Surface

- **T-28-08** (synthetic liveness seed masking the real gate) — mitigated: `SeedHostProcessorLive` is OMITTED (grep-enforced); the test polls the REAL container's heartbeat, so a false-green with the container stopped is impossible.
- **T-28-09** (forged/random SourceHash) — mitigated: the GENUINE reflected embedded hash is registered (no `RandomSha256Hex`, no recompute — grep-enforced); the DB `^[a-f0-9]{64}$` validator + `uq_processor_source_hash` gate it.
- **T-28-10** (leaked skp:data:* breaking the gate SHA) — mitigated: net-zero teardown drains the run's `skp:data:*` key via `L2KeysToCleanup`; the container's short `ExecutionDataTtl: 5` also self-expires it; the steady-state liveness key is left in both snapshots.
- No new threat surface beyond the plan's `<threat_model>`.

## Next Phase Readiness

- TEST-01 is satisfied — the live round-trip + truthful liveness-gated Start are proven end-to-end. Phase 28 = 3/4 plans. Plan 04 (the phase-28 close gate: 3-consecutive-GREEN + triple-SHA BEFORE=AFTER, scan-clean teardown covering the new liveness + execution-data key families) is unblocked. The compose stack (incl. the rebuilt orchestrator/baseapi + the live processor-sample) is up and current, and the steady-state `skp:{procId:D}` liveness key + `{procId:D}` dispatch queue are stable across runs — the precondition the close gate's BEFORE==AFTER snapshots depend on.

---
*Phase: 28-sourcehash-identity-processor-sample-e2e-closeout*
*Completed: 2026-06-02*

## Self-Check: PASSED

Created file `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` exists on disk; SUMMARY `28-03-SUMMARY.md` exists. Task 1 commit `9320128` present in git history. No source files were modified (Task 2 is gate-only). Grep guards verified: the file contains `GetCustomAttributes<...AssemblyMetadataAttribute>`, `L2ProjectionKeys.Processor`, `skp:data`, `orchestration/start`, `NoContent` and contains ZERO occurrences of `RandomSha256Hex` / `ComputeHash` / `SHA256` / `SeedHostProcessorLive`. Live E2E passed (44.9s); hermetic suite 392/0; Release build 0 Warning / 0 Error.
