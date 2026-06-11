# Phase 50: Contracts & Slot-Array L2 Key Reshape - Pattern Map

**Mapped:** 2026-06-11
**Files analyzed:** 19 touched (2 contracts edited, 2 contracts deleted, 2 contracts verify-unchanged, 1 key builder edited, 1 pipeline edited, 4 consumer files deleted, 4 survivor consumers + base stubbed, 1 options deleted, 1 options cref-fix, 1 Program.cs edited, + 9 test files in-wave) + 1 new reflection-guard test
**Analogs found:** 19 / 19 (all analogs are in-repo; this is a reshape of existing code, every new shape mirrors a live sibling)

This phase has NO files without an analog. Every new shape is a mirror of an existing live pattern in the same codebase. The planner mirrors the excerpts verbatim — do not invent new schemes (see RESEARCH.md "Don't Hand-Roll").

---

## File Classification

| File | Role | Build-keeping Data Flow | Closest Analog | Match |
|------|------|------------------------|----------------|-------|
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` | key builder | add-field (`MessageIndex`) + delete-orphan (`CompositeBackup`) | `ExecutionData` builder (same file, line 42) | exact (same file, same scheme) |
| `src/Messaging.Contracts/KeeperInject.cs` | contract record | add-field (`EntryId`/`Data`/`DeleteEntryId`) | `KeeperReinject.cs` (5-id base + `EntryId`/string-extra idiom) + deleted `KeeperUpdate.ValidatedData` (string role) | exact |
| `src/Messaging.Contracts/KeeperReinject.cs` | contract record | verify-unchanged | itself (already A18-shaped) | exact |
| `src/Messaging.Contracts/KeeperDelete.cs` | contract record | verify-unchanged | itself (already A18-shaped) | exact |
| `src/Messaging.Contracts/KeeperUpdate.cs` | contract record | delete-orphan | — (whole file deleted) | n/a |
| `src/Messaging.Contracts/KeeperCleanup.cs` | contract record | delete-orphan | — (whole file deleted) | n/a |
| `src/Messaging.Contracts/IKeeperRecoverable.cs` | partition marker iface | verify-unchanged (D-03) | itself | exact |
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` | pipeline | delete-orphan (`BuildUpdate`/`BuildCleanup` + 2 sends) + stub-survivor (Post block) | surviving `BuildInject`/`BuildDelete`/`BuildReinject` (same file) | exact |
| `src/Keeper/Recovery/UpdateConsumer.cs` | consumer | delete-orphan | — (deleted) | n/a |
| `src/Keeper/Recovery/UpdateConsumerDefinition.cs` | consumer definition | delete-orphan + re-home single-owner | re-home target = `ReinjectConsumerDefinition.cs` | exact |
| `src/Keeper/Recovery/CleanupConsumer.cs` | consumer | delete-orphan | — (deleted) | n/a |
| `src/Keeper/Recovery/CleanupConsumerDefinition.cs` | consumer definition | delete-orphan | — (deleted) | n/a |
| `src/Keeper/Recovery/ReinjectConsumerDefinition.cs` | consumer definition | re-home single-owner (absorbs retry+partitioner) | `UpdateConsumerDefinition.cs` (the current single-owner) | exact |
| `src/Keeper/Recovery/ReinjectConsumer.cs` | consumer | stub-survivor (drop `BackupOptions` ctor arg; body kept/no-op per Pitfall 5) | itself + `RecoveryConsumerBase` ctor reshape | exact |
| `src/Keeper/Recovery/InjectConsumer.cs` | consumer | stub-survivor (drop `BackupOptions` ctor arg; stub composite body) | itself + `DeleteConsumer` (no-composite survivor shape) | exact |
| `src/Keeper/Recovery/DeleteConsumer.cs` | consumer | stub-survivor (drop `BackupOptions` ctor arg only) | itself (body already composite-free) | exact |
| `src/Keeper/Recovery/RecoveryConsumerBase.cs` | abstract consumer base | stub-survivor (drop `IOptions<BackupOptions>` param + `TtlDays`) | itself | exact |
| `src/Keeper/Program.cs` | DI registration | delete-orphan (2 `AddConsumer` + `Configure<BackupOptions>`) | surviving `ReinjectConsumer` registration (same file, line 52) | exact |
| `src/Keeper/BackupOptions.cs` | options | delete-orphan | — (deleted) | n/a |
| `src/Keeper/RecoveryOptions.cs` | options | verify-unchanged + cref-fix (`<see cref="BackupOptions"/>`) | itself | exact |
| `tests/.../Projection/L2ProjectionKeysTests.cs` | golden test | add-field (`MessageIndex` pin) + delete-orphan (`CompositeBackup` `[Fact]`) | `ExecutionData_Produces_*` pin (same file, lines 55-60) | exact |
| `tests/.../Contracts/KeeperContractTests.cs` | golden/contract test | stub-survivor (reduce to 3 contracts + D-08 fields) | existing 5-contract assertions | exact |
| **NEW** `tests/.../Resilience/ModelBContractsRetiredFacts.cs` | reflection-guard test | add-field (new file) | `ReactivePathRetiredFacts.cs` (whole) | exact |
| `tests/.../Keeper/RecoveryPartitionFacts.cs` | reflection test | re-home (re-point `PartitionKey`/`PartitionGuid` owner) | itself | exact |
| `tests/.../Keeper/RecoveryGateWaitFacts.cs` | test kit | re-home (`KeeperCleanup`→`KeeperDelete` vehicle) | itself | exact |
| `tests/.../Keeper/{Update,Cleanup}ConsumerFacts.cs`, `BackupOptionsBoundTests.cs` | tests | delete-orphan | — (deleted) | n/a |
| `tests/.../Keeper/RecoveryTestKit.cs`, `RecoveryDeadLetterFacts.cs`, `Keeper/InjectConsumerFacts.cs`, `Processor/PipelinePostFacts.cs`, `Orchestrator/SC2RecoveryPathsE2ETests.cs` | tests | stub-survivor / delete-orphan (drop `BackupOptions` arg; composite blocks) | per Test Impact Map (RESEARCH §) | exact |

---

## Pattern Assignments

### `L2ProjectionKeys.cs` — add `MessageIndex` (key builder, add-field) — D-04/D-06/D-07

**Analog:** `ExecutionData` in the same file (line 42) — the ONLY existing builder that carries a literal segment discriminator (`data:`) and bakes NO TTL. This is the precedent for the `msg:` discriminator and the caller-owns-TTL contract.

**Builder excerpt to mirror** (`L2ProjectionKeys.cs:40-42`):
```csharp
/// <summary>D-08: the sole GUID-keyed L2 data builder — <c>skp:data:{entryId:D}</c> (no TTL baked
/// in; caller concern). The legacy 64-hex content-addressed string overload was removed in v4.0.0.</summary>
public static string ExecutionData(Guid entryId) => $"{Prefix}data:{entryId:D}";
```

**New builder (target):**
```csharp
public static string MessageIndex(Guid messageId) => $"{Prefix}msg:{messageId:D}";
```

**Delete the `CompositeBackup` builder + its doc** (`L2ProjectionKeys.cs:44-47`):
```csharp
/// <summary>D-09: composite backup key — <c>skp:{corr:D}:{wf:D}:{proc:D}:{exec:D}</c>. ...</summary>
public static string CompositeBackup(Guid correlationId, Guid workFlowId, Guid processorId, Guid executionId)
    => $"{Prefix}{correlationId:D}:{workFlowId:D}:{processorId:D}:{executionId:D}";
```

**Doc-comment `<list>` sweep (NOT a compile error, but 0-warning review):** the class XML `<list>` (lines 19-26) has a `<item>` for `CompositeBackup` (line 25). Delete that `<item>` and add a `MessageIndex` `<item>` mirroring the `ExecutionData` `<item>` (line 24) wording, or the doc drifts. No `<see cref>` to `CompositeBackup` exists in this list — it is `<description>` prose, so no CS1574 here.

---

### `KeeperInject.cs` — add 3 fields (contract record, add-field) — D-08

**Analog (idiom):** `KeeperReinject.cs:7-13` — the 5-id positional base ctor + `init` props + a `string` extra defaulting to `""`. This is the exact idiom the 3 new `KeeperInject` fields follow.

**Idiom to mirror** (`KeeperReinject.cs:7-13`):
```csharp
public sealed record KeeperReinject(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; }   // D-11: REINJECT-only extra
    public string Payload     { get; init; } = "";   // D-01: REINJECT carries the step config ...
}
```

**Secondary analog (the `Data` string role):** the deleted `KeeperUpdate.ValidatedData` (`KeeperUpdate.cs:11`) — `public string ValidatedData { get; init; } = "";`. `KeeperInject.Data` continues this raw-JSON-string-on-the-wire convention (D-02/D-08) verbatim.

**Current `KeeperInject.cs:6-10` (the no-extra base):**
```csharp
public sealed record KeeperInject(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
}
```

**Target — add 3 `init` props (D-08):**
```csharp
public Guid EntryId       { get; init; }   // D-08: allocation to write L2[entryId]=data
public string Data        { get; init; } = "";   // D-08: raw-JSON output, in-hand on the envelope (was KeeperUpdate.ValidatedData)
public Guid DeleteEntryId { get; init; }   // D-08: source entryId deleted after the orchestrator send (A18 literal `deleteEntryId`)
```
Note (RESEARCH §4): these are `init` defaults, so `ProcessorPipeline.BuildInject` (line 240-241) still compiles unchanged — `EntryId`/`DeleteEntryId` default `Guid.Empty`, `Data` defaults `""`. Real population is Phase 51.

---

### `KeeperReinject.cs` / `KeeperDelete.cs` — verify-unchanged (D-09)

Both already match A18. Read confirms:
- `KeeperReinject.cs:7-13` carries `EntryId` (Guid) + `Payload` (string `= ""`) → matches `REINJECT(ids, entryId, payload)`.
- `KeeperDelete.cs:7-12` carries `EntryId` (Guid) → matches `DELETE(entryId)`.

No edit. The `KeeperContractTests.cs` rewrite asserts these unchanged shapes.

---

### `ProcessorPipeline.cs` — delete 2 builders + 2 sends, stub Post (pipeline, delete-orphan + stub-survivor) — D-01

**Analog (the surviving-builder shape to keep, and what `BuildUpdate`/`BuildCleanup` looked like):** the sibling builders in the same file.

**Survivors to KEEP untouched** (`ProcessorPipeline.cs:231-241`):
```csharp
private static KeeperReinject  BuildReinject(EntryStepDispatch d) =>
    new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = d.ExecutionId, EntryId = d.EntryId, Payload = d.Payload };
private static KeeperDelete    BuildDelete(EntryStepDispatch d) =>
    new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = d.ExecutionId, EntryId = d.EntryId };
private static KeeperInject    BuildInject(EntryStepDispatch d, ProcessItem item) =>
    new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = item.ExecutionId };
```

**DELETE these two builders** (`ProcessorPipeline.cs:237-238` + `243-244`):
```csharp
private static KeeperUpdate    BuildUpdate(EntryStepDispatch d, ProcessItem item) =>
    new(...) { ..., ValidatedData = item.Data };
private static KeeperCleanup   BuildCleanup(EntryStepDispatch d, ProcessItem item) =>
    new(...) { ... };
```

**DELETE the two Post-stage sends that call them** (`ProcessorPipeline.cs:139` + `148`):
```csharp
await SendKeeper(BuildUpdate(d, item), limit, ct);  // line 139 — first Post send
// ...
await SendKeeper(BuildCleanup(d, item), limit, ct);  // line 148 — after write-success
```

**Compile-keep guard (RESEARCH §4):** removing lines 139 + 148 leaves the `Completed` branch (137-157) writing L2 + minting entryId + sending `StepCompleted` — that still compiles. The `write.Succeeded` branch (143-147, calls `BuildInject`) and the `logger.BeginScope` block (149-156) stay valid. `SendKeeper` (207-213) takes `IKeeperRecoverable` — unaffected. D-01 discretion permits `throw NotImplementedException` OR no-op; the no-op-delete-the-two-sends path is the minimal 0-warning compile-keep.

**Doc-comment sweep:** the Post-stage doc comment (lines ~35-38) names `KeeperUpdate`/`KeeperCleanup`; the inline comments on 139 (`UPDATE before write...`) and 148 (`composite backup now redundant`) reference the deleted mechanic — update to prose, NO dangling cref.

---

### `UpdateConsumerDefinition.cs` → `ReinjectConsumerDefinition.cs` — single-owner re-home (consumer definition) — D-01

**Source (the single-owner being deleted):** `UpdateConsumerDefinition.cs` — sole owner on `keeper-recovery` of `UseMessageRetry` + the shared `Partitioner` + 5 `UsePartitioner<T>` lines + the `PartitionKey`/`PartitionGuid` static helpers.

**Re-home target (current no-op):** `ReinjectConsumerDefinition.cs:10-24` — currently a parameterless ctor + intentional no-op `ConfigureConsumer`.

**The full block to MOVE (from `UpdateConsumerDefinition.cs`):**

Ctor + fields (`:30-38`):
```csharp
private readonly IOptions<RetryOptions> _retryOptions;
private readonly IOptions<RecoveryOptions> _recoveryOptions;

public UpdateConsumerDefinition(IOptions<RetryOptions> retryOptions, IOptions<RecoveryOptions> recoveryOptions)
{
    _retryOptions = retryOptions;
    _recoveryOptions = recoveryOptions;
    EndpointName = KeeperQueues.Recovery;
}
```

`ConfigureConsumer` retry + partitioner (`:47-66`) — **DROP the `KeeperUpdate` (line 62) and `KeeperCleanup` (line 66) lines; keep ONLY the 3 survivors:**
```csharp
endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));
var partition = new Partitioner(_recoveryOptions.Value.PartitionCount, new Murmur3UnsafeHashGenerator());
endpointConfigurator.UsePartitioner<KeeperReinject>(partition, p => PartitionGuid(p.Message));
endpointConfigurator.UsePartitioner<KeeperInject>(partition, p => PartitionGuid(p.Message));
endpointConfigurator.UsePartitioner<KeeperDelete>(partition, p => PartitionGuid(p.Message));
```

Static helpers — **MOVE verbatim (do NOT re-derive — byte-pinned by `RecoveryPartitionFacts`)** (`:74-84`):
```csharp
public static string PartitionKey(IKeeperRecoverable m) =>
    $"{m.CorrelationId:D}:{m.WorkflowId:D}:{m.ProcessorId:D}:{m.ExecutionId:D}";

public static Guid PartitionGuid(IKeeperRecoverable m)
{
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(PartitionKey(m)));
    return new Guid(hash.AsSpan(0, 16));
}
```

Carry the `using` block from `UpdateConsumerDefinition.cs:1-7` (`System.Security.Cryptography`, `System.Text`, `MassTransit`, `MassTransit.Middleware`, `Microsoft.Extensions.Options`, etc.) onto `ReinjectConsumerDefinition.cs`.

**Single-owner invariant (RESEARCH §5a):** after the move, `ReinjectConsumerDefinition` is the SOLE owner; `InjectConsumerDefinition`/`DeleteConsumerDefinition` stay intentional no-ops. `RecoveryPartitionFacts.cs` (8 refs to `UpdateConsumerDefinition.PartitionKey/PartitionGuid`) re-points to `ReinjectConsumerDefinition.*`.

---

### `RecoveryConsumerBase.cs` + 3 survivors — drop `BackupOptions` (consumer, stub-survivor) — D-01/B4

**This is the atomic fan-out edit (RESEARCH Pitfall 1).** `BackupOptions` deletion breaks ALL FIVE consumer ctors + the base, not just the composite readers.

**Base reshape** (`RecoveryConsumerBase.cs:26-38`) — remove the last ctor param + the `TtlDays` property:
```csharp
// CURRENT:
public abstract class RecoveryConsumerBase<TMessage>(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider, IL2HealthGate gate,
    IOptions<RetryOptions> retryOptions, IOptions<RecoveryOptions> recoveryOptions,
    IOptions<BackupOptions> backupOptions) : IConsumer<TMessage>   // <- DROP backupOptions
    where TMessage : class, IKeeperRecoverable
{
    ...
    protected int TtlDays => backupOptions.Value.TtlDays;   // <- DELETE (only UpdateConsumer used it)
```
Drop the `using Messaging.Contracts.Configuration;`-adjacent `BackupOptions` import if it becomes unused (CS8019 unused-using is fatal under 0-warning).

**Each survivor drops the 6th base-call arg in lockstep.** The cleanest analog is `DeleteConsumer.cs` (already composite-free body):
```csharp
// DeleteConsumer.cs:14-21 — CURRENT
public sealed class DeleteConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider, IL2HealthGate gate,
    IOptions<RetryOptions> retryOptions, IOptions<RecoveryOptions> recoveryOptions,
    IOptions<BackupOptions> backupOptions)                                              // <- DROP
    : RecoveryConsumerBase<KeeperDelete>(redis, sendProvider, gate, retryOptions, recoveryOptions, backupOptions)  // <- DROP last arg
{
    protected override async Task HandleAsync(KeeperDelete m, CancellationToken ct)
        => await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(m.EntryId)), ct);   // body unchanged (composite-free)
}
```
Apply the identical ctor-arg drop to `ReinjectConsumer.cs:17-21` and `InjectConsumer.cs:28-32`.

**`InjectConsumer.cs` body stub (it DOES reference `CompositeBackup` at line 36):** the current body (34-65) reads `CompositeBackup`, mints entryId, writes `ExecutionData`, sends `StepCompleted`, deletes composite. Phase 50 reduces it to a compile-only stub (the A18 INJECT body — write `L2[entryId]=m.Data`, send, delete `m.DeleteEntryId` — is Phase 52). **Pitfall 5 / RESEARCH Test Impact Map:** prefer a no-op / shape-preserving stub (NOT `throw NotImplementedException`) so hermetic tests publishing to it stay green; delete/skip the behavioral `InjectConsumerFacts` until Phase 52.

**`ReinjectConsumer.cs` body:** does NOT reference any deleted contract in its body (only the ctor arg breaks). Per Pitfall 5 + `RecoveryDeadLetterFacts` interaction, keep the gate-then-op shape (no-op-preserving) rather than `throw`, so the data-gone hermetic path stays green.

---

### `Program.cs` — DI registration (delete-orphan) — D-01

**Analog (the survivor registration line to keep):** `Program.cs:52` —
```csharp
x.AddConsumer<Keeper.Recovery.ReinjectConsumer, Keeper.Recovery.ReinjectConsumerDefinition>();
```

**DELETE** (`Program.cs:30`):
```csharp
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection("Backup"));
```
**DELETE** the two orphan `AddConsumer` lines (`:51` `UpdateConsumer`, `:55` `CleanupConsumer`); keep `:52-54` (the 3 survivors). Update the doc-comment block (`:46-50`) "five gate-open-only recovery consumers" → "three" and re-name the SINGLE OWNER from `UpdateConsumerDefinition` to `ReinjectConsumerDefinition` (prose — 0-warning review, not a compile break). Drop the `using` for `BackupOptions` if unused.

---

### `RecoveryOptions.cs` — cref-fix (options, verify-unchanged) — B4 / Pitfall 2

No structural change, but `RecoveryOptions.cs:4` has a dangling `<see cref="BackupOptions"/>` that becomes CS1574 (fatal) after `BackupOptions` is deleted:
```csharp
/// ... bound from the "Recovery" appsettings section (mirrors <see cref="ProbeOptions"/> / <see cref="BackupOptions"/>). ...
```
Remove the `<see cref="BackupOptions"/>` clause (replace with `<see cref="ProbeOptions"/>` only, or plain prose).

---

### `L2ProjectionKeysTests.cs` — golden pin (golden test, add-field + delete-orphan) — D-06

**Analog:** the `ExecutionData_Produces_*` pin in the same file (lines 55-60).
```csharp
[Fact]
public void ExecutionData_Produces_Prefix_Data_Discriminator_Plus_HyphenatedGuid()
{
    Assert.Equal(
        "skp:data:55555555-5555-5555-5555-555555555555",
        L2ProjectionKeys.ExecutionData(Guid.Parse("55555555-5555-5555-5555-555555555555")));
}
```

**ADD the `MessageIndex` golden pin (mirror exactly):**
```csharp
[Fact]
public void MessageIndex_Produces_Prefix_Msg_Discriminator_Plus_HyphenatedGuid()
{
    Assert.Equal(
        "skp:msg:55555555-5555-5555-5555-555555555555",
        L2ProjectionKeys.MessageIndex(Guid.Parse("55555555-5555-5555-5555-555555555555")));
}
```

**DELETE the `CompositeBackup_Produces_*` `[Fact]`** (`L2ProjectionKeysTests.cs:71-82`) — its builder is gone. (D-05: the slot is a HASH field, NOT in the key string, so there is no slot-string to pin — a sibling structural note can be deferred; the planner's discretion D-06 is to pin in this same file matching the `ExecutionData` precedent.)

---

### NEW `ModelBContractsRetiredFacts.cs` — reflection guard (new file) — SC-2

**Analog:** `ReactivePathRetiredFacts.cs` (whole file) — the Phase-48 RETIRE precedent. Mirror its structure verbatim: assembly anchors via `typeof(...).Assembly`, `[Fact]`/`[Trait("Phase","50")]`, reflection over `GetMethods`/`GetTypes`, and the `Assert.Single`/`Assert.DoesNotContain` idioms. FACT 4 (lines 128-147) is the closest single template:

```csharp
// ReactivePathRetiredFacts.cs:132-145 — the falsifiable-reflection template
var executionDataOverloads = typeof(L2ProjectionKeys)
    .GetMethods(BindingFlags.Public | BindingFlags.Static)
    .Where(m => m.Name == "ExecutionData")
    .ToList();
var only = Assert.Single(executionDataOverloads);
var param = Assert.Single(only.GetParameters());
Assert.Equal(typeof(Guid), param.ParameterType);

foreach (var asm in new[] { Orchestrator, BaseProcessorCore })
    Assert.DoesNotContain(asm.GetTypes(), t => t.Name.Contains("Manifest"));
```

**Assembly anchors to reuse (verbatim from the analog, lines 31-39):**
```csharp
private static readonly Assembly Keeper = typeof(global::Keeper.Health.BitHealthLoop).Assembly;
private static readonly Assembly Contracts = typeof(global::Messaging.Contracts.KeeperInject).Assembly;
private static readonly Assembly BaseProcessorCore =
    typeof(global::BaseProcessor.Core.Processing.ProcessorPipeline).Assembly;
```

**New facts to author (SC-2, lighter than RETIRE-03 — full sweep is Phase 53):**
1. `L2ProjectionKeys` has NO method named `CompositeBackup` (mirror FACT 4's `GetMethods().Where(name==...)` → `Assert.Empty`).
2. `Messaging.Contracts` assembly has NO type named `KeeperUpdate` or `KeeperCleanup` (mirror FACT 1's `Assert.DoesNotContain(types, t => t.Name is ... or ...)`).
3. `Keeper` assembly has NO type named `BackupOptions` (same idiom).
4. (Optional, mirrors FACT 3) `L2ProjectionKeys` DOES retain `MessageIndex` and `ExecutionData` (positive survivor assertion).

The planner may instead EXTEND `ReactivePathRetiredFacts.cs` with these facts (CONTEXT discretion + RESEARCH Wave-0 gap allows either); a sibling `ModelBContractsRetiredFacts.cs` keeps the Phase-50 guard separately traited.

---

### Test re-home / drop / stub (per RESEARCH Test Impact Map)

| Test file | Pattern | Analog / action |
|-----------|---------|-----------------|
| `Contracts/KeeperContractTests.cs` | stub-survivor | reduce 5→3 contracts; assert `KeeperInject` carries `EntryId`/`Data`/`DeleteEntryId` (mirror `KeeperReinject` assertion shape); drop `KeeperUpdate.ValidatedData`/`KeeperCleanup` |
| `Keeper/RecoveryPartitionFacts.cs` | re-home | re-point 8 refs `UpdateConsumerDefinition.PartitionKey/PartitionGuid` → `ReinjectConsumerDefinition.*` |
| `Keeper/RecoveryGateWaitFacts.cs` | re-home | `ProbeConsumer : RecoveryConsumerBase<KeeperCleanup>` → `<KeeperDelete>` (surviving vehicle); drop the `BackupOptions` ctor arg |
| `Keeper/RecoveryTestKit.cs` | stub-survivor | remove the `Backup()` helper (36-37); drop the 6th ctor arg at every consumer build |
| `Keeper/RecoveryDeadLetterFacts.cs` | stub-survivor | drop `BackupOptions` registration + 6th ctor arg; keep no-op/shape-preserving REINJECT stub (Pitfall 5) or skip until P52 |
| `Keeper/InjectConsumerFacts.cs` | delete/stub | composite behavior gone — delete or rewrite to the stub until P52 |
| `Processor/PipelinePostFacts.cs` | delete/rewrite | asserts the removed Model-B Post send order (`KeeperUpdate`/`KeeperCleanup` via `SentKeeper.OfType<>`) |
| `Orchestrator/SC2RecoveryPathsE2ETests.cs` | stub-survivor (RealStack — but MUST COMPILE) | remove/rewrite the STATE-3 composite block (line 165 `CompositeBackup` ref breaks compile under 0-warning even though excluded from hermetic run — Pitfall 3) |
| `Keeper/UpdateConsumerFacts.cs`, `CleanupConsumerFacts.cs`, `BackupOptionsBoundTests.cs` | delete-orphan | delete whole files (subjects gone) |

---

## Shared Patterns

### Single-source-of-truth L2 key builder
**Source:** `L2ProjectionKeys.cs` (`ExecutionData`, line 42)
**Apply to:** the new `MessageIndex` builder — `$"{Prefix}{discriminator}:{guid:D}"`, no TTL baked in. Both writer (P51) + reader (P51) consume one shape (D-01/Phase-21). Do NOT inline-interpolate at call sites (RESEARCH "Don't Hand-Roll").

### 5-id positional record + init-prop extras
**Source:** `KeeperReinject.cs:7-13` (and `KeeperDelete.cs:7-12`)
**Apply to:** all Keeper-state contracts. `sealed record X(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable` + `init` props for `CorrelationId`/`ExecutionId` + per-state extras. Default STJ, NO `[JsonPropertyName]`. `string` extras default `= ""`.

### Partition 4-tuple helper (move-verbatim, never re-derive)
**Source:** `UpdateConsumerDefinition.cs:74-84` (`PartitionKey`/`PartitionGuid`)
**Apply to:** the re-home onto `ReinjectConsumerDefinition`. Byte-pinned by `RecoveryPartitionFacts` — MOVE the bytes verbatim, do not re-derive (slot-drift risk, RESEARCH "Don't Hand-Roll"). The `IKeeperRecoverable` 4-tuple (D-03) is unchanged.

### Single-owner endpoint definition
**Source:** `UpdateConsumerDefinition.cs` (the current owner) + `ReinjectConsumerDefinition.cs` (a current no-op sibling)
**Apply to:** the re-home. Exactly ONE `ConsumerDefinition` on `keeper-recovery` registers `UseMessageRetry` + `UsePartitioner<>`; the others stay intentional no-ops (Pitfalls 1 & 4). Preserve this invariant.

### Reflection RETIRE guard
**Source:** `ReactivePathRetiredFacts.cs` (whole; FACT 4 lines 128-147 closest)
**Apply to:** the new `ModelBContractsRetiredFacts`. `typeof(...).Assembly` anchors + `GetMethods`/`GetTypes` reflection + `Assert.Single`/`Assert.DoesNotContain`. Phase-50 scope is "symbols no longer exist"; the full source-scan sweep (FACT 2 pattern) is RETIRE-03 / Phase 53.

### 0-warning cref hygiene
**Source:** observed dangling cref at `RecoveryOptions.cs:4` (`<see cref="BackupOptions"/>`)
**Apply to:** every doc comment referencing a deleted symbol (`BackupOptions`, `CompositeBackup`, `KeeperUpdate`, `KeeperCleanup`). Under `Directory.Build.props` `TreatWarningsAsErrors=true`, a dangling `<see cref>` is CS1574 (fatal). Sweep comments, not just code (Pitfall 2). Also sweep unused `using`s (CS8019).

---

## No Analog Found

None. Every file in this phase reshapes existing code; all 19 touched files + the 1 new test mirror a live in-repo pattern. The planner uses these excerpts directly rather than RESEARCH.md's generic examples.

---

## Metadata

**Analog search scope:** `src/Messaging.Contracts/`, `src/Keeper/Recovery/`, `src/Keeper/` (Program + options), `src/BaseProcessor.Core/Processing/`, `tests/BaseApi.Tests/` (Projection, Contracts, Keeper, Resilience).
**Files read this session (full):** `L2ProjectionKeys.cs`, `KeeperInject.cs`, `KeeperReinject.cs`, `KeeperDelete.cs`, `KeeperUpdate.cs`, `UpdateConsumerDefinition.cs`, `ReinjectConsumerDefinition.cs`, `RecoveryConsumerBase.cs`, `InjectConsumer.cs`, `DeleteConsumer.cs`, `ReinjectConsumer.cs`, `Program.cs`, `RecoveryOptions.cs`, `BackupOptions.cs`, `L2ProjectionKeysTests.cs`, `ReactivePathRetiredFacts.cs`; `ProcessorPipeline.cs` (lines 120-245, targeted).
**Pattern extraction date:** 2026-06-11
