# Phase 33: Fault-Recovery Spike (De-Risk) - Research

**Researched:** 2026-06-05
**Domain:** MassTransit 8.5.5 `Fault<T>` pub/sub bind → unwrap → re-inject-by-type → `flag[H]`-collapse (RealStack xUnit v3 E2E spike)
**Confidence:** HIGH

## Summary

This is a **proof spike**, not a build. ~80% of the machinery already exists in `IdempotentExactlyOnceE2ETests` (the clone source) and the reverted Phase-32 code is recoverable verbatim from git. The four assumptions the spike must prove (pub/sub bind both fault types while command-faults are not delivered; `.Message.Message` + 6-id + `H` unwrap; re-inject verbatim to origin endpoint by type via `Send`; collapse on duplicate via the receiver's surviving Phase-31 `flag[H]` gate) are all backed by proven precedents in this codebase. Every load-bearing mechanism has a `[VERIFIED]` precedent below.

The single flagged research item — **the orchestrator-result poison surface (D-06)** — is RESOLVED in favor of a **deterministic live trip**. The orchestrator `ResultConsumer.Consume` reads `flag[m.H]` via `StringGetAsync` as its *first* Redis operation (`ResultConsumer.cs:65`), BEFORE the L1 read and BEFORE any business branch. That read is INFRA (no catch) → propagates to `Immediate(Limit)` → `Fault<ExecutionResult>` is published. Because `m.H` is fully a-priori computable test-side (the SampleProcessor echoes its payload, so the whole result-path hash chain is deterministic — see Architecture Pattern 4), the test can pre-seed `skp:flag:{resultH}` as a Redis LIST and force WRONGTYPE on every attempt. **The synthetic `Fault<ExecutionResult>` fallback (D-06) is therefore NOT needed** — but it is documented below as a safety net because the result-path `resultH` derivation has more moving parts than the dispatch-path `dispatchH` (it depends on the manifest serialization), so if the live result-trip proves fiddly in the operator run, the fallback keeps bind+unwrap+re-inject provable.

**Primary recommendation:** Clone `IdempotentExactlyOnceE2ETests`; resurrect the reverted `FaultConsumerBindingFacts` double-`.Message` pattern + the `CancelledCircuitBreakerE2ETests` WRONGTYPE recipe from git; stand up a short-lived in-test `IBusControl` registering `IConsumer<Fault<EntryStepDispatch>>` + `IConsumer<Fault<ExecutionResult>>` on temporary endpoints against live `sk-rabbitmq`; trip BOTH faults live via WRONGTYPE (dispatch: poison `skp:data:{HashBlob(payload)}`; result: poison `skp:flag:{resultH}`); re-inject the extracted `Fault<T>.Message` verbatim via `GetSendEndpoint(queue:{procId:D})` / `GetSendEndpoint(queue:orchestrator-result)` + `Send`; prove collapse by double-`Send` + one-effect assertion. Scope the live half as an `autonomous: false` operator runbook (Phase-31/32 precedent).

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** Vehicle = ONE throwaway-but-kept RealStack xUnit E2E test, `FaultRecoverySpikeE2ETests`, `[Trait("Category","RealStack")]` (excluded from hermetic suite). Cloned from `IdempotentExactlyOnceE2ETests` — reuses its entire rig (embedded-SourceHash reflection, GET-or-create Processor row, liveness poll, `PollEsForLog`, `RealStackWebAppFactory`, net-zero `skp:*` teardown).
- **D-02:** Only genuinely new machinery = a short-lived in-test `IBusControl` on live `sk-rabbitmq`, registering `IConsumer<Fault<EntryStepDispatch>>` + `IConsumer<Fault<ExecutionResult>>` on a temporary endpoint (mirrors the file's existing short-lived-`IBusControl`-to-`Send` trick — here to CATCH faults).
- **D-03:** NO production Keeper code in Phase 33. Keeper console is Phase 34. Findings land in `33-SUMMARY` + feed `33-VERIFICATION`.
- **D-04:** Disposition = KEEP the test as a standing RealStack regression guard. Cheap + RealStack-gated, so not deleted after findings captured.
- **D-05:** Trip BOTH fault types LIVE via the WRONGTYPE recipe symmetrically. Dispatch: pre-seed `skp:data:{hash}` as a Redis LIST so the processor's `StringSetAsync` output write throws WRONGTYPE every attempt → `Immediate(N)` exhausts → MassTransit publishes the fault. Result: poison the orchestrator `ResultConsumer`'s a-priori-computable `flag[m.H]` key the same WRONGTYPE way.
- **D-06:** Documented FALLBACK for the result type only — if a clean deterministic live trip proves fiddly, publish a synthetic `Fault<ExecutionResult>` to still prove bind + unwrap + re-inject-to-`orchestrator-result`. The novel risk is fully exercised by the dispatch trip; the result trip mainly proves the second endpoint/type works. **(Resolved: live trip IS deterministic — see Architecture Pattern 4. Fallback retained as safety net.)**
- **D-07:** Re-inject forwards the EXTRACTED `Fault<T>.Message` instance VERBATIM (same `H`, no hand-reconstruction) to its origin endpoint by type via `GetSendEndpoint(...)` + `Send` (NOT `Publish`, NO orchestrator round-trip). Verbatim forward guarantees the receiver's gate sees the identical `H` — the point of the collapse proof.
- **D-08:** Exactly-once + duplicate-collapse proof = re-inject the same extracted message TWICE, assert ONE downstream effect via `PollEsForLog` + a hit-count probe over a settle window. The receiver's surviving Phase-31 `flag[H]` (processor) / `flag[m.H]` (orchestrator) gate drops the second.
- **D-09:** Active synthetic negative — publish `Fault<StartOrchestration>` + `Fault<StopOrchestration>` and assert the spike's two execution-fault consumers record ZERO captures over a settle window. Tests that the bindings are type-scoped. (Rejected: structural-only tautology; organic `Start`-fault trip — its main exception `WorkflowRootNotFoundException` is `Ignore`d.)
- **D-10:** Keep `{procId}_error` as the TTL'd forensic copy consolidating source-agnostically into DLQ-1 (Phase 36, INTAKE-03/DLQ-02) — NEVER Keeper's worklist. Operator triage axis is MECHANISM (DLQ-1 forensic-TTL vs DLQ-2 `keeper-dlq` probe-give-up), not origin component. Phase 33 RECORDS this only — no `_error` topology change.
- **D-11:** NO metric work in Phase 33. No producer-side `*_exhausted` counter added/designed. The reverted `workflow_cancelled` stays gone. Existing exhaustion signals left as-is and NOT asserted by the spike.

### Claude's Discretion
- Re-inject plumbing details (the `GetSendEndpoint` URI construction, the short-lived `IBusControl` lifecycle/teardown), settle-window durations, the exact `PollEsForLog` query shape, and which single processor/workflow topology the spike seeds — all builder choices within the established precedent rig.

### Deferred Ideas (OUT OF SCOPE)
- Producer-side `*_exhausted` business metric (and emitting `GetRetryAttempt()` as a tag/log field) — explicitly LEFT AS-IS per user direction. Exhaustion/attempt observability is Keeper-side only (`keeper_fault_consumed`, `keeper_l2_probe_failed`, DLQ depths — KMET-02/03, Phase 38). A producer-side exhaustion rate signal is a future-milestone candidate, not v3.7.0.
- No reviewed-but-deferred todos matched this phase.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| INTAKE-01 | Pub/sub bind BOTH fault types (`Fault<EntryStepDispatch>` + `Fault<ExecutionResult>`); command-faults (`Fault<StartOrchestration>`/`Fault<StopOrchestration>`) NOT consumed | MassTransit publishes `Fault<T>` to a **durable fanout message-type exchange** `MassTransit:Fault--{MessageType}` [CITED: masstransit.io/documentation/configuration/topology/message]. An external `IBusControl` registering `IConsumer<Fault<T>>` auto-declares a temporary queue bound to THAT exchange → catches faults from ALL producer replicas. Type-scoping is structural: a `Fault<EntryStepDispatch>` consumer binds only the `Fault--EntryStepDispatch` exchange, never `Fault--StartOrchestration`. Proven hermetically by the reverted `FaultConsumerBindingFacts` [VERIFIED: git 3aca386]. |
| INTAKE-02 | Unwrap to inner message + full 6-id `IExecutionCorrelated` tuple + `H` from `Fault<T>.Message` | `context.Message.Message` (double `.Message`) IS the original instance — `FaultConsumerBindingFacts` PROVED `Fault<EntryStepDispatch>.Message.WorkflowId` round-trips (not `Guid.Empty`), no fallback [VERIFIED: git 3aca386]. Both `EntryStepDispatch` and `ExecutionResult` implement `IExecutionCorrelated` (6 ids) + carry `H` [VERIFIED: EntryStepDispatch.cs:11-18, ExecutionResult.cs:7-25]. |
| INTAKE-04 | Re-inject to origin endpoint by type — `queue:{processorId:D}` (dispatch), `queue:orchestrator-result` (result) | `GetSendEndpoint(new Uri($"queue:{procId:D}"))` + `Send` is the proven dispatch re-inject [VERIFIED: IdempotentExactlyOnceE2ETests.cs:272-273]. Result endpoint name = `OrchestratorQueues.Result` = `"orchestrator-result"` [VERIFIED: OrchestratorQueues.cs:16]; processor sends results to `queue:{OrchestratorQueues.Result}` [VERIFIED: EntryStepDispatchConsumer.cs:246]. |
| PROBE-06 | No Keeper dedup; re-injection idempotency rides the receiver's `flag[H]` gate | Processor `flag[dispatch.H]=="Ack"` gate intact [VERIFIED: EntryStepDispatchConsumer.cs:76-84]; orchestrator `flag[m.H]=="Ack"` gate intact [VERIFIED: ResultConsumer.cs:65-72]. Both SURVIVED the 32.1 revert (the revert removed only the breaker/cancelled-marker — STATE.md line 48 confirms both flag gates KEPT byte-intact). |
</phase_requirements>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Fault pub/sub capture | Spike in-test `IBusControl` (external subscriber) | RabbitMQ message-type exchange | The spike plays the Keeper-prototype role: a NEW subscriber binding the fault fanout exchange, separate from the producing replicas. |
| Fault trip induction | Redis L2 (poison key) + processor/orchestrator consumers | MassTransit retry pipeline | The trip is an INFRA fault forced by WRONGTYPE on the consumer's first/output Redis op → `Immediate(N)` → MassTransit auto-publishes `Fault<T>`. |
| Re-inject by type | Spike in-test `IBusControl` (`Send`) | RabbitMQ direct queue | `Send` (not `Publish`) targets the exact origin queue — no orchestrator round-trip, no fan-out. |
| Duplicate collapse | RECEIVER's Phase-31 `flag[H]` gate (processor/orchestrator) | Redis CAS | Idempotency is OWNED by the existing receiver gate; the spike/Keeper adds nothing (PROBE-06). |
| Downstream-effect observation | Elasticsearch (`PollEsForLog` + hit-count) | OTLP log export | The one-effect proof is an ES log presence + count, the live inverse of the StepB4×2 bug. |
| Net-zero teardown | `RealStackWebAppFactory.L2KeysToCleanup` | Redis | Every poison/flag/data key registered for deletion so the close-gate triple-SHA holds. |

## Standard Stack

### Core (all already in the solution — nothing new to install)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MassTransit | 8.5.5 | Bus, `Fault<T>` framework type, `IBusControl`, `IConsumer<T>`, `GetSendEndpoint`/`Send` | The project's message backbone; `Fault<T>` is a MassTransit framework type (NOT in `Messaging.Contracts`) [VERIFIED: Directory.Packages.props ref BaseApi.Core.csproj:103; CONTEXT canonical_refs] |
| MassTransit.RabbitMQ | 8.5.5 | RabbitMQ transport, message-type fanout topology, `Bus.Factory.CreateUsingRabbitMq` | Live `sk-rabbitmq` transport |
| StackExchange.Redis | (solution-pinned) | WRONGTYPE poison (`ListLeftPushAsync`/`ListRightPushAsync` on a flag/data key), flag polling, net-zero scan | The proven WRONGTYPE-trip vector [VERIFIED: git a6c6825 `ArmWrongTypePoisonAsync`] |
| xUnit v3 | (solution-pinned) | `[Fact]`, `[Trait]`, `TestContext.Current.CancellationToken` | The test framework; `TestContext.Current.CancellationToken` is the v3 idiom [VERIFIED: IdempotentExactlyOnceE2ETests.cs:87] |
| Cronos | (transitive) | `* * * * *` cron drive | Used by the workflow seed (cron fire) — no direct test dependency |

### Supporting (from the clone source's rig — reuse verbatim)
| Asset | Purpose | When to Use |
|-------|---------|-------------|
| `RealStackWebAppFactory` (private nested in clone) | Points in-process WebApi at host stack (RMQ 5673, Redis 6380, PG 5433, OTLP 4317); `L2KeysToCleanup`/`ParentIndexMembersToSrem` net-zero teardown | Copy verbatim from `IdempotentExactlyOnceE2ETests.cs:510-588` |
| `ElasticsearchTestClient` + `PollEsForLog` | Downstream-effect proof on ES | Copy the `BuildEffectQuery`/`CountEsHitsAsync` shape [VERIFIED: IdempotentExactlyOnceE2ETests.cs:283-341] |
| `PollForHealthyLivenessAsync` | Truthful container-Healthy gate before Start | Verbatim [VERIFIED: …:345-380] |
| `SeedProcessorAsync`/`SeedStepAsync`/`SeedWorkflowAsync` | GET-or-create Processor + Steps + Workflow via HTTP | Verbatim [VERIFIED: …:442-499] |
| `ScanKeys(discriminator)` | Net-zero `skp:data:*`/`skp:flag:*` enumeration | Verbatim [VERIFIED: …:418-438] |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Live WRONGTYPE result-trip | Synthetic `Fault<ExecutionResult>` publish (D-06 fallback) | Synthetic skips the genuine `Immediate(N)`-exhaustion publish path but still proves bind+unwrap+re-inject. Use ONLY if the live result-trip proves fiddly in the operator run (it should not — see Pattern 4). |
| `AddMassTransitTestHarness` (in-memory) | Live `Bus.Factory.CreateUsingRabbitMq` | The hermetic harness (used by `FaultConsumerBindingFacts`) proves the double-`.Message` semantics but NOT the live cross-replica fanout-exchange bind — the spike needs the live broker (RealStack). |

**Installation:** None. All packages are already pinned via CPM (`Directory.Packages.props`).

**Version verification:** MassTransit 8.5.5 is the solution-pinned version [VERIFIED: BaseApi.Core.csproj:103 comment "MassTransit 8.5.5, CPM"]. No registry lookup needed — the spike must match the running containers' pinned version exactly; bumping it is out of scope.

## Architecture Patterns

### System Architecture Diagram

```
                         ┌──────────────────────────────────────────────────┐
                         │  FaultRecoverySpikeE2ETests (in-process WebApi +   │
                         │  short-lived in-test IBusControl on sk-rabbitmq)   │
                         └──────────────────────────────────────────────────┘
                                  │ (1) seed Processor/Steps/Workflow via HTTP
                                  │ (2) POST /orchestration/start  + pre-seed WRONGTYPE poison key
                                  ▼
   ┌─────────────┐  fire   ┌──────────────┐  Send queue:{proc:D}  ┌────────────────────────┐
   │ Orchestrator│────────▶│ StepDispatcher│──────────────────────▶│ processor-sample        │
   │ (cron fire) │         └──────────────┘                        │ EntryStepDispatchConsumer│
   └─────────────┘                                                 └────────────────────────┘
         ▲                                                              │ output StringSetAsync
         │ Send queue:orchestrator-result                              │   hits WRONGTYPE (poison)
         │                                                             ▼  every retry → Immediate(N) exhausts
   ┌──────────────┐                                          MassTransit auto-PUBLISH
   │ ResultConsumer│  flag[m.H] StringGetAsync                 Fault<EntryStepDispatch>
   │ (1st Redis op)│  hits WRONGTYPE (poison) ───────────────────────┐  to fanout exchange
   └──────────────┘    every retry → Immediate(N) exhausts            │  MassTransit:Fault--EntryStepDispatch
         │                                                            ▼
         └── MassTransit auto-PUBLISH Fault<ExecutionResult> ──▶ ┌──────────────────────────────────┐
             to fanout exchange Fault--ExecutionResult ────────▶ │ SPIKE temp queue binds BOTH        │
                                                                 │ IConsumer<Fault<EntryStepDispatch>>│
                                                                 │ IConsumer<Fault<ExecutionResult>>  │
                                                                 └──────────────────────────────────┘
                                                                    │ (3) unwrap context.Message.Message
                                                                    │     (6 ids + H), VERBATIM
                                                                    │ (4) GetSendEndpoint + Send  ×2
                                                                    ▼
                                       re-inject queue:{proc:D} / queue:orchestrator-result
                                                                    │
                                                                    ▼ receiver flag[H]=="Ack" gate
                                                          delivery 1 → effect ; delivery 2 → DROP
                                                                    │
                                                                    ▼
                                              Elasticsearch: PollEsForLog + hit-count == 1
                                              (negative: Fault<Start/Stop>Orchestration → 0 captures)
```

### Pattern 1: Live `Fault<T>` capture via short-lived `IBusControl` (D-02 / INTAKE-01)

**What:** Stand up an `IBusControl` against live `sk-rabbitmq` with TWO `ReceiveEndpoint`s, each binding one `IConsumer<Fault<T>>`. MassTransit declares a temporary queue per endpoint bound to the durable fanout exchange `MassTransit:Fault--EntryStepDispatch` / `--ExecutionResult`, so the spike receives faults from ALL producer replicas.

**When to use:** The capture half of the spike.

```csharp
// Source: pattern composed from IdempotentExactlyOnceE2ETests.cs:265-279 (IBusControl-to-Send)
//         + git 3aca386 FaultConsumerBindingFacts (IConsumer<Fault<T>> + double .Message).
// NOTE: the clone's SendDispatchAsync builds a Send-only bus; here we add ReceiveEndpoints with consumers.
var captured = new ConcurrentBag<(string h, Guid corr, Guid wf, Guid step, Guid proc, Guid entry, Guid exec, object inner)>();

var bus = Bus.Factory.CreateUsingRabbitMq(cfg =>
{
    cfg.Host("localhost", 5673, "/", h => { h.Username("guest"); h.Password("guest"); });

    // Temporary auto-delete endpoints; ConfigureConsumeTopology binds the Fault--T fanout exchange.
    cfg.ReceiveEndpoint("spike-fault-dispatch", e =>
    {
        e.Consumer(() => new FaultDispatchProbe(captured));   // IConsumer<Fault<EntryStepDispatch>>
    });
    cfg.ReceiveEndpoint("spike-fault-result", e =>
    {
        e.Consumer(() => new FaultResultProbe(captured));     // IConsumer<Fault<ExecutionResult>>
    });
});
await bus.StartAsync(ct);
try { /* drive the trip, await capture, re-inject */ }
finally { await bus.StopAsync(ct); }   // IBusControl is NOT IAsyncDisposable — bracket Start/Stop (clone:264)
```

Inside each probe: `var m = context.Message.Message;` then read `m.CorrelationId, m.WorkflowId, m.StepId, m.ProcessorId, m.EntryId, m.ExecutionId, m.H` — the FULL 6-id tuple + H [VERIFIED: EntryStepDispatch.cs / ExecutionResult.cs both `: IExecutionCorrelated`].

### Pattern 2: WRONGTYPE deterministic live trip — DISPATCH (D-05)

**What:** SampleProcessor echoes its payload → output content address = `HashBlob(payload)` → pre-create `skp:data:{HashBlob(payload)}` as a Redis LIST → the processor's `StringSetAsync` output write (`EntryStepDispatchConsumer.cs:178`, INFRA, no catch) throws WRONGTYPE every attempt → `Immediate(Limit)` exhausts → `Fault<EntryStepDispatch>` published.

```csharp
// Source: git a6c6825 CancelledCircuitBreakerE2ETests (ArmWrongTypePoisonAsync + TripPayload).
const string TripPayload = "spike-dispatch-trip";
var poisonedKey = L2ProjectionKeys.ExecutionData(MessageIdentity.HashBlob(TripPayload));  // skp:data:{64hex}
// ArmWrongTypePoison: await db.ListRightPushAsync(poisonedKey, "wrongtype");  // makes StringSet throw WRONGTYPE
factory.L2KeysToCleanup.Add(poisonedKey);
// Send a dispatch carrying JsonSerializer.Serialize(TripPayload) to queue:{procId:D}.
```

### Pattern 3: WRONGTYPE deterministic live trip — RESULT (D-06 RESOLVED)

**What:** The orchestrator `ResultConsumer.Consume` reads `flag[m.H]` via `StringGetAsync` as its FIRST Redis op (`ResultConsumer.cs:60,65`), BEFORE the L1 read and BEFORE any business branch. WRONGTYPE there is INFRA (no catch) → `Immediate(Limit)` exhausts → `Fault<ExecutionResult>` published. The result's `m.H` is a-priori computable (Pattern 4) → pre-seed `skp:flag:{resultH}` as a Redis LIST.

```csharp
// Source: ResultConsumer.cs:65 — the StringGetAsync(Flag(m.H)) read is the trip surface.
var resultH = /* computed per Pattern 4 */;
var poisonedFlag = L2ProjectionKeys.Flag(resultH);   // skp:flag:{64hex}
// ArmWrongTypePoison: await db.ListRightPushAsync(poisonedFlag, "wrongtype");
factory.L2KeysToCleanup.Add(poisonedFlag);
// Drive a normal round-trip so the processor sends an ExecutionResult with H=resultH to queue:orchestrator-result;
// the ResultConsumer's first StringGetAsync(Flag(resultH)) hits the LIST → WRONGTYPE → exhaust → Fault<ExecutionResult>.
```

**Caveat (why D-06 fallback is retained):** unlike `flag[dispatch.H]` which the test pre-seeds (Pattern 2 analog), `flag[resultH]` is seeded by the PROCESSOR's outbound pre-write (`EntryStepDispatchConsumer.cs:210-212`) at `expiry: ExecutionDataTtlSeconds`. The poison LIST must be created and the processor's `StringSetAsync(Flag(resultH),"Pending")` write itself would ALSO hit WRONGTYPE FIRST (it runs before the result is sent), tripping a `Fault<EntryStepDispatch>` on the PROCESSOR side rather than a `Fault<ExecutionResult>` on the orchestrator side. **This is the fiddly part the planner must resolve at plan time:** the cleanest result-trip is to let the processor complete normally (do NOT poison `flag[resultH]` before the processor's pre-write) and instead poison it in the window AFTER the processor's pre-write but BEFORE the orchestrator consumes — OR use the D-06 synthetic fallback. See Open Question 1.

### Pattern 4: A-priori result-path `resultH` computation (resolves D-06 feasibility)

The entire result-path hash chain is deterministic test-side because SampleProcessor echoes its payload:

```
payload         = "spike-result-trip"                                  (test chooses)
config          = JsonSerializer.Serialize(payload)                    (the dispatch Payload)
ProcessAsync    → [ new ProcessResult(payload) ]                       (SampleProcessor.cs:37-38, echo)
blobHash        = MessageIdentity.HashBlob(payload)                    (output content address)
manifestJson    = JsonSerializer.Serialize(new[]{ blobHash })         ( ["<64hex>"] )
manifestEntryId = MessageIdentity.HashManifest(manifestJson)          (EntryStepDispatchConsumer.cs:196-197)
resultH         = MessageIdentity.ComputeH(corr, wf, step, proc, manifestEntryId)   (…:208-209)
```
[VERIFIED: SampleProcessor.cs:27-39 + EntryStepDispatchConsumer.cs:162,196-209 + MessageIdentity.cs:36-47]. Every input (corr chosen test-side, wf/step/proc from the seeded topology) is known → `resultH` is fully computable → `skp:flag:{resultH}` is addressable a-priori. **The result trip IS deterministic.**

### Pattern 5: Re-inject verbatim by type (D-07 / INTAKE-04)

```csharp
// Source: IdempotentExactlyOnceE2ETests.cs:272-273 (dispatch); OrchestratorQueues.cs:16 (result name).
var inner = (EntryStepDispatch)captured.inner;                          // the VERBATIM extracted instance
var ep = await bus.GetSendEndpoint(new Uri($"queue:{inner.ProcessorId:D}"));   // dispatch origin by type
await ep.Send(inner, ct);                                               // same H — NOT Publish, no round-trip
// For ExecutionResult:
var ep2 = await bus.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}")); // "queue:orchestrator-result"
await ep2.Send((ExecutionResult)captured.inner, ct);
```

### Pattern 6: Duplicate-collapse proof (D-08 / PROBE-06)

Re-inject the SAME extracted instance TWICE; the receiver's `flag[H]=="Ack"` gate drops the second. BUT the clone's exactly-once mechanic depends on a `flag[H]="Pending"` SEED existing so the receiver's `When.Exists` Pending→Ack flip has a key to flip [VERIFIED: IdempotentExactlyOnceE2ETests.cs:162-175 + EntryStepDispatchConsumer.cs:225]. On a re-injected FAULTED message the original `flag[H]="Pending"` seed may already exist (the StepDispatcher pre-wrote it before the original Send, `StepDispatcher.cs:59`) — the planner must confirm whether the poison/fault path left `flag[dispatch.H]` in "Pending" (it did: the processor's WRONGTYPE fault is on the DATA write, AFTER the inbound flag read but the inbound flag was never flipped to Ack because the effect never completed). So delivery 1 of the re-inject produces the effect + flips Pending→Ack, delivery 2 sees Ack → dropped. Mirror the clone's `PollForFlagAckAsync` between the two sends.

### Anti-Patterns to Avoid
- **Re-`Publish` instead of `Send`:** Publish fans out to ALL bound consumers / could loop back through the orchestrator. D-07 mandates `Send` to the exact origin queue. [VERIFIED: StepDispatcher.cs:62-63 uses Send; ResultConsumer dead-letters via Send only.]
- **Re-writing `flag[H]="Pending"` before each re-inject:** resets the gate Ack→Pending, re-arms it, and lets the duplicate leak — defeating the collapse proof [VERIFIED: IdempotentExactlyOnceE2ETests.cs:162-168 warns of exactly this].
- **Hand-reconstructing the inner message from the 6 ids:** drift risk on `H`. Forward `context.Message.Message` verbatim (D-07).
- **Leaving the cron workflow running / not net-zeroing poison keys:** churns the close-gate triple-SHA. POST `/orchestration/stop` in teardown + register every poison/data/flag key in `L2KeysToCleanup` [VERIFIED: clone:203-218; MEMORY close-gate churn note].
- **Poisoning `flag[resultH]` before the processor's outbound pre-write:** trips a processor-side `Fault<EntryStepDispatch>` instead of the intended orchestrator-side `Fault<ExecutionResult>` (Pattern 3 caveat).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Fault capture | Custom queue binding / AMQP consumer | `IConsumer<Fault<T>>` on an `IBusControl` ReceiveEndpoint | MassTransit auto-binds the `Fault--T` fanout exchange; manual AMQP would miss the topology contract [CITED: masstransit.io message topology] |
| Inner-message extraction | Header parsing / JSON re-deserialize | `context.Message.Message` (double `.Message`) | Proven verbatim instance, no fallback [VERIFIED: git 3aca386] |
| Deterministic trip | Container restart / broker teardown | WRONGTYPE LIST poison on a content-addressed key | Fully external, reproducible, no infra mutation [VERIFIED: git a6c6825] |
| Identity hashing | Re-implement SHA-256 hex | `MessageIdentity.ComputeH/HashBlob/HashManifest` | Single canonical hash path; any second canonicalization desyncs `H` [VERIFIED: MessageIdentity.cs:1-9 D-04] |
| Dedup | New Keeper-side dedup | Receiver's existing `flag[H]` CAS gate | PROBE-06 mandates riding the receiver gate; Keeper adds nothing |
| Net-zero teardown | Manual key tracking | `RealStackWebAppFactory.L2KeysToCleanup` + `ScanKeys` | Reused verbatim from the clone |

**Key insight:** The spike is 80% clone + 15% git-recovered Phase-32 patterns + 5% genuinely new (the dual-`Fault<T>` capture endpoints). Almost nothing should be authored from scratch.

## Runtime State Inventory

> Spike is additive (one new test file) — no rename/refactor. But the LIVE half mutates live Redis, so the net-zero surface is inventoried.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | Live Redis `skp:data:{64hex}` (round-trip output), `skp:flag:{64hex}` (dedup state), WRONGTYPE poison LIST(s) at `skp:data:{HashBlob(payload)}` and/or `skp:flag:{resultH}` | Register ALL in `L2KeysToCleanup`; net-zero scan post-run (clone:213-218 pattern). |
| Live service config | n8n / external services: none touched. The cron workflow self-reschedules — must be STOPPED in teardown | `POST /orchestration/stop` in teardown (NET-ZERO-31) [VERIFIED: clone:203-207]. |
| OS-registered state | None — verified by phase scope (one test file, no scheduler/service registration). | None. |
| Secrets/env vars | RealStackWebAppFactory sets host-stack env vars (RMQ/Redis/PG/OTLP) in-ctor and Restores in DisposeAsync — reused verbatim, code-only | None — reused as-is [VERIFIED: clone:544-556]. |
| Build artifacts | The live half requires REBUILT processor-sample/orchestrator/baseapi containers (embedded SourceHash must match host build) | Operator runbook step: `docker compose up -d --build processor-sample orchestrator baseapi-service` before the live run [VERIFIED: MEMORY + STATE.md:121]. |

## Common Pitfalls

### Pitfall 1: Result-trip poisons the wrong side
**What goes wrong:** Pre-seeding `skp:flag:{resultH}` as a LIST before the processor runs trips a processor-side `Fault<EntryStepDispatch>` (the processor's outbound `StringSetAsync(Flag(resultH),"Pending")` hits WRONGTYPE first), not the intended orchestrator-side `Fault<ExecutionResult>`.
**Why it happens:** The processor pre-writes `flag[resultH]` BEFORE sending the result (`EntryStepDispatchConsumer.cs:210-212`); that write precedes the orchestrator's read.
**How to avoid:** Either (a) poison `flag[resultH]` only in the window after the processor's pre-write and before the orchestrator consumes (timing-sensitive), or (b) use the D-06 synthetic `Fault<ExecutionResult>` fallback. The dispatch trip (Pattern 2) has no such ambiguity and fully exercises the novel risk.
**Warning signs:** The spike captures a `Fault<EntryStepDispatch>` when it expected a `Fault<ExecutionResult>`.

### Pitfall 2: Re-inject leaks the duplicate (flag re-armed)
**What goes wrong:** A second downstream effect appears for the re-injected identity.
**Why it happens:** `flag[H]` was re-seeded to "Pending" between the two sends, re-arming the gate; or delivery 2 raced delivery 1 before the flip.
**How to avoid:** Pre-seed `flag[H]="Pending"` ONCE; `PollForFlagAckAsync` between the two sends so delivery 2 deterministically observes "Ack" [VERIFIED: clone:227-260].
**Warning signs:** `CountEsHitsAsync` returns 2.

### Pitfall 3: Fault never published (retry budget not exhausted)
**What goes wrong:** The trip throws but no `Fault<T>` arrives at the spike.
**Why it happens:** The WRONGTYPE is on a CAUGHT (business) path, not the INFRA path; or `Immediate(Limit)` not exhausted.
**How to avoid:** Poison ONLY the INFRA Redis ops — processor output `StringSetAsync` (`:178`, no catch) and orchestrator `flag[m.H]` `StringGetAsync` (`:65`, no catch). `Limit` defaults to 3 (`Immediate(3)`), so the trip needs 4 deliveries (attempts 0,1,2,3) to exhaust [VERIFIED: RetryOptions.cs:10 + git 998dd49 RetryAttemptNumberingFacts: attempts 0,1,2,3; total = Limit+1; Fault published on exhaustion].
**Warning signs:** Spike capture count == 0; `{procId}_error` queue does not grow.

### Pitfall 4: Spike consumer captures command-faults (D-09 negative proof fails)
**What goes wrong:** Publishing `Fault<StartOrchestration>` causes a spike capture.
**Why it happens:** It shouldn't — the spike binds only `Fault<EntryStepDispatch>` + `Fault<ExecutionResult>` exchanges. If it captures, a binding/topology bug exists.
**How to avoid:** Assert ZERO captures over a settle window after publishing the two command-faults [VERIFIED: D-09]. The negative proof is the structural type-scoping guarantee made observable.

### Pitfall 5: Live container SourceHash mismatch → liveness gate fails
**What goes wrong:** `PollForHealthyLivenessAsync` times out.
**Why it happens:** The host-built `Processor.Sample` embedded SourceHash diverges from the running container.
**How to avoid:** Operator rebuilds the three containers before the live run (Runtime State Inventory → Build artifacts).

## Code Examples

### Recovering the reverted Phase-32 patterns from git (planner: resurrect these)
```bash
# Double-.Message unwrap + live-fault-publication proof (hermetic precedent for the spike consumers):
git show 3aca386:tests/BaseApi.Tests/Orchestrator/FaultConsumerBindingFacts.cs

# WRONGTYPE deterministic live-trip recipe (ArmWrongTypePoisonAsync + TripPayload + SendDispatchAsync):
git show a6c6825:tests/BaseApi.Tests/Orchestrator/CancelledCircuitBreakerE2ETests.cs

# Fault<EntryStepDispatch> consumer binding + endpoint/retry config:
git show 33b1d8b:src/Orchestrator/Consumers/FaultUnscheduleConsumer.cs
git show 33b1d8b:src/Orchestrator/Consumers/FaultUnscheduleConsumerDefinition.cs

# GetRetryAttempt()==Limit exhaustion boundary (Immediate(Limit) → Limit+1 deliveries → Fault):
git show 998dd49:tests/BaseApi.Tests/Orchestrator/RetryAttemptNumberingFacts.cs
```

### Inner-message unwrap probe (the spike's consumer body)
```csharp
// Source: git 3aca386 FaultConsumerBindingFacts:47-52 — the proven double-.Message extraction.
public Task Consume(ConsumeContext<Fault<EntryStepDispatch>> context)
{
    var m = context.Message.Message;   // inner EntryStepDispatch (the VERBATIM original instance)
    // full 6-id tuple + H — all present (IExecutionCorrelated):
    _captured.Add((m.H, m.CorrelationId, m.WorkflowId, m.StepId, m.ProcessorId, m.EntryId, m.ExecutionId, m));
    return Task.CompletedTask;
}
```

## State of the Art

| Old Approach (reverted Phase 32) | Current Approach (spike) | When Changed | Impact |
|----------------------------------|--------------------------|--------------|--------|
| `FaultUnscheduleConsumer` IN the orchestrator on a per-replica `InstanceId+Temporary` fan-out endpoint, unwraps `WorkflowId`, unschedules Quartz | EXTERNAL in-test `IBusControl` binding `IConsumer<Fault<T>>` for BOTH types, unwraps FULL tuple + H, re-injects by type | 32.1 revert (`c046cc8`/`f325a5f`) + Phase 33 | The spike is the Keeper-prototype: a separate subscriber, not orchestrator-internal. Same MassTransit fanout-exchange bind; same double-`.Message`. |
| Cancelled circuit-breaker (no-TTL marker + Quartz halt) | Plain dead-lettering on exhaustion (`flag[H]` dedup retained) | 32.1 revert | The breaker is GONE; the spike relies ONLY on the surviving `flag[H]` gates + plain `_error` dead-lettering [VERIFIED: STATE.md:46-52]. |

**Deprecated/outdated (do NOT resurrect into production):**
- `L2ProjectionKeys.Cancelled(Guid)` + `CancelledMarkerValue` — removed in the revert; the spike must NOT reference them.
- `ProcessorMetrics.WorkflowCancelled` — removed; D-11 forbids any new exhaustion metric.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The orchestrator-side live result-trip can poison `flag[resultH]` without first tripping a processor-side fault | Pattern 3 / Pitfall 1 | If timing-fragile, the result trip captures the wrong fault type; mitigated by the D-06 synthetic fallback (acceptable — dispatch trip carries the novel risk). |
| A2 | The re-injected faulted dispatch finds `flag[dispatch.H]` still in "Pending" (never flipped to Ack, because the original effect never completed) so delivery 1 of the re-inject produces the effect | Pattern 6 | If the seed expired (300s TTL) before re-inject, delivery 1's `When.Exists` flip is a no-op; the clone tolerates this (downstream child-H dedup absorbs it) but the one-effect count could be affected. Planner: re-seed `flag[H]="Pending"` once before re-inject (mirroring clone:168) if the TTL window is exceeded. |
| A3 | `Immediate(Limit=3)` is the budget the spike exhausts against for BOTH producers | Pitfall 3 | Verified default; if a container overrides the `Retry` config section the deliveries differ. The trip is robust to any finite Limit (it just needs Limit+1 deliveries). [VERIFIED: RetryOptions.cs:10 default 3; both definitions bind the same section.] |

## Open Questions

1. **Result-trip mechanism: live WRONGTYPE vs D-06 synthetic.**
   - What we know: `resultH` is fully a-priori computable (Pattern 4); the orchestrator's first Redis op is the poisonable `flag[m.H]` read (Pattern 3). The result trip IS deterministic in principle.
   - What's unclear: the processor's OWN outbound `StringSetAsync(Flag(resultH),"Pending")` pre-write (`:210`) runs before the result is sent, so poisoning `flag[resultH]` up-front trips a PROCESSOR fault, not the orchestrator one (Pitfall 1). A clean orchestrator-only trip needs the poison applied in a narrow window, or a topology where the processor's pre-write targets a different key.
   - Recommendation: Plan BOTH — the live result-trip as the primary (it proves the genuine exhaustion-publish path), with the D-06 synthetic `Fault<ExecutionResult>` publish as a committed fallback the operator can fall to if the live timing proves fragile. The dispatch trip (unambiguous) carries the full novel risk regardless, so the milestone is de-risked either way.

2. **Should the spike re-inject use the SAME in-test `IBusControl` it captures on, or a second Send-only bus?**
   - What we know: the clone uses a fresh Send-only bus per `SendDispatchAsync` (clone:265-279); the spike already holds a capturing `IBusControl`.
   - Recommendation: Claude's discretion (D-stated). Reusing the capturing bus's `GetSendEndpoint` is simpler and avoids a second connection; either works against the same live broker.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| RabbitMQ (sk-rabbitmq) | Fault pub/sub + re-inject | live-stack only | localhost:5673 | — (live half is operator-gated) |
| Redis (sk-redis) | WRONGTYPE poison + flag poll + net-zero | live-stack only | localhost:6380 | — |
| Postgres | Processor/Step/Workflow seed | live-stack only | localhost:5433 | — |
| Elasticsearch | `PollEsForLog` one-effect proof | live-stack only | localhost:9200 | — |
| OTLP collector | log export | live-stack only | localhost:4317 | — |
| REBUILT containers (processor-sample/orchestrator/baseapi) | embedded SourceHash match | operator must rebuild | — | — (gate fails without) |

**Missing dependencies with no fallback:** The entire live stack — but this is EXPECTED. The live trip + assertions are NOT runnable in a non-interactive executor. **The live half is an `autonomous: false` operator runbook** (Phase-31/32 precedent: author + commit the test, document the live run in the SUMMARY Pending-Verification section, do-not-block). The hermetic build + `--filter-not-trait "Category=RealStack"` suite IS runnable to prove the test compiles and the rest of the suite is green.

## Validation Architecture

> nyquist_validation: enabled (no `.planning/config.json` workflow.nyquist_validation:false found; treat as enabled). The spike's deliverable IS a validation test, so this section maps the 4 success criteria to observable signals.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit v3 (solution-pinned) |
| Config file | none — RealStack tests excluded via `[Trait("Category","RealStack")]` |
| Quick run command | `dotnet test tests/BaseApi.Tests -c Debug -- --filter-not-trait "Category=RealStack"` (proves compile + no hermetic regression) |
| Full RealStack command (operator) | `dotnet test tests/BaseApi.Tests -- --filter-class "*FaultRecoverySpikeE2ETests"` (live stack only) |

### Phase Requirements → Observable Signal Map
| Req / Criterion | Behavior | Observable Signal | Test Type | File Exists? |
|-----------------|----------|-------------------|-----------|-------------|
| INTAKE-01 (bind) | Both fault types captured | Spike `IConsumer<Fault<EntryStepDispatch>>` + `IConsumer<Fault<ExecutionResult>>` capture-count ≥ 1 each | RealStack E2E | ❌ Wave 0 (new file) |
| INTAKE-01 (negative, D-09) | Command-faults NOT delivered | After publishing `Fault<StartOrchestration>` + `Fault<StopOrchestration>`, spike capture-count == 0 over settle window | RealStack E2E | ❌ Wave 0 |
| INTAKE-02 (unwrap) | 6 ids + H extracted | Captured tuple `(corr,wf,step,proc,entry,exec)` all non-empty + `H` matches the a-priori `ComputeH(...)` | RealStack E2E | ❌ Wave 0 |
| INTAKE-04 (re-inject) | Verbatim Send to origin queue by type | A NEW `skp:data:*` (dispatch) / advance-log (result) appears for the re-injected identity = the effect produced post-re-inject | RealStack E2E | ❌ Wave 0 |
| PROBE-06 / D-08 (collapse) | Duplicate dropped by receiver `flag[H]` | `PollEsForLog` effect present + `CountEsHitsAsync` == 1 (NOT 2) over an 8s+ ingest-settle window | RealStack E2E | ❌ Wave 0 |
| (trip realism) | Fault published on exhaustion | `{procId}_error` queue depth grows AND spike captures the fault (proves `Immediate(Limit)` exhausted) | RealStack E2E | ❌ Wave 0 |

### Sampling Rate
- **Per task commit (hermetic):** `dotnet build SK_P.sln -c Release` (0/0) + `--filter-not-trait "Category=RealStack"` green (proves the new file compiles + zero regression).
- **Per wave merge:** same hermetic suite.
- **Phase gate (operator, autonomous:false):** rebuild 3 containers → run `*FaultRecoverySpikeE2ETests` GREEN → (optional) a `phase-33`-style close gate if the roadmap calls for one; net-zero `skp:*` triple-SHA must hold.

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs` — the spike (clone of `IdempotentExactlyOnceE2ETests` + dual `Fault<T>` capture + WRONGTYPE trips + re-inject + negative proof).
- [ ] Two `IConsumer<Fault<T>>` probe classes (nested or file-local) — resurrect the `FaultConsumerBindingFacts` double-`.Message` shape.
- [ ] No framework install needed; no shared-fixture changes (the clone's `RealStackWebAppFactory` is copied private).

## Security Domain

> `security_enforcement`: no `.planning/config.json` found in repo root indicating `false`; treat as enabled. Scoped narrowly — this is a test-only spike with no new production surface, no auth, no external input.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Test connects to local dev RabbitMQ with guest/guest (dev-only, matches existing clone) |
| V3 Session Management | no | — |
| V4 Access Control | no | — |
| V5 Input Validation | no | No new external input; payloads are test-chosen literals |
| V6 Cryptography | no | `MessageIdentity` SHA-256 is content-addressing (not security) — reused, not modified [VERIFIED: MessageIdentity.cs:11-13 T-31-03] |

### Known Threat Patterns for this spike
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Net-zero key leak churns close-gate SHA | (operational integrity) | Register every poison/data/flag key in `L2KeysToCleanup` + stop the workflow in teardown [VERIFIED: clone:203-218] |
| WRONGTYPE poison left in live Redis | (operational) | Poison keys registered for deletion; no-TTL keys (none in this spike — all flag/data keys are TTL'd) |

**No new production attack surface** — the spike adds one test file; the only mutation of live state is bounded, TTL'd, and net-zeroed.

## Sources

### Primary (HIGH confidence)
- Codebase (VERIFIED via Read/Grep): `IdempotentExactlyOnceE2ETests.cs`, `ResultConsumer.cs`, `StepDispatcher.cs`, `EntryStepDispatchConsumer.cs`, `MessageIdentity.cs`, `L2ProjectionKeys.cs`, `OrchestratorQueues.cs`, `EntryStepDispatch.cs`, `ExecutionResult.cs`, `RetryOptions.cs`, `SampleProcessor.cs`, `ResultConsumerDefinition.cs`, `ProcessorStartupOrchestrator.cs`, `Orchestrator/Program.cs`, `StartOrchestration.cs`, `StopOrchestration.cs`.
- Git history (VERIFIED via `git show`): `3aca386:FaultConsumerBindingFacts.cs`, `a6c6825:CancelledCircuitBreakerE2ETests.cs`, `33b1d8b:FaultUnscheduleConsumer.cs` + `:FaultUnscheduleConsumerDefinition.cs`.
- `.planning/phases/33-fault-recovery-spike-de-risk/33-CONTEXT.md`, `.planning/STATE.md`.

### Secondary (MEDIUM confidence)
- [MassTransit Message Topology](https://masstransit.io/documentation/configuration/topology/message) — Fault published to durable fanout message-type exchange; external subscribers auto-declare a queue bound to it.
- [MassTransit RabbitMQ Transport](https://masstransit.io/documentation/transports/rabbitmq) — fanout exchange / durable queue defaults.
- [MassTransit Consumers](https://masstransit.io/documentation/concepts/consumers) — `IConsumer<Fault<T>>` semantics, `FaultAddress` header.

### Tertiary (LOW confidence)
- None — every load-bearing claim is backed by a codebase `[VERIFIED]` precedent or a git-recovered proven test.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all packages already pinned; nothing new to install.
- Architecture (bind/unwrap/re-inject/collapse): HIGH — every mechanism has a VERIFIED codebase or git precedent.
- Result-trip surface (D-06): HIGH that it's deterministic in principle; MEDIUM that the live timing is clean (Pitfall 1) — hence the retained synthetic fallback. The de-risk goal is met regardless because the dispatch trip carries the full novel risk.
- Pitfalls: HIGH — drawn directly from the clone's own inline warnings + the recovered Phase-32 recipe.

**Research date:** 2026-06-05
**Valid until:** 2026-07-05 (stable — pinned MassTransit 8.5.5, mature codebase; the only volatility is the live-stack operator timing on the result trip).
