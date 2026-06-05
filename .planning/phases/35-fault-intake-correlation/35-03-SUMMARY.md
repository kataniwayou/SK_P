---
phase: 35-fault-intake-correlation
plan: 03
subsystem: keeper-fault-intake
tags: [realstack, e2e, sc3, correlation, fault-intake, INTAKE-03, KMET-04, operator-gated]
requires:
  - "FaultEntryStepDispatchConsumer (35-02) — the running-Keeper-container observe-and-ack consumer that restores the manual CorrelationId scope + opens the 5-id exec scope and emits the 'keeper fault intake' Information log"
  - "FaultRecoverySpikeE2ETests (Phase 33) — the proven RealStack harness (RealStackWebAppFactory, embedded SourceHash reflection, seed helpers, PollForHealthyLivenessAsync, ArmWrongTypePoisonAsync WRONGTYPE live-trip, BuildEffectQuery, PollEsForLog, net-zero teardown) cloned verbatim as a SIBLING"
  - "ElasticsearchTestClient.PollEsForLog + the ES field-path constants (resource.attributes.service.name, attributes.CorrelationId, attributes.StepId, body.text)"
provides:
  - "KeeperFaultIntakeE2ETests — the RealStack SC3 proof: a live WRONGTYPE trip publishes a real Fault<EntryStepDispatch>, the RUNNING Keeper container consumes it, and PollEsForLog asserts the Keeper-emitted ES log is correlated to the original execution by service.name=keeper + attributes.CorrelationId == the tripped correlationId + attributes.StepId == the tripped step + body.text ~ 'keeper fault intake'"
  - "The INTAKE-03 Phase-35 separation slice (confirmation, not build): Keeper recovers off the Fault<T> pub/sub stream (its keeper-fault-recovery queue binds the fault exchanges; the intake fires off the published fault event), never the _error queue — no DLQ-1/TTL/keeper-dlq topology introduced (Phase 36)"
affects:
  - "tests/BaseApi.Tests — adds ONE RealStack-trait-excluded E2E class (0 hermetic tests added; the fast suite count is unchanged)"
  - "The Phase-38 live close gate (the authoritative full-suite + net-zero signal): this test's run-minted skp:data:*/skp:flag:*/poison keys are TTL'd/registered for teardown, workflow stopped — net-zero preserved"
tech-stack:
  added: []
  patterns:
    - "SC3 running-container correlated-ES-log proof: live WRONGTYPE trip (no in-test Fault probe, no re-inject — let the deployed Keeper container consume the published Fault<EntryStepDispatch>) → PollEsForLog filtered on service.name=keeper + the PROPAGATED correlationId + StepId + body.text wildcard"
    - "Sibling-clone of a standing RealStack spike: copy the rig verbatim, keep the spike's invariant isolated (do NOT mutate FaultRecoverySpikeE2ETests)"
key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/KeeperFaultIntakeE2ETests.cs
  modified: []
  deleted: []
decisions:
  - "SC3's correlation guard is the END-TO-END proof of the manual CorrelationId scope (Plan 02 / T-35-08): the assertion is attributes.CorrelationId == the ORIGINAL tripped correlationId (not a fresh Guid). A Fault<T> envelope is neither IExecutionCorrelated nor ICorrelated, so the bus-wide filter would emit a fresh Guid; a hit on the propagated id proves the consumer's manual BeginScope works through the deployed container."
  - "The LIVE half is operator-gated per the established Phase-31..34 precedent: the authored test + this operator runbook complete the plan; the live Assert.NotNull(hit) GREEN against the REBUILT stack flips SC3 to proven. The test was NOT run live in this session (no Docker stack was started) — SC3 is recorded as OPERATOR-PENDING, not observed."
  - "No scope creep into Phase 36: the test only CONFIRMS the separation (the fault binds the fault exchanges; the intake fires off the event); it builds NO DLQ-1/TTL/keeper-dlq topology and touches NO _error topology. Acceptance grep x-message-ttl|x-dead-letter-exchange|keeper-dlq == 0 enforces this (Pitfall 4)."
metrics:
  duration: ~1 task authored (Task 1) + operator-gated checkpoint (Task 2)
  completed: 2026-06-05
  tasks: 2
  files: 1
---

# Phase 35 Plan 03: RealStack SC3 — running-Keeper-container correlated-ES-log proof Summary

Authored `KeeperFaultIntakeE2ETests` — the RealStack SC3 proof that a live WRONGTYPE trip publishes a real `Fault<EntryStepDispatch>`, the RUNNING Keeper container (D-09, not an in-test bus) consumes it, and `PollEsForLog` asserts the Keeper-emitted ES log is correlated to the original execution by `service.name = keeper` + `attributes.CorrelationId == the tripped correlationId` + `attributes.StepId == the tripped step` + `body.text ~ "keeper fault intake"`. The test is a SIBLING clone of the standing Phase-33 `FaultRecoverySpikeE2ETests` (the spike is untouched). The solution builds 0/0 Release and the hermetic suite is unchanged (the RealStack-trait file adds 0 hermetic tests). **Per the Phase-31..34 operator-gate precedent, the LIVE run is operator-pending — it was NOT run in this session.** SC3 flips to proven on the operator's GREEN run against the REBUILT stack (runbook below).

## What Was Built

**Task 1 — KeeperFaultIntakeE2ETests authored (commit `1b64143`)**
A sibling clone of `FaultRecoverySpikeE2ETests` reusing the rig VERBATIM: the three traits (`[Trait("Category","E2E")]` + `[Trait("Category","RealStack")]` + `[Collection("Observability")]`), `RealStackWebAppFactory` + the `InitializeAsync` env-var host overrides (incl. `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`), the embedded SourceHash reflection off `Processor.Sample.SampleProcessor`, the seed helpers (`cron:"* * * * *"`), `PollForHealthyLivenessAsync`, `ArmWrongTypePoisonAsync`, and the net-zero `skp:*` teardown (`ParentIndexMembersToSrem`/`L2KeysToCleanup`).

The SC3 delta vs the spike:
1. **NO in-test Fault probe and NO re-inject** — the RUNNING Keeper container consumes the published `Fault<EntryStepDispatch>` (D-09). The poison is NOT cleared after arming (the fault must be published and consumed by the container).
2. **Live-trip via the proven WRONGTYPE recipe:** seed processor/step/workflow → poll `processor-sample` Healthy → build the entry-step dispatch via `MessageIdentity.EntryEntryId`/`ComputeH` to get `dispatchH` while capturing the dispatch's `CorrelationId` (`dCorr`) and `StepId` (`stepId`) → `ArmWrongTypePoisonAsync(L2ProjectionKeys.Flag(dispatchH))` as a LIST → the consumer's dedup-gate GET throws WRONGTYPE every delivery → `Immediate(N)` exhausts → `Fault<EntryStepDispatch>` fans out to `keeper-fault-recovery` → the Keeper container's `FaultEntryStepDispatchConsumer` emits the intake log.
3. **The KEEPER container's correlated ES log assertion:** the effect query filters `resource.attributes.service.name = "keeper"` + `attributes.CorrelationId = dCorr:D` + `attributes.StepId = stepId:D` + a `body.text` wildcard on `"*keeper fault intake*"` (the D-08 phrasing from Plan 02). `Assert.NotNull(hit)` via `PollEsForLog(query, EsPollTimeoutMs, ct)` (`EsPollTimeoutMs = 120_000`). The hit's presence with the matching `attributes.CorrelationId` is the SC3 + INTAKE-03-separation proof: the log fired off the `Fault<T>` event with the PROPAGATED correlationId (not a fresh Guid).
4. **Net-zero teardown:** `POST /api/v1/orchestration/stop` + every run-minted `skp:data:*`/`skp:flag:*` and the poison key registered via `L2KeysToCleanup` (`skp:{wfId}`, `skp:{wfId}:{stepId}`, the dispatch poison key, the run-minted data/flag keys) + `ParentIndexMembersToSrem` (the workflow id).

The `MassTransit.ExecutionResult` vs `Messaging.Contracts.ExecutionResult` ambiguity is resolved with the alias mirroring the spike. NO DLQ-1/TTL/`keeper-dlq` topology and NO `_error` topology is built (Pitfall 4 — that is Phase 36).

## Verification

### Authored half (PASSED — committed in Task 1, `1b64143`)
- `dotnet build SK_P.sln -c Release` — **0 Warning / 0 Error** (verified at Task 1 commit time).
- Hermetic suite unchanged — the RealStack-trait file adds **0 hermetic tests** (correctly excluded from the fast suite).
- Acceptance greps (re-verified at finalization against the committed file):
  - `[Trait("Category", "RealStack")]` present == **1**.
  - `service.name` == **4**; the query filters `"keeper"` (`grep -c '"keeper"'` == **2**, in the service.name term).
  - `PollEsForLog` == **3**.
  - `ArmWrongTypePoisonAsync` == **3** (the proven live-trip).
  - `CorrelationId` references == **8**; `StepId` references == **9** (the correlation assertion).
  - `keeper fault intake` == **2** (the body.text wildcard matches the D-08 log phrasing).
  - Scope-creep guard `grep -cE "x-message-ttl|x-dead-letter-exchange|keeper-dlq"` == **0** (Pitfall 4 — no DLQ-1/TTL/DLQ-2 build).
  - Net-zero teardown: `L2KeysToCleanup`/`ParentIndexMembersToSrem` register the run-minted keys + poison; `POST /orchestration/stop` present == **1**.

### LIVE half (SC3) — OPERATOR-PENDING (NOT observed this session)
The live `KeeperFaultIntakeE2ETests` GREEN against the REBUILT v3.7.0 stack was **not run** in this session (no Docker stack was started). SC3 — the running-Keeper-container correlated ES log — remains operator-pending. See the runbook below. Per the Phase-31..34 do-not-block-on-human-verify precedent, this gate was auto-approved to finalize the plan; the live observation is deferred to the operator (and ultimately confirmed by the Phase-38 live close gate).

## Pending Verification (operator runbook)

The authored test + this runbook complete the plan; the live `Assert.NotNull(hit)` GREEN flips SC3 to proven.

**1. Rebuild + bring up the stack (Keeper MUST be rebuilt — Pitfall 5):**
```
docker compose up -d --build keeper processor-sample orchestrator baseapi-service
```
A stale Keeper SourceHash runs the OLD Phase-34 placeholder consumer and emits NO intake log — the rebuild is mandatory or `PollEsForLog` times out with no `service.name=keeper` hit.

**2. Wait for all four services Healthy** (compose health gate).

**3. Run the SC3 class-filtered RealStack test:**
```
dotnet test tests/BaseApi.Tests -- --filter-class "*KeeperFaultIntakeE2ETests"
```

**4. Expected: GREEN.** `PollEsForLog` returns a hit with:
- `resource.attributes.service.name = "keeper"`,
- `attributes.CorrelationId == the tripped dispatch's correlationId` (the PROPAGATED id — proves the manual CorrelationId scope from Plan 02 works end-to-end through the deployed container),
- `attributes.StepId == the tripped step`,
- `body.text` matching `"keeper fault intake"`.

This proves the running Keeper container emitted the correlated log end-to-end (SC3) off the `Fault<T>` event (the INTAKE-03 Phase-35 separation slice: Keeper recovers off the pub/sub stream, never the `_error` queue).

**5. Net-zero check:** no leftover `skp:*` keys after the run (teardown registers all run-minted `data:`/`flag:` + the poison key and stops the workflow). The Phase-38 close-gate triple-SHA must stay BEFORE==AFTER (the durable `keeper-fault-recovery` queue is intentional/enduring and belongs in the `rabbitmqctl list_queues` baseline).

**Failure triage:** if `PollEsForLog` times out with no `service.name=keeper` hit, the most likely cause is (a) a STALE Keeper container (the `--build keeper` step was skipped — Pitfall 5), or (b) `OTEL_EXPORTER_OTLP_ENDPOINT` not wired (the live env-var knob, NOT the appsettings key). Confirm the Keeper container's logs show the `Fault<EntryStepDispatch>` consumed, then re-run.

## Requirement Status (honest)

- **INTAKE-03** (Phase-35 separation slice — Keeper recovers off the `Fault<T>` events, never the `_error`/DLQ-1 queue; the consolidated TTL'd DLQ-1 BUILD is Phase 36) — **code-complete (35-02 consumers + definitions) and hermetically proven (KeeperFaultConsumerScopeTests 3/3); live ES-correlation (SC3) OPERATOR-PENDING.** NOT ticked in REQUIREMENTS.md (live proof unobserved).
- **KMET-04** (Keeper emits OTel logs carrying the propagated correlationId + execution-scope ids) — **hermetically proven (35-02, scope carries CorrelationId + 5 exec ids); live end-to-end ES emission (SC3) OPERATOR-PENDING.** NOT ticked in REQUIREMENTS.md (live proof unobserved).

The orchestrator/verifier handles requirement traceability once the operator reports the GREEN live run.

## Deviations from Plan

None — Task 1 was authored exactly as specified (sibling clone, spike untouched, no scope creep). Task 2 is the operator-gated live-verify checkpoint; finalized per the Phase-31..34 do-not-block precedent WITHOUT claiming the live run was observed. No auto-fixes (Rules 1-3 not triggered), no architectural decisions (Rule 4 not triggered), no authentication gates.

## Working-Tree Hygiene

Task 1's commit (`1b64143`) staged the test file by EXPLICIT path only. This finalization commits ONLY `35-03-SUMMARY.md` + the doc-state files (`STATE.md`, `ROADMAP.md`) by explicit path. The ~242 pre-existing unstaged `.planning/phases/*` archive deletions (UNRELATED to this plan) are NOT staged and remain uncommitted — verified `git status --short -- .planning/ | grep -c "^ D"` == **242**.

## Self-Check: PASSED (authored artifact) — LIVE SC3: PENDING

- `tests/BaseApi.Tests/Orchestrator/KeeperFaultIntakeE2ETests.cs` — FOUND (git-tracked), `[Trait("Category", "RealStack")]` present, filters `service.name = "keeper"` + `attributes.CorrelationId` + `attributes.StepId` + `body.text ~ "keeper fault intake"`, net-zero teardown registered, scope-creep grep == 0.
- Commit `1b64143` — FOUND in git log (`test(35-03): KeeperFaultIntakeE2ETests — SC3 running-Keeper-container correlated-ES-log proof`).
- **LIVE SC3 (running-Keeper-container correlated ES log) — PENDING (operator-gated):** NOT observed in this session; routed to human verification per the runbook above. Do NOT treat SC3 as observed live.
