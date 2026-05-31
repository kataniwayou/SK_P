# Phase 22: L2 Root-Parent Restructure + Processor Self-Registration Boundary - Pattern Map

**Mapped:** 2026-05-31
**Files analyzed:** 21 (1 new + ~20 modified, incl. tests)
**Analogs found:** 21 / 21

> This phase is a **RESTRUCTURE of existing code**, not greenfield. Almost every "analog" is the
> file's own current state plus a sibling in the same folder. The single NEW production file
> (`ProcessorLivenessValidator.cs`) mirrors three existing siblings: `SchemaEdgeValidator` (shape),
> `OrchestrationValidationException.SchemaEdge` (factory), and `RedisProjectionWriter` (Redis GET +
> deserialize + `TimeProvider`). The single NEW test file mirrors `SchemaEdgeFacts` (HTTP 204/422)
> + `StopCleanupFacts` (direct L2 seeding via the multiplexer).

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| **NEW** `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` | validator | request-response + Redis read | `Validation/SchemaEdgeValidator.cs` (shape) + `Projection/RedisProjectionWriter.cs` (Redis GET/deserialize/TimeProvider) | exact (shape) + role-match (I/O) |
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` | key-builder | transform | itself (current builders) | self |
| `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs` | key-builder (forwarder) | transform | itself + `OrchestratorL2Keys.cs` | self |
| `src/Orchestrator/Messaging/OrchestratorL2Keys.cs` | key-builder (forwarder) | transform | itself + `RedisProjectionKeys.cs` | self |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs` | exception factory | transform | existing `SchemaEdge(...)` factory + `SchemaEdgeOffending` record | exact (same file) |
| `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs` | writer | CRUD (Redis write) | itself (UpsertAsync batch) | self |
| `src/BaseApi.Service/Features/Orchestration/Projection/RedisL2Cleanup.cs` | cleanup | event-driven (BFS GET-and-follow) | itself (existing wave-BFS) | self |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` | orchestrator | request-response | itself (StartAsync validator order) | self |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` | config (DI) | n/a | existing `AddScoped<SchemaEdgeValidator>()` + ctor wiring | exact |
| `src/BaseApi.Core/Configuration/RedisProjectionOptions.cs` | config | n/a | itself | self |
| `src/BaseApi.Service/appsettings.json` | config | n/a | itself | self |
| `src/Orchestrator/appsettings.json` | config | n/a | itself | self |
| `src/Orchestrator/Messaging/OrchestratorRedisOptions.cs` | config | n/a | itself | self |
| `src/Orchestrator/Program.cs` | config (composition) | n/a | itself (line 19-20) | self |
| `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` | consumer | request-response | itself + `StopOrchestrationConsumer.cs` | self |
| `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` | consumer | request-response | itself + `StartOrchestrationConsumer.cs` | self |
| **NEW** `tests/.../Features/Orchestration/ProcessorLivenessFacts.cs` | test | request-response + L2 seed | `SchemaEdgeFacts.cs` (HTTP 204/422) + `StopCleanupFacts.cs` (direct L2 seed) | exact (combined) |
| `tests/.../Features/Orchestration/Projection/L2ProjectionKeysTests.cs` | test | n/a | itself (golden strings) | self |
| `tests/.../Composition/RedisProjectionOptionsBindingFacts.cs` | test | n/a | itself | self |
| `tests/.../Composition/AppsettingsFacts.cs` | test | n/a | itself | self |
| `tests/.../Composition/RedisFixture.cs` + `Phase8WebAppFactory.cs` + `RedisFixtureFacts.cs` | test infra | n/a | itself | self |
| `tests/.../Features/Orchestration/{RedisProjectionWriterFacts,StopCleanupFacts,GateNoWriteFacts}.cs` | test | n/a | themselves | self |

---

## Pattern Assignments

### NEW: `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` (validator, request-response + Redis read)

**Primary shape analog:** `Validation/SchemaEdgeValidator.cs`
**Redis-read/TimeProvider analog:** `Projection/RedisProjectionWriter.cs`
**Snapshot-iteration analog:** `Validation/PayloadConfigSchemaValidator.cs:39` (`snapshot.Processors.TryGetValue`)

**read_first:** `SchemaEdgeValidator.cs` (full, 62 lines), `RedisProjectionWriter.cs:34-51,100-113`,
`OrchestrationValidationException.cs:59-65,82-83`, `ProcessorProjection.cs` (full),
`LivenessProjection.cs` (full), `WorkflowGraphSnapshot.cs:57`.

**Class-shape pattern to mirror** (from `SchemaEdgeValidator.cs:23-29` — `internal sealed`, single
public validate method, throws the domain exception). The NEW class is **async** (D-14) and takes ctor
deps (D-14: `IConnectionMultiplexer` + `TimeProvider`), unlike the parameterless sync `SchemaEdgeValidator`:

```csharp
internal sealed class ProcessorLivenessValidator
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly TimeProvider _clock;

    public ProcessorLivenessValidator(IConnectionMultiplexer multiplexer, TimeProvider clock)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _clock       = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    // async — it reads Redis, unlike the three sync gates (D-14).
    public async Task ValidateAsync(WorkflowGraphSnapshot snapshot, CancellationToken ct)
    {
        var db = _multiplexer.GetDatabase();
        var now = _clock.GetUtcNow().UtcDateTime;   // TimeProvider, mirrors RedisProjectionWriter.cs:60
        foreach (var proc in snapshot.Processors.Values)   // ProcessorReadDto; key = proc.Id
        {
            var raw = await db.StringGetAsync(RedisProjectionKeys.Processor(proc.Id));  // new no-prefix sig
            if (raw.IsNullOrEmpty)
                throw OrchestrationValidationException.ProcessorNotLive(proc.Id, "absent");

            var projection = JsonSerializer.Deserialize<ProcessorProjection>(raw!)!;
            var liveness = projection.Liveness;
            // D-16: interval from the entry (NOT hardcoded 0). timestamp + interval*2 > now.
            var deadline = liveness.Timestamp.AddSeconds(liveness.Interval * 2);   // confirm unit at plan time
            if (deadline <= now)
                throw OrchestrationValidationException.ProcessorNotLive(proc.Id, "stale");
        }
    }
}
```

**Redis GET + deserialize pattern** copied from `RedisProjectionWriter` / `RedisL2Cleanup.cs:43-46,64-67`
(`db.StringGetAsync` → `IsNullOrEmpty` guard → `JsonSerializer.Deserialize<...>(raw!)!`).
**TimeProvider `now`** copied verbatim from `RedisProjectionWriter.cs:60`: `_clock.GetUtcNow().UtcDateTime`.

**OBSERV-REDIS-03 op-tagging (D-15):** the *caller* (`OrchestrationService.StartAsync`) wraps the
`ValidateAsync` call in a `catch (RedisException ex) { ex.Data["redisOp"] = "..."; throw; }` — copy the
existing wrap at `OrchestrationService.cs:131-135` / `148-156`. The validator itself does NOT catch.

> **Liveness-unit caveat for the planner:** `LivenessProjection.Interval` is an `int`
> (`LivenessProjection.cs:13`) and today is hardcoded `0` (`RedisProjectionWriter.cs:61`,
> `StopCleanupFacts.cs:36`). The phase math is `timestamp + interval*2 > now` (SPEC PROC-LIVE-01,
> D-16). The interval **time unit is not defined in code** (no scheduler yet — see Deferred). The
> planner must lock the unit (seconds vs ms) in the plan and make the test seed match. Tests seed L2
> directly so they fully control `timestamp`/`interval`.

---

### `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` (key-builder, transform) — D-01/D-02

**Analog:** itself (current builders, lines 24-31).

**Current** (each builder takes `string prefix`):
```csharp
public static string Root(string prefix, Guid workflowId) => $"{prefix}{workflowId:D}";
public static string Step(string prefix, Guid workflowId, Guid stepId) => $"{prefix}{workflowId}:{stepId}";
public static string Processor(string prefix, Guid processorId) => $"{prefix}{processorId}";
```

**Target** (D-01 const + D-02 `ParentIndex()`; prefix param DROPPED from all builders):
```csharp
public const string Prefix = "skp:";
public static string ParentIndex() => Prefix;                          // D-02 — bare prefix, the parent-index SET key
public static string Root(Guid workflowId) => $"{Prefix}{workflowId:D}";
public static string Step(Guid workflowId, Guid stepId) => $"{Prefix}{workflowId}:{stepId}";
public static string Processor(Guid processorId) => $"{Prefix}{processorId}";
```
Update the XML doc (lines 14-17) which currently says `prefix stays a parameter on every builder (D-05)` —
that statement is now reversed by this phase's D-01. `Root` keeps the `:D` specifier; `ParentIndex()`
returns `Prefix` verbatim (`"skp:"`). Workflow IDs are stored in the SET in `D` (hyphenated) format (D-02),
consistent with `Root`.

---

### `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs` + `src/Orchestrator/Messaging/OrchestratorL2Keys.cs` (forwarders, transform) — D-04

**Analog:** each other (both are thin forwarders to `L2ProjectionKeys`, HARDEN-03).

Writer forwarder current (`RedisProjectionKeys.cs:13-17`):
```csharp
public static string Root(string prefix, Guid workflowId) => L2ProjectionKeys.Root(prefix, workflowId);
public static string Step(string prefix, Guid workflowId, Guid stepId) => L2ProjectionKeys.Step(prefix, workflowId, stepId);
public static string Processor(string prefix, Guid processorId) => L2ProjectionKeys.Processor(prefix, processorId);
```
**Target:** drop the `prefix` param on all three forwarders; **add** a `ParentIndex()` forwarder on the
writer side (the only side that `SADD`/`SREM`s):
```csharp
public static string ParentIndex() => L2ProjectionKeys.ParentIndex();
public static string Root(Guid workflowId) => L2ProjectionKeys.Root(workflowId);
public static string Step(Guid workflowId, Guid stepId) => L2ProjectionKeys.Step(workflowId, stepId);
public static string Processor(Guid processorId) => L2ProjectionKeys.Processor(processorId);
```
`OrchestratorL2Keys.cs:14` reader-side: drop the prefix param → `Root(Guid workflowId) => L2ProjectionKeys.Root(workflowId)`. Reader needs only `Root` (its only call site).

---

### `src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs` (exception factory, transform) — D-17

**Analog:** the existing `SchemaEdge(...)` factory (lines 59-65) + `SchemaEdgeOffending` record (line 83).

Existing factory shape to mirror:
```csharp
public static OrchestrationValidationException SchemaEdge(Guid parentStepId, Guid childStepId)
    => new(
        "schemaEdge",
        "Schema-edge mismatch between steps",
        $"Schema-edge mismatch on edge '{parentStepId}' -> '{childStepId}': ...",
        new SchemaEdgeOffending(parentStepId, childStepId));
...
public sealed record SchemaEdgeOffending(Guid parentStepId, Guid childStepId);
```

**Add** (D-17 — `Gate = "processorLiveness"`, offending carries `procId` + `reason ∈ {"absent","stale"}`):
```csharp
public static OrchestrationValidationException ProcessorNotLive(Guid procId, string reason)
    => new(
        "processorLiveness",
        "Participating processor is not live",
        $"Processor '{procId}' is not live: {reason}.",
        new ProcessorLivenessOffending(procId, reason));
...
public sealed record ProcessorLivenessOffending(Guid procId, string reason);
```
No handler change needed — `OrchestrationValidationExceptionHandler.cs:34-47` already maps ANY
`OrchestrationValidationException` to 422 + RFC-7807 via `ex.ErrorsExtension` (`{ gate, offending }`).
The `Gate` discriminator XML comment at line 23 should be extended to mention `"processorLiveness"`.

---

### `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs` (writer, CRUD) — D-08/D-09

**Analog:** itself, `UpsertAsync` (lines 53-131).

**D-05 prefix removal:** delete `var prefix = _options.KeyPrefix;` (line 55). All key builders lose the
`prefix` arg → `RedisProjectionKeys.Root(wf.Id)`, `RedisProjectionKeys.Step(wf.Id, step.Id)`.

**D-08 add `SADD ParentIndex()`:** add one task to the existing batch (idempotent on re-Start). Mirror the
existing batch-task pattern at line 80 (`tasks.Add(batch.StringSetAsync(...))`). The new op is a SET add:
```csharp
tasks.Add(batch.SetAddAsync(RedisProjectionKeys.ParentIndex(), wf.Id.ToString("D")));
```
(`SetAddAsync` is the SE.Redis `SADD`; member is the `D`-format GUID string per D-02. Batch/pipeline grouping
is planner discretion, D — group with the existing root/step writes.)

**D-09 remove the processor-write loop (lines 100-113):** delete the entire
`foreach (var proc in snapshot.Processors.Values) { ... StringSetAsync(Processor(...), ...) }` block plus the
`TimeSpan? ttl = ...` line (101). The writer creates ZERO `skp:{procId}` keys after this. `ProcessorKeyTtlDays`
(`_options.ProcessorKeyTtlDays`, line 56) becomes unused — keep or prune per planner discretion (D, not load-bearing).
Update the class XML doc (lines 16-24) which currently documents the TTL'd processor write.

---

### `src/BaseApi.Service/Features/Orchestration/Projection/RedisL2Cleanup.cs` (cleanup, BFS GET-and-follow) — D-10/D-11/D-12

**Analog:** itself, `StopCleanupAsync` (lines 36-81) — the iterative wave-BFS.

**Keep** the entire BFS GET-and-follow walk verbatim (lines 49-72): `visited` plain `List<Guid>`,
`currentWave`/`nextWave`, `Distinct()` dedupe, dangling-step skip (line 65). This is the one place Redis
traversal is unavoidable (D-12).

**D-05 prefix removal:** delete `var prefix = _options.KeyPrefix;` (line 38); all `RedisProjectionKeys.Root(workflowId)` / `Step(workflowId, stepId)` lose the prefix arg.

**D-10 add `SREM ParentIndex()`:** add to the final delete batch (lines 76-80). The current batch deletes root
(unconditional) + steps (when any). Add the index removal:
```csharp
var batch = db.CreateBatch();
var delTasks = new List<Task>
{
    batch.SetRemoveAsync(RedisProjectionKeys.ParentIndex(), workflowId.ToString("D")),  // D-10 — SREM this wf
    batch.KeyDeleteAsync(RedisProjectionKeys.Root(workflowId)),
};
if (stepKeysToDelete.Count > 0) delTasks.Add(batch.KeyDeleteAsync(stepKeysToDelete.ToArray()));
batch.Execute();
await Task.WhenAll(delTasks);
```
**Caveat (D-12 ordering):** the routine early-returns at line 44 when the root is absent
(`if (rootJson.IsNullOrEmpty) return;`). If the planner wants `SREM` to run even when the root is already
gone (idempotent GC), the `SREM` must be hoisted ABOVE that early return — flag this for the planner to
decide; D-10 lists `SREM` as step 1 of the teardown, which argues for hoisting it before the absent-root
return. `SREM` is scoped to THIS routine only this phase (D-11); the Stop *flow* is Phase 23.

---

### `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` (orchestrator, request-response) — D-05/D-14/D-15

**Analog:** itself, `StartAsync` (lines 110-172).

**D-14/D-15 inject + invoke the new validator:** add `ProcessorLivenessValidator` as a private readonly
field + ctor param (mirror the existing `_schemaEdgeValidator` field at line 56 and ctor param/null-check at
lines 77,90). Invoke it in `StartAsync` AFTER the three sync gates (lines 142-144) and BEFORE `UpsertAsync`
(line 150), wrapped in the existing `redisOp` tag pattern (D-15):
```csharp
_cycleDetector.Validate(snapshot);
_schemaEdgeValidator.Validate(snapshot);
_payloadConfigSchemaValidator.Validate(snapshot);
// NEW (D-15) — async liveness gate, after the sync trio, before the write:
try
{
    await _processorLivenessValidator.ValidateAsync(snapshot, ct);
}
catch (RedisException ex)
{
    ex.Data["redisOp"] = "UpsertAsync";   // OBSERV-REDIS-03 consistent tag (D-15)
    throw;
}
```
**D-05 KeyPrefix removal (line 98 + line 64 field + line 206 usage):** the `_keyPrefix` field is currently
set from `options.Value.KeyPrefix` (line 98) and read in `StopAsync` at line 206
(`RedisProjectionKeys.Root(_keyPrefix, id)`). Rework to `RedisProjectionKeys.Root(id)` (no-prefix sig) and
remove the `_keyPrefix` field + its ctor assignment. **Note (caveat):** if `IOptions<RedisProjectionOptions>`
is no longer used by this class after removing `_keyPrefix`, the ctor param + the matching DI factory arg
(`OrchestrationServiceCollectionExtensions.cs:71`) must be removed together — they are coupled.

---

### `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` (config/DI) — D-14

**Analog:** the existing `AddScoped<SchemaEdgeValidator>()` (line 74) + the explicit `OrchestrationService`
factory ctor arg list (lines 58-71).

Add the registration next to the other validators (line 74-75):
```csharp
services.AddScoped<ProcessorLivenessValidator>();   // D-14 — AddScoped, same as the sync gates
```
Add the resolved arg into the `OrchestrationService` factory (after `_payloadConfigSchemaValidator` at
line 64): `sp.GetRequiredService<ProcessorLivenessValidator>(),`. `IConnectionMultiplexer` and
`TimeProvider` are already in the container (the writer + service consume them), so the validator's deps
resolve without new registrations. (DI ordering is planner discretion, D.)

> **Caveat:** if D-05 removes the `IOptions<RedisProjectionOptions>` ctor param from `OrchestrationService`
> (see above), drop the matching `sp.GetRequiredService<IOptions<RedisProjectionOptions>>()` at line 71 in
> the same edit — the factory arg order must match the ctor exactly.

---

### Config removal (L2PREFIX-01) — D-05/D-06/D-07

**`RedisProjectionOptions.cs` (D-05):** remove the `KeyPrefix` property (lines 14-19); keep
`ProcessorKeyTtlDays` + `Serialization`. Update the XML doc (lines 5-11) which names `KeyPrefix`.

**`appsettings.json` both (D-06):** remove the `"KeyPrefix": "skp:",` line from the `"Redis"` section.
- `src/BaseApi.Service/appsettings.json:27`
- `src/Orchestrator/appsettings.json:21` (whose `"Redis"` section is `{ "KeyPrefix": "skp:" }` only — the
  whole `"Redis"` section can go since `ConnectionStrings:Redis` carries the connection).

**`OrchestratorRedisOptions.cs` (D-07):** this record's ONLY field is `KeyPrefix`. Since the reader now calls
`OrchestratorL2Keys.Root(wf)` with no prefix, the record loses its purpose — delete the file and remove its
registration (`Program.cs:19-20`) + ctor injections (`StartOrchestrationConsumer.cs:24,33`,
`StopOrchestrationConsumer.cs:20,29`).

**`Program.cs` (D-07):** delete lines 18-20 (`AddSingleton(new OrchestratorRedisOptions(...))`) and the
`using Orchestrator.Messaging;` if it becomes unused.

**`StartOrchestrationConsumer.cs` / `StopOrchestrationConsumer.cs` (D-07):** drop the
`OrchestratorRedisOptions options` ctor param (line 24 / 20) and change the GET call from
`OrchestratorL2Keys.Root(options.KeyPrefix, workflowId)` (line 33 / 29) to `OrchestratorL2Keys.Root(workflowId)`.

**Acceptance grep (D-05):** `grep src/` shows zero `KeyPrefix` reads feeding key construction.

---

### NEW TEST: `tests/.../Features/Orchestration/ProcessorLivenessFacts.cs` (test) — D-24 (PROC-LIVE-01)

**HTTP 204/422 analog:** `SchemaEdgeFacts.cs` (full) — uses `HarnessWebAppFactory` (in-memory MassTransit,
so `/start` returns without a publish-timeout hang), HTTP-seeds Schema→Processor→Step→Workflow, POSTs
`/api/v1/orchestration/start`, asserts `HttpStatusCode.NoContent` (204) / `UnprocessableEntity` (422) +
`errors.gate` + `errors.offending`.

**Direct L2-seed analog:** `StopCleanupFacts.cs:36-61` — seeds L2 keys directly via
`_factory.RedisMultiplexer.GetDatabase()` + `db.StringSetAsync(key, JsonSerializer.Serialize(projection))`.
This simulates external processor self-registration (the write path is out of scope — Deferred).

**read_first:** `SchemaEdgeFacts.cs` (full), `StopCleanupFacts.cs:34-61`, `GateNoWriteFacts.cs:60-70`
(`Assert422Gate` helper + ASVS no-leak), `ProcessorProjection.cs`, `LivenessProjection.cs`.

**Three facts required (D-24 / SPEC acceptance):**
1. **204 all-live** — seed each participating processor's `skp:{procId}` with
   `new ProcessorProjection(null, null, new LivenessProjection(now, interval, "Live"))` where
   `now + interval*2 > now` (a positive interval) → `/start` returns 204.
2. **422 absent** — do NOT seed one participating processor's key → 422 with `errors.gate=="processorLiveness"`,
   `errors.offending.reason=="absent"`, `offending.procId==<that proc>`.
3. **422 stale** — seed with a timestamp far in the past (or `interval=0`) so `timestamp + interval*2 <= now`
   → 422 with `reason=="stale"`.

**Critical seeding detail:** the L2 processor key the validator reads is `RedisProjectionKeys.Processor(procId)`
(= `{prefix}{procId:D}`). Because the writer no longer creates processor keys (D-09), the test MUST seed them
itself BEFORE calling `/start`. Use the HTTP-seeded processor's returned `Id` as `procId`, and seed the key
under the live test prefix (`_factory.RedisKeyPrefix` while it still exists pre-D-23, OR `"skp:"` const
post-D-23 — see Shared Patterns / test isolation below). Compute `now` from the same clock the validator
uses; the service uses real `TimeProvider.System` in integration, so seed `DateTime.UtcNow`.

> **Caveat — assertion gate name:** the new gate string is `"processorLiveness"` (D-17). The 422 facts assert
> `errors.gate == "processorLiveness"`, mirroring `SchemaEdgeFacts.cs:137`.

---

### Existing-test golden/shape updates — D-24

| Test file | Change | Anchor |
|-----------|--------|--------|
| `L2ProjectionKeysTests.cs` | New parameterless signatures (`Root(Workflow)` etc.) + add a `ParentIndex()` golden returning `"skp:"`. Drop the per-class-prefix fact (line 49-57) since prefix is now a const. | lines 21-57 |
| `RedisProjectionWriterFacts.cs` | Assert ZERO `{prefix}{procId}` keys post-Start; assert `SMEMBERS` of `ParentIndex()` contains the `wf.Id:D`. Remove/adjust the `ProcessorProjection_Ttl` fact (lines 204-238) and the processor-keyspace asserts (lines 184-199). Keys now use no-prefix builders. | lines 120-238 |
| `StopCleanupFacts.cs` | Add an assert that `SREM` removed the wf from `ParentIndex()`; seed the parent-index SET in setup so the SREM is observable. Keys use no-prefix builders. | lines 36-92 |
| `GateNoWriteFacts.cs` | Add a `processorLiveness` arm (422 + zero L2 keys) mirroring the three existing gate arms (lines 190-264) using `Assert422Gate(resp, "processorLiveness", ct)`. | lines 60-264 |
| `RedisProjectionOptionsBindingFacts.cs` | Drop the two `KeyPrefix` binding facts (lines 47-53, 63-72); keep the `Serialization.JsonOptions` facts. | lines 47-72 |
| `AppsettingsFacts.cs` | Drop `Appsettings_Has_Redis_Section_KeyPrefix_skp` (lines 50-54). Keep the connection-string + abortConnect facts. | lines 50-54 |

---

## Shared Patterns

### Redis GET + deserialize (validator + cleanup)
**Source:** `RedisL2Cleanup.cs:43-46` / `RedisProjectionWriter.cs` / `StartOrchestrationConsumer.cs:33-43`
**Apply to:** `ProcessorLivenessValidator`
```csharp
var raw = await db.StringGetAsync(key);
if (raw.IsNullOrEmpty) { /* absent branch */ }
var projection = JsonSerializer.Deserialize<ProcessorProjection>(raw!)!;
```

### TimeProvider `now`
**Source:** `RedisProjectionWriter.cs:60` — `var now = _clock.GetUtcNow().UtcDateTime;`
**Apply to:** `ProcessorLivenessValidator` (injected `TimeProvider`, D-14).

### OBSERV-REDIS-03 op-tagging
**Source:** `OrchestrationService.cs:131-135,148-156` — `catch (RedisException ex) { ex.Data["redisOp"] = "UpsertAsync"; throw; }`
**Apply to:** the `OrchestrationService` call site of `ProcessorLivenessValidator.ValidateAsync` (D-15 — same `redisOp` tag).

### camelCase JSON via `[property: JsonPropertyName]` on positional records (load-bearing)
**Source:** `ProcessorProjection.cs:12-15`, `LivenessProjection.cs:11-14`
**Apply to:** any new/seeded projection deserialize — the existing records are reused as-is (no shape change
this phase). Tests that hand-build JSON for seeding should serialize the record (not hand-write JSON) so the
camelCase names stay correct, exactly as `StopCleanupFacts.cs:50,60` does.

### HARDEN-03 single-source-of-truth key construction
**Source:** `L2ProjectionKeys.cs` (authoritative) ← `RedisProjectionKeys.cs` (writer forwarder) +
`OrchestratorL2Keys.cs` (reader forwarder).
**Apply to:** the new `Prefix` const + `ParentIndex()` builder + all no-prefix signatures — NO hand-copied
interpolation literals outside `L2ProjectionKeys` (D-01..D-04). The new `SADD`/`SREM` member format
(`wf.Id.ToString("D")`) is the one literal not in the builder — keep it consistent with `Root`'s `:D`.

### Validation gate ordering (LOCKED Phase 14)
**Source:** `OrchestrationService.cs:141-144` — cycle → schemaEdge → payloadConfigSchema.
**Apply to:** the new async liveness gate slots AFTER this trio, BEFORE `UpsertAsync` (D-15).

### Test isolation rewrite (D-20..D-23) — applies to ALL parent-index-touching tests
**Source today:** `RedisFixture.cs:40` (`KeyPrefix = "test:cls-{Guid:N}:"`), `RedisFixture.cs:52-94`
(SCAN MATCH `{KeyPrefix}*` cleanup), `Phase8WebAppFactory.cs:193` (`["Redis:KeyPrefix"] = RedisKeyPrefix`).
**Target (D-20..D-23):**
- Hardcoding the prefix removes the per-class isolation seam — tests now run on the **prod keyspace** (`"skp:"`),
  DB0, so the triple-SHA gate scans the same keyspace.
- `RedisFixture` no longer carries a unique prefix; `Phase8WebAppFactory` no longer injects `Redis:KeyPrefix`
  (delete line 193 + the `RedisKeyPrefix` property at lines 116-117). **Caveat:** `RedisKeyPrefix` is read by
  `RedisProjectionWriterFacts`, `StopCleanupFacts`, `GateNoWriteFacts` — those call sites must switch to the
  `"skp:"` const (or `L2ProjectionKeys.Prefix`) in the same change.
- Per-class cleanup changes from `SCAN MATCH {prefix}*` to **deleting the specific known GUID keys the test
  created** (D-23) — a prefix scan on `skp:*` would now catch sibling classes' keys.
- Tests that touch the shared `ParentIndex()` SET go into a **single non-parallel xUnit collection** (D-22);
  each `SREM`s its own workflow ids so the index is empty between tests.
- `RedisFixtureFacts.cs` (lines 16-49) asserts the `test:cls-{Guid:N}:` prefix — those facts (and the
  residual-key fail-loud fact) must be rewritten to the known-key cleanup model (D-23).

---

## No Analog Found

None. Every file is either a modification of existing code or a new file whose shape is fully covered by a
same-folder sibling (validator shape, exception factory, writer Redis-IO, test 204/422 + L2-seed).

---

## Metadata

**Analog search scope:** `src/Messaging.Contracts/Projections`, `src/BaseApi.Service/Features/Orchestration`
(+ `Validation`, `Projection`, `Loading`), `src/BaseApi.Core/Configuration`, `src/Orchestrator`
(`Messaging`, `Consumers`, `Program.cs`), `tests/BaseApi.Tests/Composition`,
`tests/BaseApi.Tests/Features/Orchestration` (+ `Projection`).
**Files scanned:** ~24 read in full or targeted.
**Pattern extraction date:** 2026-05-31
