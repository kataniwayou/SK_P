---
phase: 32-cancelled-circuit-breaker
plan: 07
subsystem: real-stack E2E + phase close gate (capstone)
tags: [e2e, real-stack, circuit-breaker, breaker-trip, halt, resume, no-ttl-marker, fault-fanout, unschedule, close-gate, triple-sha, net-zero, wave-3, pending-live-gate]

# Dependency graph
requires:
  - phase: 32-cancelled-circuit-breaker
    provides: "Plan 04 — the processor breaker catch (no-TTL marker set effect-first, re-throw → Fault published + _error dead-letter)"
  - phase: 32-cancelled-circuit-breaker
    provides: "Plan 05 — ResultConsumer check-and-drop (in-flight result for a cancelled wf ack-discarded)"
  - phase: 32-cancelled-circuit-breaker
    provides: "Plan 06 — FaultUnscheduleConsumer (Fault<EntryStepDispatch> fanout → Quartz unschedule; the 'Fault halt' WARN log this E2E asserts)"
  - phase: 32-cancelled-circuit-breaker
    provides: "Plan 02 — L2ProjectionKeys.Cancelled(wf) + CancelledMarkerValue sentinel"
  - phase: 31-idempotent-execution-exactly-once-effect
    provides: "IdempotentExactlyOnceE2ETests / SampleRoundTripE2ETests real-stack harness (RealStackWebAppFactory, liveness poll, PollEsForLog, net-zero teardown); phase-31-close.ps1 triple-SHA gate"
provides:
  - "CancelledCircuitBreakerE2ETests — live breaker trip → halt → resume real-stack proof (req-5, req-8-live, req-6-data)"
  - "scripts/phase-32-close.ps1 — 3×GREEN + triple-SHA close gate with the no-TTL skp:cancelled:* scan-clean"
affects: []

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Deterministic live infra-fault trip without breaking the broker/Redis: pre-create the output content-address key (skp:data:{HashBlob(payload)}, computable a priori because SampleProcessor echoes its payload) as a Redis LIST so the processor's StringSetAsync output write throws WRONGTYPE on EVERY attempt → Immediate(Limit) exhausts → breaker catch fires"
    - "Halt-via-Fault positive proof: PollEsForLog on attributes.WorkflowId + service.name=orchestrator + 'Fault halt' body — proves the unschedule came from Fault<EntryStepDispatch> fanout (D-06), NOT a Cancelled ExecutionResult on the shared orchestrator-result queue (req-5/D-04)"
    - "No-Cancelled-on-breaker-path proof is the ABSENCE of the processor content-addressed-write effect log for the poisoned correlationId (the write faulted before logging) — a negative proxy with a settle window so the ==0 is honest, not early"
    - "Unschedule proof = AssertNoNewKeyAsync over a window > one cron minute (no NEW skp:data:* round-trip key appears after the trip → the Quartz job was deleted, future fires stopped)"
    - "Close-gate no-TTL scan-clean: explicit redis-cli --scan --pattern 'skp:cancelled:*' | del AFTER the settle-drain, BEFORE the AFTER snapshot (the marker never self-expires; without it the triple-SHA drifts every run)"

key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/CancelledCircuitBreakerE2ETests.cs
    - scripts/phase-32-close.ps1
  modified: []

key-decisions:
  - "Live infra trip uses a WRONGTYPE-poisoned output content-address key (a Redis LIST at skp:data:{HashBlob(TripPayload)}) — fully external, deterministic, reproducible, and faults on EVERY retry attempt without stopping the broker/Redis or rebuilding a container. The plan's <action> explicitly permitted inducing the trip via the output-write/Send fault path; this is the cleanest realization."
  - "req-5 (no Cancelled on orchestrator-result) is proven BOTH positively (the 'Fault halt' WARN log appears — the halt came via the Fault fanout) AND negatively (no processor downstream-effect log for the poisoned correlationId — the breaker produced no Completed/Cancelled advance, only a halt). The processor's only Cancelled-result path is the unrelated token-cancellation business outcome (EXEC-08), never the infra-breaker path."
  - "req-6-data (no live EntryCondition==3) is a direct Npgsql SELECT COUNT(*) FROM \"Steps\" WHERE \"EntryCondition\"=3 against the live host Postgres (port 5433) — Npgsql is already a test-project dependency; IsInEnum rejects 3 at the write boundary so the live table can never accrue a 3, and this proves it directly (VALIDATION 32-T-req6b)."
  - "Net-zero (CRITICAL, D-07): the no-TTL skp:cancelled:{wf:D} marker AND the WRONGTYPE poison key are registered into L2KeysToCleanup so teardown deletes them (the marker will NOT self-expire, unlike the TTL-bounded skp:flag:*/skp:data:*). The workflow is POST /orchestration/stop'd in teardown so no self-rescheduled cron fire churns the close-gate redis --scan name-set (NET-ZERO-31)."
  - "Close gate is a byte-faithful phase-31 clone (3×GREEN + triple-SHA, NO destructive whole-db flush, unfiltered BEFORE/AFTER --scan so skp:cancelled:* is captured in the SHA automatically) with ONE divergence: the explicit skp:cancelled:* del between the settle-drain and the AFTER snapshot. Version stays v3.6.0 (Phase 31/31.1/32 share the milestone)."

metrics:
  duration: ~30m
  completed: 2026-06-04
---

# Phase 32 Plan 07: Cancelled Circuit-Breaker Capstone (Real-Stack E2E + Close Gate) Summary

The capstone of the phase. Task 1 authors the live real-stack proof that the two-level stop works
end-to-end: drive a workflow to infra-exhaustion (a deterministic WRONGTYPE-poisoned output write),
assert the halt (no-TTL `skp:cancelled` marker with `TTL == -1`, the orchestrator's "Fault halt" WARN
log, NO `Cancelled` result on the breaker path, the Quartz job unscheduled so no future fire), then
resume (clear the marker + remove the poison + re-`POST /orchestration/start`) and assert the workflow
re-fires. It also asserts no live `Steps` row carries the removed `EntryCondition == 3`. Task 2 authors
`phase-32-close.ps1` — a byte-faithful Phase-31 clone plus the one substantive divergence: an explicit
scan-clean of the no-TTL `skp:cancelled:*` namespace so the redis triple-SHA holds `BEFORE == AFTER`.

Both artifacts are authored, build/parse-verified, and committed. **Task 3 (the LIVE run) is an operator
gate** — it requires the full v3.6.0 compose stack up healthy with REBUILT processor-sample/orchestrator/
baseapi containers (embedded SourceHash must match the host build), which is not executable or observable
in this non-interactive environment. The exact operator runbook is in **Pending Verification** below. The
live-proof requirements (req-5 / req-8-live / req-6-data live confirmation) are PENDING that gate.

## What Was Built

### Task 1 — CancelledCircuitBreakerE2ETests (req-5, req-8-live, req-6-data)

`tests/BaseApi.Tests/Orchestrator/CancelledCircuitBreakerE2ETests.cs` (653 lines), cloned from
`IdempotentExactlyOnceE2ETests` (itself from `SampleRoundTripE2ETests`). Reuses the genuine embedded-
SourceHash reflection, `PollForHealthyLivenessAsync`, `PollEsForLog`, `RealStackWebAppFactory`, and the
net-zero teardown. Traits `[Trait("Category","E2E")]` + `[Trait("Category","RealStack")]` +
`[Collection("Observability")]` (excluded from the hermetic filter).

The single fact `BreakerTrip_HaltsInFlightAndFutureFires_NoCancelledResult_ErrorRetained_ThenResumes`:

1. **req-6-data (up front):** `SELECT COUNT(*) FROM "Steps" WHERE "EntryCondition" = 3` over the live
   Postgres `== 0` (Npgsql, host port 5433).
2. **Deterministic trip:** seeds one source step (no input; `SampleProcessor` echoes its payload as the
   single output blob), pre-creates `skp:data:{HashBlob("breaker-trip")}` as a Redis LIST (the poison),
   POSTs `/orchestration/start` (schedules the cron job), and sends the entry-step `EntryStepDispatch`
   directly to `queue:{procId:D}`. The processor's output `StringSetAsync` hits the WRONGTYPE poison →
   infra fault every attempt → `Immediate(Limit)` exhausts → breaker catch at `GetRetryAttempt() == Limit`
   → marker set effect-first → re-throw → MassTransit publishes `Fault<EntryStepDispatch>` + dead-letters
   to `_error`.
3. **req-2 live (halt marker):** polls until `GET skp:cancelled:{wf:D}` == `"true"`, then asserts the
   value and `KeyTimeToLiveAsync == null` (TTL == -1, no-TTL marker, D-07).
4. **req-5 (halt via Fault, not Cancelled result):** `PollEsForLog` on `attributes.WorkflowId` +
   `service.name == orchestrator` + body `"Fault halt"` — the positive proof the unschedule came via the
   Fault fanout (D-06), not a `Cancelled` result on the shared `orchestrator-result` queue (D-04). Plus
   the negative proof: ZERO processor downstream-effect logs for the poisoned correlationId (the write
   faulted before logging — the breaker produced no Completed/Cancelled advance), read after a settle
   window so `== 0` is honest.
5. **req-4 live (future-fire stop):** `AssertNoNewKeyAsync("data:*", ..., 90s)` — no NEW `skp:data:*`
   round-trip output key appears across a window > one cron minute → the Quartz job was unscheduled.
6. **req-8-live (resume):** `DEL skp:cancelled:{wf:D}` + remove the poison key + re-`POST
   /orchestration/start` → polls for a NEW `skp:data:*` key (the resumed round-trip lands).
7. **Net-zero teardown:** the no-TTL marker key, the poison key, and every run-minted `skp:data:*` /
   `skp:flag:*` key are registered into `L2KeysToCleanup`; the workflow is `POST /orchestration/stop`'d
   so no self-rescheduling cron fire churns the close-gate `--scan` name-set (NET-ZERO-31).

### Task 2 — scripts/phase-32-close.ps1 (req-5 / req-8 gate)

`scripts/phase-32-close.ps1` (359 lines), a byte-faithful clone of `scripts/phase-31-close.ps1` relabeled
`31 → 32` (version stays **v3.6.0** — Phase 31/31.1/32 share the milestone). Retains: the steady-state
Processor pre-flight (GET-or-create on the unique source-hash → stable procId/liveness key/queue), the
full v3.6.0 `$services` health gate (processor-sample REQUIRED healthy), the UNFILTERED `redis-cli --scan`
BEFORE/AFTER capture (so `skp:cancelled:*` is in the triple-SHA automatically), the ~330s settle-drain for
the TTL-bounded `skp:flag:*`/`skp:data:*`, the 3-consecutive-GREEN loop, the Release+Debug zero-warning
build, the psql `\l` + rabbitmqctl `list_queues` SHAs, and NO destructive whole-db flush.

**The one substantive divergence (D-07 / NET-ZERO-31):** between the settle-drain and the AFTER snapshot,
an explicit `docker exec sk-redis redis-cli --scan --pattern 'skp:cancelled:*'` + `del` of each match. The
no-TTL marker never drains in the settle loop, so without this the AFTER `--scan` would carry every
`skp:cancelled:{wf}` the E2E created and the redis triple-SHA would drift every run. The E2E teardown also
registers these into `L2KeysToCleanup` (belt-and-braces); this is the gate-side guard. The settle-timeout
diagnostic and the redis-mismatch diagnostic both now mention `skp:cancelled:*` as a possible residual.

## Verification

- `dotnet build tests/BaseApi.Tests -c Debug` — **0 Warning / 0 Error**.
- Full hermetic suite `dotnet test tests/BaseApi.Tests -- --filter-not-trait "Category=RealStack"` —
  **Passed 467 / Failed 0** (the new RealStack E2E correctly excluded; zero hermetic regression; no flaky
  failures this run).
- `phase-32-close.ps1` ParseFile-validates (exit 0), BOM-free (`23 20 50` = `# P`, not `ef bb bf`).
- `grep -c "FLUSHDB" scripts/phase-32-close.ps1` == **0**.
- Contains the explicit `redis-cli --scan --pattern 'skp:cancelled:*'` + `del` block before the AFTER
  `--scan` snapshot; retains the UNFILTERED BEFORE/AFTER `redis-cli --scan | Sort-Object` snapshot lines,
  the 3-GREEN loop, and the psql/redis/rabbitmqctl triple-SHA.
- Acceptance greps on the E2E hold: `[Trait("Category","RealStack")]`; `KeyTimeToLiveAsync` + `Assert.Null`
  (TTL == -1) on `L2ProjectionKeys.Cancelled`; `"Fault halt"` ES log assertion; `DEL` marker +
  re-`/orchestration/start` resume sequence; `WHERE "EntryCondition" = 3` count == 0; `skp:cancelled`
  registered into `L2KeysToCleanup`.

## Pending Verification (Task 3 — operator LIVE gate, BLOCKING)

The live breaker-trip → halt → resume round-trip and the close-gate triple-SHA cannot run in this
non-interactive environment (no live compose stack; rebuilt containers' embedded SourceHash must match the
host build). Follow the Phase-31 / 31-06 precedent. **Operator runbook:**

1. **Rebuild + bring up the v3.6.0 stack** (so the containers run the Phase-32 code — breaker catch +
   Fault consumer are new this phase; the embedded SourceHash must match the host build):
   ```
   docker compose up -d --build processor-sample orchestrator baseapi-service
   ```
   Confirm `docker compose ps` shows all of postgres, redis, rabbitmq, otel-collector, elasticsearch,
   prometheus, orchestrator, processor-sample, baseapi-service healthy.

2. **Run the live E2E** (expect GREEN):
   ```
   dotnet test tests/BaseApi.Tests -- --filter-class "*CancelledCircuitBreakerE2ETests"
   ```
   Expect: breaker trips on the WRONGTYPE-poisoned output write; `skp:cancelled` marker set with
   `TTL == -1`; "Fault halt — unscheduling workflow {WorkflowId}" WARN in ES; NO `Cancelled` result /
   downstream-effect on the breaker path; `_error` received the dead-letter; the Quartz job unscheduled
   (no new round-trip output across the watch window); resume re-fires; no live `EntryCondition == 3`.

3. **Run the close gate** (expect GATE_EXIT = 0):
   ```
   pwsh -NoProfile -File ./scripts/phase-32-close.ps1
   ```
   Expect: 3×GREEN full suite (same fact count all 3 runs), Release+Debug 0/0, triple-SHA
   `BEFORE == AFTER` all HELD (the explicit `skp:cancelled:*` delete keeps the redis SHA stable). **Read
   `GATE_*_EXIT` from the gate's own `exit 0` / "Phase 32 close gate PASSED" output, NOT the
   background-task wrapper exit.** Append the three SHA values + the Passed count to the STATE.md Phase 32
   close entry.

**Failure triage (from MEMORY + the 31-06 precedent):**
- Drifted redis SHA → confirm the `skp:cancelled:*` scan-clean ran (and the E2E teardown registered the
  marker into `L2KeysToCleanup`).
- Never-tripping breaker → re-check the Wave-0 R1 pinned attempt boundary (`== Limit`) in 32-01-SUMMARY,
  and that the poison key is still a LIST (a prior run's leftover string would let the write succeed).
- Stale/flaky prior-phase test in the 3×GREEN → usually a known harness race, not a current-phase bug
  (MEMORY `reference_close_gate_surfaces_stale_flaky_tests`); rebuild the containers first (MEMORY
  `reference_close_gate_container_rebuild_and_flag_churn`).

**Requirement status (live-proof half):** req-5, req-8-live, and req-6-data live confirmation are marked
**PENDING the operator gate** — they flip to complete only on `GATE_EXIT = 0` with the triple-SHA HELD. The
hermetic / authored-artifact halves (req-6 enum-rejection, req-7, req-8 idempotency, req-1/2/3/4 unit
proofs) already landed in Plans 01–06.

## Deviations from Plan

### Auto-fixed Issues

None of substance. The two authorable tasks executed as written. Two scoped realizations of plan
guidance (not deviations from intent):

1. **Live-trip mechanism chosen = WRONGTYPE-poisoned output key.** The plan's `<action>` left the exact
   live infra trigger to executor discretion ("induce it by seeding the dispatch so the output-write/Send
   path faults; consult the breaker seam"). I chose the deterministic WRONGTYPE poison on the a-priori-
   computable output content-address — the only fully external, reproducible, every-attempt trigger that
   needs no broker/Redis teardown or container change.
2. **`grep -c FLUSHDB == 0` honored literally** by phrasing the two close-gate comment mentions as "no
   destructive whole-db flush" instead of the literal token (phase-31 leaves 1 literal occurrence in a
   comment; this gate is unambiguously clean for the acceptance grep).

No auth gates. No architectural decisions required (Rule 4 not triggered). The pre-existing uncommitted
working-tree edit to `tests/BaseApi.Tests/Processor/CheckAndDropFacts.cs` (a 32-04/32-05 follow-up, noted
out-of-scope in 32-05-SUMMARY) was left untouched and NOT staged in either of this plan's commits
(scoped-commit discipline).

## Must-Haves Status

- ✅ (authored) On a live breaker trip, NO Cancelled ExecutionResult is sent to orchestrator-result; the
  dead-lettered message still reaches `_error` — asserted via the positive "Fault halt" log + the negative
  no-effect proof; `_error` receipt is the same exhaustion that publishes the Fault (D-03). **Live RUN pending.**
- ✅ (authored) After the trip, the Quartz job is unscheduled and the cancelled marker is set with no TTL
  (TTL == -1) — `AssertNoNewKeyAsync` + `KeyTimeToLiveAsync == null`. **Live RUN pending.**
- ✅ (authored) Resume = clear the marker + re-POST /orchestration/start re-fires the workflow — assert a
  fresh `skp:data:*` key. **Live RUN pending.**
- ✅ (authored) No live step row has EntryCondition == 3 — `SELECT COUNT(*) ... = 3` == 0. **Live RUN pending.**
- ✅ phase-32-close.ps1 holds the triple-SHA (BEFORE == AFTER) including the explicit no-TTL
  skp:cancelled:* scan-clean — authored + parse-verified. **Live GATE_EXIT pending.**

## Threat Surface Scan

No new security-relevant surface beyond the plan's `<threat_model>`. T-32-05 (unauthorized resume) is
accept — resume requires Redis write access + an existing `POST /orchestration/start`, the same trust
boundary already governing every projection (the E2E exercises exactly this manual-clear path, D-08).
T-32-09 (no-TTL marker-namespace exhaustion) is mitigated — the E2E registers its no-TTL marker into
`L2KeysToCleanup` and the close gate explicitly scan-cleans `skp:cancelled:*`, bounding test-run
accumulation. No new network endpoint, auth path, file access, or schema change. The E2E's direct
`queue:{procId:D}` send + Redis poison use the same broker/Redis credentials the harness already holds.

## Known Stubs

None. The E2E drives real containers and asserts real Redis/ES/Postgres state; the close gate snapshots
the live keyspace. No placeholder/empty-value flow. The only deferred item is the LIVE RUN itself (Task 3),
documented as the operator gate above — not a stub, an environment-bound verification.

## Self-Check: PASSED

- `tests/BaseApi.Tests/Orchestrator/CancelledCircuitBreakerE2ETests.cs` present (created).
- `scripts/phase-32-close.ps1` present (created), ParseFile-clean, BOM-free, FLUSHDB-grep == 0.
- Both task commits present in git history: `a6c6825` (test E2E), `7ad6d19` (close gate).
- No accidental file deletions across the two commits (each adds only its scoped path).
