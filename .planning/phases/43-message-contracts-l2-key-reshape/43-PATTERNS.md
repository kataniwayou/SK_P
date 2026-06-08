# Phase 43: Message Contracts & L2 Key Reshape - Pattern Map

**Mapped:** 2026-06-08
**Files analyzed:** 26 (12 new, 10 modified incl. dark-path, 4 test reshapes)
**Analogs found:** 26 / 26 (every new/modified file has a verified in-repo analog)

> All analogs were read directly this session. Excerpts below are copy-paste-ready for an executor. Line numbers are the analog's CURRENT lines, valid as of this mapping.

## Binding Conventions (apply to EVERY file in this phase)

These are enforced by code precedent (no `CLAUDE.md` / skills in repo — RESEARCH §"Project Constraints"). The planner MUST treat them as rules:

| Rule | Source precedent | Enforcement |
|------|------------------|-------------|
| Wire contracts are `sealed record` | `ExecutionResult.cs:7`, `EntryStepDispatch.cs:11`, `PauseWorkflow.cs:4` | Every new contract record |
| **NO `[JsonPropertyName]`** anywhere | `ExecutionResult.cs:3` ("// NOTE: bus envelope — NO [JsonPropertyName]") | All records |
| **NO `JsonStringEnumConverter`** — enums serialize as int | `StepOutcome.cs:9-10`, `ExecutionResultContractTests.cs:79-86` (`"Outcome":2`) | `StepOutcome` stays int; no converter added |
| Non-positional ids are `init`-only | `ExecutionResult.cs:13-15`, `EntryStepDispatch.cs:14-17` | All records |
| Positional ctor for `WorkflowId, StepId, ProcessorId` (+ extra positional like `Outcome`/`Payload`); `CorrelationId`/`ExecutionId`/`EntryId` are `init` bodies | `ExecutionResult.cs:7-15`, `EntryStepDispatch.cs:11-17` | The four `Step*` records keep this split byte-identical so the golden test is a copy of `ExecutionResultContractTests` |
| `:D` (hyphenated) GUID rendering in L2 keys | `L2ProjectionKeys.cs:34` (`$"{Prefix}{workflowId:D}"`) | Both new key builders |
| `skp:` prefix is the `const Prefix` owned in `L2ProjectionKeys` — never a param, never per-host config | `L2ProjectionKeys.cs:30` | `CompositeBackup` + `ExecutionData(Guid)` use `Prefix` |
| Single-source-of-truth static key/queue classes | `L2ProjectionKeys.cs:28`, `KeeperQueues.cs:7`, `OrchestratorQueues.cs:8` | `Recovery` const + key builders live ONLY here |
| CPM — no `Version=` on `PackageReference` | RESEARCH §"Standard Stack" (no new deps) | No package changes this phase |
| `init`-only ids hard-default to a sentinel where applicable | `EntryStepDispatch.cs:15` (`= Guid.Empty`), `ExecutionResult.cs:15` (`= ""`) | `StepFailed/Cancelled/Processing` default `EntryId = Guid.Empty` (D-06a) |

## File Classification

### NEW files

| New File | Role | Data Flow | Closest Analog | Match Quality |
|----------|------|-----------|----------------|---------------|
| `Messaging.Contracts/IStepResult.cs` | interface (marker) | request-response | `IExecutionCorrelated.cs` (layering), `ICorrelated.cs` | exact (marker pattern) |
| `Messaging.Contracts/StepCompleted.cs` | wire contract | request-response | `ExecutionResult.cs` (record shape) | exact |
| `Messaging.Contracts/StepFailed.cs` | wire contract | request-response | `ExecutionResult.cs` (+ `ErrorMessage`) | exact |
| `Messaging.Contracts/StepCancelled.cs` | wire contract | request-response | `ExecutionResult.cs` (+ `CancellationMessage`) | exact |
| `Messaging.Contracts/StepProcessing.cs` | wire contract | request-response | `ExecutionResult.cs` (no diagnostic field) | exact |
| `Messaging.Contracts/SourceStep.cs` | utility (predicate) | transform | `MessageIdentity.cs` (single-source static helper), `L2ProjectionKeys.cs` (static class) | role-match |
| `Messaging.Contracts/IKeeperRecoverable.cs` | interface (marker) | event-driven | `ICorrelated.cs` / `IExecutionCorrelated.cs` (marker layering) | exact |
| `Messaging.Contracts/KeeperUpdate.cs` | wire contract | event-driven | `ExecutionResult.cs` (record) + `IExecutionCorrelated` layering | role-match |
| `Messaging.Contracts/KeeperReinject.cs` | wire contract | event-driven | `ExecutionResult.cs` (record) | role-match |
| `Messaging.Contracts/KeeperInject.cs` | wire contract | event-driven | `ExecutionResult.cs` (record) | role-match |
| `Messaging.Contracts/KeeperDelete.cs` | wire contract | event-driven | `ExecutionResult.cs` (record) | role-match |
| `Messaging.Contracts/KeeperCleanup.cs` | wire contract | event-driven | `ExecutionResult.cs` (record) | role-match |
| `Keeper/BackupOptions.cs` | config (options) | config | `Keeper/ProbeOptions.cs` | exact |
| `tests/.../Contracts/StepResultContractTests.cs` | golden test | request-response | `Orchestrator/ExecutionResultContractTests.cs` | exact |
| `tests/.../Contracts/SourceStepTests.cs` | golden test | transform | `ExecutionResultContractTests.cs` (reflection facts) | role-match |
| `tests/.../Contracts/KeeperContractTests.cs` | golden test | event-driven | `ExecutionResultContractTests.cs` (reflection + round-trip) | role-match |
| `tests/.../Keeper/BackupOptionsBoundTests.cs` | golden test | config | `Keeper/ProbeOptionsBoundTests.cs` | exact |

### MODIFIED files

| Modified File | Role | Data Flow | Governing Precedent | Match Quality |
|---------------|------|-----------|---------------------|---------------|
| `Messaging.Contracts/IExecutionCorrelated.cs` | interface | request-response | self (drop `H`, `EntryId` string→Guid) | exact |
| `Messaging.Contracts/EntryStepDispatch.cs` | wire contract | request-response | self + `ExecutionResult.cs` (Guid init pattern) | exact |
| `Messaging.Contracts/ExecutionLogScope.cs` | utility | transform | self + new `SourceStep.IsSource` predicate | exact |
| `Messaging.Contracts/Projections/L2ProjectionKeys.cs` | key builder | transform | self (existing `Root`/`ExecutionData(Guid)` `:D` convention) | exact |
| `Messaging.Contracts/KeeperQueues.cs` | config (const) | config | self + `OrchestratorQueues.cs` const style | exact |
| `Orchestrator/Consumers/ResultConsumer.cs` | consumer adaptation | request-response | self (delete dedup/manifest/Redis) | exact |
| `Orchestrator/Dispatch/StepDispatcher.cs` | consumer adaptation | request-response | self (delete H compute + flag pre-write) | exact |
| `Orchestrator/Scheduling/WorkflowFireJob.cs` | consumer adaptation | request-response | self (replace hash entryId with `Guid.Empty`) | exact |
| `BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` | consumer adaptation | request-response | self (delete CAS/content-addr/manifest) | exact |
| `BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs` | middleware | request-response | NO body change — fixed by `ExecutionLogScope` edit | exact |

### DELETED files

| Deleted File | Reason | Coupled to |
|--------------|--------|-----------|
| `Messaging.Contracts/ExecutionResult.cs` | Replaced by four `Step*` records (D-06) | MSG-01 |
| `Messaging.Contracts/Hashing/MessageIdentity.cs` | `H` computation gone (RETIRE-01, coupled D-01) | sole consumers `StepDispatcher`/`EntryStepDispatchConsumer`/`WorkflowFireJob` all reshaped this phase |

### DARK (kept present + compiling, NOT functionally rewritten — D-14)

| Dark File | Minimal-change action | Analog/precedent |
|-----------|----------------------|------------------|
| `Keeper/Recovery/KeeperRecoveryHandler.cs` | Rebind generic off `IExecutionCorrelated.H`; derive a local string key from the 4-tuple wherever it read `inner.H` | self (see §Dark Path) |
| `Keeper/Recovery/L2ProbeRecovery.cs` | Point `ExecutionData(...)` at the Guid builder | self |
| `Keeper/Consumers/FaultExecutionResultConsumer.cs` + `FaultExecutionResultConsumerDefinition.cs` | DROP (the `Fault<ExecutionResult>` consumer — its message type no longer exists). Do NOT delete the *file* unless the planner confirms; per D-14 the diff must not show `KeeperRecoveryHandler.cs`/`FaultExecutionResultConsumer.cs` disappearing — so neutralize the body, keep the file | self / D-14 |
| `Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` | Keep — bind reactive consumer on `Fault<EntryStepDispatch>` only | self |
| `Keeper/Program.cs` | Drop the `FaultExecutionResultConsumer` registration (line 56) | Program.cs:53-57 |
| `Messaging.Contracts/PauseWorkflow.cs` / `ResumeWorkflow.cs` | Carry their OWN `string H` positional (NOT `IExecutionCorrelated.H`) — compile fine standalone; only break via `KeeperRecoveryHandler` constructing them with `inner.H` | self |

---

## Pattern Assignments

### `IStepResult.cs` (interface marker) — D-06c

**Analog:** `IExecutionCorrelated.cs` + `ICorrelated.cs` (marker-interface layering).

The whole file is a one-line marker. Mirror the `ICorrelated.cs:4` shape (namespace + doc + empty interface body):
```csharp
namespace Messaging.Contracts;

/// <summary>Marker grouping the four typed step-result records (D-06c) so they co-locate on
/// OrchestratorQueues.Result and the InboundExecutionScopeConsumeFilter (keyed on IExecutionCorrelated)
/// covers them unchanged.</summary>
public interface IStepResult : IExecutionCorrelated { }
```
> `IExecutionCorrelated` already extends `ICorrelated` (`IExecutionCorrelated.cs:10`), so `IStepResult : IExecutionCorrelated` transitively requires `CorrelationId` + the execution id-set + (post-edit) `Guid EntryId`.

---

### `StepCompleted.cs` / `StepFailed.cs` / `StepCancelled.cs` / `StepProcessing.cs` (wire contracts) — D-06/D-06a/D-06b

**Analog:** `ExecutionResult.cs` (lines 7-25) — fork its shape into four. Keep the positional/`init` split byte-identical.

**Current `ExecutionResult` shape to fork** (`ExecutionResult.cs:1-25`):
```csharp
namespace Messaging.Contracts;

// NOTE: bus envelope — NO [JsonPropertyName], default STJ serialization
public sealed record ExecutionResult(
    Guid WorkflowId,
    Guid StepId,
    Guid ProcessorId,
    StepOutcome Outcome) : IExecutionCorrelated
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId { get; init; }
    public string EntryId { get; init; } = "";
    public string H { get; init; } = "";                  // ← REMOVE on the new records
    public string? ErrorMessage { get; init; }            // ← migrates to StepFailed only
    public string? CancellationMessage { get; init; }     // ← migrates to StepCancelled only
}
```

**New shape — drop `Outcome` positional, drop `H`, `EntryId` → `Guid`** (RESEARCH §Pattern 3, lines 192-205):
```csharp
namespace Messaging.Contracts;

// bus envelope — NO [JsonPropertyName], default STJ (mirrors ExecutionResult)
public sealed record StepCompleted(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IStepResult
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; }            // the REAL data key (D-06a) — no Guid.Empty default
}

public sealed record StepFailed(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IStepResult
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; } = Guid.Empty;   // hard-default sentinel (D-06a)
    public string? ErrorMessage { get; init; }               // D-06b
}

// StepCancelled = StepFailed shape but CancellationMessage instead of ErrorMessage (+ Guid.Empty default).
// StepProcessing = StepFailed shape with NEITHER diagnostic field (+ Guid.Empty default).
```
> **Per-record id-set (all six):** `CorrelationId, WorkflowId, StepId, ProcessorId, ExecutionId, EntryId`. `WorkflowId/StepId/ProcessorId` positional (mirrors `ExecutionResult.cs:8-10`); `CorrelationId/ExecutionId/EntryId` `init` (mirrors `ExecutionResult.cs:13-15`). `StepCompleted`/`StepProcessing` carry NO diagnostic field (D-06b).

---

### `SourceStep.cs` (single sentinel predicate) — D-07

**Analog:** `MessageIdentity.cs` (a single-source-of-truth static helper class in `Messaging.Contracts`) — mirror the `public static class` + doc convention; `L2ProjectionKeys.cs:28` is the other static-class precedent.

```csharp
namespace Messaging.Contracts;

/// <summary>D-07: the SINGLE shared source-step sentinel predicate. Every consumer branches
/// "skip read / skip end-delete" off THIS helper — never an ad-hoc `== Guid.Empty` (Anti-Pattern).</summary>
public static class SourceStep
{
    public static bool IsSource(Guid entryId) => entryId == Guid.Empty;
}
```
> Replaces the deleted `MessageIdentity.cs` as the new "one canonical helper" in the leaf. Used by `ExecutionLogScope.BuildState` (string→Guid skip) + the processor input-read skip.

---

### `IKeeperRecoverable.cs` + five Keeper records (`KeeperUpdate/Reinject/Inject/Delete/Cleanup.cs`) — D-11/D-12

**Analog:** `IExecutionCorrelated.cs:10-20` (interface exposing an id-set as get-only members) for the marker; `ExecutionResult.cs` for the `sealed record` body; `ICorrelated.cs:4` for the optional `: ICorrelated` add (discretion, leaning yes per CONTEXT).

**Marker — expose ONLY the partition 4-tuple** (D-12: `stepId` is NOT in the partition key). Mirror the `IExecutionCorrelated.cs:11-16` get-only member style:
```csharp
namespace Messaging.Contracts;

/// <summary>D-12: marker exposing the partition 4-tuple (corr:wf:proc:exec == composite-backup key)
/// the Phase-46 MassTransit UsePartitioner consumes for per-key ordering. stepId rides as a plain
/// property on each record, NOT part of the partition key.</summary>
public interface IKeeperRecoverable   // : ICorrelated (discretion — leaning yes; member names = discretion)
{
    Guid CorrelationId { get; }
    Guid WorkflowId    { get; }
    Guid ProcessorId   { get; }
    Guid ExecutionId   { get; }
}
```
> If `: ICorrelated` is added, drop the `CorrelationId` re-declaration (it's inherited) — mirror how `IExecutionCorrelated : ICorrelated` does NOT redeclare `CorrelationId` (`IExecutionCorrelated.cs:10-16`).

**Five records** — `sealed record : IKeeperRecoverable`, all carry `{corr, wf, step, proc, exec}`; deltas per D-11. Mirror the positional/`init` split:
```csharp
namespace Messaging.Contracts;

// All five: bus envelope, NO [JsonPropertyName]. WorkflowId/StepId/ProcessorId positional; the rest init.
public sealed record KeeperUpdate(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public string ValidatedData { get; init; } = "";   // D-11: UPDATE-only extra
}
// KeeperReinject / KeeperDelete: + `public Guid EntryId { get; init; }` (D-11)
// KeeperInject / KeeperCleanup: the 5-id base, no extra
```
> Naming: `Keeper`-prefix avoids BCL/EF `Update`/`Delete` collisions (RESEARCH A5). `stepId` is a plain `init` property on each record but is NOT on `IKeeperRecoverable`.

---

### `BackupOptions.cs` (options class) — D-10

**Analog (exact):** `Keeper/ProbeOptions.cs` (lines 8-15) — `public sealed class` with `int` props + inline defaults.

```csharp
namespace Keeper;

/// <summary>D-10: composite-backup TTL knob (crash-backstop only — normally deleted by CLEANUP/INJECT).
/// Bound from appsettings (mirrors ProbeOptions). Default 2 days.</summary>
public sealed class BackupOptions
{
    public int TtlDays { get; set; } = 2;
}
```

**Binding (exact analog):** `Keeper/Program.cs:29` — `builder.Services.Configure<ProbeOptions>(builder.Configuration.GetSection("Probe"));`. Add a sibling line:
```csharp
// Program.cs (Keeper) — section name "Backup" mirrors "Probe"/"Retry" (discretion)
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection("Backup"));
```
> The TTL is APPLIED only at the Keeper `UPDATE` write (Phase 46) — never baked into the key builder (Anti-Pattern). `BackupOptions` lives Keeper-side (sibling to `ProbeOptions`, RESEARCH A2).

---

### `IExecutionCorrelated.cs` (MODIFIED — drop `H`, `EntryId` string→Guid) — D-04/D-05

**Current** (`IExecutionCorrelated.cs:10-20`):
```csharp
public interface IExecutionCorrelated : ICorrelated
{
    Guid ExecutionId { get; }
    Guid WorkflowId  { get; }
    Guid StepId      { get; }
    Guid ProcessorId { get; }
    string EntryId   { get; }     // ← becomes  Guid EntryId { get; }
    string H { get; }             // ← DELETE this member + its <summary>
}
```
**After:** `Guid EntryId { get; }`; remove `string H { get; }` (lines 18-19) entirely. `ICorrelated` (just `Guid CorrelationId`) is UNCHANGED.

---

### `EntryStepDispatch.cs` (MODIFIED — drop `H`, `EntryId` string→Guid) — D-04/D-05

**Current** (`EntryStepDispatch.cs:11-18`):
```csharp
public sealed record EntryStepDispatch(
    Guid WorkflowId, Guid StepId, Guid ProcessorId, string Payload) : IExecutionCorrelated
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId  { get; init; } = Guid.Empty;
    public string EntryId    { get; init; } = "";        // ← Guid EntryId { get; init; } = Guid.Empty;
    public string H          { get; init; } = "";        // ← DELETE
}
```
**After:** `public Guid EntryId { get; init; } = Guid.Empty;`; delete the `H` line (17). Update the XML-doc (it references "64-hex string" + "H ... Plan 04").

---

### `ExecutionLogScope.cs` (MODIFIED — string→Guid skip via D-07 predicate) — RISK SITE

**Analog:** self + the new `SourceStep.IsSource`. The five `const` key strings (lines 12-16) are UNCHANGED — `ExecutionLogScopeKeyTests` still passes verbatim.

**Current** (`ExecutionLogScope.cs:31`):
```csharp
if (!string.IsNullOrEmpty(ec.EntryId)) state[EntryId] = ec.EntryId;
```
**After** (route through the D-07 predicate — never inline `== Guid.Empty`, Anti-Pattern):
```csharp
if (!SourceStep.IsSource(ec.EntryId)) state[EntryId] = ec.EntryId.ToString();
```
> Pitfall (RESEARCH Pitfall 2): a naive `ec.EntryId.ToString()` without the `IsSource` guard would EMIT the all-zero GUID into the log scope for every source step (silent noise + behavior change). The guard is load-bearing.

---

### `L2ProjectionKeys.cs` (MODIFIED — collapse `ExecutionData`, add `CompositeBackup`, remove `Flag`) — D-08/D-09

**Existing builders + the `:D` convention to mirror** (`L2ProjectionKeys.cs:30-48`):
```csharp
public const string Prefix = "skp:";
public static string Root(Guid workflowId) => $"{Prefix}{workflowId:D}";   // :D hyphenated convention
public static string ExecutionData(Guid entryId) => $"{Prefix}data:{entryId:D}";  // ALREADY EXISTS (line 48) — PROMOTE
```
**Edits:**
- KEEP `ParentIndex` (32), `Root` (34), `Step` (36), `Processor` (38).
- `ExecutionData`: DELETE the `(string)` overload (line 41); KEEP the `(Guid)` overload (line 48) as the sole builder (D-08).
- ADD (D-09, `skp:`-prefixed, four `:D` GUIDs colon-joined — RESEARCH §Pattern 1):
```csharp
/// <summary>D-09: composite backup key — skp:{corr:D}:{wf:D}:{proc:D}:{exec:D}. The four-GUID shape
/// IS the namespace (no data:/backup: segment — Anti-Pattern). No TTL baked in (caller concern).</summary>
public static string CompositeBackup(Guid correlationId, Guid workFlowId, Guid processorId, Guid executionId)
    => $"{Prefix}{correlationId:D}:{workFlowId:D}:{processorId:D}:{executionId:D}";
```
- REMOVE `Flag(string)` (line 51) — coupled RETIRE-01.
- `KeeperProbe(string)` (54) / `KeeperRecoverAttempts(string)` (57): KEEP — the DARK reactive path still calls them with a *local* key string (see §Dark Path). They are `(string)`-keyed, so they survive `H`'s removal as long as the caller supplies a string.
- Update the class `<summary>` (lines 24-25 reference `data:{64hex}` content-addressing + the `Flag` builder — both gone).

---

### `KeeperQueues.cs` (MODIFIED — add `Recovery` const) — D-13

**Analog/style:** `OrchestratorQueues.cs:16` (`public const string Result = "orchestrator-result";`) + the two existing `KeeperQueues` consts (lines 15, 21).

Add (KEEP `FaultRecovery` line 15 + `DeadLetter` line 21 — retired in 47/48, NOT here):
```csharp
/// <summary>D-13: gate-open-only recovery consumer queue (Phase 46 binds it).</summary>
public const string Recovery = "keeper-recovery";
```

---

### `ResultConsumer.cs` (MODIFIED — straight-through, delete dedup/manifest/Redis) — D-03/D-06e/A4

**Analog:** self. Re-target `IConsumer<ExecutionResult>` → `IConsumer<StepCompleted>` (A4: the straight-through "single result" consumer; the four typed consumers + `TypedResultConsumer<T>` base are Phase 46).

Concrete deletions (current `ResultConsumer.cs`):
- Line 8 alias + line 44 `IConnectionMultiplexer redis` ctor param + line 60 `redis.GetDatabase()` — DELETE (Redis leaves the consumer, D-06e; Pitfall 5 — make ctor-param removal + DI/test updates ONE atomic task; grep `new ResultConsumer(`).
- Lines 62-72 (`Flag(m.H) == "Ack"` dedup gate + `ResultDeduped`) — DELETE (RETIRE-01).
- Lines 84-93 (manifest unbundle / `ExecutionData(m.EntryId)` read) — DELETE (content-addr/manifest gone).
- Lines 103-107 (N×M `foreach item × SelectNext`) — COLLAPSE to one message = one item:
```csharp
foreach (var (stepId, step) in advancement.SelectNext(StepOutcome.Completed, completed, wf.Steps))
    await dispatcher.DispatchAsync(
        m.WorkflowId, stepId, step.ProcessorId, step.Payload,
        m.CorrelationId, NewId.NextGuid(), m.EntryId, context.CancellationToken);
```
> `SelectNext` takes a `StepOutcome` (Pitfall 4) — the straight-through `StepCompleted` consumer hardcodes `StepOutcome.Completed` (the per-type knob is Phase 46). `entryId` is now `m.EntryId` (a `Guid`). `StepAdvancement` itself does NOT change.
- Lines 109-116 (effect-first `Flag` flip) — DELETE (RETIRE-01).
- KEEP line 55 `metrics.ResultConsumed` + lines 74-82 L1-miss business-ack.

---

### `StepDispatcher.cs` (MODIFIED — delete H compute + flag pre-write; entryId Guid) — D-03

**Current** (`StepDispatcher.cs:38-59`):
```csharp
public async Task DispatchAsync(..., Guid executionId, string entryId, CancellationToken ct)   // ← Guid entryId
{
    var h = MessageIdentity.ComputeH(correlationId, workflowId, stepId, processorId, entryId);  // ← DELETE
    var msg = new EntryStepDispatch(workflowId, stepId, processorId, payload)
    {
        CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId, H = h,      // ← drop H
    };
    var db = redis.GetDatabase();
    await db.StringSetAsync(L2ProjectionKeys.Flag(h), "Pending", expiry: FlagTtl);               // ← DELETE
    ...
}
```
**After:**
- `IStepDispatcher.DispatchAsync` signature `string entryId` → `Guid entryId` (ripples to the interface + every caller — `ResultConsumer`, `WorkflowFireJob`).
- DELETE line 44 (`ComputeH`), line 51 (`H = h,`), lines 58-59 (`db` + `Flag` pre-write).
- Drop `using Messaging.Contracts.Hashing;` (line 3) + the `IConnectionMultiplexer redis` ctor dep (line 29) + `FlagTtl` (line 35) if no longer used.
- KEEP the `Send` block (62-63) + `DispatchSent` metric (69).

---

### `WorkflowFireJob.cs` (MODIFIED — replace hash entryId with `Guid.Empty`) — D-03

**Current** (`WorkflowFireJob.cs:86-89`):
```csharp
var entryId = MessageIdentity.EntryEntryId(correlationId, entryStepId);   // ← DELETE
await dispatcher.DispatchAsync(
    workflowId, entryStepId, step.ProcessorId, step.Payload,
    correlationId, Guid.Empty, entryId, context.CancellationToken);       // ← entryId arg → Guid.Empty
```
**After:** seed `entryId = Guid.Empty` (the source sentinel — replaces the hash); pass it directly:
```csharp
await dispatcher.DispatchAsync(
    workflowId, entryStepId, step.ProcessorId, step.Payload,
    correlationId, Guid.Empty, Guid.Empty, context.CancellationToken);
```
> Remove `using Messaging.Contracts.Hashing;` (line 3). The `Guid.Empty` executionId (lineage) is unchanged; the new `Guid.Empty` is the entryId sentinel.

---

### `EntryStepDispatchConsumer.cs` (MODIFIED — delete CAS/content-addr/manifest; minimal compile-and-pass) — D-03

**Analog:** self. This is the largest single edit — RETIRE-01 (CAS dedup) + RETIRE-02 (content-addressing + manifest). Concrete map (current `EntryStepDispatchConsumer.cs`):
- Line 7 `using Messaging.Contracts.Hashing;` + line 12 alias — DELETE/adjust.
- Lines 72-84 (`Flag(dispatch.H) == "Ack"` gate + `DispatchDeduped`) — DELETE (RETIRE-01).
- Lines 91-93 input read — replace the `string.IsNullOrEmpty(dispatch.EntryId)` skip with the D-07 predicate + Guid builder:
```csharp
var raw = SourceStep.IsSource(dispatch.EntryId)
    ? RedisValue.Null
    : await db.StringGetAsync(L2ProjectionKeys.ExecutionData(dispatch.EntryId));
```
- Lines 146-216 (`HashBlob`, content-addr write, manifest assembly, `HashManifest`, outbound `flag[resultH]="Pending"`) — DELETE. Straight-through: per result, mint `entryId` (a `Guid` via `NewId.NextGuid()`), write `ExecutionData(entryId)`, send one `StepCompleted`/`StepFailed`/`StepCancelled` (no `H`). The Pre/In/Post rewrite is Phase 44 — keep 43 to minimal compile-and-pass.
- Lines 218-225 (effect-first CAS flip) — DELETE (RETIRE-01).
- Lines 244-306 `SendResult` + builders — re-shape to return/send the matching `Step*` record:
  - `BuildCompleted` (279-286) → `new StepCompleted(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = executionId, EntryId = newEntryId }` (real Guid key — D-06a).
  - `BuildFailed` (288-296) → `new StepFailed(...) { ..., EntryId = Guid.Empty, ErrorMessage = error }`.
  - `BuildCancelled` (298-306) → `new StepCancelled(...) { ..., EntryId = Guid.Empty, CancellationMessage = msg }`.
- `OutcomeLabel(result.Outcome)` (line 250): `Outcome` is no longer a wire field (Pitfall 4) — derive the metric `outcome` tag from the record TYPE or a label passed into `SendResult` (e.g. `SendResult` takes an explicit `string outcomeLabel` from each build path).
> RESEARCH flags this file as the "compiles-but-red" risk — keep the SendResult-as-single-owner structure (line 244 doc) so no send path is uncounted.

---

### `InboundExecutionScopeConsumeFilter.cs` (MODIFIED — NO body change)

**Analog:** self. The body (`InboundExecutionScopeConsumeFilter.cs:22-29`) keys on `IExecutionCorrelated` and delegates to `ExecutionLogScope.BuildState(ec)` — fixed ENTIRELY by the `ExecutionLogScope` edit. The four `Step*` records implement `IStepResult : IExecutionCorrelated` (D-06c) so they flow through unchanged. Only the doc-comment line 11 ("empty-string EntryId skipped") should be touched to read "Guid.Empty EntryId skipped".

---

## Dark Path (D-14 — keep compiling, NOT functionally rewritten)

**Governing decision:** the reactive Keeper path structurally depends on `IExecutionCorrelated.H`, `Fault<ExecutionResult>`, and `ExecutionData(string)` — all removed in 43 — yet its real retirement (RETIRE-03) is Phases 47/48. It goes DARK: compiles, does not run, files NOT deleted (the diff must not show `KeeperRecoveryHandler.cs`/`FaultExecutionResultConsumer.cs` disappearing).

### `KeeperRecoveryHandler.cs` — rebind off `H`, local identity source

**Current dependencies on removed members** (`KeeperRecoveryHandler.cs`):
- Line 69 `where T : class, IExecutionCorrelated` — the generic bound.
- Lines 76, 85, 92, 98, 106, 148 read `inner.H` (and `inner.EntryId` at line 98, now a `Guid`).

**Minimal compile-preserving move (D-14):** give the handler a LOCAL identity string derived from the 4-tuple wherever it read `inner.H`. The 4-tuple is exactly the new `CompositeBackup` key shape — reuse it:
```csharp
// derive a local key instead of inner.H (no wire H re-introduced):
var localKey = L2ProjectionKeys.CompositeBackup(
    inner.CorrelationId, inner.WorkflowId, inner.ProcessorId, inner.ExecutionId);
```
- Line 92/148 `new PauseWorkflow(inner.WorkflowId, inner.H)` / `new ResumeWorkflow(..., inner.H)` — `PauseWorkflow`/`ResumeWorkflow` keep their OWN `string H` positional (`PauseWorkflow.cs:4` — independent of the interface), so pass `localKey` (or `""`) there.
- Line 98 `recovery.RunAsync(inner.EntryId, inner.H, ...)` — `inner.EntryId` is now a `Guid`; `L2ProbeRecovery.RunAsync` takes `string entryId` (see below) — convert or re-type.
- Line 106 `KeeperRecoverAttempts(inner.H)` — pass `localKey` (the `(string)` builder survives).
> The handler keeps its bound on `IExecutionCorrelated` (still exists) but no longer reads `.H`. No behavior promised — dormant.

### `L2ProbeRecovery.cs` — point at the Guid `ExecutionData`

**Current** (`L2ProbeRecovery.cs:24,37`):
```csharp
public async Task<ProbeOutcome> RunAsync(string entryId, string h, string procId, CancellationToken ct)
...
_ = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(entryId));   // ← ExecutionData(string) removed
```
**After:** re-type the `entryId` param to `Guid` (the `ExecutionData(Guid)` builder is the surviving one), OR keep `string` and have the caller stringify — minimal change is re-typing to `Guid entryId` and calling `ExecutionData(entryId)` (now the Guid overload). `KeeperProbe(h)` at line 38 stays `(string)`-keyed (caller passes `localKey`).

### `FaultExecutionResultConsumer.cs` / `...Definition.cs` — drop the `Fault<ExecutionResult>` consumer

**Current** (`FaultExecutionResultConsumer.cs:20-26`): `IConsumer<Fault<ExecutionResult>>` — its message type (`ExecutionResult`) no longer exists, so `Fault<ExecutionResult>` does not resolve. Per D-14, bind the reactive consumer on `Fault<EntryStepDispatch>` ONLY and DROP this consumer/definition.
- Remove its registration from `Keeper/Program.cs:56` (`x.AddConsumer<FaultExecutionResultConsumer, FaultExecutionResultConsumerDefinition>();`).
- `FaultEntryStepDispatchConsumer.cs` (the analog one-liner, lines 17-23) is KEPT — it binds `Fault<EntryStepDispatch>`, whose type survives.
> D-14 says do NOT delete `KeeperRecoveryHandler.cs`/`FaultExecutionResultConsumer.cs` as files (that's RETIRE-03 pulled forward). The planner should confirm whether "drop the consumer" means neutralize the body vs remove the file. Escalation flagged in RESEARCH Q1/A3 (MEDIUM-HIGH risk) — the planner MUST resolve this as a D-row before Wave 3, not as an incidental compile-fix.

---

## Shared Patterns

### Single-source sentinel predicate (D-07)
**Source (new):** `Messaging.Contracts/SourceStep.cs` — `IsSource(Guid)`.
**Apply to:** `ExecutionLogScope.BuildState` (string→Guid skip), `EntryStepDispatchConsumer` input-read skip, and any future "skip read / skip end-delete" branch. NEVER inline `== Guid.Empty` (Anti-Pattern, RESEARCH line 210).

### Single-source L2 key builders
**Source:** `Messaging.Contracts/Projections/L2ProjectionKeys.cs` (`:D` GUID convention, `const Prefix`).
**Apply to:** every L2 key string in this phase (`ExecutionData(Guid)`, `CompositeBackup`). Callers forward — never `$"skp:..."` per-caller (Don't-Hand-Roll, RESEARCH line 220).

### appsettings-bound options
**Source:** `Keeper/ProbeOptions.cs` + `Keeper/Program.cs:29` binding + `tests/.../Keeper/ProbeOptionsBoundTests.cs` test.
**Apply to:** `BackupOptions` (+ its binding line + a `BackupOptionsBoundTests` mirror).

### `sealed record` bus-envelope contract
**Source:** `ExecutionResult.cs` (positional `WorkflowId/StepId/ProcessorId` + `init` extras, no `[JsonPropertyName]`, int enums).
**Apply to:** all four `Step*` records + all five `Keeper*` records.

### Marker-interface layering
**Source:** `ICorrelated.cs` → `IExecutionCorrelated.cs` (get-only member set; no redeclare of inherited members).
**Apply to:** `IStepResult : IExecutionCorrelated` (empty marker) + `IKeeperRecoverable` (4-tuple, optional `: ICorrelated`).

---

## Golden Test Pattern Assignments

### `StepResultContractTests.cs` (NEW) — SC-1/SC-2
**Analog (exact):** `Orchestrator/ExecutionResultContractTests.cs` — pure STJ round-trip + reflection, no harness. Copy its structure (lines 17-46 round-trip; 89-100 absent-property reflection; 102-106 interface assert). New assertions per RESEARCH §"Code Examples" lines 408-423:
```csharp
Assert.Null(typeof(StepCompleted).GetProperty("H"));                         // H absent (SC-1)
Assert.True(typeof(IStepResult).IsAssignableFrom(typeof(StepCompleted)));
Assert.Equal(Guid.Empty, new StepFailed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()).EntryId);   // D-06a/SC-2
```
> Also pin: `default(StepFailed).EntryId` serializes to `"00000000-0000-0000-0000-000000000000"` (Pitfall 1 — the sentinel is a present zero-GUID, not an omitted field).

### `SourceStepTests.cs` (NEW) — SC-2
**Analog:** reflection/fact style of `ExecutionResultContractTests`. Per RESEARCH lines 427-431:
```csharp
Assert.True(SourceStep.IsSource(Guid.Empty));
Assert.False(SourceStep.IsSource(Guid.NewGuid()));
```

### `KeeperContractTests.cs` (NEW) — SC-3
**Analog:** `ExecutionResultContractTests.cs` reflection + round-trip. Pin: five records exist; each `: IKeeperRecoverable` exposing the 4-tuple; `KeeperUpdate` has `ValidatedData`; `KeeperReinject`/`KeeperDelete` have `EntryId`; `stepId` is a property but NOT on `IKeeperRecoverable`.

### `BackupOptionsBoundTests.cs` (NEW) — D-10
**Analog (exact):** `Keeper/ProbeOptionsBoundTests.cs:6-20` — instantiate-defaults-and-assert-invariant:
```csharp
Assert.Equal(2, new BackupOptions().TtlDays);   // default 2 days (D-10)
```

### `L2ProjectionKeysTests.cs` (MODIFIED) — SC-4
**Analog:** self (`L2ProjectionKeysTests.cs:54-60` `ExecutionData` golden — the expectation is already `skp:data:{guid:D}`, so it likely passes verbatim once the string overload is gone). ADD the `CompositeBackup` golden per RESEARCH lines 388-399:
```csharp
Assert.Equal(
    "skp:11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222:" +
    "33333333-3333-3333-3333-333333333333:44444444-4444-4444-4444-444444444444",
    L2ProjectionKeys.CompositeBackup(corr, wf, proc, exec));
```

### `ExecutionResultContractTests.cs` + `EntryStepDispatchTests.cs` (RESHAPE/DELETE)
- `ExecutionResultContractTests.cs` → split into the four `Step*` contract tests (or fold into `StepResultContractTests`). The `ExecutionResult` type is gone.
- `EntryStepDispatchTests.cs:36-37` currently asserts `dispatch.EntryId == ""` and `dispatch.H == ""` — RESHAPE to assert `Guid` `EntryId` (default `Guid.Empty`) + `Assert.Null(typeof(EntryStepDispatch).GetProperty("H"))`.

---

## No Analog Found

None. Every new/modified/dark/test file maps to a verified in-repo precedent (the codebase already contains `ExecutionResult`, `IExecutionCorrelated`, `ProbeOptions`, `L2ProjectionKeys`, `OrchestratorQueues`, and the golden-test templates this phase forks).

## Metadata

**Analog search scope:** `src/Messaging.Contracts/` (+ `Projections/`, `Hashing/`), `src/Keeper/` (+ `Recovery/`, `Consumers/`), `src/Orchestrator/` (`Consumers/`, `Dispatch/`, `Scheduling/`), `src/BaseProcessor.Core/Processing/`, `src/BaseConsole.Core/Messaging/`, `tests/BaseApi.Tests/` (`Contracts/`, `Orchestrator/`, `Keeper/`, `Features/Orchestration/Projection/`).
**Files read this session:** 21 source/test analogs + 2 planning inputs (CONTEXT.md, RESEARCH.md).
**Pattern extraction date:** 2026-06-08

## PATTERN MAPPING COMPLETE
