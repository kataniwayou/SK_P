# Phase 50: Contracts & Slot-Array L2 Key Reshape - Research

**Researched:** 2026-06-11
**Domain:** .NET/C# contract-shape + compile-keeping (MassTransit recovery contracts, Redis L2 key builders, xUnit golden/reflection tests)
**Confidence:** HIGH (all findings are direct reads of the current source tree; zero web/training reliance)

## Summary

This is a **contract-shape + compile-keeping only** phase. Every WHAT is locked in CONTEXT.md (A18 decisions D-01..D-09) and the LOCKED design-doc §"Recovery Re-architecture (A18)". This research maps the exact CURRENT CODE STATE so the planner can write concrete edits, and pins precisely what compile-breaks when the Model-B contracts are deleted.

The break surface is well-bounded and fully enumerated below. Deleting `KeeperUpdate`/`KeeperCleanup`, `L2ProjectionKeys.CompositeBackup`, and `Keeper.BackupOptions` breaks: (1) `ProcessorPipeline` (two send call-sites + two builder methods + a stale doc comment); (2) the single-owner `UpdateConsumerDefinition` (two `UsePartitioner<>` lines + the class itself is keyed on the to-be-deleted `UpdateConsumer`); (3) `UpdateConsumer` + `CleanupConsumer` bodies (to delete); (4) the THREE surviving consumer bodies (`Reinject`/`Inject`/`Delete`) and `RecoveryConsumerBase` — ALL of which ctor-inject `IOptions<BackupOptions>`, so deleting `BackupOptions` breaks all four constructors even though only `Inject`/`Cleanup`/`Update` reference `CompositeBackup` in their bodies; (5) `Program.cs` (two `AddConsumer` registrations + the `Configure<BackupOptions>` line); and (6) a sizeable test surface that the planner must update in the SAME wave or the hermetic suite goes red.

**Primary recommendation:** Follow D-01 verbatim — delete the two orphan consumers + their definitions + `BackupOptions` + the two pipeline builders; re-home endpoint single-ownership onto `ReinjectConsumerDefinition` (reduced to the 3 surviving `UsePartitioner<>` types); reduce the 3 surviving consumer bodies + the pipeline Post Model-B mechanics to compile-only stubs; drop the `IOptions<BackupOptions>` ctor parameter from `RecoveryConsumerBase` and all three surviving consumers. The largest hidden cost is the **test surface** (Section "Test Impact Map") — six test files reference the deleted symbols and must be deleted or rewritten in-wave to keep the hermetic suite green.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| L2 key string format (incl. new `MessageIndex`) | `Messaging.Contracts` (leaf) | — | D-01/Phase-21: single source of truth consumed by both writer and reader; `MessageIndex` joins the existing flat-scheme builders |
| Keeper-state contract shapes (`REINJECT`/`INJECT`/`DELETE`) | `Messaging.Contracts` (leaf) | — | The wire envelopes both `BaseProcessor.Core` (P51) and `Keeper` (P52) build against |
| Partition 4-tuple marker (`IKeeperRecoverable`) | `Messaging.Contracts` (leaf) | — | D-03: unchanged; survives the composite-key-builder deletion |
| Processor Post-Process recovery sends | `BaseProcessor.Core` | — | Real A18 forward/recovery rewrite is Phase 51; Phase 50 only stubs the Model-B mechanics |
| Keeper recovery consumer bodies | `Keeper` console | — | Real 3-state A18 bodies are Phase 52; Phase 50 only stubs survivors + deletes orphans |
| Endpoint single-ownership (retry + partitioner) on `keeper-recovery` | `Keeper` console (`ConsumerDefinition`) | — | Must re-home off the deleted `UpdateConsumerDefinition` onto a survivor, preserving the single-owner invariant |

## User Constraints (from CONTEXT.md)

### Locked Decisions (A18 — verbatim)

- **D-01 — Delete-orphans + stub survivors.** Delete `UpdateConsumer`+`CleanupConsumer`+their `ConsumerDefinition`s, their `Program.cs` registrations, `BackupOptions`, and `ProcessorPipeline.BuildUpdate`/`BuildCleanup`. Re-home endpoint single-ownership (currently on `UpdateConsumerDefinition` — sole owner of `UseMessageRetry` + the `UsePartitioner<>` set on `keeper-recovery`) onto a surviving definition (e.g. `ReinjectConsumerDefinition`); drop the partitioner set to the 3 surviving types. Reduce the 3 surviving consumer bodies (`Reinject`/`Inject`/`Delete`) and the `ProcessorPipeline` Post Model-B mechanics to compile-only stubs (`throw NotImplementedException` / no-op).
- **D-02 — "dark-but-compiling pending real retirement"** precedent (Phase 43). Locked build order: 50 → 51 → 52 → 53.
- **D-03 — `IKeeperRecoverable` partition marker unchanged.** It is the partition 4-tuple, not the deleted L2 key string; `PartitionKey`/`PartitionGuid` stay.
- **D-04 — Array-key + Redis HASH.** New builder `L2ProjectionKeys.MessageIndex(Guid messageId)` → `skp:msg:{messageId:D}`. `msg:` is a required namespace discriminator. `messageId` = MassTransit broker `MessageId` (a `Guid`).
- **D-05 — slot is a Redis HASH field** (int slot index → entryId value), NOT part of the key string. Builder signature locks the structure family for Phase 51.
- **D-06 — Golden-test-pinned.** A golden test pins the exact `skp:msg:{messageId:D}` string.
- **D-07 — Defer TTL to Phase 51.** The key builder bakes NO TTL (caller concern, mirrors `ExecutionData`).
- **D-08 — INJECT gains `EntryId` (Guid), `Data` (string), `DeleteEntryId` (Guid)** beyond the 5-id base. `DeleteEntryId` name tracks the A18 spec literal `deleteEntryId`.
- **D-09 — `KeeperReinject` (`EntryId`+`Payload`) and `KeeperDelete` (`EntryId`) already match A18 — no change** (verify match).

### Claude's Discretion
- Exact surviving definition chosen as the re-homed endpoint single-owner (`ReinjectConsumerDefinition` suggested).
- Stub style (`NotImplementedException` vs no-op return) per call site, as long as the solution builds 0-warning and no Model-B contract reference survives.
- Whether the HASH-field slot index is pinned in the same golden test or a sibling.

### Deferred Ideas (OUT OF SCOPE)
- Slot-array `TTL(random)` min/max range + options record → Phase 51.
- Real processor FORWARD/RECOVERY pass logic → Phase 51.
- Real 3-state Keeper recovery consumer → Phase 52.
- Full Model-B teardown + reflection/source remnant sweep (RETIRE-03) → Phase 53.

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| RETIRE-01 | The composite backup key `L2[corr:wf:ProcessorId:executionId]` + its `BackupOptions` TTL are removed | `L2ProjectionKeys.CompositeBackup` (lines 44-47) + `Keeper/BackupOptions.cs` mapped; all references enumerated in "Break Analysis" + "Test Impact Map" |
| RETIRE-02 | The `UPDATE` and `CLEANUP` keeper-state contracts + consumers are removed | `KeeperUpdate.cs`, `KeeperCleanup.cs`, `UpdateConsumer(.cs/Definition.cs)`, `CleanupConsumer(.cs/Definition.cs)` mapped; the partition single-ownership re-home path identified |

**Scope note:** RETIRE-03 (full reflection/source remnant sweep) is Phase 53, NOT this phase. Phase 50 removes the contracts at the contract level and keeps the solution compiling; the negative remnant-scan that proves NO Model-B references survive anywhere is a Phase-53 concern. Success criterion 2 ("a source/reflection scan finds no references") is satisfiable in Phase 50 at the level of "the symbols no longer exist and nothing references them" — see Validation Architecture for the lighter in-phase guard.

---

## Per-File Current-State Map

### 1. `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` (EDIT: add `MessageIndex`, delete `CompositeBackup`)

Static class, `const string Prefix = "skp:"` (line 30). Flat single-prefix scheme, NO type discriminator except where a literal segment is added (`data:`). Current builders:

| Builder | Signature | Output | Line |
|---------|-----------|--------|------|
| `ParentIndex()` | `()` | `skp:` | 32 |
| `Root(Guid workflowId)` | | `skp:{workflowId:D}` | 34 |
| `Step(Guid workflowId, Guid stepId)` | | `skp:{workflowId}:{stepId}` | 36 |
| `Processor(Guid processorId)` | | `skp:{processorId}` | 38 |
| `ExecutionData(Guid entryId)` | | `skp:data:{entryId:D}` | 42 |
| **`CompositeBackup(Guid corr, wf, proc, exec)`** | | `skp:{corr:D}:{wf:D}:{proc:D}:{exec:D}` | **44-47 — DELETE** |
| `KeeperProbe(string h)` | | `skp:keeper:probe:{h}` | 50 |

**ExecutionData is the precedent for D-04** (`msg:` discriminator + no baked TTL): `=> $"{Prefix}data:{entryId:D}"`. New builder per D-04/D-06:
```csharp
public static string MessageIndex(Guid messageId) => $"{Prefix}msg:{messageId:D}";
```
**Note on the XML doc-comment `<list>` (lines 19-26):** it documents every builder including the `CompositeBackup` item (line 25). Deleting the builder requires deleting its `<item>` AND adding a `MessageIndex` item, or the comment drifts (not a compile error, but a 0-warning-build review concern — the doc comment references `<see cref="Root"/>` etc., not `CompositeBackup` by cref, so no cref-break).

### 2. The five Keeper-state contracts (`src/Messaging.Contracts/`)

All are `sealed record`s with the positional 5-id base ctor `(Guid WorkflowId, Guid StepId, Guid ProcessorId)` + `init` props for `CorrelationId`/`ExecutionId` + per-state extras. Default STJ, NO `[JsonPropertyName]`. All implement `IKeeperRecoverable`.

| Contract | File | Current extras | A18 action |
|----------|------|----------------|------------|
| `KeeperReinject` | `KeeperReinject.cs` | `EntryId` (Guid, init), `Payload` (string, init, `= ""`) | **UNCHANGED** (D-09 — matches `REINJECT(ids, entryId, payload)`) — verify only |
| `KeeperDelete` | `KeeperDelete.cs` | `EntryId` (Guid, init) | **UNCHANGED** (D-09 — matches `DELETE(entryId)`) — verify only |
| `KeeperInject` | `KeeperInject.cs` | NONE (just the 5-id base + CorrelationId/ExecutionId) | **ADD** `EntryId` (Guid), `Data` (string `= ""`), `DeleteEntryId` (Guid) per D-08 |
| `KeeperUpdate` | `KeeperUpdate.cs` | `ValidatedData` (string, init, `= ""`) | **DELETE** entire file |
| `KeeperCleanup` | `KeeperCleanup.cs` | NONE | **DELETE** entire file |

Current `KeeperInject.cs` (lines 6-10): `public sealed record KeeperInject(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable { public Guid CorrelationId { get; init; } public Guid ExecutionId { get; init; } }`. Target adds three init props following the exact idiom (`Data` mirrors the deleted `KeeperUpdate.ValidatedData` raw-string convention, D-02/D-08):
```csharp
public Guid EntryId       { get; init; }   // D-08: allocation to write L2[entryId]=data
public string Data        { get; init; } = "";   // D-08: raw-JSON output, in-hand on the envelope
public Guid DeleteEntryId { get; init; }   // D-08: source entryId deleted after the orchestrator send
```

### 3. `src/Messaging.Contracts/IKeeperRecoverable.cs` (UNCHANGED — D-03, verify)

Interface declaring exactly the 4-tuple `CorrelationId`, `WorkflowId`, `ProcessorId`, `ExecutionId` (lines 10-13) — directly, NOT inherited, so `GetProperties()` surfaces exactly four. StepId deliberately NOT here. The doc comment (line 3) calls this "the composite-backup key" 4-tuple — that is the PARTITION key, distinct from the deleted `L2ProjectionKeys.CompositeBackup` string builder. D-03 confirms this stays; `PartitionKey`/`PartitionGuid` live on `UpdateConsumerDefinition`, not here.

### 4. `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (EDIT: delete builders + stub Post mechanics)

`sealed class` primary-ctor pipeline runner. References to delete/stub:

| Line | Reference | Action |
|------|-----------|--------|
| 35, 38 | Doc comment naming `KeeperUpdate`/`KeeperCleanup` in the Post stage | Update comment (0-warning review) |
| 139 | `await SendKeeper(BuildUpdate(d, item), limit, ct);` — first Post send on a completed item | Stub/remove per D-01 (Model-B mechanic) |
| 148 | `await SendKeeper(BuildCleanup(d, item), limit, ct);` — after write-success | Stub/remove per D-01 |
| 237-238 | `private static KeeperUpdate BuildUpdate(...)` | **DELETE** |
| 243-244 | `private static KeeperCleanup BuildCleanup(...)` | **DELETE** |

**Survives untouched in Phase 50** (real A18 rewrite is Phase 51): `BuildReinject` (231-232), `BuildDelete` (234-235), `BuildInject` (240-241). NOTE `BuildInject` currently sends a `KeeperInject` with ONLY the 5-id base + CorrelationId/ExecutionId (line 241) — after D-08 adds three required-by-A18 fields, the existing call still COMPILES (the new fields are `init` with defaults: `EntryId`/`DeleteEntryId` default `Guid.Empty`, `Data` defaults `""`). So adding the `KeeperInject` fields does NOT break `BuildInject`. The real population of those fields is Phase 51. **The Phase-50 stub of the Post mechanics must leave `RunAsync` compiling**: removing lines 139 + 148 leaves a `foreach` over `items` whose completed branch (137-157) writes L2 + mints entryId + sends `StepCompleted` — that still compiles without the two keeper sends; verify the `using (logger.BeginScope...)` block (149-156) and the `write.Succeeded` branch (143-147, which calls `BuildInject`) remain valid. Discretion (D-01) allows `throw new NotImplementedException()` OR a no-op stub of the whole Post block as long as it compiles 0-warning — but note the existing PipelinePostFacts tests assert the live Model-B behavior and will need deletion/rewrite (see Test Impact Map).

`SendKeeper` (207-213) takes `IKeeperRecoverable` — unaffected by which concrete types exist.

### 5. `src/Keeper/Recovery/` — orphans to delete, survivors to stub

**DELETE (4 files):**
- `UpdateConsumer.cs` — body reads `CompositeBackup` (line 24) + writes `m.ValidatedData` with `TtlDays` (25). Subclasses `RecoveryConsumerBase<KeeperUpdate>`.
- `UpdateConsumerDefinition.cs` — **THE SINGLE-OWNER endpoint definition** (see below).
- `CleanupConsumer.cs` — body deletes `CompositeBackup` (23). Subclasses `RecoveryConsumerBase<KeeperCleanup>`.
- `CleanupConsumerDefinition.cs` — intentional no-op `ConfigureConsumer`.

**STUB survivors (3 consumer bodies + base):**
- `ReinjectConsumer.cs` — body (24-49) reads `ExecutionData(m.EntryId)` via STRLEN (33), reconstructs `EntryStepDispatch` (38-43), sends to `queue:{ProcessorId:D}`. Does NOT reference `CompositeBackup` or any deleted contract in its body — BUT its ctor (17-21) injects `IOptions<BackupOptions> backupOptions` and passes it to the base (21). **Deleting `BackupOptions` breaks this ctor.**
- `InjectConsumer.cs` — body (34-65) reads `CompositeBackup` (36) → writes `ExecutionData(entryId)` → sends `StepCompleted` → deletes composite. **References `CompositeBackup` (36)** AND injects `IOptions<BackupOptions>` (31). Both break. The A18 INJECT body (write `L2[entryId]=data` from the in-hand `m.Data`, send, delete `m.DeleteEntryId`) is the Phase-52 rewrite; Phase 50 reduces this to a compile-only stub.
- `DeleteConsumer.cs` — body (20-21) deletes `ExecutionData(m.EntryId)` — no `CompositeBackup` reference — BUT ctor (14-18) injects `IOptions<BackupOptions>` (17). Ctor breaks.
- `RecoveryConsumerBase.cs` — abstract base. **ctor parameter `IOptions<BackupOptions> backupOptions` (line 32) + `protected int TtlDays => backupOptions.Value.TtlDays;` (38) both reference `BackupOptions`** and break on deletion. Removing the parameter + the `TtlDays` property is required; all subclass `: RecoveryConsumerBase<T>(redis, ..., backupOptions)` base-calls must drop the last arg in lockstep. Only `UpdateConsumer` used `TtlDays` — the survivors do not.
- `ReinjectConsumerDefinition.cs` / `InjectConsumerDefinition.cs` / `DeleteConsumerDefinition.cs` — all currently no-op `ConfigureConsumer`. **One of these (D-01 suggests `ReinjectConsumerDefinition`) becomes the new single-owner.**

**Unaffected survivors:** `L2ProbeRecovery.cs`, `RecoveryDataGoneException.cs`, `RecoveryGateTimeoutException` (in `RecoveryConsumerBase.cs`, 85-88).

### 5a. The single-owner endpoint re-home (`UpdateConsumerDefinition` → `ReinjectConsumerDefinition`)

`UpdateConsumerDefinition.cs` (28-85) is the SOLE owner on `keeper-recovery` of:
- `EndpointName = KeeperQueues.Recovery;` (37)
- `endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));` (47)
- ONE shared `Partitioner` instance (61) + **five** `UsePartitioner<T>` lines (62-66): `KeeperUpdate`, `KeeperReinject`, `KeeperInject`, `KeeperDelete`, `KeeperCleanup`.
- `public static string PartitionKey(IKeeperRecoverable m)` (74-75) → `{corr:D}:{wf:D}:{proc:D}:{exec:D}`
- `public static Guid PartitionGuid(IKeeperRecoverable m)` (80-84) — SHA256-over-PartitionKey, first 16 bytes.
- Ctor injects `IOptions<RetryOptions>` + `IOptions<RecoveryOptions>` (33).
- Uses `MassTransit.Middleware.Partitioner` + `Murmur3UnsafeHashGenerator` (8.5.5 namespace, verified vs assembly — line 4 comment).

Re-home target (D-01: `ReinjectConsumerDefinition`) must:
1. Carry the ctor `(IOptions<RetryOptions>, IOptions<RecoveryOptions>)` (currently parameterless).
2. Move `UseMessageRetry` + the shared `Partitioner` + the partitioner lines for the **3 surviving types only** (`KeeperReinject`, `KeeperInject`, `KeeperDelete`) — DROP the `KeeperUpdate`/`KeeperCleanup` lines (those types are gone).
3. Move `PartitionKey`/`PartitionGuid` static helpers. **CRITICAL:** `RecoveryPartitionFacts.cs` references them as `UpdateConsumerDefinition.PartitionKey`/`.PartitionGuid` (8 call-sites) — the test must be re-pointed to the new owner OR the helpers' home type changes and the test follows. The doc comment on these helpers (73) explains they are `public static` specifically so `RecoveryPartitionFacts` can pin the shape without `InternalsVisibleTo`.
4. The remaining two definitions (`InjectConsumerDefinition`, `DeleteConsumerDefinition`) stay intentional no-ops; `ReinjectConsumerDefinition` is no longer a no-op.

### 6. `src/Keeper/Program.cs` (EDIT)

| Line | Reference | Action |
|------|-----------|--------|
| 28-30 | `builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection("Backup"));` (+ its D-10 comment) | **DELETE** (BackupOptions gone) |
| 51 | `x.AddConsumer<Keeper.Recovery.UpdateConsumer, Keeper.Recovery.UpdateConsumerDefinition>();` | **DELETE** |
| 55 | `x.AddConsumer<Keeper.Recovery.CleanupConsumer, Keeper.Recovery.CleanupConsumerDefinition>();` | **DELETE** |
| 52-54 | `ReinjectConsumer`/`InjectConsumer`/`DeleteConsumer` registrations | KEEP (these are the 3 survivors) |

The comment block (46-50) describing "five gate-open-only recovery consumers" + "`UpdateConsumerDefinition` is the SINGLE OWNER" must be updated to "three" + the new owner (0-warning review concern, not a compile break).

### 7. `src/Keeper/BackupOptions.cs` (DELETE) + `src/Keeper/RecoveryOptions.cs` (verify, no change)

- `BackupOptions.cs` — `sealed class { int TtlDays = 2; }`. **DELETE.** Bound from "Backup" appsettings section.
- `RecoveryOptions.cs` — `PartitionCount` (8) + `GateWaitSeconds` (300). The doc comment (line 4) `<see cref="BackupOptions"/>` cref **BREAKS** when `BackupOptions` is deleted (a dangling cref → CS1574 → fatal under `TreatWarningsAsErrors`). Fix: remove/adjust the cref. No structural coupling otherwise.

### 8. Golden-test project + key-format pinning location

**Single test project:** `tests/BaseApi.Tests/BaseApi.Tests.csproj` (xUnit v3 / Microsoft.Testing.Platform, OutputType=Exe). It ProjectReferences all of `BaseApi.Core`, `BaseApi.Service`, `BaseConsole.Core`, `Orchestrator`, `Keeper`, `BaseProcessor.Core`, `Processor.Sample`.

**Key-format golden tests:** `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs` ([Trait Phase=22]). Pins exact byte strings for `ParentIndex`/`Root`/`Step`/`Processor`/`ExecutionData` AND a `CompositeBackup_Produces_...` test (lines 71-82) that **must be deleted** (the builder is going away). The `ExecutionData` pins (55-60, 85-88) are the template for the new `MessageIndex` pin (D-06):
```csharp
[Fact]
public void MessageIndex_Produces_Prefix_Msg_Discriminator_Plus_HyphenatedGuid()
    => Assert.Equal("skp:msg:55555555-5555-5555-5555-555555555555",
        L2ProjectionKeys.MessageIndex(Guid.Parse("55555555-5555-5555-5555-555555555555")));
```
Discretion D-06: pin in this same file (matches the existing `ExecutionData` precedent). The HASH-field slot index is NOT part of the key string (D-05), so there is nothing key-string to pin for the slot — a sibling structural note can be deferred.

### 9. Build / test commands (verified from Directory.Build.props + scripts/phase-49-close.ps1)

- **0-warning is enforced at compile time** via `Directory.Build.props`: `TreatWarningsAsErrors=true`, `Nullable=enable`, `EnforceCodeStyleInBuild=true`, `AnalysisMode=latest`, `LangVersion=latest`, `TargetFramework=net8.0` (repo-wide; per-csproj must NOT redeclare). A dangling XML `<see cref>` (CS1574) or unused-using becomes a fatal build error.
- **Both-config build gate** (the success-criterion-4 "0-warning Release + Debug"):
  ```
  dotnet build SK_P.sln -c Release
  dotnet build SK_P.sln -c Debug
  ```
- **Hermetic suite** (the success-criterion-4 "hermetic suite green"). The full-suite command in the close script is `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release --no-build`. RealStack E2E facts are tagged `[Trait("Category","RealStack")]`; the hermetic subset is the trait-filtered run. The historical hermetic filter idiom in this repo is `--filter-not-trait Category=RealStack` (see ROADMAP Phase-37 entry: "477-pass hermetic suite (`--filter-not-trait Category=RealStack`)"). For xUnit v3 / MTP the equivalent is the MTP filter; the planner should use the project's established hermetic invocation:
  ```
  dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release --filter-not-trait "Category=RealStack"
  ```
  `xunit.runner.json` caps `maxParallelThreads: 6`, `parallelAlgorithm: conservative`.
- **CONFIRM** the exact MTP filter flag at plan-time — xUnit v3 MTP changed CLI surface vs VSTest. `[ASSUMED]` that `--filter-not-trait` still resolves under the MTP runner; the close script itself runs the FULL suite against a live stack, so the hermetic-only filter is used in dev iteration, not the close gate.

---

## Build-Keeping Break Analysis (what compile-breaks + the minimal stub)

| # | Deleted symbol | Breaks (file:line) | Minimal compile-keep |
|---|----------------|--------------------|----------------------|
| B1 | `L2ProjectionKeys.CompositeBackup` | `UpdateConsumer.cs:24`, `CleanupConsumer.cs:23`, `InjectConsumer.cs:36` (src); `L2ProjectionKeysTests.cs:72-82`, `UpdateConsumerFacts.cs:42`, `CleanupConsumerFacts.cs:40`, `InjectConsumerFacts.cs:30,89`, `SC2RecoveryPathsE2ETests.cs:165` (test) | Update/Cleanup consumers deleted; InjectConsumer body stubbed; delete the composite golden test + the Update/Cleanup facts; rewrite/stub Inject facts |
| B2 | `KeeperUpdate` (record) | `ProcessorPipeline.cs:237-238` (`BuildUpdate`), `:139` (call); `UpdateConsumer.cs:20,22`; `UpdateConsumerDefinition.cs:62` (`UsePartitioner<KeeperUpdate>`); `KeeperContractTests.cs:22,53-56`; `PipelinePostFacts.cs:62,110`; `UpdateConsumerFacts.cs` (whole); `DispatchTestKit.cs:250` (comment) | Delete `BuildUpdate` + its call; delete `UpdateConsumer`; drop the partitioner line; update contract test; rewrite/delete pipeline + update facts |
| B3 | `KeeperCleanup` (record) | `ProcessorPipeline.cs:243-244` (`BuildCleanup`), `:148` (call); `CleanupConsumer.cs:19,21`; `UpdateConsumerDefinition.cs:66`; `RecoveryGateWaitFacts.cs:41,44,51,62-63` (uses `KeeperCleanup` as the probe message type!); `KeeperContractTests.cs:23,86-89`; `PipelinePostFacts.cs:63,113`; `CleanupConsumerFacts.cs` (whole) | Delete `BuildCleanup` + call; delete `CleanupConsumer`; drop partitioner line; **re-point `RecoveryGateWaitFacts.ProbeConsumer` to a SURVIVING contract type** (e.g. `KeeperDelete`); update contract test |
| B4 | `Keeper.BackupOptions` | `RecoveryConsumerBase.cs:32,38`; `UpdateConsumer.cs:19`; `CleanupConsumer.cs:18`; `ReinjectConsumer.cs:20`; `InjectConsumer.cs:31`; `DeleteConsumer.cs:17`; `Program.cs:30`; `RecoveryOptions.cs:4` (XML cref!); `BackupOptionsBoundTests.cs` (whole); `RecoveryTestKit.cs:36-37` (`Backup()` helper) + every `RecoveryTestKit.Backup(...)` call-site; `RecoveryDeadLetterFacts.cs:66,158`; `RecoveryGateWaitFacts.cs:40,60` | Remove the `IOptions<BackupOptions>` ctor param from base + all 3 survivors; delete `Program.cs` line; fix `RecoveryOptions` cref; delete `BackupOptionsBoundTests`; remove the `Backup()` helper + drop the arg at every consumer construction in test kit/facts |
| B5 | `UpdateConsumer` (class) | Referenced by `UpdateConsumerDefinition : ConsumerDefinition<UpdateConsumer>`; `Program.cs:51` | Both deleted together |
| B6 | `UpdateConsumerDefinition.{PartitionKey,PartitionGuid}` (moves) | `RecoveryPartitionFacts.cs:39,44,45,51,52,69,70,75,76` (8 refs) | Re-point all to the new owner type (`ReinjectConsumerDefinition`) |

**Key cascade insight:** the `IOptions<BackupOptions>` ctor injection in `RecoveryConsumerBase` + all five consumers means **deleting `BackupOptions` touches every recovery consumer ctor**, not just the two that read the composite key. This is the largest single-edit fan-out and the planner must sequence it as one atomic change (base + 3 survivors + 2 deletions + test kit) or the build never reaches green mid-wave.

---

## Test Impact Map (the hidden cost — must be addressed in-wave)

| Test file | References | Action |
|-----------|-----------|--------|
| `Features/Orchestration/Projection/L2ProjectionKeysTests.cs` | `CompositeBackup` test (71-82) | DELETE that one `[Fact]`; ADD the `MessageIndex` golden pin (D-06) |
| `Contracts/KeeperContractTests.cs` | All five records incl. `KeeperUpdate`/`KeeperCleanup` (22-23, 53-56, 86-89) | Reduce to the 3 surviving contracts; assert `KeeperInject` now carries `EntryId`/`Data`/`DeleteEntryId` (D-08); drop the `KeeperUpdate.ValidatedData`/`KeeperCleanup` assertions |
| `Keeper/UpdateConsumerFacts.cs` | `UpdateConsumer`, `KeeperUpdate`, `CompositeBackup` | DELETE (consumer gone) |
| `Keeper/CleanupConsumerFacts.cs` | `CleanupConsumer`, `KeeperCleanup`, `CompositeBackup` | DELETE (consumer gone) |
| `Keeper/InjectConsumerFacts.cs` | `KeeperInject`, `CompositeBackup` (30,89), composite read/write/delete order | REWRITE to the stub (or delete until Phase 52) — the Model-B INJECT body is being stubbed; its current behavioral assertions no longer hold |
| `Keeper/BackupOptionsBoundTests.cs` | `BackupOptions` | DELETE (options gone) |
| `Keeper/RecoveryTestKit.cs` | `BackupOptions` `Backup()` helper (36-37); `db.StringSetAsync(...TimeSpan?...)` stub (58-61) | Remove the `Backup()` helper + drop the 6th ctor arg at every consumer build |
| `Keeper/RecoveryGateWaitFacts.cs` | Uses `KeeperCleanup` as the `ProbeConsumer` message type (41,44,51,62-63) + `BackupOptions` (40,60) | Re-point `ProbeConsumer` to a SURVIVING type (`KeeperDelete`); drop the `BackupOptions` ctor arg |
| `Keeper/RecoveryDeadLetterFacts.cs` | `BackupOptions` (66,158); `ReinjectConsumer` ctor | Drop the `BackupOptions` registration + the 6th ctor arg; otherwise survives (REINJECT body is stubbed in Phase 50 — but this test exercises the REINJECT data-gone path; **note** if the body is stubbed to `NotImplementedException` this test breaks behaviorally → either keep a no-op stub that preserves the gate-then-throw shape, or delete/skip the test until Phase 52) |
| `Keeper/RecoveryPartitionFacts.cs` | `UpdateConsumerDefinition.PartitionKey/PartitionGuid` (8 refs) | Re-point to the re-homed owner (`ReinjectConsumerDefinition`) |
| `Processor/PipelinePostFacts.cs` | `KeeperUpdate`/`KeeperCleanup` via `SentKeeper.OfType<>` (62-65,110-113) | REWRITE/delete — asserts the Model-B Post send order being removed |
| `Processor/DispatchTestKit.cs` | `KeeperUpdate`/`KeeperCleanup` in a doc comment (250-251) | Update comment (0-warning if it were a cref; it's prose) |
| `Orchestrator/SC2RecoveryPathsE2ETests.cs` | `CompositeBackup` (34 comment, 165), `KeeperInject` (171), `KeeperDelete`, `KeeperReinject` | **RealStack-tagged** (`Category=RealStack`, Phase=49) → excluded from the hermetic suite, but it STILL MUST COMPILE (it's in the same assembly under `TreatWarningsAsErrors`). The `CompositeBackup` ref (165) breaks compilation. STATE-3 (INJECT, 152-196) seeds + reads the composite — that block must be removed/rewritten since the composite key is gone. This is the trickiest edit: a `Category=RealStack` test is never run hermetically but its compilation gates the whole build. |

**Decision discretion interaction (D-01 stub style):** `RecoveryDeadLetterFacts` and the surviving-consumer body stubs interact. If survivors are stubbed `throw NotImplementedException`, hermetic tests that publish to them (RecoveryDeadLetterFacts) go red. The planner should prefer **no-op / shape-preserving stubs** for the REINJECT path (or delete the behavioral facts until Phase 52) so the hermetic suite stays green — CONTEXT D-01 explicitly permits either, and success criterion 4 ("hermetic suite green") makes the green-keeping choice the constrained one.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| New L2 key format | A new prefix scheme / inline interpolation at call-sites | Add `MessageIndex` to `L2ProjectionKeys` following the `ExecutionData` precedent | D-01/Phase-21 single-source-of-truth invariant; both writer + reader consume one shape |
| Partition key derivation | A new hashing scheme on the re-home | MOVE the existing `PartitionKey`/`PartitionGuid` verbatim | They are byte-pinned by `RecoveryPartitionFacts`; re-deriving risks slot drift |
| Slot-array TTL | A TTL knob in Phase 50 | Defer to Phase 51 (D-07) — builder bakes NO TTL | Mirrors `ExecutionData` caller-owns-TTL convention |
| Reflection remnant scan | A full RETIRE-03 sweep now | Defer to Phase 53; Phase 50 only needs the symbols gone | RETIRE-03 is explicitly Phase 53 scope |

## Common Pitfalls

### Pitfall 1: `BackupOptions` deletion is not local
**What goes wrong:** Deleting `BackupOptions` and only fixing `UpdateConsumer`/`CleanupConsumer` (the composite readers) leaves the build broken — `RecoveryConsumerBase` and all 3 survivors inject `IOptions<BackupOptions>`.
**How to avoid:** Treat B4 (above) as one atomic edit across base + 3 survivors + 2 deletions + the test kit's `Backup()` helper + every ctor call-site.
**Warning sign:** Build error in `ReinjectConsumer`/`DeleteConsumer` ctors even though their bodies never touched the composite key.

### Pitfall 2: Dangling XML `<see cref>` is a fatal build error
**What goes wrong:** `RecoveryOptions.cs:4` and `UpdateConsumer.cs:12` (and the `L2ProjectionKeys` doc `<list>`) reference `BackupOptions`/`CompositeBackup` by cref. Under `TreatWarningsAsErrors=true`, CS1574 is fatal.
**How to avoid:** Sweep doc comments for crefs to deleted symbols, not just code references.

### Pitfall 3: RealStack tests still gate the build
**What goes wrong:** `SC2RecoveryPathsE2ETests.cs` is `Category=RealStack` (excluded from hermetic runs) but lives in the test assembly — its `CompositeBackup` reference (line 165) breaks compilation, which fails the 0-warning build gate even though the test never runs hermetically.
**How to avoid:** The STATE-3 INJECT composite block must be removed/rewritten for compilation; do not assume RealStack exclusion means "ignore it."

### Pitfall 4: `RecoveryGateWaitFacts` uses `KeeperCleanup` as a vehicle type
**What goes wrong:** That test's `ProbeConsumer` subclasses `RecoveryConsumerBase<KeeperCleanup>` purely to test the gate-wait base behavior — it is unrelated to CLEANUP semantics, but `KeeperCleanup` is being deleted.
**How to avoid:** Re-point `ProbeConsumer` to a surviving contract (`KeeperDelete`).

### Pitfall 5: Stub style vs hermetic-green
**What goes wrong:** `throw NotImplementedException` in the surviving consumer bodies turns green hermetic tests that publish to them red.
**How to avoid:** Prefer no-op / shape-preserving stubs (or delete the behavioral facts until Phase 52). D-01 permits either; success criterion 4 forces the green-keeping choice.

## Code Examples

### Current `MessageIndex` precedent (`ExecutionData`)
```csharp
// Source: src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:42
public static string ExecutionData(Guid entryId) => $"{Prefix}data:{entryId:D}";
// Target (D-04): public static string MessageIndex(Guid messageId) => $"{Prefix}msg:{messageId:D}";
```

### Current 5-id record idiom (the template for `KeeperInject`'s 3 new fields)
```csharp
// Source: src/Messaging.Contracts/KeeperReinject.cs:7-13
public sealed record KeeperReinject(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; }
    public string Payload     { get; init; } = "";
}
```

### Golden-pin template (existing `ExecutionData` pin)
```csharp
// Source: tests/.../L2ProjectionKeysTests.cs:55-60
[Fact]
public void ExecutionData_Produces_Prefix_Data_Discriminator_Plus_HyphenatedGuid()
{
    Assert.Equal("skp:data:55555555-5555-5555-5555-555555555555",
        L2ProjectionKeys.ExecutionData(Guid.Parse("55555555-5555-5555-5555-555555555555")));
}
```

### Reflection remnant-guard precedent (Phase 48 — the RETIRE template)
```csharp
// Source: tests/BaseApi.Tests/Resilience/ReactivePathRetiredFacts.cs:130-146
// ExecutionData has exactly one Guid overload + no *Manifest* type survives — the
// pattern a Phase-50 lightweight guard can mirror for "CompositeBackup/KeeperUpdate/
// KeeperCleanup no longer exist" (full RETIRE-03 sweep is Phase 53).
```

## Runtime State Inventory

> Rename/refactor-adjacent (contract deletion). The composite-backup KEY format is being retired; live Redis may hold composite keys written by the running v4 Keeper.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | Redis may hold live `skp:{corr}:{wf}:{proc}:{exec}` composite-backup keys (2-day TTL) written by the running v4 Keeper UPDATE state. The new code can no longer write or read them. | None in Phase 50 (contract/code only). They self-expire via their 2-day TTL. The Phase-49 close-gate teardown already scan-deletes them (`SC2RecoveryPathsE2ETests` DisposeAsync lines 500-511). No data migration — these are crash-backstop ephemera, not durable state. |
| Live service config | `appsettings*.json` `"Backup"` section (bound by `Program.cs:30` to `BackupOptions`). After deletion the section is dead config. | Remove the `"Backup"` section from Keeper `appsettings*.json` (verify presence at plan-time). A stale unused section is not a compile break but is config drift. |
| OS-registered state | None — no Task Scheduler / systemd / pm2 references to the renamed symbols. | None — verified by scope (this is a library-contract change). |
| Secrets/env vars | None — `BackupOptions` had no secret; `TtlDays` is a plain int. | None. |
| Build artifacts | None expected — pure source change; no egg-info/compiled-name coupling in .NET. The test assembly recompiles from source. | None — verified (`dotnet build` regenerates all artifacts). |

**The canonical question — after every file is updated, what runtime systems still have the old string?** Only Redis composite keys with live TTLs (self-expiring) and the Keeper `appsettings` `"Backup"` section (dead config). Both are addressed above; neither blocks Phase 50.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build + hermetic test | ✓ (repo builds today) | net8.0 (Directory.Build.props) | — |
| dotnet test / MTP runner | hermetic suite | ✓ | xUnit v3 3.2.2 / Microsoft.Testing.Platform | — |

**No external services required for Phase 50** — it is a pure code/contract + hermetic-test change. Redis/RabbitMQ/Postgres are only exercised by the `Category=RealStack` E2E tests, which are EXCLUDED from the hermetic suite (and deferred to Phase 54 for live proof). The phase needs only a working .NET build + the hermetic test run.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The hermetic filter under xUnit v3 / MTP is `--filter-not-trait "Category=RealStack"` | Build/test commands | LOW — the ROADMAP Phase-37 entry documents this exact idiom; if the MTP CLI flag differs, the planner confirms the project's actual hermetic invocation at plan-time. Does not change WHAT must build, only the test-run command. |
| A2 | A Keeper `appsettings` `"Backup"` section exists and becomes dead config after deletion | Runtime State Inventory | LOW — `Program.cs:30` binds it, implying a section exists; if absent the cleanup is a no-op. Verify by reading Keeper `appsettings*.json` at plan-time. |
| A3 | Stubbing surviving consumer bodies as no-ops (not `throw`) keeps the hermetic suite green | Test Impact Map / Pitfall 5 | MEDIUM — depends on which behavioral facts the planner keeps vs deletes. The safe path (delete behavioral facts until Phase 52, or shape-preserving stubs) is within D-01 discretion. |

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit v3 (3.2.2) on Microsoft.Testing.Platform; NSubstitute for mocks |
| Config file | `tests/BaseApi.Tests/xunit.runner.json` (maxParallelThreads 6, conservative) |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release --filter-not-trait "Category=RealStack"` (hermetic) |
| Full suite command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release` (incl. RealStack — close-gate only) |
| 0-warning gate | `dotnet build SK_P.sln -c Release` AND `dotnet build SK_P.sln -c Debug` (TreatWarningsAsErrors fatal) |

### Phase Requirements → Test Map (the 4 success criteria)

| SC | Behavior | Test Type | Automated Command | File Exists? |
|----|----------|-----------|-------------------|--------------|
| SC-1 | `L2ProjectionKeys.MessageIndex(messageId)` == `skp:msg:{messageId:D}` (golden pin) | unit/golden | `dotnet test ... --filter "MessageIndex"` | ❌ Wave 0 — add to `L2ProjectionKeysTests.cs` |
| SC-1 | `ExecutionData` (no-TTL GUID data key) retained, exactly one Guid overload | unit/reflection | (existing `ExecutionData_*` pins + `ReactivePathRetiredFacts` FACT 4 pattern) | ✅ existing |
| SC-2 | `CompositeBackup` builder no longer exists; `KeeperUpdate`/`KeeperCleanup`/`BackupOptions` deleted; no references survive | reflection/source-scan + compile | A lightweight guard mirroring `ReactivePathRetiredFacts` (reflect `L2ProjectionKeys` has no `CompositeBackup` method; `Messaging.Contracts` assembly has no `KeeperUpdate`/`KeeperCleanup` type; `Keeper` assembly has no `BackupOptions` type) | ❌ Wave 0 — add a `ModelBContractsRetiredFacts` (full RETIRE-03 sweep deferred to P53) |
| SC-3 | `KeeperInject` carries `EntryId`+`Data`+`DeleteEntryId`; `KeeperReinject` carries `EntryId`+`Payload`; `KeeperDelete` carries `EntryId`; all implement `IKeeperRecoverable` (4-tuple) | reflection/contract | Rewrite `Contracts/KeeperContractTests.cs` to the 3 surviving shapes + the D-08 INJECT fields | ✅ rewrite existing |
| SC-4 | Solution 0-warning Release + Debug; hermetic suite green | build + suite | both `dotnet build` configs + the hermetic `dotnet test` | ✅ gate exists (phase-49-close pattern) |

### Sampling Rate
- **Per task commit:** `dotnet build SK_P.sln -c Release` (catch the 0-warning break early — the dominant failure mode here is dangling refs/crefs).
- **Per wave merge:** hermetic `dotnet test ... --filter-not-trait "Category=RealStack"` + Debug build.
- **Phase gate:** both-config 0-warning build + full hermetic suite green before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] `L2ProjectionKeysTests.cs` — ADD `MessageIndex` golden pin (D-06); DELETE the `CompositeBackup` `[Fact]`.
- [ ] `Contracts/KeeperContractTests.cs` — REWRITE to 3 surviving contracts + D-08 INJECT field assertions.
- [ ] NEW `ModelBContractsRetiredFacts.cs` (or extend `ReactivePathRetiredFacts`) — reflection guard: no `CompositeBackup` method, no `KeeperUpdate`/`KeeperCleanup` type, no `BackupOptions` type (SC-2 in-phase guard; full source/reflection sweep is Phase 53/RETIRE-03).
- [ ] DELETE `UpdateConsumerFacts.cs`, `CleanupConsumerFacts.cs`, `BackupOptionsBoundTests.cs`.
- [ ] REWRITE/RE-POINT `RecoveryGateWaitFacts.cs` (KeeperCleanup→KeeperDelete vehicle), `RecoveryPartitionFacts.cs` (PartitionKey/Guid owner), `RecoveryTestKit.cs` (drop `Backup()`), `RecoveryDeadLetterFacts.cs`, `InjectConsumerFacts.cs`, `PipelinePostFacts.cs`, `SC2RecoveryPathsE2ETests.cs` (composite block).
- [ ] No framework install needed — existing test infra covers all phase requirements.

## State of the Art

| Old (Model B, v4.0.0) | Current target (A18, v5.0.0) | Phase | Impact |
|-----------------------|------------------------------|-------|--------|
| Keeper-owned composite backup `skp:{corr}:{wf}:{proc}:{exec}` | Processor-owned slot array `L2[messageId]` (`skp:msg:{messageId:D}`, Redis HASH slots) | 50 (key shape) / 51 (writes) | Recovery becomes processor-owned; keeper shrinks 5→3 states |
| 5 keeper states (UPDATE/CLEANUP/REINJECT/INJECT/DELETE) | 3 keeper states (REINJECT/INJECT/DELETE) | 50 (contracts) / 52 (consumer) | UPDATE/CLEANUP deleted; INJECT becomes forward-only (data in-hand) |
| `KeeperInject` (ids only) | `KeeperInject(ids, EntryId, Data, DeleteEntryId)` | 50 | INJECT carries its own data; no composite read |

## Sources

### Primary (HIGH confidence — direct source reads, this session)
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs`, `KeeperInject.cs`, `KeeperReinject.cs`, `KeeperDelete.cs`, `KeeperUpdate.cs`, `KeeperCleanup.cs`, `IKeeperRecoverable.cs`
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs`
- `src/Keeper/Program.cs`, `BackupOptions.cs`, `RecoveryOptions.cs`, `Recovery/{Update,Cleanup,Reinject,Inject,Delete}Consumer.cs` + `*Definition.cs`, `RecoveryConsumerBase.cs`
- `tests/BaseApi.Tests/` — `Features/Orchestration/Projection/L2ProjectionKeysTests.cs`, `Contracts/KeeperContractTests.cs`, `Keeper/{Update,Cleanup,Inject}ConsumerFacts.cs`, `Keeper/RecoveryTestKit.cs`, `Keeper/RecoveryDeadLetterFacts.cs`, `Keeper/RecoveryGateWaitFacts.cs`, `Keeper/RecoveryPartitionFacts.cs`, `Keeper/BackupOptionsBoundTests.cs`, `Processor/PipelinePostFacts.cs`, `Processor/DispatchTestKit.cs`, `Orchestrator/SC2RecoveryPathsE2ETests.cs`, `Resilience/ReactivePathRetiredFacts.cs`, `BaseApi.Tests.csproj`, `xunit.runner.json`
- `Directory.Build.props`, `scripts/phase-49-close.ps1`
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` (A18 §, lines 130-227, LOCKED)
- `.planning/REQUIREMENTS.md` (RETIRE-01/02/03), `.planning/ROADMAP.md` (Phases 50-54), `.planning/phases/50-.../50-CONTEXT.md`

### Cross-referenced source-wide grep
- `CompositeBackup|KeeperUpdate|KeeperCleanup|BackupOptions|BuildUpdate|BuildCleanup|ValidatedData` across `src/` (12 hits, all enumerated in Break Analysis) and `tests/` (Test Impact Map).

## Metadata

**Confidence breakdown:**
- Current code state / signatures: HIGH — every file read in full this session.
- Break analysis: HIGH — source-wide grep confirms the complete reference set in `src/`; test refs cross-checked.
- Build/test commands: HIGH for build (Directory.Build.props + close script); MEDIUM for the exact hermetic MTP filter flag (A1 — confirm at plan-time).
- A18 contract shapes: HIGH — LOCKED design doc + CONTEXT D-08/D-09 verbatim.

**Research date:** 2026-06-11
**Valid until:** 7 days (active milestone; source changes per phase) — re-verify line numbers if any 50-XX plan touches these files before others.

## RESEARCH COMPLETE
