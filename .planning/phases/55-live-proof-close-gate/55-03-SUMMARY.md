---
phase: 55-live-proof-close-gate
plan: 03
subsystem: testing (RealStack E2E — recovery paths)
tags: [SC2, keeper-recovery, A19, slot-array, organic-recovery, TEST-01]
requires:
  - "DeleteConsumer two-key DEL (KeeperDelete.MessageId, Phase 54 A19/GC-03)"
  - "ProcessorPipeline if-exist L2[messageId] recovery branch + send-before-retire + two-key DEL tail (Phase 51/54)"
  - "L2ProjectionKeys.MessageIndex / ExecutionData (Phase 50)"
provides:
  - "SC2 3-state keeper RealStack proof (REINJECT present/absent, INJECT, A19 both-key DELETE)"
  - "Organic recovery-pass RealStack proof (broker-MessageId Send → recovery branch → send-before-retire → two-key net-zero)"
affects:
  - "Phase-55 close gate (scripts/phase-55-close.ps1 runs the full suite incl. these facts against the live v5 stack)"
tech-stack:
  added: []
  patterns:
    - "MassTransit ISendEndpoint.Send<T>(T, Action<SendContext<T>>, ct) pipe-callback to set the broker MessageId (slot-array branch key)"
    - "Truthful liveness gate (SC1 idiom): genuine embedded SourceHash reflection + GET-or-create seed + skp:{procId:D} heartbeat poll"
    - "Slot-retire poll (HashGetAsync slot == Guid.Empty, treating an absent HASH as already-retired)"
key-files:
  created: []
  modified:
    - "tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs"
decisions:
  - "STATE 4 DELETE rewritten as the A19 both-key delete: KeeperDelete carries MessageId; BOTH skp:data{entryId} AND skp:msg{messageId} seeded then asserted gone after one DEL"
  - "Organic test asserts the Redis-observable send-before-retire (slot retired to Guid.Empty) as the proof the completed step was re-sent — no orchestrator-result queue read needed (a slot retires ONLY after a confirmed send, SLOT-03)"
  - "Organic test stays in [Collection(Observability)] (D-03 discretion — it does NOT stop redis)"
  - "Dead GAP-49-8 composite-sweep block deleted from DisposeAsync (Landmine 3); L2KeysToCleanup + ParentIndexMembersToSrem drain retained"
metrics:
  duration: "~25 min"
  completed: 2026-06-12
---

# Phase 55 Plan 03: SC2 v5 3-State Keeper Rewrite + Organic Recovery Pass Summary

SC2RecoveryPathsE2ETests was rewritten for the v5 3-state keeper: STATE 4 DELETE is now the A19 both-key delete (KeeperDelete carries a MessageId; the DeleteConsumer deletes BOTH the execution-data key and the slot-array allocation index in one atomic multi-key DEL), and a new organic recovery-pass `[Fact]` drives the live processor's `if exist L2[messageId]` branch end-to-end (broker-MessageId Send → send-before-retire → two-key net-zero). The file is retagged `[Trait("Phase","55")]`, the v4 "four states" naming is dropped, and the dead composite-sweep teardown block is removed.

## What Was Built

### Task 1 — STATE 4 DELETE → A19 both-key delete; retag + cleanup (commit 174452e)
- **STATE 4 rewrite:** mints `entryId` + `messageId`, pre-seeds BOTH `skp:data:{entryId}` (StringSet) AND a one-slot `skp:msg:{messageId}` HASH (`HashSetAsync(indexKey, 0, entryId)`), registers both into `L2KeysToCleanup`, sends `KeeperDelete { EntryId, MessageId }`, then asserts BOTH keys ABSENT via two `PollForKeyAbsentAsync` calls — proving `DeleteConsumer.cs:19-24` deletes both operands in one DEL.
- **Retag:** `[Trait("Phase","49")]` → `[Trait("Phase","55")]`; XML doc + Phase/HUMAN-UAT mentions updated to the v5 3-state model.
- **Rename:** `LiveKeeperRecovery_AllFourStates_...` → `LiveKeeperRecovery_AllThreeStates_...`; all "FOUR states"/"AllFour" naming removed.
- **Landmine 3:** deleted the dead `GAP-49-8` composite-sweep block (`skp:*:{wfId}:*` glob) from `DisposeAsync`; the `L2KeysToCleanup` + `ParentIndexMembersToSrem` drain and the `BrokerQueuesToPurge`/`BrokerQueuesToDelete` teardown are unchanged.
- **STATEs 1-3 kept verbatim** (REINJECT present/absent silent-drop + DLQ-no-climb, INJECT write+source-delete).

### Task 2 — Organic recovery-pass test (commit cc0ac56)
- Second `[Fact]` `LiveOrganicRecovery_PreSeededSlotArray_ReSendsCompletedThenRetiresThenTwoKeyDelete`.
- Truthful liveness gate (SC1 idiom): genuine embedded `SourceHash` reflection → `SeedProcessorAsync` (GET-or-create) → `SeedStepAsync` → `PollForHealthyLivenessAsync` so the dispatch reaches a REAL container bound to `queue:{procId:D}`.
- Pre-seeds a populated slot-array index (`HashSetAsync(indexKey, 0, entryId)`) + completed data key (`StringSetAsync(dataKey, ...)`), both registered for net-zero.
- Fires the recovery branch: `dispatchEndpoint.Send(dispatch, ctx => ctx.MessageId = messageId, ct)` — the MassTransit pipe-callback overload setting the broker MessageId (the `EntryStepDispatchConsumer.cs:42` slot-array branch key).
- Asserts: (1) slot retired to `Guid.Empty` (`PollForHashSlotRetiredAsync` — the send-before-retire SLOT-03 proof the completed step was re-sent), (2) two-key net-zero (`PollForKeyAbsentAsync` on BOTH keys — the RECOV-03 all-clear tail).
- Stays in `[Collection("Observability")]` (NOT the serial outage collection — D-03).

## Verification

- **Autonomous gate (both tasks):** `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` → **Build succeeded, 0 Warning(s), 0 Error(s)** after each task.
- **Acceptance-criteria grep checks (all PASS):**
  - `ctx.MessageId = messageId` present (1); `MessageId = messageId` on KeeperDelete + organic (2).
  - `HashSetAsync(indexKey, 0` present (2); `StringSetAsync(dataKey` present; `HashGetAsync(indexKey` poll present (1).
  - No `skp:*:` (0); no `GAP-49-8` (0); no `AllFour` (0); no `FOUR states` (0); no `[Trait("Phase","49")]` (0).
  - Exactly two real `[Fact]` methods (lines 84, 276); class stays `[Collection("Observability")]`, no `RedisOutageSerial`.
- **RealStack facts EXCLUDED from the hermetic run by design** (`Category=RealStack`). The full live suite is the operator-gated Phase-55 close gate, not an autonomous step — the local docker broker is not running in this executor environment (MassTransit `Connection Failed: rabbitmq://` background-retry log noise during the attempted hermetic run is the absent-broker symptom, not a test failure).

## Deviations from Plan

None — plan executed as written. The Edit tooling required splitting the large XML-doc rewrite into several smaller anchored edits (em-dash/`<para>` whitespace mismatches), but the resulting content matches the plan's intent exactly.

## Threat Surface

No new security-relevant surface introduced. The plan's threat register (T-55-07 silent net-zero loss → mitigated by asserting BOTH keys gone in STATE 4 and the organic test; T-55-08 stale composite glob → mitigated by deleting the dead sweep; T-55-09 forged MessageId → accepted, upstream fail-fast) is satisfied by this plan's edits.

## Self-Check: PASSED

- `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` — FOUND (modified).
- Commit 174452e (Task 1) — FOUND in git log.
- Commit cc0ac56 (Task 2) — FOUND in git log.
