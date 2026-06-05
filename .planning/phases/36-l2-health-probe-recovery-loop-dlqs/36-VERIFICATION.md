---
phase: 36-l2-health-probe-recovery-loop-dlqs
verified: 2026-06-06T01:00:00Z
status: human_needed
score: 5/5 hermetic truths verified (3 live-gated items → human_verification)
overrides_applied: 0
human_verification:
  - test: "Run the KeeperRecoveryE2ETests RealStack suite: rebuild all containers (keeper + processor-sample + orchestrator + baseapi-service) then execute `dotnet test tests/BaseApi.Tests --filter \"Category=RealStack&FullyQualifiedName~KeeperRecovery\"`"
    expected: "KeeperRecovery_RecoversBothPaths GREEN (CountEsHitsAsync == 1 exactly-once for dispatch re-inject; verbatim ExecutionResult re-inject caught on queue:orchestrator-result with matching H + CorrelationId)"
    why_human: "Requires a rebuilt live Docker compose stack with all containers healthy; the deployed Keeper container must run its probe loop, re-inject the verbatim inner to its origin, and the downstream effect must propagate through Redis/ES. Cannot test without the running stack."
  - test: "Run give-up path: `dotnet test tests/BaseApi.Tests --filter \"Category=RealStack&FullyQualifiedName~KeeperRecovery_GivesUp\"` — optionally set Probe__MaxAttempts=2 on the keeper container to shorten the 60s loop window"
    expected: "Original Fault<ExecutionResult> envelope (not the bare inner) arrives on queue:keeper-dlq; is acked-drained by the in-test probe; keeper-dlq is net-zero post-test; skp:keeper:probe:* scratch keys are net-zero (TTL self-cleans)"
    why_human: "Requires the live stack and a poisoned skp:data:{entryId} key that keeps the probe loop failing for the full MaxAttempts window. Cannot test without the running stack."
  - test: "(Optional / VALIDATION.md Manual-Only) kill-mid-loop crash-redelivery: trip a fault, `docker kill keeper` while the loop is mid-await, observe redelivery + loop restart in logs/ES, confirm no message loss"
    expected: "The redelivered Fault<T> is re-processed by a new Keeper replica; the loop restarts from the beginning; no message is lost; the downstream effect fires exactly once (PROBE-05 at-least-once)"
    why_human: "Requires interactive Docker control during a live probe-loop window; cannot automate in a hermetic session."
---

# Phase 36: L2 Health-Probe Recovery Loop & DLQs Verification Report

**Phase Goal:** Implement the core recovery engine — a bounded, crash-survivable L2 read+write probe loop that re-injects to origin on first success or parks the unrecoverable in `keeper-dlq` (DLQ-2) on give-up — plus the two-DLQ topology (Immediate(N) exhaustion → DLQ-1; probe exhaustion → DLQ-2) and the shared `Immediate(N)` policy across all consumers.
**Verified:** 2026-06-06T01:00:00Z
**Status:** human_needed
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths (from ROADMAP Success Criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| SC-1 | Keeper runs a recovery loop with config-driven inter-attempt delay and max-attempts; each iteration probes L2 by reading `skp:data:{entryId}` AND write-then-deleting a scratch key; `delay × attempts` is bounded under RabbitMQ's delivery-ack timeout | ✓ VERIFIED | `L2ProbeRecovery.cs` implements the loop with `StringGetAsync` + `StringSetAsync(expiry:30s)` + `KeyDeleteAsync`; `ProbeOptions` defaults `DelaySeconds=5, MaxAttempts=12`; `ProbeOptions_Bound` hermetic test asserts `5×12=60s < 1800s`; hermetic facts `Probe_RequiresReadAndWrite`, `Probe_FailThenSucceed`, `Probe_FailToMax` all GREEN |
| SC-2 | On first successful probe Keeper exits and re-injects inner to origin; on max-attempts exhaustion parks original Fault<T> to `keeper-dlq` | ✓ VERIFIED (hermetic) / ? HUMAN (live) | Both consumers call `recovery.RunAsync()` → `Recovered` ⇒ `GetSendEndpoint(queue:{ProcessorId:D}` or `queue:orchestrator-result`) + `Send(inner)`; `GaveUp` ⇒ `GetSendEndpoint(queue:keeper-dlq)` + `Send(context.Message)`; hermetic facts `Probe_Success_Reinjects`, `Probe_GiveUp_ParksToDlq` GREEN; live half operator-gated (Plan 04) |
| SC-3 | Fault message is acked only after the loop exits; killing Keeper mid-loop leaves it un-acked → redelivered → loop restarts | ✓ VERIFIED (hermetic) / ? HUMAN (live kill) | Loop is `await`ed inside `Consume`; `Probe_AcksOnlyAfterLoop` hermetic fact asserts `Consumed.Any` completes only after the awaited loop; live kill-mid-loop is documented Manual-Only in VALIDATION.md (operator runbook in 36-04-SUMMARY) |
| SC-4 | Two DLQs split by mechanism: DLQ-1 (`skp-dlq-1`, Immediate(N) exhaustion, x-message-ttl=7d) and DLQ-2 `keeper-dlq` (probe give-ups, no TTL) | ✓ VERIFIED (hermetic) / ? HUMAN (live broker args) | `ConsolidatedErrorTransportFilter` moves exhausted messages to `skp-dlq-1`; `KeeperQueues.DeadLetter = "keeper-dlq"`; `MessagingServiceCollectionExtensions` declares `skp-dlq-1` with `SetQueueArgument("x-message-ttl", (int)TimeSpan.FromDays(7).TotalMilliseconds)`; hermetic facts `Dlq1_Consolidated`, `Dlq_TopologyArgs` GREEN; live broker topology proof operator-gated (Phase 39) |
| SC-5 | All consumers use the same `Immediate(N)` from shared `RetryOptions` appsettings, routed uniformly to DLQ-1 | ✓ VERIFIED | `AddConfigureEndpointsCallback` in `MessagingServiceCollectionExtensions` installs `ConfigureError(GenerateFaultFilter + ConsolidatedErrorTransportFilter)` once per endpoint across ALL consoles; `Keeper/appsettings.json` has `"Retry": { "Limit": 3, "Strategy": "Immediate" }`; hermetic fact `Keeper_SendFault_RetriesToDlq1` GREEN |

**Hermetic Score:** 5/5 truths structurally verified against source code and hermetic tests.

**Live-gated items (human_verification, not gaps):** SC-2 (live recover/give-up), SC-3 (live kill-mid-loop), SC-4 (live broker queue-arg proof). These are INTENTIONALLY operator-gated per the established Phases 33–35 LIVE-GATE precedent — source code fully implements all requirements; the live proof requires the rebuilt Docker stack.

---

### Deferred Items

None. All deferred live items are classified under `human_verification` per the project-specific LIVE-GATE precedent, not as deferred to a later phase.

---

## Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/Messaging.Contracts/KeeperQueues.cs` | `DeadLetter = "keeper-dlq"` const | ✓ VERIFIED | Line 21: `public const string DeadLetter = "keeper-dlq";` |
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` | `KeeperProbe(string h)` builder | ✓ VERIFIED | Line 54: `public static string KeeperProbe(string h) => $"{Prefix}keeper:probe:{h}";` |
| `src/Keeper/ProbeOptions.cs` | `DelaySeconds=5`, `MaxAttempts=12` sealed class | ✓ VERIFIED | 12-line sealed class with correct defaults; Keeper-local namespace |
| `src/Keeper/Recovery/L2ProbeRecovery.cs` | Bounded probe loop with `catch (RedisException)` only | ✓ VERIFIED | 41 lines; `StringGetAsync` + `StringSetAsync(expiry:30s)` + `KeyDeleteAsync`; `catch (RedisException)` = 1; `catch (Exception` = 0; `When.Exists`/`keepTtl` = 0 |
| `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` | `async Task Consume`; probe loop → re-inject or park | ✓ VERIFIED | `async Task Consume`; 2× `GetSendEndpoint`; `Send(context.Message)` for park; `.Publish` = 0; Phase-35 scopes kept verbatim |
| `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` | `async Task Consume`; probe loop → re-inject or park | ✓ VERIFIED | Identical shape to dispatch consumer; `queue:{OrchestratorQueues.Result}` for re-inject origin |
| `src/BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs` | `IFilter<ExceptionReceiveContext>` moving to `skp-dlq-1` | ✓ VERIFIED | 106 lines; `Dlq1 = "skp-dlq-1"` const; typed `ConsolidatedFault` envelope; faithful header/body move; `GenerateFaultFilter` not removed |
| `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` | `AddConfigureEndpointsCallback` + `ConfigureError` + skp-dlq-1 declaration | ✓ VERIFIED | `ConfigureError` with `GenerateFaultFilter` + `ConsolidatedErrorTransportFilter`; `SetQueueArgument("x-message-ttl", (int)TimeSpan.FromDays(7).TotalMilliseconds)`; all 4 correlation filters + `configureBus?.Invoke` + `ConfigureEndpoints(ctx)` preserved |
| `src/Keeper/Program.cs` | `Configure<ProbeOptions>` + `AddSingleton<L2ProbeRecovery>` | ✓ VERIFIED | Both registrations present; `AddBaseConsoleRedis` = 0 (no double-registration) |
| `src/Keeper/appsettings.json` | `"Probe": { "DelaySeconds": 5, "MaxAttempts": 12 }` | ✓ VERIFIED | Lines 29–32 present; JSON-valid; before `"ConsoleHealth"` |
| `tests/BaseApi.Tests/Keeper/FakeRedis.cs` | Down/HalfOpen/Up test double with `RedisConnectionException` | ✓ VERIFIED | NSubstitute-backed; `RedisHealth` enum; `SetFailuresBeforeUp(n)`; `BringUp/Down/HalfOpen`; throws `RedisConnectionException(ConnectionFailureType.UnableToConnect, "fake-down")` |
| `tests/BaseApi.Tests/Keeper/ProbeOptionsBoundTests.cs` | `ProbeOptions_Bound_Under_RabbitMq_ConsumerTimeout` fact | ✓ VERIFIED | Asserts `5 × 12 = 60 > 0` and `60 < 1800`; filter `--filter ProbeOptions_Bound` matches |
| `tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs` | 6 hermetic facts: helper layer (3) + harness layer (3) | ✓ VERIFIED | `Probe_RequiresReadAndWrite`, `Probe_FailThenSucceed`, `Probe_FailToMax`, `Probe_Success_Reinjects`, `Probe_GiveUp_ParksToDlq`, `Probe_AcksOnlyAfterLoop` all present |
| `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` | `Dlq1_Consolidated`, `Keeper_SendFault_RetriesToDlq1`, `Dlq_TopologyArgs` | ✓ VERIFIED | All 3 facts present; VALIDATION.md filter names match |
| `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs` | RealStack sibling with both facts + net-zero scan | ✓ VERIFIED | 731 lines; 3 RealStack traits; `ArmWrongTypePoisonAsync` + `PollEsForLog` cloned; `keeper:probe:*` scan; `keeper-dlq` assertion; `KeeperRecovery_RecoversBothPaths` + `KeeperRecovery_GivesUp_ParksToDlq` facts |

---

## Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `L2ProbeRecovery.cs` | `L2ProjectionKeys.ExecutionData` + `L2ProjectionKeys.KeeperProbe` | `StringGetAsync` + `StringSetAsync` + `KeyDeleteAsync` | ✓ WIRED | Lines 27–30: both key builders called with real `entryId` and `h` args |
| `FaultEntryStepDispatchConsumer.cs` | `queue:{ProcessorId:D}` (recover) and `queue:keeper-dlq` (give-up) | `context.GetSendEndpoint` + `Send` | ✓ WIRED | Lines 55–56 (recover): `new Uri($"queue:{inner.ProcessorId:D}")` + `Send(inner)`; lines 62–63 (give-up): `new Uri($"queue:{KeeperQueues.DeadLetter}")` + `Send(context.Message)` |
| `FaultExecutionResultConsumer.cs` | `queue:orchestrator-result` (recover) and `queue:keeper-dlq` (give-up) | `context.GetSendEndpoint` + `Send` | ✓ WIRED | Lines 56–57 (recover): `new Uri($"queue:{OrchestratorQueues.Result}")` + `Send(inner)`; lines 62–63 (give-up): `Send(context.Message)` |
| `MessagingServiceCollectionExtensions.cs` | `ConsolidatedErrorTransportFilter` + `skp-dlq-1` declaration | `AddConfigureEndpointsCallback` + `c.ReceiveEndpoint` | ✓ WIRED | Lines 56–63: callback installs `ConfigureError`; lines 79–82: `ReceiveEndpoint("skp-dlq-1", ...)` with `SetQueueArgument` |
| `Keeper/Program.cs` | `ProbeOptions` (bind) + `L2ProbeRecovery` (singleton) | `Configure<ProbeOptions>` + `AddSingleton<Keeper.Recovery.L2ProbeRecovery>` | ✓ WIRED | Lines 27 and 31 respectively; no double `AddBaseConsoleRedis` |
| `Keeper/appsettings.json` | `ProbeOptions.DelaySeconds` + `ProbeOptions.MaxAttempts` | `GetSection("Probe")` | ✓ WIRED | `"Probe": { "DelaySeconds": 5, "MaxAttempts": 12 }` present; bound via `Configure<ProbeOptions>(builder.Configuration.GetSection("Probe"))` |

---

## Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|--------------|--------|-------------------|--------|
| `L2ProbeRecovery.RunAsync` | `db.StringGetAsync(...)` result | Live `IConnectionMultiplexer.GetDatabase()` | Yes — real Redis calls (value need not exist; Null is a valid up-result) | ✓ FLOWING |
| `FaultEntryStepDispatchConsumer.Consume` | `inner` (the verbatim `EntryStepDispatch` from `context.Message.Message`) | `Fault<EntryStepDispatch>` broker message | Yes — real message from broker | ✓ FLOWING |
| `FaultExecutionResultConsumer.Consume` | `inner` (the verbatim `ExecutionResult`) | `Fault<ExecutionResult>` broker message | Yes — real message from broker | ✓ FLOWING |
| `ConsolidatedErrorTransportFilter.Send` | `context.Body.GetBytes()` + `context.Exception` | `ExceptionReceiveContext` from MassTransit error pipe | Yes — real exhausted message body + exception | ✓ FLOWING |

---

## Behavioral Spot-Checks

Step 7b is SKIPPED for live RealStack behaviors per the LIVE-GATE precedent (Plans 36-03/36-04). Hermetic probe loop behaviors are verified via the test facts listed above.

| Behavior | Test Name | Result | Status |
|----------|-----------|--------|--------|
| HalfOpen Redis (read OK, write fails) → GaveUp | `Probe_RequiresReadAndWrite` | HERMETIC GREEN (SUMMARY: 464/0) | ✓ PASS |
| Fail-then-succeed → Recovered within budget | `Probe_FailThenSucceed` | HERMETIC GREEN | ✓ PASS |
| All MaxAttempts fail → GaveUp | `Probe_FailToMax` | HERMETIC GREEN | ✓ PASS |
| Fault<EntryStepDispatch> + Up Redis → `Sent.Any<EntryStepDispatch>` | `Probe_Success_Reinjects` | HERMETIC GREEN | ✓ PASS |
| All-Down → `Sent.Any<Fault<EntryStepDispatch>>` (not bare inner) | `Probe_GiveUp_ParksToDlq` | HERMETIC GREEN | ✓ PASS |
| Ack only after loop exits | `Probe_AcksOnlyAfterLoop` | HERMETIC GREEN | ✓ PASS |
| Exhausted consumer routes to `skp-dlq-1` + `Fault<T>` still published | `Dlq1_Consolidated` | HERMETIC GREEN | ✓ PASS |
| Keeper infra fault exhausts under Immediate(N) → DLQ-1 | `Keeper_SendFault_RetriesToDlq1` | HERMETIC GREEN | ✓ PASS |
| DLQ topology split: `skp-dlq-1` TTL'd vs `keeper-dlq` no-TTL | `Dlq_TopologyArgs` | HERMETIC GREEN | ✓ PASS |
| ProbeOptions 5×12 = 60s bounded under 1800s consumer_timeout | `ProbeOptions_Bound_Under_RabbitMq_ConsumerTimeout` | HERMETIC GREEN | ✓ PASS |
| Live recover-both-paths (exactly-once ES effect) | `KeeperRecovery_RecoversBothPaths` | NOT RUN — operator gate | ? SKIP (live) |
| Live give-up → keeper-dlq park + net-zero | `KeeperRecovery_GivesUp_ParksToDlq` | NOT RUN — operator gate | ? SKIP (live) |

---

## Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| PROBE-01 | 36-01, 36-02 | Bounded probe loop with config-driven delay and max-attempts; `delay × attempts` bounded under consumer_timeout | ✓ SATISFIED (hermetic) | `ProbeOptions(5,12)` + `ProbeOptions_Bound` GREEN; loop in `L2ProbeRecovery` |
| PROBE-02 | 36-01, 36-02 | Each iteration reads `skp:data:{entryId}` AND write-then-deletes scratch key (not mere connectivity) | ✓ SATISFIED (hermetic) | `StringGetAsync` + `StringSetAsync(expiry:30s)` + `KeyDeleteAsync`; `Probe_RequiresReadAndWrite` GREEN |
| PROBE-03 | 36-02, 36-04 | On first successful probe, Keeper re-injects verbatim inner to origin endpoint by type | ✓ SATISFIED (hermetic) / ? HUMAN (live) | `Probe_Success_Reinjects` GREEN; live run operator-gated per LIVE-GATE precedent |
| PROBE-04 | 36-02, 36-04 | On MaxAttempts exhaustion, parks ORIGINAL `Fault<T>` envelope to `keeper-dlq` | ✓ SATISFIED (hermetic) / ? HUMAN (live) | `Probe_GiveUp_ParksToDlq` GREEN (`Send(context.Message)` not bare inner); live run operator-gated |
| PROBE-05 | 36-02, 36-04 | Fault acked only after loop exits; mid-loop kill → redelivery → loop restarts (at-least-once) | ✓ SATISFIED (hermetic) / ? HUMAN (live kill) | `Probe_AcksOnlyAfterLoop` GREEN; live kill-mid-loop is Manual-Only in VALIDATION.md |
| DLQ-01 | 36-03 | Keeper's own infra faults retry under `Immediate(N)` then land in DLQ-1 | ✓ SATISFIED (hermetic) | `Keeper_SendFault_RetriesToDlq1` GREEN; `AddConfigureEndpointsCallback` applies to Keeper endpoints too |
| DLQ-02 | 36-03 | Two DLQs split by mechanism: DLQ-1 (`Immediate(N)` exhaustion) and DLQ-2 (`keeper-dlq`, probe give-ups) | ✓ SATISFIED (hermetic) | `Dlq_TopologyArgs` asserts `"skp-dlq-1" != "keeper-dlq"`; mechanism split confirmed in `ConsolidatedErrorTransportFilter` vs consumer `Send(context.Message)` |
| DLQ-03 | 36-01, 36-03 | `keeper-dlq` depth is the primary operator alert; on give-up workflow stays paused (no auto-resume) | ✓ SATISFIED (const) / ? HUMAN (live depth/pause) | `KeeperQueues.DeadLetter = "keeper-dlq"` (no TTL by design); pause/resume is Phase 37 scope — the DLQ-03 doc-comment and `Dlq_TopologyArgs` confirm no-TTL requirement |
| DLQ-04 | 36-03 | All consumers share `Immediate(N)` from `RetryOptions` appsettings, routed uniformly to DLQ-1 via shared error-transport | ✓ SATISFIED (hermetic) | `AddConfigureEndpointsCallback` in `MessagingServiceCollectionExtensions` (BaseConsole.Core) applies to ALL three consoles' endpoints; `Dlq1_Consolidated` GREEN |

**REQUIREMENTS.md traceability note:** All 9 Phase-36 REQ-IDs show "Not started" in REQUIREMENTS.md per the established honesty precedent (live proof unobserved). The traceability table is the orchestrator's domain to tick on the operator's GREEN live run; this does not indicate a code gap.

---

## Anti-Patterns Found

| File | Pattern | Severity | Assessment |
|------|---------|----------|------------|
| `L2ProbeRecovery.cs` | `catch (RedisException)` ONLY — no `catch (Exception)` | ✓ Good | Correct by design (T-36-06): genuine bugs propagate |
| `FaultEntryStepDispatchConsumer.cs` | No `.Publish` calls | ✓ Good | Re-inject is via `Send` (spike-proven) |
| `FaultExecutionResultConsumer.cs` | No `.Publish` calls | ✓ Good | Re-inject is via `Send` |
| `MessagingServiceCollectionExtensions.cs` | `GenerateFaultFilter` kept in `ConfigureError` | ✓ Good | Required — Keeper rides `Fault<T>` pub/sub stream |
| `MessagingServiceCollectionExtensions.cs` | 6 grep hits for `ConfigureEndpoints\|UseConsumeFilter\|UseSendFilter\|UsePublishFilter\|configureBus\|ConfigureEndpoints` | ✓ Good | Exactly 4 correlation filters + 1 `configureBus?.Invoke` + 1 `ConfigureEndpoints(ctx)` — all preserved verbatim |

No STUB, PLACEHOLDER, TODO, FIXME, or empty-implementation patterns found in any Phase-36 artifact.

---

## Human Verification Required

### 1. Live Recover-Both-Paths (PROBE-03 live + DLQ live drain)

**Test:** Rebuild all containers (`docker compose up -d --build keeper processor-sample orchestrator baseapi-service`), run one-time broker hygiene (delete pre-TTL `skp-dlq-1` if present, purge orphan `{queue}_error` queues), then run:
```
dotnet test tests/BaseApi.Tests --filter "Category=RealStack&FullyQualifiedName~KeeperRecovery_RecoversBothPaths"
```
**Expected:** GREEN. `CountEsHitsAsync == 1` for the dispatch re-inject (exactly-once, receiver `flag[H]` collapses the dup). Verbatim `ExecutionResult` re-inject caught on `queue:orchestrator-result` with matching `H` + `CorrelationId`. Net-zero: no lingering `skp:keeper:probe:*` keys after the 30s TTL; no lingering test-minted L2 keys.
**Why human:** Requires the rebuilt live Docker compose stack with all containers healthy. The deployed Keeper container must run its probe loop; ES/OTLP pipeline must be live; cannot test without the running stack.

### 2. Live Give-Up → keeper-dlq Park (PROBE-04 live)

**Test:** With stack running, optionally set `Probe__MaxAttempts=2` on the keeper container to shorten the loop, then run:
```
dotnet test tests/BaseApi.Tests --filter "Category=RealStack&FullyQualifiedName~KeeperRecovery_GivesUp"
```
**Expected:** GREEN. Original `Fault<ExecutionResult>` envelope (not the bare inner) arrives on `queue:keeper-dlq`; in-test probe ack-drains it (terminal queue empty post-test); `skp:keeper:probe:*` net-zero; poison key cleaned up.
**Why human:** Requires a live stack with the probe loop genuinely exhausting MaxAttempts against a poisoned `skp:data:{entryId}` LIST key; keeper-dlq depth observable only on the real broker.

### 3. Kill-Mid-Loop Crash Redelivery (PROBE-05 at-least-once — VALIDATION.md Manual-Only)

**Test:** Trip a live fault, `docker kill keeper` while the loop is mid-`await Task.Delay`, observe in logs/ES that the redelivered `Fault<T>` restarts the loop on a new replica, and the downstream effect fires exactly once.
**Expected:** No message loss; the loop restarts cleanly; downstream effect count = 1.
**Why human:** Requires interactive Docker process management at a precise timing window during a live probe-loop iteration; cannot automate.

---

## Gaps Summary

No gaps found. All hermetic truths are VERIFIED against the source code. The three `human_verification` items above are live-stack proofs intentionally deferred to the operator per the established Phases 33–35 LIVE-GATE precedent. The Phase-39 3×GREEN triple-SHA close gate is the authoritative live signal.

The one notable deviation from the plan specifications — `RunAsync` taking `(string entryId, string h)` instead of `IExecutionCorrelated inner` — was a necessary and correct fix (the `H` property is not on `IExecutionCorrelated`; the fix was logged in 36-02-SUMMARY). The resulting behaviour is identical to the plan's intent.

The `ConsolidatedFault` typed envelope (in place of a raw `byte[]` move) is a strengthening deviation: it preserves message identity for forensics AND is observable by the in-memory harness. Both deviations were logged and are not regressions.

---

_Verified: 2026-06-06T01:00:00Z_
_Verifier: Claude (gsd-verifier)_
