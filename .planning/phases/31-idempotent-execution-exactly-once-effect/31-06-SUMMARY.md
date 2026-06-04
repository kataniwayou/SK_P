---
phase: 31-idempotent-execution-exactly-once-effect
plan: 06
subsystem: test-e2e
tags: [exactly-once-effect, real-stack-e2e, merge-topology, induced-redelivery, close-gate, triple-sha, req-8]

# Dependency graph
requires:
  - phase: 31-idempotent-execution-exactly-once-effect
    plan: 03
    provides: "processor producer half — content-addressed write + manifest + effect-first flag[dispatch.H] dedup gate (the live behavior the E2E proves drops the duplicate)"
  - phase: 31-idempotent-execution-exactly-once-effect
    plan: 04
    provides: "orchestrator consumer half — inbound flag[m.H] dedup + manifest N x M fan-out + entry-step EntryId; the symmetric round-trip the E2E exercises"
  - phase: 31-idempotent-execution-exactly-once-effect
    plan: 05
    provides: "config-bound Immediate(Limit) retry budget (the retry path an induced redelivery would exercise)"
provides:
  - "IdempotentExactlyOnceE2ETests — real-stack merge-topology (StepA1,StepA2 -> StepB) + induced redelivery (same H sent twice) asserting exactly-once downstream effect per CorrelationId (req-8)"
  - "phase-31-close.ps1 — 3-consecutive-GREEN + triple-SHA BEFORE==AFTER close gate; unfiltered redis --scan covers the new skp:flag:{64hex} + content-addressed skp:data:{64hex} namespaces (D-12)"
affects: []

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Induced duplicate via test-as-sender (D-11): reconstruct the EntryStepDispatch with deterministic EntryId=EntryEntryId(corr,stepId) + H=ComputeH(...), pre-write flag[H]=Pending (symmetric StepDispatcher analog), Send twice to queue:{procId:D}"
    - "Zero-duplicate ES assertion: PollEsForLog (first hit) + a track_total_hits count probe with an ingest-settle window so a leaked duplicate would be counted, not raced past"
    - "Net-zero teardown extended to two namespaces (D-12): scan skp:data:* AND skp:flag:* post-run, register every key absent before Start for deletion"
    - "Close gate snapshots the FULL unfiltered keyspace — the new skp:flag:* lands in the redis SHA automatically; no prefix filter, no FLUSHDB"

key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/IdempotentExactlyOnceE2ETests.cs
    - scripts/phase-31-close.ps1
  modified: []

key-decisions:
  - "Test plays the SENDER role for the induced duplicate (re-Send EntryStepDispatch twice with identical H) rather than capturing the in-flight orchestrator dispatch — the lighter, less-flaky mechanism the CONTEXT chose (D-11) over broker fault injection; mirrors production StepDispatcher's flag[H]=Pending pre-write so the consumer's When.Exists Ack flip arms"
  - "Downstream-effect marker = the processor 'step output written content-addressed' log scoped to attributes.CorrelationId + service.name=processor-sample; the duplicate (same H) is dropped by the effect-first gate so EXACTLY ONE effect appears (the StepB4-x2 inverse)"
  - "Close gate is a byte-faithful clone of phase-29-close.ps1 with labels 29->31 / 3.5.0->3.6.0; the existing unfiltered redis-cli --scan already captures skp:flag:* — no scan widening needed, only documented; the single residual 'phase-29' reference is the provenance comment (clone source), not a phase label"

requirements-completed: []

# Metrics
duration: 9min
completed: 2026-06-04
---

# Phase 31 Plan 06: Real-Stack Exactly-Once-Effect Proof + Close Gate Summary

**The milestone's only end-to-end exactly-once-EFFECT guarantee (req-8): `IdempotentExactlyOnceE2ETests` clones `SampleRoundTripE2ETests` and diverges to a merge topology (StepA1, StepA2 -> StepB via `NextStepIds`) + an induced redelivery (the SAME `EntryStepDispatch` identity sent twice with identical `H`), asserting per `CorrelationId` that the processor downstream-effect log appears EXACTLY once — the live inverse of the StepB4-x2 over-execution bug. `phase-31-close.ps1` is the byte-faithful clone of the prior close gate (labels 29->31, v3.5.0->v3.6.0), whose unfiltered `redis-cli --scan` triple-SHA automatically covers the new `skp:flag:{64hex}` + content-addressed `skp:data:{64hex}` namespaces (D-12). Both compile/parse clean; the LIVE run is the blocking human-verify gate (Task 3).**

## Performance

- **Duration:** ~9 min
- **Started:** 2026-06-04T14:39:56Z
- **Completed (authored tasks):** 2026-06-04T14:49:26Z
- **Tasks:** 2 of 3 authored + verified; Task 3 is the operator-run live gate (pending)
- **Files created:** 2

## Accomplishments

- **Task 1 — `IdempotentExactlyOnceE2ETests` (req-8):** Cloned `SampleRoundTripE2ETests` wholesale (genuine embedded SourceHash reflection off `Processor.Sample.dll`, GET-or-create Processor row, truthful `PollForHealthyLivenessAsync` against the real `skp:{procId:D}` heartbeat, `RealStackWebAppFactory` host-stack overrides, net-zero teardown), then diverged:
  - **Merge topology (Open Q3):** seeds `StepB` first, then `StepA1` + `StepA2` each with `NextStepIds -> [StepB]`; the workflow lists BOTH A-steps as entry steps with a `* * * * *` cron so the orchestrator one-shot Quartz job fires.
  - **Induced duplicate (D-11):** after the live round-trip lands a fresh `skp:data:*` key, reconstructs the entry-step dispatch (`EntryId = MessageIdentity.EntryEntryId(corr, stepA1Id)`, `H = MessageIdentity.ComputeH(corr, wf, stepA1, proc, entryId)`), pre-writes `flag[H]="Pending"` (the symmetric sender analog of `StepDispatcher`), and `Send`s it TWICE to `queue:{procId:D}` via a short-lived `IBusControl`. The two deliveries carry the IDENTICAL `H`; the processor's effect-first `flag[dispatch.H]` gate drops the second.
  - **Zero-duplicate assertion:** `PollEsForLog` confirms the downstream-effect log ("step output written content-addressed") for `dupCorrelationId` exists, then a `track_total_hits` count probe (with an 8s ingest-settle window so a leaked duplicate would be counted, not raced past) asserts the hit count == 1 — the duplicate added none.
  - **Net-zero teardown (D-12):** scans BOTH `skp:data:*` and `skp:flag:*` post-run and registers every key absent before Start into `L2KeysToCleanup` (a generalized `ScanKeys(discriminator)` replaces the single-namespace `ScanExecutionDataKeys`).
  - **Verification:** `dotnet build tests/BaseApi.Tests -c Debug` = 0 Warning / 0 Error; full hermetic suite `--filter-not-trait "Category=RealStack"` = **Passed 441 / Failed 0** (the new test is `Category=RealStack`, correctly excluded — zero regression).

- **Task 2 — `phase-31-close.ps1` (req-8 + D-12):** Byte-faithful clone of `scripts/phase-29-close.ps1`. Updated phase-identifying labels `29 -> 31` and version `v3.5.0 -> v3.6.0`; documented the new `skp:data:{64hex}` (re-typed from Guid) + `skp:flag:{64hex}` (NEW) namespaces in the header. Confirmed: `processor-sample` REQUIRED healthy at pre-flight (stable Processor row via idempotent GET-or-create on the unique source-hash); the `redis-cli --scan` is UNFILTERED (the new flag namespace is in the SHA automatically); NO `FLUSHDB`; the 3-consecutive-GREEN loop + Release+Debug zero-warning build + triple-SHA (psql `\l` + redis `--scan` + rabbitmqctl `list_queues`) unchanged. Added a header note that the v3.6.0 wire contract is NOT backward-compatible (rebuild `processor-sample`).
  - **Verification:** `ParseFile` exit 0 (PARSE OK, no errors); BOM-free; `phase-31`/`Phase 31` labels present; the only residual `phase-29` reference is the provenance comment naming the clone source (matches the phase-29 gate's own reference to phase-22); `processor-sample` in `$services`; triple-SHA + 3-GREEN present; no `FLUSHDB` invocation (the lone `FLUSHDB` token is the "NO FLUSHDB" assertion comment).

## Task Commits

1. **Task 1: IdempotentExactlyOnceE2ETests** — `98bae9f` (test)
2. **Task 2: phase-31-close.ps1** — `2b945f5` (feat)

## Decisions Made

- **Test-as-sender for the induced duplicate.** Rather than intercept the in-flight orchestrator dispatch, the test reconstructs the entry-step `EntryStepDispatch` with the deterministic server-derived identity (`EntryEntryId` + `ComputeH`) and `Send`s it twice. Because `H` is deterministic over the five identity fields (executionId excluded), both deliveries hash identically and the processor's `flag[dispatch.H]` gate collapses the second. The test pre-writes `flag[H]="Pending"` first (the symmetric inbound analog of `StepDispatcher`'s production pre-write) so the consumer's `When.Exists` Pending->Ack flip has a key to flip — without it the gate could never arm.
- **Count probe with a settle window.** The zero-duplicate assertion uses `track_total_hits` + an ingest-settle delay so that, were the dedup gate ever to fail, the leaked second effect would have been ingested and counted (count==2 -> RED). A naive single-hit `NotNull` could pass even on a leak; the count makes the inverse-of-StepB4-x2 explicit.
- **Close gate: clone, do not widen.** The phase-29 gate already snapshots the FULL unfiltered keyspace, so `skp:flag:*` is captured with zero code change — only the header documentation was extended (D-12). Net-zero is enforced by the E2E teardown (Task 1), per the plan's "the gate's job is to SNAPSHOT-and-compare."

## Deviations from Plan

None — both autonomous tasks executed exactly as written. No Rule 1/2/3 auto-fixes were required (the only build error was a mechanical `IBusControl`-not-`IAsyncDisposable` fix in the same task before its first successful build, not a deviation from plan intent).

## Threat Surface

No new security surface (matches the plan's `<threat_model>`). T-31-17 (induced-duplicate re-publish, accept): the test re-Sends a message with the SAME server-derived identity inside the existing live-stack boundary — it exercises the dedup, it does not weaken it. T-31-18 (ES log assertions, mitigate): the E2E queries ES on ids already emitted as scope VALUES under fixed keys; it reads them, introducing no new template interpolation. T-31-19 (per-fire key accumulation, mitigate): the net-zero teardown scan-cleans BOTH `skp:data:*` and `skp:flag:*` (D-12) so keys do not accumulate across gate runs.

## Known Stubs

None. The E2E drives the real content-addressed round-trip end-to-end; the close gate is a complete clone of the proven prior gate.

## Pending Verification (Task 3 — blocking human-verify gate)

The LIVE run requires the full v3.6.0 compose stack up healthy with a **REBUILT** `processor-sample` (the wire contract is not backward-compatible). This environment cannot confirm the stack is running, so the live proof is NOT claimed here (mirrors the 30-04 / 29-05 human-verify discipline). Operator steps:
1. `docker compose up -d --build processor-sample orchestrator baseapi-service`
2. `dotnet test tests/BaseApi.Tests -- --filter-class "*IdempotentExactlyOnceE2ETests"` (expect GREEN — exactly-once downstream effect under the induced redelivery)
3. `pwsh -NoProfile -File ./scripts/phase-31-close.ps1` (expect GATE_EXIT=0 — 3xGREEN + triple-SHA BEFORE==AFTER, incl. the new skp:flag:* / skp:data:* in the redis SHA)

Read `GATE_*_EXIT` from the gate output (project memory: the bg-task "exit 0" is the wrapper's, not the gate's). A first-live-run RED is often a stale/flaky prior-phase ES assertion or dirty-BEFORE redis SHA, not a current-phase bug.

## Self-Check: PASSED
- FOUND: tests/BaseApi.Tests/Orchestrator/IdempotentExactlyOnceE2ETests.cs (created)
- FOUND: scripts/phase-31-close.ps1 (created, BOM-free, ParseFile exit 0)
- FOUND commit 98bae9f (Task 1 — test)
- FOUND commit 2b945f5 (Task 2 — feat)
- VERIFIED: dotnet build tests/BaseApi.Tests -c Debug = 0/0
- VERIFIED: full hermetic suite --filter-not-trait "Category=RealStack" = Passed 441 / Failed 0 (zero regression; the new RealStack E2E correctly excluded)

---
*Phase: 31-idempotent-execution-exactly-once-effect*
*Completed (authored): 2026-06-04 — live gate (Task 3) pending operator verification*
