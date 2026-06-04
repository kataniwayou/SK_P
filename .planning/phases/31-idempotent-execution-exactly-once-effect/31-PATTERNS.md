# Phase 31: Idempotent Execution Round-Trip (Exactly-Once-Effect) - Pattern Map

**Mapped:** 2026-06-04
**Files analyzed:** 16 production + 8 test (new/modified)
**Analogs found:** 16 / 16 (every cluster has an in-tree analog; this is a rework-existing phase, not a greenfield one)

This map serves the planner: each file to create/modify is classified, assigned its closest in-tree analog, and given concrete copy-from excerpts with file paths + line numbers. No CLAUDE.md exists; no applicable project skills (the skills registry is UX/frontend-only). Conventions are encoded in the analogs below.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Messaging.Contracts/IExecutionCorrelated.cs` | contract (interface) | request-response (wire) | self (in place; `Guid EntryId` -> `string`) | self-edit |
| `src/Messaging.Contracts/EntryStepDispatch.cs` | contract (record) | request-response (wire) | self + `ExecutionResult.cs` | self-edit |
| `src/Messaging.Contracts/ExecutionResult.cs` | contract (record) | request-response (wire) | self + `EntryStepDispatch.cs` | self-edit |
| `src/Messaging.Contracts/Hashing/*` (NEW hash helper) | utility (static) | transform | `SourceHash.targets:49-66` + `HashHelpers.cs` | role-match (algorithm-exact) |
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` | config (key builder) | transform | self (`ExecutionData`/`Root` methods) | self-edit |
| `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` | consumer (receiver) | event-driven (dedup + content-addr write) | self (current mint+write+send shape) | self-edit (heavy rework) |
| `src/Orchestrator/Consumers/ResultConsumer.cs` | consumer (receiver) | event-driven (dedup + fan-out) | self (already L1-idempotent) | self-edit |
| `src/Orchestrator/Dispatch/StepAdvancement.cs` | service (pure fn) | transform | self (`SelectNext` generator) | self-edit |
| `src/Orchestrator/Dispatch/StepDispatcher.cs` | service (sender) | request-response | self (build-and-Send + pre-write Pending) | self-edit |
| `src/Orchestrator/Dispatch/IStepDispatcher.cs` | service (interface) | request-response | self (signature: `Guid entryId` -> `string`) | self-edit |
| `src/Orchestrator/Scheduling/WorkflowFireJob.cs` | job (producer) | event-driven (cron fire) | self (`:54` correlationId mint, `:84` `Guid.Empty`) | self-edit |
| `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs` | middleware (filter) | event-driven (scope) | self (`:33` `!= Guid.Empty` guard) | self-edit |
| `src/Messaging.Contracts/Configuration/RetryOptions.cs` (NEW) | config (record) | config-bind | `ProcessorLivenessOptions` (IOptions pattern) | role-match |
| `src/Orchestrator/Consumers/{Result,Start,Stop}*Definition.cs` | config (retry seam) | config-bind | self (`Immediate(3)` x3) | self-edit |
| `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` | startup (inline bind) | config-bind | self (`:149-153` inline `ConnectReceiveEndpoint`) | self-edit (structurally different from 1-3) |
| `tests/BaseApi.Tests/.../IdempotentExactlyOnceE2ETests.cs` (NEW) | test (E2E) | event-driven (real-stack) | `SampleRoundTripE2ETests.cs` | role-match (clone) |

## Pattern Assignments

### Cluster 1 - Contract field changes (`EntryId` Guid->string + new `H`)

**Files:** `IExecutionCorrelated.cs`, `EntryStepDispatch.cs`, `ExecutionResult.cs`
**Analog:** the three files themselves - copy how fields are already declared.

How fields are declared today (D-01 changes the `Guid EntryId` lines to `string`; D-02 adds `H`):

`EntryStepDispatch.cs:9-15` - positional ctor for the stable identity fields; init-only props with defaults for the correlated id-set:
```csharp
public sealed record EntryStepDispatch(
    Guid WorkflowId, Guid StepId, Guid ProcessorId, string Payload) : IExecutionCorrelated
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId  { get; init; } = Guid.Empty;
    public Guid EntryId      { get; init; } = Guid.Empty;   // -> string EntryId { get; init; } = "";  + add  string H { get; init; } = "";
}
```

`ExecutionResult.cs:7-21` - same shape; note the comment "NO [JsonPropertyName], default STJ serialization" - the new `H` and string `EntryId` inherit this default-STJ wire convention (no attribute), critical for cross-process byte-stability (RESEARCH Pitfall 2):
```csharp
// NOTE: bus envelope - NO [JsonPropertyName], default STJ serialization (mirrors EntryStepDispatch).
public sealed record ExecutionResult(
    Guid WorkflowId, Guid StepId, Guid ProcessorId, StepOutcome Outcome) : IExecutionCorrelated
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId { get; init; }
    public Guid EntryId { get; init; }   // -> string;  + add  string H { get; init; }
    public string? ErrorMessage { get; init; }
    public string? CancellationMessage { get; init; }
}
```

`IExecutionCorrelated.cs:9-16` - the interface that ripples the type change everywhere (`:15` `Guid EntryId` -> `string EntryId`; add `string H` if `H` is interface-surfaced, planner's call - both contracts carry it concretely regardless):
```csharp
public interface IExecutionCorrelated : ICorrelated
{
    Guid ExecutionId { get; }
    Guid WorkflowId  { get; }
    Guid StepId      { get; }
    Guid ProcessorId { get; }
    Guid EntryId     { get; }   // -> string EntryId { get; }
}
```

**Ripple sites (RESEARCH Pitfall 1 - must change in the same wave or the tree will not compile):** `StepDispatcher.cs:15,22` (signature + assignment), `IStepDispatcher.cs:21-22`, `WorkflowFireJob.cs:84`, `ResultConsumer.cs:67-68`, `EntryStepDispatchConsumer.cs:74,91,153,171,251-274`, `InboundExecutionScopeConsumeFilter.cs:33`.

---

### Cluster 2 - Hash helper (NEW, in `Messaging.Contracts`)

**Analog (algorithm-exact, copy byte-for-byte):** `src/BaseProcessor.Core/SourceHash.targets:54-66`

This is the canonical UTF-8 -> SHA-256 -> lowercase `x2` -> 64-hex convention the new helper MUST mirror (D-03/D-04). The `H`, `EntryId = hash(blob)`, and `hash(manifest)` paths all route through ONE static method:
```csharp
var text = System.IO.File.ReadAllText(p);                 // UTF-8 default
text = text.Replace("\r\n", "\n").Replace("\r", "\n");    // LF-normalize (content-only; NOT needed over Guid/hex inputs)
using (var per = System.Security.Cryptography.SHA256.Create())
{
    var h = per.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
    // ...
}
var sb = new System.Text.StringBuilder(64);
foreach (var b in final) sb.Append(b.ToString("x2"));     // lowercase 64-hex, matches ^[a-f0-9]{64}$
Hash = sb.ToString();
```

**Lowercase-hex test-side precedent:** `tests/BaseApi.Tests/TestHelpers/HashHelpers.cs:24-26` - `string.Concat(bytes.Select(b => b.ToString("x2")))`. The golden tests pin exact 64-hex output.

**Canonicalization rule (D-03, planner builds the helper):** `canonical(correlationId, workflowId, stepId, processorId, EntryId)` = each Guid as `g.ToString("D")` (invariant, lowercase by default in .NET), `EntryId` as its 64-hex string, joined by a reserved unit-separator byte (Claude's discretion - a byte that cannot appear in Guid "D" or hex text), then UTF-8 -> SHA-256 -> `x2`. Order is FIXED in the one helper (Pitfall 2(e)).

**Reader-of-embedded-hash precedent** (shows the fail-fast + "name the KEY only, never the value" disclosure convention to keep when logging any hash): `src/BaseProcessor.Core/Identity/AssemblyMetadataSourceHashProvider.cs:21-33`.

**DO NOT:** use `Convert.ToHexString` / `X2` (uppercase breaks `^[a-f0-9]{64}$` + cross-process parity - RESEARCH "Don't Hand-Roll").

---

### Cluster 3 - L2 key builders (`L2ProjectionKeys`)

**Analog:** the existing builder methods in the same file - copy their exact shape.

`L2ProjectionKeys.cs:29-39` - `Prefix` const + the discriminator-segment precedent (`ExecutionData` is the existing `data:` discriminator). D-01 changes `ExecutionData(Guid)` -> `ExecutionData(string)` (64-hex, drop the `:D` Guid specifier); D-05 adds a sibling `Flag(string h)`:
```csharp
public const string Prefix = "skp:";
public static string Root(Guid workflowId) => $"{Prefix}{workflowId:D}";
public static string ExecutionData(Guid entryId) => $"{Prefix}data:{entryId:D}";
//  -> public static string ExecutionData(string entryId) => $"{Prefix}data:{entryId}";   // 64-hex, no :D
//  ADD: public static string Flag(string h)            => $"{Prefix}flag:{h}";           // skp:flag:{64hex}
```
The XML doc block (`:19-25`) enumerates each key shape - update it to add the `Flag` line and the new `data:{64hex}` shape (the file documents itself as the single source of truth; shims `RedisProjectionKeys`/`OrchestratorL2Keys` forward it - never edit there).

**Golden test target (req-7):** both builders match `^skp:(data|flag):[a-f0-9]{64}$`.

---

### Cluster 4 - Redis CAS / flag I/O

**Analogs (StackExchange.Redis primitives already in tree):**

1. **`StringSetAsync` with TTL (the content-addressed `data` write the receiver already does)** - `EntryStepDispatchConsumer.cs:170-173`:
```csharp
await db.StringSetAsync(
    L2ProjectionKeys.ExecutionData(newEntryId),   // -> ExecutionData(hash(blob))
    r.OutputData,
    expiry: TimeSpan.FromSeconds(opts.ExecutionDataTtlSeconds));
```
Same call also at `ProcessorLivenessHeartbeat.cs:97-100` (TTL'd write, last-write-wins, no read-modify-write - the flag write follows suit).

2. **`When.*` conditional set + the "no When tricks / probe-then-act" posture** - `OrchestrationService.cs:135-138` documents WHY the codebase avoids `When.NotExists` value-claims (overwrite defeats first-win). For D-05 the CAS is `StringSet(Flag(H), "Ack", When.Exists)` - SET XX maps to Pending->Ack because Pending is the only pre-existing value. There is NO existing `When.Exists` write to copy verbatim (the tree only references `When.NotExists` in prose), so the planner writes:
```csharp
// effect-first CAS (D-06/D-07): false return is the DESIGNED residual (Pending was lost), NOT an error - do NOT throw on it.
await db.StringSetAsync(L2ProjectionKeys.Flag(h), "Ack", when: When.Exists);
```

3. **Sender pre-write `flag[H_child] = "Pending"` (unconditional, idempotent on re-send, no `When`)** - new, modelled on the plain TTL'd `StringSetAsync` above. Sites: `StepDispatcher.DispatchAsync` and the processor send loop.

4. **`IBatch` / `CreateBatch` (if the planner batches flag + data writes)** - `RedisProjectionWriter.cs:76-108`:
```csharp
var batch = db.CreateBatch();
var tasks = new List<Task>();
tasks.Add(batch.StringSetAsync(RedisProjectionKeys.Root(wf.Id), rootJson));
// ... add per-key tasks ...
batch.Execute();
await Task.WhenAll(tasks);
```

**Convention to preserve:** a Redis fault on get/set is INFRA and PROPAGATES (no catch) -> retry; only `ProcessAsync`/business outcomes are caught-and-acked (`EntryStepDispatchConsumer.cs:67-69`).

---

### Cluster 5 - Processor receiver rework (`EntryStepDispatchConsumer`)

**Analog:** the file's current shape - the rework replaces three identifiable regions.

1. **Effect-first dedup gate (NEW, at the very top of `Consume`, before line 71's input resolution)** - there is no analog gate in this file; model the drop-on-Ack + INFRA-propagate from the existing Redis access at `:67-69`:
```csharp
var db = redis.GetDatabase();   // existing :69 - Redis fault is INFRA, propagates
// NEW (D-06): if (await db.StringGetAsync(L2ProjectionKeys.Flag(dispatch.H)) == "Ack") return;  // drop+ack
```

2. **Remove the `EntryId == Guid.Empty` branch -> key on `InputDefinition == null`** - `EntryStepDispatchConsumer.cs:74-111`. The else-branch (`:89-111`) ALREADY does "read L2 -> empty -> InputDefinition null -> empty input"; D-01 collapses to that path:
```csharp
if (dispatch.EntryId == Guid.Empty)   // <- REMOVE this branch entirely (:74-88)
{ ... }
else
{
    var raw = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(dispatch.EntryId));   // key now string; keep the read+InputDefinition-null path (:89-111)
    ...
}
```

3. **Per-result mint -> content-addressed write + manifest (the core change)** - `EntryStepDispatchConsumer.cs:142-201`. Today (`:153`) `NewId.NextGuid()` per result + one-by-one Send (`:196-200`). Becomes: validate blob (keep `:146`), `EntryId = hash(blob)`, write `data[hash(blob)]`, collect into manifest `[hash(r1)..]`, write `data[hash(manifest)]`, send ONE `ExecutionResult{EntryId=hash(manifest), H}`, pre-write its `flag[H_result]=Pending`, then the CAS flip + ack:
```csharp
var newEntryId  = NewId.NextGuid();   // -> hash(r.OutputData)  (D-03 helper)
var executionId = NewId.NextGuid();   // STAYS lineage (regenerated freely - excluded from H, D-02)
await db.StringSetAsync(L2ProjectionKeys.ExecutionData(newEntryId), r.OutputData, expiry: ...);   // -> ExecutionData(hash(blob))
built.Add(BuildCompleted(dispatch, executionId, newEntryId));   // one-per-result -> ONE manifest result
```
**Empty-result unification (RESEARCH Pitfall 4):** the current empty-list early `return` at `:193-194` must instead send ONE result with `EntryId = hash("[]")` so the orchestrator observes-and-terminates (req-3 "empty advances zero successors AND is acked").

4. **Builders set `EntryId`** - `:251-274`. `BuildCompleted` takes the manifest EntryId; `BuildFailed`/`BuildCancelled` (`:264,273`) set `EntryId = Guid.Empty` today -> string sentinel (Open Q1: `""` vs `hash("[]")`, planner pins + tests it).

---

### Cluster 6 - Orchestrator inbound dedup + manifest fan-out

**Analog:** `ResultConsumer.cs` (already L1-idempotent) + `StepAdvancement.SelectNext` (pure generator).

`ResultConsumer.cs:45-71` - the L1 read is unchanged; the dedup gate is NEW at the top, and the single `DispatchAsync` call (`:66-68`) is wrapped in a manifest loop:
```csharp
// NEW (D-06): if (await db.StringGetAsync(L2ProjectionKeys.Flag(m.H)) == "Ack") return;   // requires injecting IConnectionMultiplexer (see DI note)
if (!store.TryGet(m.WorkflowId, out var wf) || !wf.Steps.TryGetValue(m.StepId, out var completed))   // :55 unchanged
{ ...graceful business-ack... }

// NEW manifest unbundle (D-08): var items = JsonSerializer.Deserialize<string[]>(await db.StringGetAsync(L2ProjectionKeys.ExecutionData(m.EntryId)));
foreach (var (stepId, step) in advancement.SelectNext(m.Outcome, completed, wf.Steps))   // :64 - the M loop
{
    // wrap in: foreach (var itemEntryId in items)  -> N x M dispatches, each EntryId=itemEntryId, H_child computed
    await dispatcher.DispatchAsync(
        m.WorkflowId, stepId, step.ProcessorId, step.Payload,
        m.CorrelationId, m.ExecutionId, /* EntryId */ m.EntryId, context.CancellationToken);   // :66-68 - EntryId becomes itemEntryId
}
// NEW: StringSet(Flag(m.H), "Ack", When.Exists) before normal-return ack (effect-first, D-06)
```
**DI note:** `ResultConsumer` has no Redis dependency today (its ctor `:38-43` is L1-only). The dedup gate + manifest read require injecting `IConnectionMultiplexer` (mirror `EntryStepDispatchConsumer.cs:42` ctor injection).

`StepAdvancement.cs:36-43` - `SelectNext` is the pure successor seam; it stays as-is (the N x M wrap lives in `ResultConsumer`, not here). The N loop is orthogonal to this M generator (RESEARCH data-flow diagram).

`WorkflowFireJob.cs:54,82-84` - entry-step EntryId stamp (req-2). `:54` correlationId mint stays; `:84` `Guid.Empty` for entryId -> `hash(correlationId, stepId)` via the D-03 helper:
```csharp
var correlationId = NewId.NextGuid();   // :54 - per-fire, unchanged
await dispatcher.DispatchAsync(
    workflowId, entryStepId, step.ProcessorId, step.Payload,
    correlationId, Guid.Empty, /* entryId */ Guid.Empty, context.CancellationToken);   // :82-84 -> hash(correlationId, entryStepId)
```

`StepDispatcher.cs:15-27` - the sender (build-and-Send). Signature `Guid entryId` -> `string` (`:15`); assignment `EntryId = entryId` (`:22`); ADD a pre-write `flag[H_child] = "Pending"` before/after the Send (D-06). `IStepDispatcher.cs:21-22` signature changes in lockstep.

---

### Cluster 7 - Retry config binding (`RetryOptions` -> 4 sites)

**Analog A - the `IOptions<T>` ctor-injection pattern:** `EntryStepDispatchConsumer.cs:41-48` (`IOptions<ProcessorLivenessOptions> options`) + read `var opts = options.Value;` (`:65`). The new `RetryOptions { int Limit = 3; RetryStrategy Strategy = Immediate; }` (D-10) binds from appsettings section `Retry` per process and is injected the same way.

**Analog B - the 3 structurally-identical orchestrator sites (`ConsumerDefinition`):**
- `ResultConsumerDefinition.cs:24-32` - `endpointConfigurator.UseMessageRetry(r => r.Immediate(3));`
- `StartOrchestrationConsumerDefinition.cs:29-33` - `r.Immediate(3); r.Ignore<WorkflowRootNotFoundException>();`
- `StopOrchestrationConsumerDefinition.cs:24-28` - same as Start.

These are `ConsumerDefinition<T>` with a parameterless ctor today. To thread `Limit`, inject `IOptions<RetryOptions>` into each definition ctor (definitions are DI-resolved) and use `r.Immediate(opts.Limit)`:
```csharp
endpointConfigurator.UseMessageRetry(r => r.Immediate(opts.Limit));   // was r.Immediate(3)
// Start/Stop also keep: r.Ignore<WorkflowRootNotFoundException>();
```

**Analog C - the structurally-DIFFERENT 4th site (inline, NOT a ConsumerDefinition):** `ProcessorStartupOrchestrator.cs:149-153`:
```csharp
var handle = endpointConnector.ConnectReceiveEndpoint(queueName, (ctx, cfg) =>
{
    cfg.UseMessageRetry(r => r.Immediate(3));                 // -> r.Immediate(retryOpts.Limit)
    cfg.ConfigureConsumer<EntryStepDispatchConsumer>(ctx);
});
```
This binds from the BaseProcessor.Core process (separate appsettings, D-10 per-process). `IOptions<RetryOptions>` must be injected into the `ProcessorStartupOrchestrator` DI ctor (it is a hosted service, not a definition) and captured for the inline lambda - this is the landmine: it is NOT a `ConfigureConsumer` override, so the binding path differs from sites 1-3.

**Discretion (RESEARCH Open Q2):** one shared `RetryOptions` record type in a leaf both processes reference (e.g. `Messaging.Contracts`), bound independently per process. Shape shared, values per-process.

---

### Cluster 8 - Real-stack E2E (clone of `SampleRoundTripE2ETests`)

**Analog (clone wholesale, then diverge):** `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs`

Copy these load-bearing pieces verbatim:
- **Genuine embedded SourceHash** (`:96-104`): read `AssemblyMetadataAttribute "SourceHash"` off the built `Processor.Sample.dll`, register the DB row with it - the container resolves identity against THAT row.
- **Truthful liveness gate** (`:110-114` + `PollForHealthyLivenessAsync` `:195-234`): poll host Redis for the real container's `skp:{procId:D}` Healthy heartbeat before POSTing Start.
- **`PollEsForLog` proof** (`:150-167`): term on `attributes.WorkflowId` / `attributes.CorrelationId` scoped by `resource.attributes.service.name`. For req-8 the assertion is "the StepB4-equivalent downstream-effect line appears EXACTLY the expected per-fire count" (the StepB4-x2 inverse).
- **`ScanExecutionDataKeys`** (`:270-290`): scans `skp:data:*` by prefix; ADD a parallel `skp:flag:*` scan (D-12).
- **Net-zero teardown** (`RealStackWebAppFactory.DisposeAsync` `:427-444` + `L2KeysToCleanup`/`ParentIndexMembersToSrem` `:130-142,422-425`): register the run's new-format `skp:data:{64hex}` AND `skp:flag:{64hex}` keys for deletion so the triple-SHA BEFORE==AFTER holds.

**Diverge (RESEARCH Open Q3):** seed TWO entry steps feeding ONE successor (merge topology via `NextStepIds`), and INDUCE a duplicate test-only - re-publish the same `EntryStepDispatch` and/or a throw-once processor forcing `Immediate(N)` re-run (D-11). Assert per `CorrelationId`: zero duplicate downstream effects even with the induced retry.

**Close gate:** clone `phase-NN-close.ps1` (the prior phase's close gate) -> `phase-31-close.ps1`; extend its scan-clean to `skp:flag:*` + 64-hex `skp:data:*` (D-12); 3-consecutive-GREEN + triple-SHA BEFORE==AFTER.

## Shared Patterns

### Deterministic hashing (the one canonical path)
**Source:** `src/BaseProcessor.Core/SourceHash.targets:54-66` (UTF-8 -> SHA-256 -> `b.ToString("x2")` -> 64-hex)
**Apply to:** the NEW `Messaging.Contracts` hash helper - and ONLY there. `H`, `EntryId=hash(blob)`, `hash(manifest)` all call the same method (D-04). Any second canonical-string builder is a determinism bug (Pitfall 2). Hex is ALWAYS lowercase `x2`, never `X2`/`Convert.ToHexString`.

### Effect-first dedup at a receiver
**Source:** modelled on `EntryStepDispatchConsumer.cs:67-69` (Redis access is INFRA, propagates) + the D-06 protocol.
**Apply to:** BOTH `EntryStepDispatchConsumer.Consume` and `ResultConsumer.Consume`, at the top:
```
if flag[H] == Ack -> return (drop + ack)
else -> produce effect (write+send / dispatch) FIRST -> StringSet(flag[H], "Ack", When.Exists) -> normal-return ack
```
The `When.Exists` false-return is the DESIGNED residual (Pending was lost), never an error - do NOT throw on the bool (Pitfall 3).

### INFRA-throw vs BUSINESS-ack split (preserve everywhere)
**Source:** `EntryStepDispatchConsumer.cs:67-69` + `ResultConsumer.cs` class doc (`:27-36`).
**Apply to:** every consumer touched. Redis get/set faults + broker Send faults PROPAGATE (-> bounded retry -> `_error`); `ProcessAsync` exceptions + schema-validation failures + L1 misses are caught-and-acked (business). The new flag I/O is INFRA (no catch).

### `IOptions<T>` config binding
**Source:** `EntryStepDispatchConsumer.cs:41-48,65` (`IOptions<ProcessorLivenessOptions>` -> `options.Value`).
**Apply to:** `RetryOptions` injection into the 3 orchestrator ConsumerDefinitions + the `ProcessorStartupOrchestrator` hosted-service ctor.

### Ids into log SCOPE values, never templates
**Source:** `EntryStepDispatchConsumer.cs:162-187` (nested `BeginScope` under fixed `ExecutionLogScope` keys) + `InboundExecutionScopeConsumeFilter.cs:28-36`.
**Apply to:** any new `H`/`EntryId` logging - value under a fixed key, never interpolated into the message template (T-18-04). The `:33` `!= Guid.Empty` guards in the filter become `!string.IsNullOrEmpty` once `EntryId` is a string.

### Net-zero L2 teardown
**Source:** `SampleRoundTripE2ETests.cs:427-444` + `:130-142`.
**Apply to:** the new E2E - register both new namespaces (`skp:data:{64hex}`, `skp:flag:{64hex}`) into `L2KeysToCleanup`; extend `ScanExecutionDataKeys` with a `skp:flag:*` scan (D-12).

## No Analog Found

None. Every file is either an in-place edit of an existing file or a clone/role-match of a verified in-tree analog. The two genuinely NEW production files (the hash helper, `RetryOptions`) have algorithm-exact / pattern-exact analogs (`SourceHash.targets`, `ProcessorLivenessOptions` IOptions binding) - no file requires falling back to RESEARCH.md generic patterns.

## Metadata

**Analog search scope:** `src/Messaging.Contracts`, `src/BaseProcessor.Core`, `src/Orchestrator`, `src/BaseApi.Service`, `src/BaseConsole.Core`, `tests/BaseApi.Tests`.
**Files scanned (read in full or targeted):** 18 (every CONTEXT/RESEARCH-named analog + the Redis-primitive grep inventory).
**Pattern extraction date:** 2026-06-04
