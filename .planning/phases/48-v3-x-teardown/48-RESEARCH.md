# Phase 48: v3.x Teardown - Research

**Researched:** 2026-06-09
**Domain:** Pure-deletion / remnant-sweep — removal of the reactive `Fault<T>` Keeper recovery path + `keeper-dlq` (RETIRE-03) and a final RETIRE-01/02 remnant verify, leaving the solution buildable + green on the v4 path alone.
**Confidence:** HIGH (every kill-list entry and every keep/delete boundary is backed by grep/read evidence against this checkout; no training-data assumptions about library behavior were needed — this is an enumeration phase, not a design phase.)

<user_constraints>
## User Constraints (from 48-CONTEXT.md)

### Locked Decisions
- **D-01 (Test disposition):** Delete the orphaned reactive-only test classes, then add a Phase-48 `[Trait("Phase","48")]` negative-guard fact set proving the absence. Reactive-only classes that stop compiling: `KeeperFaultConsumerScopeTests`, `KeeperRecoverCapTests` (per-`H`/4-tuple attempt cap), `KeeperRoundRobinTests` (the `keeper-fault-recovery` durable round-robin), and the reactive recover-loop portions of `KeeperProbeLoopTests`. The new guard asserts: Keeper registers **no** `Fault<EntryStepDispatch>`/`Fault<ExecutionResult>` consumer; no `keeper-fault-recovery` endpoint is wired; no `keeper-dlq` (`KeeperQueues.DeadLetter`) topology const is reachable on the execution path. Mirror the Phase-47 `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs` reflection/source-scan pattern so the teardown is self-verifying and regression-proof. **Rejected:** delete-only; convert-in-place.
- **D-02 (Remnant-sweep scope):** Exhaustive orphan hunt — dead config knobs (`RecoveryOptions`; reactive-only `ProbeOptions`/`BackupOptions` members), `KeeperMetrics` fault/recover-attempt counters, retired `KeeperQueues.FaultRecovery` + `KeeperQueues.DeadLetter` consts, dead `Ignore<>`/bus bindings, stale `H`/manifest/`Fault<T>` comments. Verify RETIRE-01/02 symbols (`MessageIdentity`, `flag[H]`, result manifest / N×M fan-out) absent from the execution-path source. **PRESERVE** `L2ProbeRecovery.ProbeOnceAsync` (v4 `BitHealthLoop` uses it) + members the v4 keep-set reads (`BackupOptions.TtlDays`, `Probe:DelaySeconds`). **Rejected:** named-artifacts-only; sweep-but-keep-config/metrics.
- **D-03 (Close-gate depth):** Hermetic suite GREEN (×3 consecutive) + `dotnet build SK_P.sln` Release AND Debug at 0 warnings — exactly SC-4, nothing more. **NO** triple-SHA `psql`/`redis-cli`/`rabbitmqctl` infra-parity gate in Phase 48 (the queue-topology change makes `rabbitmqctl list_queues` deliberately NOT net-zero this phase). Phase 49 owns the real-stack live proof + final close gate. **Rejected:** add-triple-SHA-now; record-expected-delta-in-48.
- **D-04 (Audit/reconciliation):** Full reconciliation — three artifacts: (1) `48-TEARDOWN-AUDIT.md` traceability ledger (RETIRE-01/02/03 + SC-1..SC-4 → named proving guard test/scan, mirror `47-DLQ-AUDIT.md`); (2) mark RETIRE-01/02/03 satisfied in `REQUIREMENTS.md`; (3) additive amendment to `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` recording the reactive-path + `keeper-dlq` retirement (the A15/A16 additive-amendment pattern). **Rejected:** audit-ledger-only; verification-report-only.

### Claude's Discretion
- Exact namespace/file name of the Phase-48 negative-guard fact set (keep parity with `AtLeastOnceStructuralFacts` / firewall-test reflection style).
- The precise removal ordering within the phase (any order so long as every intermediate commit still builds — the build-before-teardown invariant from 43-CONTEXT D-01/D-03).
- `48-TEARDOWN-AUDIT.md` table columns/layout (follow the `47-DLQ-AUDIT.md` / VALIDATION.md traceability-table style).
- The precise wording of the design-doc retirement amendment.
- Exactly which `ProbeOptions`/`BackupOptions` members (if any) are reactive-only vs. shared — confirmed during research below.

### Deferred Ideas (OUT OF SCOPE)
- **Phase-49 net-zero baseline (forward note, NOT Phase-48 scope):** Phase 48 deliberately changes RabbitMQ topology (removes `keeper-dlq` + `keeper-fault-recovery`). Phase 49's recurring triple-SHA `rabbitmqctl list_queues` BEFORE==AFTER gate must take its baseline from the **post-teardown** topology. The user chose to keep 48 to SC-4 only (D-03); this note exists so Phase 49 planning starts from the correct topology, NOT so 48 records a delta artifact.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| RETIRE-03 | Remove the reactive `Fault<EntryStepDispatch>`/`Fault<ExecutionResult>` Keeper recovery path and the `keeper-dlq` queue. | The Kill-List (§1) enumerates the 3 source files + 2 queue consts + Program.cs unwiring + dead config/metrics. The Boundary Map (§2) proves the deletions have no inbound reference from the v4 keep-set. The Negative-Guard design (§5) makes the removal self-verifying. |
| RETIRE-01 | (Coupled-forward to Phase 43 per D-01/D-02 — verified here by remnant-sweep.) Remove `H` identity, `flag[H]` dedup gate, CAS `Pending→Ack`. | §6 establishes RETIRE-01 is **already proven** by the green Phase-47 `AtLeastOnceStructuralFacts.No_dedup_machinery_on_execution_path` reflection guard; the 48 audit row cites it. SC-1 = cite-existing, no new assertion needed beyond a remnant-grep verify. |
| RETIRE-02 | (Coupled-forward to Phase 43.) Remove content-addressed L2 data, the result manifest, and N×M manifest fan-out. | §6 confirms L2 data is GUID-`entryId`-only (`L2ProjectionKeys.ExecutionData(Guid)` is the sole data builder; the 64-hex overload was deleted in v4.0.0 — see code comment). No manifest type exists. SC-2 = a new/extended structural assertion OR a remnant-grep verify; §6 specifies the lightest sufficient option. |

**Mapping note:** RETIRE-01/02 were pulled forward into Phase 43 (coupled to the field/type reshape). Phase 48 carries RETIRE-03 (new removal) + the remnant-sweep VERIFY of 01/02. D-04 marks all three satisfied.
</phase_requirements>

## Summary

This is a **pure-deletion phase with a sharp safety boundary**. The reactive recovery path is three source files (`FaultEntryStepDispatchConsumer` (+Definition), `FaultExecutionResultConsumer` (+Definition), `KeeperRecoveryHandler`) plus two queue consts (`KeeperQueues.FaultRecovery`, `KeeperQueues.DeadLetter`), their Program.cs registration, and the now-orphaned config/metrics surface they alone touch. The v4 path (5-state `keeper-recovery` consumers, `BitHealthLoop` + `L2HealthGate`, global pause/resume, `TypedResultConsumer<T>`, the consolidated `skp-dlq-1`) survives untouched.

The load-bearing finding (§2) is that the reactive↔shared boundary is **cleaner than 48-CONTEXT hypothesized in two places and dirtier in one:**
- **`RecoveryOptions` is NOT a reactive orphan — it is fully v4-shared** and must STAY. Both members (`PartitionCount`, `GateWaitSeconds`) are read by the v4 5-state recovery consumers (`UpdateConsumerDefinition`, `RecoveryConsumerBase`, `Update/Reinject/Inject/Delete/CleanupConsumer`). 48-CONTEXT D-02 / code_context guessed "likely reactive-only orphan; confirm" — the answer is **confirmed shared, do not touch.**
- **`ProbeOptions` is mostly shared, with exactly ONE reactive-only member:** `RecoverAttemptCap` (read only by `KeeperRecoveryHandler`). `DelaySeconds` + `MaxAttempts` are read by `L2ProbeRecovery.RunAsync` AND by `BitHealthLoop` (`DelaySeconds`) — keep them.
- **`KeeperMetrics` becomes 100% orphaned** once the reactive path goes. All eight instruments are incremented ONLY inside `L2ProbeRecovery.RunAsync` (InFlight, L2ProbeFailed) and `KeeperRecoveryHandler` (the other six). Since `RunAsync` is itself reactive-only (only `KeeperRecoveryHandler` calls it; `BitHealthLoop` calls only the *kept* `ProbeOnceAsync`, which touches no metrics), the entire `KeeperMetrics` class + its `KeeperMetricTags` companion + the Program.cs meter wiring is dead. **The whole observability file is deletable** (not "remove only the reactive instruments" as D-02 phrased — there is no v4 instrument to keep).
- **`L2ProbeRecovery` is a partial-delete:** keep `ProbeOnceAsync` + the two BIT sentinel consts + the `IConnectionMultiplexer` ctor dep; remove `RunAsync`, the `ProbeOutcome` enum (only `RunAsync`/`KeeperRecoveryHandler` use it), and the `IOptions<ProbeOptions>` + `KeeperMetrics` ctor deps.

**Primary recommendation:** Sequence the teardown as a **single atomic wave** (one build-green commit, optionally 2-3 sub-commits each independently buildable per the discretion in D-03). Delete the consumers + handler + their tests FIRST (which is what frees every downstream orphan), then sweep the now-dead consts/options-members/metrics/registrations, then add the Phase-48 negative-guard facts, then remove the `KeeperRecoveryHandler.cs` exclusion from the Phase-47 source-scan and confirm 47 still passes. Every const removal must land in the SAME commit as the last consumer that references it (§7).

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Reactive `Fault<T>` recovery (probe→reinject→park) | Keeper console (consumers + handler) | — | DELETE entirely — superseded by the v4 push-based 5-state recovery. |
| `keeper-dlq` terminal park | RabbitMQ topology + `KeeperQueues.DeadLetter` const | Keeper handler | DELETE — replaced by the consolidated `skp-dlq-1` (Phase 47, A4). |
| L2 BIT health probe (`ProbeOnceAsync`) | Keeper `BitHealthLoop` hosted service | `L2ProbeRecovery` (shared helper) | **KEEP** — live v4 KEEP-01/02. |
| 5-state recovery (`UPDATE/REINJECT/INJECT/DELETE/CLEANUP`) | Keeper `keeper-recovery` endpoint | `RecoveryConsumerBase` + `RecoveryOptions` | **KEEP** — v4 KEEP-04..09. |
| Global pause-all / resume-all | Keeper `BitHealthLoop` → orchestrator | `IL2HealthGate` | **KEEP** — v4 KEEP-02. |
| Per-item orchestrator result consume | Orchestrator `TypedResultConsumer<T>` | — | **KEEP** — v4 ORCH-01. Out of Keeper scope entirely. |
| Recovery-consumer config knobs | `RecoveryOptions` (Keeper) | appsettings `Recovery` section | **KEEP** — v4-shared (the §2 correction). |

## Standard Stack

No new dependencies. This phase removes code; it adds nothing but one test fact-class. The existing test mechanism (xUnit + reflection over loaded assemblies + `Directory.EnumerateFiles` source-scan, anchored via `[CallerFilePath]`) is the entire toolkit — see `AtLeastOnceStructuralFacts.cs` for the verified pattern.

| Tool | Version | Purpose | Why standard |
|------|---------|---------|--------------|
| xUnit | (in-repo) | the negative-guard `[Fact]`/`[Trait]` host | matches every existing fact-class `[VERIFIED: tests/BaseApi.Tests]` |
| `System.Reflection` | net8.0 BCL | absent-consumer-type assertions over the Keeper assembly | exact pattern in `AtLeastOnceStructuralFacts.No_dedup_machinery_on_execution_path` `[VERIFIED]` |
| `System.IO.Directory` + `[CallerFilePath]` | net8.0 BCL | absent-const/endpoint source-scan with a fail-loud repo-root anchor | exact pattern in `AtLeastOnceStructuralFacts.RepoRoot()` `[VERIFIED]` |

## 1. The Kill-List

### 1A. DELETE outright (RETIRE-03 — reactive path)

| File | Evidence it is reactive-only |
|------|------------------------------|
| `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` | `IConsumer<Fault<EntryStepDispatch>>`, delegates to `KeeperRecoveryHandler`. The only registered reactive consumer (Program.cs:71). `[VERIFIED: Read]` |
| `src/Keeper/Consumers/FaultEntryStepDispatchConsumerDefinition.cs` | `EndpointName = KeeperQueues.FaultRecovery` ("keeper-fault-recovery"). `[VERIFIED: Read]` |
| `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` | `IConsumer<Fault<StepCompleted>>`, retargeted dormant per D-14, **intentionally NOT registered** in Program.cs. `[VERIFIED: Read]` |
| `src/Keeper/Consumers/FaultExecutionResultConsumerDefinition.cs` | Same `keeper-fault-recovery` endpoint; `ConfigureConsumer` is a no-op. `[VERIFIED: Read]` |
| `src/Keeper/Recovery/KeeperRecoveryHandler.cs` | The shared reactive recovery body — the 47-scan's deliberate exclusion. Sole caller of `L2ProbeRecovery.RunAsync`, sole referencer of `KeeperQueues.DeadLetter`, `RecoverAttemptCap`, `L2ProjectionKeys.KeeperRecoverAttempts`. `[VERIFIED: grep — only inbound refs are the two Fault consumers, themselves deleted]` |

### 1B. EDIT — remove named members, KEEP the rest

| File | REMOVE | KEEP | Evidence |
|------|--------|------|----------|
| `src/Messaging.Contracts/KeeperQueues.cs` | `FaultRecovery` const (line 15) + its doc-comment; `DeadLetter` const (line 25) + its doc-comment. Fix the `Recovery` doc-comment cross-`<see cref>` to `FaultRecovery` (line 18) which will dangle. | `Recovery = "keeper-recovery"` (v4). | `[VERIFIED: grep — FaultRecovery referenced only by the two deleted Definitions; DeadLetter only by deleted KeeperRecoveryHandler + the 47 test (string literal) + a comment in KeeperMetrics]` |
| `src/Keeper/Recovery/L2ProbeRecovery.cs` | `RunAsync(...)` method (lines 25-45); the `ProbeOutcome` enum (line 10 — used only by RunAsync + KeeperRecoveryHandler); the `IOptions<ProbeOptions> opts` AND `KeeperMetrics metrics` ctor params (line 20) and the `using Keeper.Observability;`. | `ProbeOnceAsync(...)` (lines 56-71); `BitProbeEntryId`/`BitProbeH` sentinels (lines 49-50); the `IConnectionMultiplexer redis` ctor param; the `using ...Projections` + `StackExchange.Redis`. | `[VERIFIED: grep — ProbeOnceAsync called by BitHealthLoop:32 (KEEP); RunAsync called only by KeeperRecoveryHandler:108 (DELETE) + reactive tests]` |
| `src/Keeper/ProbeOptions.cs` | `RecoverAttemptCap` (line 14) + its doc-comment. | `DelaySeconds`, `MaxAttempts`. | `[VERIFIED: grep — RecoverAttemptCap read only at KeeperRecoveryHandler:126; DelaySeconds/MaxAttempts read by L2ProbeRecovery + BitHealthLoop:28 + ProbeOptionsBoundTests]` |
| `src/Keeper/appsettings.json` | `"RecoverAttemptCap": 3` under `"Probe"`. | `Probe.DelaySeconds/MaxAttempts`, the whole `Recovery` + `Backup` sections. | `[VERIFIED: Read appsettings.json]` |
| `src/Keeper/Program.cs` | The `AddSingleton<KeeperRecoveryHandler>()` (line 49); the `AddSingleton<KeeperMetrics>()` + `ConfigureOpenTelemetryMeterProvider(...AddMeter(KeeperMetrics.MeterName))` (lines 57-58); the `AddConsumer<FaultEntryStepDispatchConsumer, FaultEntryStepDispatchConsumerDefinition>()` (line 71); the `using Keeper.Consumers;` and `using Keeper.Observability;`; the OTel-Metrics `using` if it becomes unused; the L2ProbeRecovery comment "Both fault consumers depend on it" (line 39-40) reword. | `Configure<RecoveryOptions>` (line 37 — v4); `AddSingleton<L2ProbeRecovery>()` (line 41 — BitHealthLoop dep); `IL2HealthGate`/`L2HealthGate` (line 44); `AddHostedService<BitHealthLoop>()` (line 46); the five `keeper-recovery` consumer registrations (lines 79-83); `Configure<ProbeOptions>`/`BackupOptions`/`RetryOptions`. See §3 for exact unwiring. | `[VERIFIED: Read Program.cs]` |

### 1C. DELETE outright (orphaned by 1A — observability)

| File | Evidence |
|------|----------|
| `src/Keeper/Observability/KeeperMetrics.cs` (the `KeeperMetrics` class **and** the `KeeperMetricTags` static class in the same file) | All eight instruments incremented ONLY in `L2ProbeRecovery.RunAsync` (InFlight, L2ProbeFailed) + `KeeperRecoveryHandler` (FaultConsumed, Recovered, DlqPushed, WorkflowPaused, WorkflowResumed, RecoveryDuration). Both are reactive-only. `KeeperMetricTags` referenced only by the same two + reactive tests. **No v4 code emits any Keeper metric.** `[VERIFIED: grep `metrics\.` across src — every hit is in those two files]` |

> **Planner note — confirm-during-plan:** Deleting the whole observability file is a deviation from D-02's literal "remove only the reactive fault/recover-attempt instruments; keep the v4 meter." The evidence shows **there is no v4 meter usage** — the v4 recovery consumers and BIT loop emit nothing through `KeeperMetrics`. If the planner/discuss wants to PRESERVE the `Keeper` meter as an empty scaffold for future v4 instrumentation, that is a discretionary call; the safe-and-minimal-dead-surface reading (SC-4: "no dead remnants") is to delete it. `[ASSUMED → see Assumptions Log A1]`

### 1D. Comments / docstrings to scrub (D-02 "stale comments")

After 1A-1C, grep for and clean any surviving prose mentions of the retired path so SC-4 "no dead … remnants" holds:
- `src/Keeper/Recovery/L2ProbeRecovery.cs` doc-comment "awaited inside Consume … RunAsync passes the real fault-context values" → reword to the BIT-only role.
- `src/Keeper/Recovery/RecoveryConsumerBase.cs` line 18/25 "mirror the L2ProbeRecovery await-inside-Consume precedent" / "mirrors L2ProbeRecovery" — these reference the SHARED helper, still valid; leave unless they name the reactive `RunAsync`. `[VERIFIED: grep]`
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:52-53` — `KeeperRecoverAttempts` builder (`skp:keeper:attempts:{h}`) is referenced ONLY by the deleted `KeeperRecoveryHandler`. **REMOVE the builder + its doc-comment** (it is dead after 1A). KEEP `KeeperProbe` (line 50 — used by the kept `ProbeOnceAsync`). `[VERIFIED: grep KeeperRecoverAttempts → only KeeperRecoveryHandler:116]`

### 1E. Explicitly NOT touched (confirmed v4-shared — anti-kill-list)

| Item | Why it STAYS |
|------|--------------|
| `src/Keeper/RecoveryOptions.cs` (both `PartitionCount` + `GateWaitSeconds`) | Read by `UpdateConsumerDefinition`, `RecoveryConsumerBase`, `Update/Reinject/Inject/Delete/CleanupConsumer`. **Corrects 48-CONTEXT "likely reactive-only orphan."** `[VERIFIED: grep RecoveryOptions]` |
| `src/Keeper/BackupOptions.cs` (`TtlDays`) | v4 composite-backup TTL (KEEP-04). `[VERIFIED: BackupOptionsBoundTests + Program.cs:33]` |
| `L2ProjectionKeys.KeeperProbe` | Used by the kept `ProbeOnceAsync`. `[VERIFIED: grep]` |
| `Messaging.Contracts/PauseWorkflow.cs` + `ResumeWorkflow.cs` (`string H` positional) | v4 BIT-gate contracts — the LEGITIMATE positional `H` that `AtLeastOnceStructuralFacts` reflection-guard deliberately does not flag (it's not `MessageIdentity`, not on an execution-path assembly). **Do NOT remove `H` here.** `[VERIFIED: Read + AtLeastOnceStructuralFacts doc-comment]` |
| `KeeperQueues.Recovery` | v4 `keeper-recovery` endpoint. `[VERIFIED]` |

## 2. Reactive-only vs. Shared Boundary Map (load-bearing safety analysis)

Each "delete" candidate below is shown to have **no inbound reference from the v4 keep-set** (`BitHealthLoop`, `RecoveryConsumerBase` + the 5 state consumers/definitions, `TypedResultConsumer`, `L2HealthGate`/pause-resume). The keep-set was enumerated from `src/Keeper/Recovery/` + `src/Keeper/Health/` + `src/Orchestrator/Consumers/`.

### `L2ProbeRecovery` members
| Member | Inbound refs | Verdict |
|--------|-------------|---------|
| `ProbeOnceAsync(ct, entryId?, h?)` | `BitHealthLoop:32` (`probe.ProbeOnceAsync(stoppingToken)`) — **v4 keep-set** | **KEEP** |
| `BitProbeEntryId` / `BitProbeH` consts | `ProbeOnceAsync` defaults | **KEEP** |
| `RunAsync(entryId, h, procId, ct)` | `KeeperRecoveryHandler:108` (delete) + `KeeperProbeLoopTests`/`KeeperMetricsFacts`/`KeeperPausePublishTests` (delete) | **DELETE** |
| `ProbeOutcome` enum | `RunAsync` + `KeeperRecoveryHandler` only | **DELETE** |
| ctor `IOptions<ProbeOptions> opts` | only `RunAsync` reads `opts.Value.MaxAttempts/DelaySeconds` | **DELETE param** |
| ctor `KeeperMetrics metrics` | only `RunAsync` (InFlight/L2ProbeFailed) | **DELETE param** |
| ctor `IConnectionMultiplexer redis` | `ProbeOnceAsync` (`redis.GetDatabase()`) | **KEEP param** |

> **Build-order trap:** removing the `opts`/`metrics` ctor params changes the `L2ProbeRecovery` ctor signature. `BitHealthLoop` ctor-injects `L2ProbeRecovery` and `Program.cs` does `AddSingleton<L2ProbeRecovery>()` — DI resolves by the surviving `IConnectionMultiplexer` singleton, so this is safe **in the same commit** that also deletes `KeeperMetrics` (otherwise `KeeperMetrics` is registered-but-unconstructed, which is harmless but a dead remnant SC-4 targets). Sequence: delete the two ctor params + `RunAsync` + the `KeeperMetrics` registration **together**.

### `RecoveryOptions` members — ALL SHARED (the §2 correction)
| Member | Inbound refs (all v4 keep-set) | Verdict |
|--------|-------------------------------|---------|
| `PartitionCount` | `UpdateConsumerDefinition:61` (`new Partitioner(...)`) | **KEEP** |
| `GateWaitSeconds` | `RecoveryConsumerBase:47` (`cts.CancelAfter(...)`) | **KEEP** |

### `ProbeOptions` members
| Member | Inbound refs | Verdict |
|--------|-------------|---------|
| `DelaySeconds` | `L2ProbeRecovery.RunAsync` (delete-site) + `BitHealthLoop:28` (**keep**) + `ProbeOptionsBoundTests` | **KEEP** |
| `MaxAttempts` | `L2ProbeRecovery.RunAsync` (delete-site) + `ProbeOptionsBoundTests` | **KEEP** (bound by `ProbeOptionsBoundTests`; not solely reactive — it is the documented broker-timeout invariant) |
| `RecoverAttemptCap` | `KeeperRecoveryHandler:126` ONLY | **DELETE** |

### `BackupOptions` — ALL SHARED
| Member | Inbound refs | Verdict |
|--------|-------------|---------|
| `TtlDays` | v4 UPDATE composite-backup write (KEEP-04) | **KEEP** |

### `KeeperMetrics` instruments — ALL ORPHANED
| Instrument | Emit site | Verdict |
|-----------|-----------|---------|
| `InFlight`, `L2ProbeFailed` | `L2ProbeRecovery.RunAsync` (delete-site) | dead |
| `FaultConsumed`, `Recovered`, `DlqPushed`, `WorkflowPaused`, `WorkflowResumed`, `RecoveryDuration` | `KeeperRecoveryHandler` (delete-site) | dead |
| `KeeperMetricTags` (all keys/values + `FaultTags`) | the same two files + reactive tests | dead |

**Conclusion:** the entire `KeeperMetrics.cs` file is orphaned (§1C). No v4 keep-set member emits any Keeper metric. `[VERIFIED: grep `metrics\.(Fault|Recovered|Dlq|Workflow|L2Probe|InFlight|RecoveryDuration)` → all hits in L2ProbeRecovery + KeeperRecoveryHandler]`

## 3. Program.cs Unwiring Plan

**Remove (reactive registrations):**
1. Line 49 — `builder.Services.AddSingleton<Keeper.Recovery.KeeperRecoveryHandler>();`
2. Lines 57-58 — `AddSingleton<KeeperMetrics>()` + `ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(KeeperMetrics.MeterName))`.
3. Line 71 — `x.AddConsumer<FaultEntryStepDispatchConsumer, FaultEntryStepDispatchConsumerDefinition>();` (this is the registration that creates the `keeper-fault-recovery` endpoint binding the `Fault<EntryStepDispatch>` message-type exchange).
4. The `using Keeper.Consumers;` (line 8) and `using Keeper.Observability;` (line 9) once their last use is gone. Check whether `OpenTelemetry.Metrics` (line 10) is still needed — it is only used by the removed `ConfigureOpenTelemetryMeterProvider` call, so **remove that using too**.
5. Reword the L2ProbeRecovery registration comment (lines 39-40) from "Both fault consumers depend on it" → "the v4 BitHealthLoop's L2 probe helper."

**KEEP intact (v4 — explicitly confirm in the plan's verification step):**
- Line 37 `Configure<RecoveryOptions>` (v4 partition/gate knobs).
- Line 41 `AddSingleton<L2ProbeRecovery>()` (BitHealthLoop dep — ctor now takes only `IConnectionMultiplexer`).
- Line 44 `AddSingleton<IL2HealthGate, L2HealthGate>()`.
- Line 46 `AddHostedService<BitHealthLoop>()` — the BIT loop + global pause/resume driver.
- Lines 79-83 — the five `keeper-recovery` consumers (`Update/Reinject/Inject/Delete/Cleanup`). After line 71 is removed, the `AddBaseConsoleMessaging` lambda contains ONLY these five (`UpdateConsumerDefinition` remains the single endpoint-retry owner).
- `Configure<ProbeOptions>` (line 29), `Configure<BackupOptions>` (line 33), `Configure<RetryOptions>` (line 25).

**Post-removal sanity:** the `keeper-fault-recovery` queue and the `Fault<EntryStepDispatch>` exchange binding disappear from the broker (intended topology delta — D-03/Deferred). The `keeper-dlq` queue disappears (no remaining `Send(... queue:keeper-dlq)`). The `keeper-recovery` endpoint + its five consumers + the BIT loop hosted service stay. `[VERIFIED: Read Program.cs + the five Definition files via grep]`

## 4. Orphaned Test Inventory

Searched `tests/` for every reference to the deleted symbols. Result: **no `FaultRecoverySpikeE2ETests` / `KeeperRecoveryE2ETests` exist** (the Phase-33 spike was already removed; the only `*E2E*` files are unrelated happy-path/metrics round-trips). `[VERIFIED: ls -R tests + grep]`

| Test file | Disposition | Reason |
|-----------|-------------|--------|
| `tests/BaseApi.Tests/Keeper/KeeperFaultConsumerScopeTests.cs` (241 ln) | **DELETE WHOLE** | Reactive consumer DI-scope tests. `[VERIFIED: grep — references FaultEntryStepDispatchConsumer/KeeperRecoveryHandler]` |
| `tests/BaseApi.Tests/Keeper/KeeperRecoverCapTests.cs` (267 ln) | **DELETE WHOLE** | The per-`H` recover-attempt cap (`RecoverAttemptCap` + `KeeperRecoverAttempts`) — entirely reactive. `[VERIFIED: grep]` |
| `tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs` (97 ln) | **DELETE WHOLE** | The `keeper-fault-recovery` durable competing-consumer round-robin. `[VERIFIED: grep]` |
| `tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs` (124 ln) | **DELETE WHOLE** | Drives `FaultEntryStepDispatchConsumer.Consume` via `KeeperRecoveryHandler` end-to-end (intake-pause / recovered-resume / give-up-no-resume). 100% reactive. **Not in 48-CONTEXT's named list — add it.** `[VERIFIED: Read]` |
| `tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs` (219 ln) | **DELETE WHOLE** | All six facts drive either `L2ProbeRecovery.RunAsync` (helper-level: `Probe_RequiresReadAndWrite`, `Probe_FailThenSucceed`, `Probe_FailToMax`) or the reactive consumer harness (`Probe_Success_Reinjects`, `Probe_GiveUp_ParksToDlq`, `Probe_AcksOnlyAfterLoop`). **None test the kept `ProbeOnceAsync` directly.** 48-CONTEXT said "keep any ProbeOnceAsync coverage" — there is none here, so delete the whole file. (BIT-loop `ProbeOnceAsync` coverage lives in the Health/ folder fixtures.) `[VERIFIED: Read — every test calls RunAsync or AddConsumer<FaultEntryStepDispatchConsumer>]` |
| `tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs` (406 ln) | **DELETE WHOLE** | Tests the orphaned `KeeperMetrics` + `KeeperMetricTags` + the consumer/handler/RunAsync emit flows (`RecoveredFlow_*`, `GiveUpFlow_*`, `InFlight_*`, the CtorHasParam wiring facts). Since `KeeperMetrics` is deleted (§1C) the whole class is orphaned. **Not in 48-CONTEXT's named list — add it.** `[VERIFIED: Read]` |

| Test file | Disposition | Reason |
|-----------|-------------|--------|
| `tests/BaseApi.Tests/Keeper/KeeperHostBootFixture.cs` | **EDIT** | Lines 39-54 register the two reactive consumers + `KeeperRecoveryHandler`. Remove those registrations; keep the AddBaseConsole* seam + ProbeOptions/RetryOptions binding + `L2ProbeRecovery`. (To stay faithful to Program.cs, also register the five `keeper-recovery` consumers OR simplify to "boots with no consumers" — planner's call; minimal edit = drop the two `AddConsumer<Fault...>` + the `KeeperRecoveryHandler` singleton.) `[VERIFIED: Read]` |
| `tests/BaseApi.Tests/Keeper/KeeperHostBootTests.cs` | **KEEP** (no edit) | Only resolves `IBusControl`; no reactive symbol. Re-runs green against the edited fixture. `[VERIFIED: Read]` |
| `tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs` | **EDIT** | Line 28 anchors the Keeper assembly via `typeof(FaultEntryStepDispatchConsumer)`. Re-anchor on a surviving public Keeper type (e.g. `typeof(global::Keeper.Recovery.ReinjectConsumer)` or `typeof(global::Keeper.Health.BitHealthLoop)`). `[VERIFIED: Read]` |
| `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` | **EDIT** | Lines 200-205 assert `KeeperQueues.DeadLetter == "keeper-dlq"` and `!= Dlq1`. After the const is deleted these stop compiling. Remove those specific assertions (and the line-189 doc-comment about DLQ-2). The other facts (`Dlq1_Consolidated`, `Keeper_SendFault_RetriesToDlq1`, `ProcessorSendExhaustion_RoutesToDlq1`) are v4 and **must stay green**. `[VERIFIED: grep lines 189-205]` |
| `tests/BaseApi.Tests/Keeper/ProbeOptionsBoundTests.cs` | **KEEP** (no edit) | Tests only `DelaySeconds`/`MaxAttempts` (kept). `[VERIFIED: Read]` |
| `tests/BaseApi.Tests/Keeper/BackupOptionsBoundTests.cs` | **KEEP** | `BackupOptions.TtlDays` kept. `[VERIFIED: ls]` |
| `tests/BaseApi.Tests/Keeper/RecoveryGateWaitFacts.cs`, `RecoveryPartitionFacts.cs`, `RecoveryDeadLetterFacts.cs`, `Update/Inject/Delete/Cleanup/ReinjectConsumerFacts.cs` | **KEEP** | v4 5-state recovery facts. `RecoveryDeadLetterFacts` (Phase 46/47) proves the data-gone `_DLQ1` terminal — stays. `[VERIFIED: 47-DLQ-AUDIT cites them]` |
| `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs` | **EDIT (widen)** | Remove the `KeeperRecoveryHandler.cs` exclusion (§6). `[VERIFIED: Read lines 96-99]` |

> **Compile-break warning for the planner:** deleting `KeeperRecoveryHandler.cs` + `L2ProbeRecovery.RunAsync` + `KeeperMetrics.cs` breaks compilation of `KeeperFaultConsumerScopeTests`, `KeeperRecoverCapTests`, `KeeperRoundRobinTests`, `KeeperPausePublishTests`, `KeeperProbeLoopTests`, `KeeperMetricsFacts`, and the `KeeperHostBootFixture` registrations **simultaneously**. All of these deletes/edits MUST land in the same wave/commit as the source deletes (you cannot delete the source, build, then delete the tests — the intermediate does not compile). See §7.

## 5. Negative-Guard Design (Phase-48 structural facts)

Author one new fact-class mirroring `AtLeastOnceStructuralFacts` — same two mechanisms (reflection + source-scan), same `[CallerFilePath]` repo-root anchor, same fail-loud-on-empty guard. Suggested name: `tests/BaseApi.Tests/Resilience/ReactivePathRetiredFacts.cs` (or `tests/BaseApi.Tests/Keeper/ReactiveTeardownFacts.cs` — discretion). Tag every fact `[Trait("Phase","48")]`.

**FACT 1 — REFLECTION (no `Fault<T>` consumer survives on the Keeper assembly):**
- Anchor the Keeper assembly via a surviving public type: `typeof(global::Keeper.Health.BitHealthLoop).Assembly` (NOT a `Consumers.Fault*` type — those are gone).
- Assert no type in the assembly implements `IConsumer<Fault<EntryStepDispatch>>` or `IConsumer<Fault<StepCompleted>>` (or, more simply and rename-proof: assert no type's name is `FaultEntryStepDispatchConsumer`/`FaultExecutionResultConsumer`, AND no type implements any `IConsumer<>` closed over a `MassTransit.Fault<>` generic). The interface-shape check is stronger: iterate `asm.GetTypes()`, for each get `GetInterfaces()`, assert none is `IConsumer<>` whose single generic arg is `Fault<>`.
- Also assert no type named `KeeperRecoveryHandler` survives.

**FACT 2 — SOURCE-SCAN (no `keeper-fault-recovery` / `keeper-dlq` reachable):**
- Scan `src/Keeper/` recursively (`*.cs`). Assert no file contains the literals `"keeper-fault-recovery"`, `"keeper-dlq"`, `KeeperQueues.FaultRecovery`, or `KeeperQueues.DeadLetter`. Since both consts are deleted from `KeeperQueues.cs`, `KeeperQueues.FaultRecovery`/`.DeadLetter` referencing code would not even compile — so the literal-string scan is the meaningful guard (a re-introduction via a raw `"keeper-dlq"` string).
- Apply the same fail-loud `Assert.True(Directory.Exists(...))` repo-root guard so a mis-anchored empty scan cannot false-pass (the T-47-01 lesson).
- **No `KeeperRecoveryHandler.cs` exclusion** here (it no longer exists) — this fact is the clean successor to the 47 scan's deliberately-excluded file.

**FACT 3 (optional, const-absence):** assert `typeof(KeeperQueues)` has no public field named `FaultRecovery` or `DeadLetter` (reflection over `KeeperQueues.GetFields()`), and DOES have `Recovery`. This is a cheap, fast, rename-proof guard that the consts are gone but the v4 one stays.

Pattern source: `AtLeastOnceStructuralFacts.cs` lines 43-111 (reflection FACT A + source-scan FACT B + `RepoRoot()`). `[VERIFIED: Read]`

## 6. RETIRE-01/02 Remnant-Verify Approach

**RETIRE-01 (SC-1: no `MessageIdentity`/`flag[H]` on the execution path):**
- **Already covered by a green test.** `AtLeastOnceStructuralFacts.No_dedup_machinery_on_execution_path` (`[Trait("Phase","47")]`) reflects over the Orchestrator + BaseProcessor.Core assemblies and asserts no `MessageIdentity` type/member survives. The 48 audit row is **cite-existing** — no new assertion needed. `[VERIFIED: Read AtLeastOnceStructuralFacts FACT A + 47-DLQ-AUDIT row]`
- For belt-and-suspenders, the plan MAY add a one-line remnant-grep verify (no `flag[` / `MessageIdentity` / CAS `Pending→Ack` in `src/`) but it would duplicate the green reflection guard. Recommendation: **cite the existing test; do not add a redundant assertion.**

**RETIRE-02 (SC-2: no content-addressed L2 data / result manifest / N×M fan-out; L2 data is GUID-entryId-only):**
- L2 data builder is **GUID-only** today: `L2ProjectionKeys.ExecutionData(Guid entryId) => "skp:data:{entryId:D}"` is the sole data builder; the doc-comment explicitly records "the legacy 64-hex content-addressed string overload was removed in v4.0.0." `[VERIFIED: Read L2ProjectionKeys:40-42]`
- **No manifest type exists** (grep for `Manifest` returns nothing in `src/`). N completed items → N per-item results (PIPE-07/ORCH-01, design doc A8/A12). `[VERIFIED: grep manifest → 0 src hits]`
- **No existing single named test asserts SC-2 structurally.** Two options:
  1. **Lightest (recommended):** add a small `[Trait("Phase","48")]` reflection/source fact: assert `L2ProjectionKeys.ExecutionData` has exactly one overload and its parameter type is `Guid` (a re-introduced `string`/64-hex overload trips it); assert no type named `*Manifest*` exists on the execution-path assemblies. Co-locate in the new Phase-48 fact-class.
  2. Cite the green Phase-43/44 tests that exercise the GUID-keyed path + the green Phase-47 `TypedResultConsumerFacts` (no-manifest per-item consume) and treat SC-2 as cite-existing.
- Recommendation: option 1's `ExecutionData`-is-Guid-only assertion is cheap, falsifiable, and directly proves "L2 data is GUID entryId scheme only" (the literal SC-2 wording). Pair it with a cited green PIPE-07/ORCH-01 test for the no-manifest fan-out.

**Removing the Phase-47 `KeeperRecoveryHandler.cs` exclusion (the §specifics task):**
- `AtLeastOnceStructuralFacts.No_v4_give_up_path_references_keeper_dlq` (lines 96-99) excludes `KeeperRecoveryHandler.cs` by filename from its `src/Keeper/Recovery/` scan, because that dormant file legitimately referenced `keeper-dlq`. Once `KeeperRecoveryHandler.cs` is **deleted** (§1A), the exclusion is **moot** — remove the `.Where(f => Path.GetFileName(f) != "KeeperRecoveryHandler.cs")` line (line 99) so the scan is unconditional over `Recovery/`. Update the FACT B doc-comment (lines 67-99) to drop the "retired Phase 48 … excluded" prose. After the edit, re-run `--filter-trait "Phase=47"` to confirm the now-unconditional scan still passes (it must — no surviving `Recovery/` file references `keeper-dlq`). `[VERIFIED: Read lines 67-111]`

## 7. Build-Order / Wave Guidance

Every intermediate commit MUST compile (43-CONTEXT D-01/D-03 invariant). The hard constraint: **a const cannot be removed while a not-yet-deleted file still references it**, and **deleting source breaks the reactive tests in the same compilation unit (`BaseApi.Tests`)**. Recommended ordering — treat as ONE wave; sub-commits are optional but each must build:

**Sub-commit 1 — Delete the reactive surface + its tests together (single atomic unit):**
- Delete `FaultEntryStepDispatchConsumer(.cs+Definition)`, `FaultExecutionResultConsumer(.cs+Definition)`, `KeeperRecoveryHandler.cs`.
- Delete `KeeperMetrics.cs`.
- Edit `L2ProbeRecovery.cs` (drop `RunAsync`, `ProbeOutcome`, the `opts`+`metrics` ctor params).
- Edit `Program.cs` (remove lines 49, 57-58, 71 + dead usings).
- Delete `KeeperFaultConsumerScopeTests`, `KeeperRecoverCapTests`, `KeeperRoundRobinTests`, `KeeperPausePublishTests`, `KeeperProbeLoopTests`, `KeeperMetricsFacts`.
- Edit `KeeperHostBootFixture` (drop the two reactive consumer registrations + the `KeeperRecoveryHandler` singleton).
- Edit `KeeperDependencyFirewallTests` (re-anchor off a surviving type).
- **Why together:** any subset leaves either a dangling `KeeperRecoveryHandler` reference (consumers) or a test referencing a deleted type (won't compile). The const `KeeperQueues.DeadLetter` is still present at this point, so `KeeperDlqConsolidationTests` lines 200-205 still compile — defer those to sub-commit 2.

**Sub-commit 2 — Remove the now-unreferenced consts + dead config/key:**
- Edit `KeeperQueues.cs` (remove `FaultRecovery` + `DeadLetter`, fix the dangling `<see cref>`).
- Edit `KeeperDlqConsolidationTests.cs` (remove the `KeeperQueues.DeadLetter` assertions — they reference the const just deleted).
- Edit `L2ProjectionKeys.cs` (remove `KeeperRecoverAttempts`).
- Edit `ProbeOptions.cs` + `appsettings.json` (remove `RecoverAttemptCap`).
- **Why after sub-commit 1:** the const's only referencers (`KeeperRecoveryHandler`, the two Definitions, `KeeperMetrics` comment) are gone, so removal compiles. `KeeperDlqConsolidationTests` is the last referencer of `DeadLetter` and is edited in the SAME commit as the const removal.

**Sub-commit 3 — Add the guard + widen the 47 scan:**
- Add `ReactivePathRetiredFacts.cs` (`[Trait("Phase","48")]`).
- Edit `AtLeastOnceStructuralFacts.cs` (remove the `KeeperRecoveryHandler.cs` exclusion — §6).
- Add the SC-2 `ExecutionData`-is-Guid-only assertion (§6 option 1).

**Sub-commit 4 — Reconciliation (D-04, docs only, no build impact):**
- `48-TEARDOWN-AUDIT.md`, `REQUIREMENTS.md` status flips, design-doc amendment (A17).

**Atomic-commit flags for the planner:**
- The const removal (`DeadLetter`) and its last test referencer (`KeeperDlqConsolidationTests` assertions) MUST share a commit.
- The `L2ProbeRecovery` ctor-param removal and the `KeeperMetrics` deletion MUST share a commit (otherwise `KeeperMetrics` is registered-but-dead, an SC-4 remnant).
- Deleting any reactive source file and the tests that reference it MUST share a commit (same `BaseApi.Tests` compilation unit).

## Common Pitfalls

### Pitfall 1: Treating `RecoveryOptions` as a reactive orphan
**What goes wrong:** 48-CONTEXT hypothesized `RecoveryOptions` is "likely reactive-only." Deleting it breaks the v4 5-state recovery consumers (`UsePartitioner` slot count + the gate-wait CTS bound).
**How to avoid:** §2 proves both members are v4-shared. **Do not touch `RecoveryOptions` or the `Recovery` appsettings section.**

### Pitfall 2: Keeping the `KeeperMetrics` meter "for the v4 path"
**What goes wrong:** D-02 says "keep the v4 meter," implying a v4 meter usage exists. It does not — no v4 code emits a Keeper metric. Keeping the registered-but-unused meter is exactly the dead remnant SC-4 forbids.
**How to avoid:** delete the whole `KeeperMetrics.cs`. Flag for discuss only if a future-instrumentation scaffold is explicitly wanted (Assumptions A1).

### Pitfall 3: Removing the legitimate `H` on Pause/Resume contracts
**What goes wrong:** `PauseWorkflow`/`ResumeWorkflow` carry a positional `string H` — this is the v4 BIT-gate key, NOT the retired dedup `H`. A naive "remove all `H`" sweep breaks the live pause/resume path and the BitHealthLoop publish.
**How to avoid:** the RETIRE-01 guard is reflection-over-`MessageIdentity`, deliberately NOT a `flag[`/`.H` string-scan, precisely to avoid this false positive (see `AtLeastOnceStructuralFacts` FACT A doc-comment). Keep `H` on those two contracts.

### Pitfall 4: Non-compiling intermediate commit
**What goes wrong:** deleting source then building before deleting the dependent tests (same `BaseApi.Tests.csproj` — the project won't compile). Or removing a const while a consumer/test still references it.
**How to avoid:** the atomic-commit grouping in §7.

### Pitfall 5: False-pass source-scan in the new guard
**What goes wrong:** a mis-anchored `Directory.EnumerateFiles` over a non-existent path silently finds zero offenders → green for the wrong reason.
**How to avoid:** copy the `Assert.True(Directory.Exists(dir), ...)` fail-loud guard + `[CallerFilePath]` repo-root walk from `AtLeastOnceStructuralFacts` (the T-47-01 lesson).

## Runtime State Inventory

This is a deletion phase touching code + RabbitMQ topology + one appsettings key. Per the rename/refactor checklist:

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | Redis key `skp:keeper:attempts:{h}` (the reactive recover-attempt counter, `L2ProjectionKeys.KeeperRecoverAttempts`) — short-TTL (300s), self-expiring. No persistent data keyed on the reactive path. | None — code-only removal of the builder. Any in-flight counter keys self-expire within 300s; hermetic phase does not touch a live Redis. |
| Live service config | RabbitMQ `keeper-fault-recovery` queue + `Fault<EntryStepDispatch>` exchange binding + `keeper-dlq` queue exist on a deployed broker (topology is bus-declared, NOT in git). | **Deliberately NOT reconciled in Phase 48** (D-03: no triple-SHA gate; the topology delta is intended). Phase 49 takes its net-zero baseline from the post-teardown topology (Deferred note). Hermetic suite does not declare these. |
| OS-registered state | None — no Task Scheduler / pm2 / systemd entries reference the reactive path. | None — verified by inspection (this is a containerized .NET console, no host registrations). |
| Secrets/env vars | None — no secret/env var references `keeper-dlq`/`keeper-fault-recovery`/`RecoverAttemptCap`. The `Recovery`/`Probe`/`Backup` sections are plain appsettings, only `RecoverAttemptCap` removed. | None beyond the appsettings edit (§1B). |
| Build artifacts | None stale — deleting `.cs` files + rebuilding `SK_P.sln` regenerates all `bin/obj`; no egg-info/global-install equivalent. | None — the `dotnet build` close gate (D-03) is the artifact refresh. |

**Canonical question — after every file is updated, what runtime systems still hold the old string?** Only the deployed RabbitMQ broker (the two retired queues + binding), which is **intentionally deferred to Phase 49's baseline** per D-03/Deferred. Nothing else.

## Validation Architecture

> nyquist_validation is `true` in `.planning/config.json` — this section is required.

**Framing for a deletion phase:** the proof is **absence + green-suite + 0-warning-build**, not new-behavior tests. "Did we remove it" is proven by (a) the build compiling with the reactive surface gone, (b) the negative-guard facts asserting absence, and (c) the full hermetic suite staying green (no v4 regression).

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (in-repo, `tests/BaseApi.Tests`) |
| Config file | none — convention-based; runner is `dotnet run --project tests/BaseApi.Tests` (the repo's established invocation, per 47-DLQ-AUDIT verify commands) |
| Quick run command | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=48"` |
| Full suite command | `dotnet test SK_P.sln` (hermetic; the default Category, no RealStack trait) |

### Phase Requirements → Test Map
| Req / SC | Behavior (absence) | Test Type | Automated Command | File Exists? |
|----------|--------------------|-----------|-------------------|-------------|
| SC-1 / RETIRE-01 | No `MessageIdentity`/dedup member on the execution path | reflection guard | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-method "*No_dedup_machinery_on_execution_path*"` | ✅ (Phase-47, green — cite-existing) |
| SC-2 / RETIRE-02 | L2 data builder is GUID-`entryId`-only; no `*Manifest*` type | reflection/source guard | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=48"` | ❌ Wave 0 (new SC-2 fact — §6 option 1) |
| SC-3 / RETIRE-03 | No `Fault<EntryStepDispatch>`/`Fault<StepCompleted>` consumer survives on the Keeper assembly | reflection guard | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=48"` | ❌ Wave 0 (new FACT 1 — §5) |
| SC-3 / RETIRE-03 | No `keeper-fault-recovery`/`keeper-dlq` literal reachable in `src/Keeper/` | source-scan guard | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=48"` | ❌ Wave 0 (new FACT 2 — §5) |
| SC-3 / RETIRE-03 | `KeeperQueues` has no `FaultRecovery`/`DeadLetter` field; has `Recovery` | reflection guard | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=48"` | ❌ Wave 0 (new FACT 3 — §5, optional) |
| SC-1 (widen) | Phase-47 `keeper-dlq` scan passes UNCONDITIONALLY (`KeeperRecoveryHandler.cs` exclusion removed) | source-scan guard | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-method "*No_v4_give_up_path_references_keeper_dlq*"` | ✅ (Phase-47, edit to remove exclusion — §6) |
| SC-4 | Full hermetic suite GREEN ×3 + Release AND Debug 0-warning build | suite + build | `dotnet test SK_P.sln` ×3; `dotnet build SK_P.sln -c Release` + `-c Debug` (assert 0 warnings) | n/a (gate command) |

### Sampling Rate
- **Per task commit:** `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=48"` (the new guards) + `dotnet build SK_P.sln -c Debug` (must compile — the build-before-teardown invariant).
- **Per wave merge:** `dotnet test SK_P.sln` (full hermetic suite green).
- **Phase gate (D-03):** hermetic suite GREEN ×3 consecutive + `dotnet build SK_P.sln` Release AND Debug at 0 warnings, then `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Resilience/ReactivePathRetiredFacts.cs` (or `Keeper/ReactiveTeardownFacts.cs`) — the Phase-48 negative-guard fact-class: FACT 1 (no `Fault<T>` consumer), FACT 2 (no `keeper-fault-recovery`/`keeper-dlq` source literal), FACT 3 (const absence), and the SC-2 `ExecutionData`-Guid-only assertion. Covers SC-2, SC-3.
- [ ] Edit `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs` — remove the `KeeperRecoveryHandler.cs` exclusion (§6).
- [ ] No new framework install — xUnit + BCL reflection already in `BaseApi.Tests`.

*(All other validation is cite-existing green tests or the build/suite gate.)*

## Security Domain

> `security_enforcement` is not present in config (treat as default). This is a pure-deletion phase removing code — it introduces no new input handling, auth, crypto, or data flow.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Service is open by carried project-wide exclusion (REQUIREMENTS Out-of-Scope). Unchanged. |
| V3 Session Management | no | n/a |
| V4 Access Control | no | n/a |
| V5 Input Validation | no | No new input surface; deletion only. |
| V6 Cryptography | no | No crypto touched. The retired path carried no secrets. |

### Known Threat Patterns
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Re-introduction of a removed dedup/recovery path (silent regression) | Tampering | The Phase-48 negative-guard facts (§5) fail the build if a `Fault<T>` consumer or `keeper-dlq` literal returns — the teardown is self-enforcing. |
| Log-injection via removed reactive handler | Information disclosure | n/a after deletion — the handler that structured-logged exception text is gone; no new logging added. |

**Net security posture: unchanged.** The deletion reduces attack surface (one fewer consumer, one fewer queue) and adds no new trust boundary.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `KeeperMetrics.cs` should be deleted WHOLE (no v4 meter usage to preserve), despite D-02's "keep the v4 meter" wording. | §1C, §2, Pitfall 2 | If a v4 instrumentation scaffold is intentionally wanted, deleting it removes a future hook. Evidence (grep: zero v4 emit sites) strongly favors deletion; flag to discuss-phase for a one-line confirm. Low risk — re-adding an empty meter later is trivial. |
| A2 | The SC-2 verify is lightest as a new `ExecutionData`-is-Guid-only reflection fact (§6 option 1) rather than cite-only. | §6, Validation Architecture | If the verifier prefers cite-existing for SC-2, the new fact is harmless redundancy. No correctness risk. |
| A3 | `MaxAttempts` is KEPT (bound by `ProbeOptionsBoundTests`, the broker-timeout invariant) even though only `RunAsync` reads it at runtime after teardown. | §1B, §2 | If the planner reads "reactive-only members" strictly, `MaxAttempts` could be flagged for removal — but `ProbeOptionsBoundTests` (a kept test) reads it, and the BIT-loop hold-time invariant documents it. Removing it would break that green test. Keep. Low risk. |

## Open Questions

1. **`KeeperHostBootFixture` faithfulness after edit.**
   - What we know: it currently mirrors Program.cs by registering the two reactive consumers. After teardown, Program.cs registers the five `keeper-recovery` consumers + BIT loop.
   - What's unclear: whether the boot fixture should be edited to mirror the *new* Program.cs (register the five v4 consumers) or simplified to "boots the AddBaseConsole* seam with no consumers."
   - Recommendation: minimal edit (drop the reactive registrations); `KeeperHostBootTests` only asserts `IBusControl` resolves, so either form passes. Planner's discretion.

2. **Design-doc amendment number.**
   - What we know: the doc uses A15, A16; D-04 wants an additive amendment recording the retirement.
   - What's unclear: nothing blocking — next is A17.
   - Recommendation: add a top-of-doc `**Amended 2026-06-09 (A17):**` line + a `| A17 | reactive Fault<T> path + keeper-dlq retired in Phase 48; proven by 48-TEARDOWN-AUDIT.md |` row in the locked-decisions table. Wording is executor's discretion (D-04).

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK (net8.0) | build + hermetic test | ✓ (repo is an active net8.0 solution) | net8.0 | — |
| RabbitMQ / Redis live stack | NOT required (hermetic phase, D-03) | n/a | — | in-memory MassTransit harness + `FakeRedis` double (already in `tests/BaseApi.Tests/Keeper/FakeRedis.cs`) |

No live external dependency is required — D-03 makes this hermetic-only. The triple-SHA infra gate is explicitly deferred to Phase 49.

## Sources

### Primary (HIGH confidence — read this checkout)
- `src/Keeper/Program.cs`, `src/Keeper/Recovery/{L2ProbeRecovery,KeeperRecoveryHandler}.cs`, `src/Keeper/Health/BitHealthLoop.cs`, `src/Keeper/{RecoveryOptions,ProbeOptions,BackupOptions}.cs`, `src/Keeper/Observability/KeeperMetrics.cs`, `src/Keeper/Consumers/Fault*.cs`, `src/Messaging.Contracts/{KeeperQueues,PauseWorkflow,ResumeWorkflow}.cs`, `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs`, `src/Keeper/appsettings.json` — read in full.
- `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs`, `tests/BaseApi.Tests/Keeper/{KeeperProbeLoopTests,KeeperMetricsFacts,KeeperPausePublishTests,KeeperHostBootFixture,KeeperHostBootTests,KeeperDependencyFirewallTests,ProbeOptionsBoundTests}.cs` — read; `KeeperDlqConsolidationTests` lines 189-205 grepped.
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` — lines 1-122 (amendment ledger + reactive-path scope note).
- `.planning/phases/48-v3-x-teardown/48-CONTEXT.md`, `.planning/REQUIREMENTS.md`, `.planning/phases/47-.../47-DLQ-AUDIT.md`, `.planning/config.json`.
- Cross-reference greps: `RecoveryOptions|GateWaitSeconds|PartitionCount|FaultRecovery|DeadLetter|keeper-dlq|KeeperRecoveryHandler|RunAsync|RecoverAttemptCap|metrics\.` across `src/**/*.cs` and `tests/**/*.cs`.

## Metadata

**Confidence breakdown:**
- Kill-list (files to delete/edit): HIGH — every entry has a read + grep inbound-reference check.
- Reactive↔shared boundary: HIGH — every keep/delete verdict shows its concrete inbound caller (or absence). The two corrections to 48-CONTEXT (RecoveryOptions shared; KeeperMetrics fully orphaned) are grep-proven.
- Test inventory: HIGH — all six delete-whole + four edit + keeps confirmed by reading; no spike/E2E reactive tests exist.
- Negative-guard design: HIGH — mirrors a verified, green in-repo pattern.
- RETIRE-01/02 verify: HIGH — RETIRE-01 cite-existing green test confirmed; RETIRE-02 L2-data-Guid-only confirmed from the key builder source.

**Research date:** 2026-06-09
**Valid until:** 2026-07-09 (stable — internal codebase enumeration; only invalidated by intervening edits to the Keeper console).
