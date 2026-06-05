---
phase: 35-fault-intake-correlation
verified: 2026-06-05T00:00:00Z
status: human_needed
score: 5/6 must-haves verified
overrides_applied: 0
human_verification:
  - test: "Run: docker compose up -d --build keeper processor-sample orchestrator baseapi-service (Keeper MUST be rebuilt — stale container runs Phase-34 placeholder, emits no intake log). Wait all four services Healthy. Then: dotnet test tests/BaseApi.Tests -- --filter-class \"*KeeperFaultIntakeE2ETests\""
    expected: "GREEN. PollEsForLog returns a hit with resource.attributes.service.name = \"keeper\", attributes.CorrelationId == the tripped dispatch's correlationId (not a fresh Guid — proves the manual CorrelationId scope from FaultEntryStepDispatchConsumer works end-to-end), attributes.StepId == the tripped step, and body.text matching \"keeper fault intake\". Net-zero: no leftover skp:* keys after the run."
    why_human: "SC3 requires the rebuilt Keeper container running live against the full v3.7.0 compose stack (RabbitMQ + Redis + Postgres + OTLP collector + Elasticsearch). The test is an operator-gated RealStack E2E identical in precedent to Phases 31-34. The authored test plus its committed code are verified — only the live container run is outstanding."
---

# Phase 35: Fault Intake & Correlation Verification Report

**Phase Goal:** Wire the production fault-intake path — consuming the two execution-path `Fault<T>` events (`Fault<EntryStepDispatch>`, `Fault<ExecutionResult>`), extracting the inner message + 6-id correlation + `H`, opening the propagated execution log-scope, and confirming the `_error` record consolidates into the TTL'd forensic DLQ-1 (never Keeper's worklist).
**Verified:** 2026-06-05
**Status:** human_needed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | A single shared `ExecutionLogScope.BuildState(IExecutionCorrelated)` is the one source of truth for the 5-key execution scope dict | ✓ VERIFIED | `src/Messaging.Contracts/ExecutionLogScope.cs` line 24: `public static Dictionary<string, object> BuildState(IExecutionCorrelated ec)` — 5 Guid.Empty/empty-string guarded keys, no CorrelationId key |
| 2 | `InboundExecutionScopeConsumeFilter` delegates its scope-dict build to `BuildState` and behaves byte-identically | ✓ VERIFIED | `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs` line 28: `using (logger.BeginScope(ExecutionLogScope.BuildState(ec)))` — inline dict build removed, pass-through and Probe unchanged |
| 3 | Keeper runs two real production consumers colocated on the single durable queue `keeper-fault-recovery` | ✓ VERIFIED | `FaultEntryStepDispatchConsumer` implements `IConsumer<Fault<EntryStepDispatch>>`; `FaultExecutionResultConsumer` implements `IConsumer<Fault<ExecutionResult>>`; both definitions set `EndpointName = KeeperQueues.FaultRecovery`; `Program.cs` registers both via `AddConsumer<…,…>` |
| 4 | Each consumer double-unwraps, opens manual CorrelationId scope + 5-id exec scope, emits ONE Information log, and acks | ✓ VERIFIED | Both consumers: `var inner = context.Message.Message` (double-unwrap); nested `BeginScope([CorrelationKeys.LogScope] = inner.CorrelationId.ToString())` + `BeginScope(ExecutionLogScope.BuildState(inner))`; `LogInformation(…)` with structured params only; `return Task.CompletedTask`; StackTrace not logged |
| 5 | The hermetic scope test proves captured scope carries CorrelationId key AND the 5 exec ids (SC2) | ✓ VERIFIED | `tests/BaseApi.Tests/Keeper/KeeperFaultConsumerScopeTests.cs` — 3 tests: Test 1 asserts `scope[CorrelationKeys.LogScope] == correlationId.ToString()` AND `scope.Count == 5` for dispatch; Test 2 same for result consumer; Test 3 asserts Guid.Empty/empty-string skips with CorrelationId still present; reported 3/3 GREEN |
| 6 | A faulted message processed by the running Keeper container produces an ES log correlated to the original execution by correlationId + ids (SC3, observable end-to-end) | ? OPERATOR-PENDING | `KeeperFaultIntakeE2ETests.cs` is authored, committed (`1b64143`), builds 0/0, adds 0 hermetic tests (RealStack-trait excluded). The live `Assert.NotNull(hit)` against the REBUILT Keeper container has NOT been run this session (per Phase-31..34 operator-gate precedent — see Human Verification section) |

**Score:** 5/6 truths verified (Truth 6 is operator-pending, not failed)

### Deferred Items

Items not yet met but explicitly addressed in later milestone phases.

| # | Item | Addressed In | Evidence |
|---|------|-------------|----------|
| 1 | Consolidated TTL'd DLQ-1 topology build — transport-exhaustion record into a self-expiring forensic queue | Phase 36 — L2 Probe Loop & DLQs | REQUIREMENTS.md DLQ-01/DLQ-02/DLQ-04; 35-03-PLAN.md explicitly states "the consolidated TTL'd DLQ-1 BUILD is Phase 36 — do NOT build it here" |
| 2 | Shared error-transport configuration (INTAKE-03 full completion) | Phase 36 | REQUIREMENTS.md INTAKE-03 full consolidation requires Phase-36 DLQ topology |

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/Messaging.Contracts/ExecutionLogScope.cs` | `public static Dictionary<string,object> BuildState(IExecutionCorrelated ec)` | ✓ VERIFIED | 5 keys, 4 Guid.Empty guards, 1 IsNullOrEmpty guard, EntryId verbatim (no .ToString()), no CorrelationId key |
| `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs` | delegates to `ExecutionLogScope.BuildState(ec)` | ✓ VERIFIED | Line 28: `using (logger.BeginScope(ExecutionLogScope.BuildState(ec)))` — inline dict removed |
| `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` | `IConsumer<Fault<EntryStepDispatch>>` observe-and-ack with manual CorrelationId + exec scope | ✓ VERIFIED | Double-unwrap, nested BeginScope for CorrelationId + BuildState, LogInformation structured, return Task.CompletedTask, no StackTrace |
| `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` | `IConsumer<Fault<ExecutionResult>>` observe-and-ack | ✓ VERIFIED | Identical pattern; alias `using ExecutionResult = Messaging.Contracts.ExecutionResult` present |
| `src/Keeper/Consumers/FaultEntryStepDispatchConsumerDefinition.cs` | `ConsumerDefinition` with `EndpointName = KeeperQueues.FaultRecovery`, owns endpoint retry | ✓ VERIFIED | Contains `EndpointName = KeeperQueues.FaultRecovery` + exactly ONE `UseMessageRetry(r => r.Immediate(…))` call |
| `src/Keeper/Consumers/FaultExecutionResultConsumerDefinition.cs` | same `EndpointName`, intentional no-op `ConfigureConsumer` (Pitfall 3) | ✓ VERIFIED | `EndpointName = KeeperQueues.FaultRecovery`; `ConfigureConsumer` body is empty comment (single retry owner pattern) |
| `src/Keeper/Program.cs` | registers both fault consumers, no placeholder references | ✓ VERIFIED | `AddConsumer<FaultEntryStepDispatchConsumer, FaultEntryStepDispatchConsumerDefinition>()` and `AddConsumer<FaultExecutionResultConsumer, FaultExecutionResultConsumerDefinition>()` present; grep "Placeholder" == 0 |
| `src/Keeper/Consumers/PlaceholderConsumer.cs` | DELETED | ✓ VERIFIED | File absent; glob finds no Placeholder*.cs under src/Keeper/Consumers/ |
| `src/Keeper/Consumers/PlaceholderConsumerDefinition.cs` | DELETED | ✓ VERIFIED | File absent |
| `src/Keeper/Consumers/KeeperPlaceholder.cs` | DELETED | ✓ VERIFIED | File absent; grep "KeeperPlaceholder\|PlaceholderConsumer" in src/ == 0 |
| `tests/BaseApi.Tests/Keeper/KeeperFaultConsumerScopeTests.cs` | hermetic SC2 scope-capture proof | ✓ VERIFIED | 3 tests present; `CorrelationKeys.LogScope` referenced 5 times; no RealStack trait; 3/3 GREEN |
| `tests/BaseApi.Tests/Orchestrator/KeeperFaultIntakeE2ETests.cs` | RealStack SC3 proof | ✓ AUTHORED | File exists, `[Trait("Category", "RealStack")]` present, filters `service.name="keeper"` + CorrelationId + StepId + body.text wildcard, PollEsForLog present, ArmWrongTypePoisonAsync present, no DLQ/TTL scope creep; LIVE run operator-pending |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `InboundExecutionScopeConsumeFilter.cs` | `ExecutionLogScope.cs` | `ExecutionLogScope.BuildState(ec)` call | ✓ WIRED | Line 28 delegates directly |
| `Program.cs` | `FaultEntryStepDispatchConsumer` + `FaultExecutionResultConsumer` | `AddConsumer<,>` for both | ✓ WIRED | Both `AddConsumer<…,…>()` registrations present; placeholder removed |
| `FaultEntryStepDispatchConsumer.cs` | `ExecutionLogScope.cs` | `ExecutionLogScope.BuildState(inner)` | ✓ WIRED | Line 37: `logger.BeginScope(ExecutionLogScope.BuildState(inner))` |
| `FaultEntryStepDispatchConsumer.cs` | MEL log scope | `BeginScope([CorrelationKeys.LogScope] = inner.CorrelationId.ToString())` | ✓ WIRED | Line 36: the mandatory manual CorrelationId scope restore |
| `FaultExecutionResultConsumer.cs` | `ExecutionLogScope.cs` | `ExecutionLogScope.BuildState(inner)` | ✓ WIRED | Same pattern as dispatch consumer |
| `KeeperFaultIntakeE2ETests.cs` | running Keeper container ES logs | `PollEsForLog` on `service.name=keeper` + `attributes.CorrelationId` + `attributes.StepId` + `body.text` | ? OPERATOR-PENDING | Code authored and wired correctly; live run not yet executed |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|-------------------|--------|
| `FaultEntryStepDispatchConsumer.cs` | `inner` (the `IExecutionCorrelated` inner message) | `context.Message.Message` — double-unwrap of the MassTransit framework-deserialized `Fault<T>` envelope | Yes — framework provides the real envelope from RabbitMQ; inner is the verbatim original message | ✓ FLOWING |
| `KeeperFaultConsumerScopeTests.cs` | `capturing.Scopes` — the scope state captured by the logger double | In-memory MassTransit harness publishing `Fault<T>` via message initializer; CapturingProvider intercepts `BeginScope` calls | Yes — harness produces real MassTransit `Fault<T>` with non-empty correlationId/workflowId/stepId/processorId/executionId/entryId | ✓ FLOWING |

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| `ExecutionLogScope.BuildState` signature exists | `grep -c "public static Dictionary<string, object> BuildState(IExecutionCorrelated" src/Messaging.Contracts/ExecutionLogScope.cs` | 1 | ✓ PASS |
| Filter delegates to BuildState, inline dict removed | `grep -c "ExecutionLogScope.BuildState(ec)" src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs` + `grep -c "var state = new Dictionary" …` | 1 + 0 | ✓ PASS |
| Both consumers implement correct interfaces | `grep -c "IConsumer<Fault<EntryStepDispatch>>" FaultEntryStepDispatchConsumer.cs` + `grep -c "IConsumer<Fault<ExecutionResult>>" FaultExecutionResultConsumer.cs` | 1 + 1 | ✓ PASS |
| StackTrace not logged in consumers | `grep -c "StackTrace" src/Keeper/Consumers/Fault*Consumer.cs` | 0 | ✓ PASS |
| Placeholder files absent, no dangling src/ refs | glob + grep | No files, 0 matches | ✓ PASS |
| Single endpoint-retry owner | `grep -rc "UseMessageRetry" src/Keeper/Consumers/Fault*Definition.cs` total | 1 (EntryStepDispatch def only) | ✓ PASS |
| No DLQ/TTL scope creep in E2E test | `grep -c "x-message-ttl\|x-dead-letter-exchange\|keeper-dlq" KeeperFaultIntakeE2ETests.cs` | 0 | ✓ PASS |
| SC3 live run | `dotnet test tests/BaseApi.Tests -- --filter-class "*KeeperFaultIntakeE2ETests"` against rebuilt stack | NOT RUN (operator-gated) | ? SKIP |

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| KMET-04 | 35-01, 35-02, 35-03 | Keeper emits OTel logs carrying the correlationId + execution-scope ids propagated from the faulted message | ✓ HERMETICALLY SATISFIED (SC2); live ES proof OPERATOR-PENDING | `KeeperFaultConsumerScopeTests` 3/3 GREEN proves captured scope carries CorrelationId + 5 exec ids; SC3 live ES observation awaits operator run |
| INTAKE-03 | 35-02, 35-03 | Transport-exhaustion record consolidates into DLQ-1 (TTL'd forensic); Keeper recovers off Fault<T> events; never double-processes from error queue | PHASE-35 SLICE SATISFIED; DLQ-1 topology DEFERRED to Phase 36 | Phase-35 slice: Keeper binds `keeper-fault-recovery` to the `Fault<T>` message-type exchanges (not `_error` queue) via both `ConsumerDefinition` registrations; the consolidated DLQ-1 TTL topology build is explicitly Phase-36 work |

### Anti-Patterns Found

| File | Pattern | Severity | Impact |
|------|---------|----------|--------|
| None found | — | — | — |

No TODOs, FIXMEs, placeholder comments, empty implementations, hardcoded empty data, or StackTrace logging found in the Phase-35 modified files.

### Human Verification Required

#### 1. SC3 Live Operator Run — Running Keeper Container Correlated ES Log

**Test:**
1. Rebuild + bring up the stack (Keeper MUST be rebuilt — a stale container runs the Phase-34 placeholder and emits no intake log, Pitfall 5):
   ```
   docker compose up -d --build keeper processor-sample orchestrator baseapi-service
   ```
2. Wait for all four services Healthy (compose health gate).
3. Run the SC3 class-filtered RealStack test:
   ```
   dotnet test tests/BaseApi.Tests -- --filter-class "*KeeperFaultIntakeE2ETests"
   ```

**Expected:** GREEN. `PollEsForLog` returns a hit with:
- `resource.attributes.service.name = "keeper"`
- `attributes.CorrelationId` == the tripped dispatch's correlationId (the PROPAGATED id — not a fresh Guid, proves the manual CorrelationId scope from `FaultEntryStepDispatchConsumer` works end-to-end through the deployed container)
- `attributes.StepId` == the tripped step
- `body.text` matching `"keeper fault intake"`

Net-zero: no leftover `skp:*` keys after the run; teardown registered all run-minted `data:`/`flag:` + poison key; `POST /orchestration/stop` stops the workflow.

**Why human:** SC3 requires the rebuilt Keeper container running live against the full v3.7.0 compose stack. The test arms a WRONGTYPE poison on the dedup-gate flag key, lets `Immediate(N)` exhaust, waits for MassTransit to publish `Fault<EntryStepDispatch>` to the fault exchanges, then polls Elasticsearch for the Keeper-container intake log. This chain requires running Docker containers (RabbitMQ, Redis, Postgres, OTLP collector, Elasticsearch) — it cannot be verified programmatically without the live stack. This follows the established Phase-31..34 operator-gate precedent; the test was authored and committed (`1b64143`) but the live run was not performed in this session.

**Failure triage:** If `PollEsForLog` times out with no `service.name=keeper` hit, the most likely causes are: (a) stale Keeper container — the `--build keeper` step was skipped; (b) `OTEL_EXPORTER_OTLP_ENDPOINT` not wired (the live env-var knob, NOT the appsettings key). Confirm the Keeper container's logs show the `Fault<EntryStepDispatch>` consumed, then re-run.

### Gaps Summary

No code-level gaps were found. All hermetically verifiable must-haves are satisfied:

- `ExecutionLogScope.BuildState` exists as the single-source-of-truth 5-key dict builder (no CorrelationId, correct skip rules)
- `InboundExecutionScopeConsumeFilter` delegates to it byte-identically
- Both `Fault<T>` consumers exist on `keeper-fault-recovery` (double-unwrap, manual CorrelationId scope, BuildState exec scope, ONE Information log, observe-and-ack)
- `KeeperFaultConsumerScopeTests` proves SC2 hermetically (CorrelationId + 5 exec ids, skip rules)
- Placeholder files fully deleted with no dangling references
- Single endpoint-retry owner across the two definitions (Pitfall 3 handled)
- `KeeperFaultIntakeE2ETests` authored with correct WRONGTYPE trip, ES correlation query, net-zero teardown, no DLQ scope creep

The only outstanding item is the operator-gated live run of SC3 (Truth 6). Per the Phase-31..34 precedent, this is a do-not-block gate: the code is complete and verified at the hermetic level; the live ES observation awaits the operator's rebuild-and-run.

---

_Verified: 2026-06-05_
_Verifier: Claude (gsd-verifier)_
