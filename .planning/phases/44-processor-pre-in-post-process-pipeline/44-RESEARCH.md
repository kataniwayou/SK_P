# Phase 44: Processor Pre/In/Post-Process Pipeline - Research

**Researched:** 2026-06-08
**Domain:** C# / .NET 8 backend messaging + resilience (MassTransit, Redis/StackExchange.Redis, Json.Schema). NO UI / AI / ORM.
**Confidence:** HIGH — every claim is anchored to a file in this repo (verified this session) or to the LOCKED design doc.

## Summary

Phase 44 rewrites the **straight-through** `EntryStepDispatchConsumer` (`src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs`) into an explicit **Pre → In → Post** pipeline with a `finally` end-delete, replacing the single `ProcessAsync(string,string,ct) → IReadOnlyList<ProcessResult>` seam with a new author seam `ProcessAsync(string validatedData, string payload, ct) → List<ProcessItem>`. It adds a `ProcessStatusException` family, a shared `RetryLoop` helper (N immediate attempts, shared `Retry:Limit`), and emits five Phase-43 Keeper messages (`KeeperReinject`/`KeeperUpdate`/`KeeperInject`/`KeeperDelete`/`KeeperCleanup`) while routing send-exhaustion give-ups to the existing `_error` bus queue. It is a clean breaking v4.0.0 change — `ProcessResult.cs` and the old seam are deleted, `Processor.Sample` is migrated.

**All Phase-43 contracts this phase consumes already exist and are GREEN** (verified: `StepCompleted/Failed/Cancelled/Processing`, `IStepResult`, `SourceStep.IsSource`, all five `Keeper*` records + `IKeeperRecoverable`, `L2ProjectionKeys.ExecutionData`/`CompositeBackup`, `KeeperQueues`). The processor does **not** write the composite backup itself — it emits `KeeperUpdate{…, ValidatedData}` and the Phase-46 keeper does the write (Model B, design §93 + KeeperUpdate.cs:11). Phase 44 only *emits* Keeper messages.

The single most load-bearing reconciliation point (D-09): `UseMessageRetry(r => r.Immediate(retryLimit))` already exists at the runtime bind in `ProcessorStartupOrchestrator.cs:174`, reading `RetryOptions.Limit` from the `"Retry"` config section. The new in-code `RetryLoop` must wrap **L2 ops and sends** so those are NOT also retried by `UseMessageRetry` re-running `Consume` — see Pitfall 1.

**Primary recommendation:** Implement Pre/In/Post as a **dedicated `ProcessorPipeline` runner class** (constructor-injected the same collaborators the consumer has today), with the consumer reduced to a thin `Consume` that increments the metric and delegates. This makes each terminal route and the `finally` semantics unit-testable in isolation without a MassTransit harness — the existing `DispatchTestKit` (`tests/BaseApi.Tests/Processor/DispatchTestKit.cs`) already supplies the fakes needed.

## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** In-Process author method keeps the name **`ProcessAsync`** (new signature). Abstract seam a concrete `Processor.<Purpose>` overrides; framework owns Pre, Post, end-delete, retry, all sends.
- **D-02:** `validatedData` and `payload` passed to the author as **raw `string` (JSON)** — author deserializes both. No framework-side generic typing / pre-parsing.
- **D-03:** In-Process return-item type is a **new record `ProcessItem(ProcessOutcome Result, string Data, Guid ExecutionId)`** where `ProcessOutcome = { Completed, Failed }`. Author **constructs it directly** and **mints `ExecutionId` itself** (new GUID per item). `ProcessAsync` returns `List<ProcessItem>` (empty list = no continuation, sends nothing).
- **D-04:** Author signals a batch-aborting status via an **abstract base `ProcessStatusException(status)` with three concrete subclasses** — `ProcessingException`, `FailedException`, `CancelledException`. Framework does `catch (ProcessStatusException e)` → map `e.Status` to matching `Step*` record; a separate `catch (Exception)` ⇒ `failed`. Any thrown status aborts the whole batch (no Post-Process), sends exactly one orchestrator result, then runs end-delete.
- **D-05:** All three status exceptions accept an **author-supplied message** flowing into the corresponding `Step*` record's message field where one exists (`StepFailed.ErrorMessage`, `StepCancelled.CancellationMessage`). If `StepProcessing` exposes no message field on the wire, the processing message is captured for logging only — but the author-facing API is uniform (all three take a message).
- **D-06:** **Clean break.** Delete the old `ProcessAsync(string inputData, string config) → IReadOnlyList<ProcessResult>` signature and the output-only `ProcessResult` record (`src/BaseProcessor.Core/Processing/ProcessResult.cs`). No compatibility adapter. Internal `ExecuteAsync` forwarder retypes to the new seam.
- **D-07:** **Migrate `Processor.Sample` in-phase** to the new seam (compile-forced once old seam deleted). Doubles as the worked example.
- **D-08:** **Shared reusable retry helper** (e.g. `RetryLoop.ExecuteAsync(op, limit, ct)`) wrapping each L2 op and each send. N immediate attempts (no backoff), surfaces exhaustion so the pipeline routes: `infra(READ)`→`REINJECT`, output-write exhaustion→`failed (infra)`→`INJECT`, end-delete exhaustion→`DELETE`, send exhaustion→bus error queue. One place for A3 semantics.
- **D-09:** Introduce a **`Retry:Limit`** config key (shared, A3) replacing hardcoded `Immediate(3)`. Bus-level `UseMessageRetry` reconciled with the in-code loop so retries are not doubly applied to L2/send ops.
- **D-10:** On in-code retry **exhaustion of a processor send**, Phase 44 lets the exception **propagate so MassTransit dead-letters to the existing bus error queue** (`_error`). `_DLQ1` rename/consolidation is **Phase 47** — do NOT build it here.

### Claude's Discretion
- **Pipeline code decomposition** — private methods on the consumer vs a dedicated pipeline-runner class. Favor testability.
- **Schema-validation reuse** — reuse `ProcessorJsonSchemaValidator` for both Pre input-schema and Post output-schema checks.
- **`RetryLoop` helper placement/signature** — exact namespace, sync/async overloads, exhaustion-surfacing (return flag vs sentinel vs typed result) are Claude's to design within D-08.
- **Keeper message construction** — exact id-set wiring for `REINJECT`/`UPDATE`/`INJECT`/`DELETE`/`CLEANUP` follows the design doc §Processor round trip verbatim.

### Deferred Ideas (OUT OF SCOPE)
- **`_DLQ1` consolidation** (rename/consolidate terminal give-up queue, remove `keeper-dlq`) — Phase 47 (A4). Phase 44 throws to the existing `_error` bus queue on send-exhaustion.
- **Keeper recovery consumer** that *processes* UPDATE/REINJECT/INJECT/DELETE/CLEANUP — Phase 46. Phase 44 only emits.
- **Keeper BIT health gate + global pause-all/resume-all** — Phase 45.

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| PIPE-01 | `BaseProcessor` runs explicit Pre→In→Post per dispatch, replacing single `ProcessAsync` seam | §Architecture Patterns (pipeline-runner decomposition); current consumer at `EntryStepDispatchConsumer.cs` is the straight-through baseline to rewrite |
| PIPE-02 | Pre reads `L2[entryId]` w/ bounded retry; read failure (Redis exception OR absent/empty) after exhaustion → `infra(READ)`; `Guid.Empty` skips read | §Pipeline decomposition (Pre); `SourceStep.IsSource` (SourceStep.cs:8); read-skip already in current consumer line 82; absent/empty detection = `RedisValue.IsNullOrEmpty` (line 85) but now counts as read-failure not business-Failed |
| PIPE-03 | Pre validates read data vs input schema; validation failure → business `Failed` (not infra) | `ProcessorJsonSchemaValidator.TryValidate(context.InputDefinition, data, out errors)` reused verbatim (validator file lines 30-84); current consumer line 108 |
| PIPE-04 | In-Process author abstract `(validatedData, payload) → List<item>`, each `{completed\|failed, data, author-minted executionId}` | §Status-exception + ProcessItem; new `BaseProcessor.ProcessAsync` seam shape (D-01/D-02/D-03) |
| PIPE-05 | In wrapped in try/catch; author may throw status exception; any exception sends one result (unexpected ⇒ failed) and aborts batch | §Status-exception family; current consumer catch pattern lines 119-134 generalizes |
| PIPE-06 | Post per `completed` item: output-validate, generate GUID entryId, write `L2[entryId]` (no TTL) w/ bounded retry (exhaust → failed(infra)); on success send Keeper `CLEANUP` | §Pipeline (Post); `L2ProjectionKeys.ExecutionData` (no TTL — drop the `expiry:` arg present today at consumer line 171); `KeeperCleanup` record |
| PIPE-07 | Post routes each item: not-infra → orchestrator result (completed carries entryId+executionId); infra → Keeper `INJECT`; N completed → N results | §Keeper message construction; `StepCompleted` carries EntryId+ExecutionId (StepCompleted.cs:11); `KeeperInject` record |
| PIPE-08 | End-delete (`finally` over read-succeeded paths) deletes `L2[entryId]` w/ bounded retry; exhaust → Keeper `DELETE` | §Pipeline (End-delete); `KeyDeleteAsync` via RetryLoop; `KeeperDelete` record |
| RESIL-01 | Every L2 op + every send wrapped in bounded retry loop (N immediate, shared `Retry:Limit`) | §RetryLoop helper; `RetryOptions.Limit` (RetryOptions.cs:10) already bound from `"Retry"` (AddBaseProcessor line 89) |

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Consume `EntryStepDispatch`, increment metric | Processor consumer (`EntryStepDispatchConsumer`) | — | Entry point; MassTransit `IConsumer<T>` (unchanged ctor + metric at line 66) |
| Pre/In/Post orchestration + terminal routing | Processor framework (new `ProcessorPipeline`) | — | Framework owns Pre/Post/end-delete/retry/sends (D-01); the only author seam is In-Process |
| In-Process transform | Concrete `Processor.<Purpose>` (author) | — | The sole overridable seam; author deserializes, mints ExecutionId, may throw status |
| L2 read/write/delete | StackExchange.Redis (`IConnectionMultiplexer`/`IDatabase`) | RetryLoop | Soft-dep Redis injected today (consumer ctor line 47); wrapped per-op by RetryLoop |
| Schema validation (input + output) | `ProcessorJsonSchemaValidator` (static) | — | SSRF-locked Json.Schema; reused for BOTH Pre and Post (Claude's discretion → confirmed reuse) |
| Orchestrator result send | MassTransit `ISendEndpointProvider` → `queue:orchestrator-result` | RetryLoop | Current `SendResult` (consumer lines 201-208) generalizes; wrapped by RetryLoop |
| Keeper message emission (5 states) | MassTransit `ISendEndpointProvider` → `queue:keeper-recovery` | RetryLoop | New send sites; `KeeperQueues.Recovery = "keeper-recovery"` (KeeperQueues.cs:19) |
| Send-exhaustion dead-letter | MassTransit `UseMessageRetry` + `_error` queue | — | D-10: propagate; existing `ProcessorStartupOrchestrator.cs:174` bind owns this |

## Standard Stack

This is an internal refactor — **no new external packages**. All libraries are already referenced and version-pinned via `Directory.Packages.props`. Verified by file inspection (no `npm`/registry equivalent — these are NuGet, already in the solution and GREEN).

### Core
| Library | Source | Purpose | Why Standard |
|---------|--------|---------|--------------|
| MassTransit | already referenced (MT 8.5.5 per `36-03-SUMMARY`) | `IConsumer<EntryStepDispatch>`, `ISendEndpointProvider`, `UseMessageRetry(Immediate(N))`, `NewId.NextGuid()` | The project's bus throughout v3.4.0+ |
| StackExchange.Redis | already referenced (SE.Redis 2.13.x per DispatchTestKit comments) | `IConnectionMultiplexer` → `IDatabase.StringGetAsync`/`StringSetAsync`/`KeyDeleteAsync` | The L2 store throughout |
| Json.Schema (`JsonSchema.Net` 9.2.1) | already referenced | input/output schema validation via `ProcessorJsonSchemaValidator` | SSRF-locked, ported into BaseProcessor.Core (validator file header) |
| `Messaging.Contracts` (this solution) | project ref | Step* records, Keeper* records, `L2ProjectionKeys`, `SourceStep`, `RetryOptions` | Phase-43 contracts, all GREEN |

### Supporting
| Library | Source | Purpose | When to Use |
|---------|--------|---------|-------------|
| `Microsoft.Extensions.Options` | referenced | `IOptions<RetryOptions>`, `IOptions<ProcessorLivenessOptions>` | RetryLoop limit + (if kept) TTL — note Post-write drops TTL per design |
| `Microsoft.Extensions.Logging` | referenced | execution-scope logs (existing `BeginScope` pattern at consumer lines 160-164) | preserve LOG-04 scope on the per-item write |
| xunit.v3 3.2.2 + NSubstitute | test project | hermetic facts (the existing `DispatchTestKit` uses NSubstitute fakes) | Wave 0 / unit tests for each terminal |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Custom `RetryLoop` (D-08) | MassTransit `UseMessageRetry` for everything | Rejected by design: `UseMessageRetry` retries the *whole Consume*, not a single op — it cannot give per-op terminal routing (REINJECT vs INJECT vs DELETE). The in-code loop is mandated. |
| Polly | hand-rolled bounded loop | Polly not referenced; D-08 wants "N immediate attempts, no backoff" — a trivial `for` loop. Adding Polly is unjustified scope. |

**Installation:** none — no package changes.

**Version verification:** N/A (no new packages). The existing pins are GREEN as of Phase 43 close.

## Architecture Patterns

### System Architecture Diagram

```
EntryStepDispatch (from orchestrator, queue:{processorId:D})
   │  [EntryStepDispatchConsumer.Consume — increments metric, delegates]
   ▼
┌─────────────────────────────── ProcessorPipeline.RunAsync ───────────────────────────────┐
│                                                                                            │
│  PRE ──────────────────────────────────────────────────────────────────────────────────  │
│   SourceStep.IsSource(entryId)? ──yes──► validatedData = ""  (skip read, skip end-delete)  │
│        │ no                                                                                 │
│        ▼                                                                                    │
│   RetryLoop( read L2[ExecutionData(entryId)] )                                              │
│        │ Redis exception OR absent/empty key, exhausted                                     │
│        ├──────────────────────────────► infra(READ): Send KeeperReinject ──► END (no       │
│        │                                 end-delete; input left intact)                     │
│        │ success                                                                            │
│        ▼                                                                                    │
│   TryValidate(InputDefinition, data) ── fail ──► Send StepFailed (business) ──► [END-DELETE]│
│        │ ok                                                                                 │
│  IN ───┼──────────────────────────────────────────────────────────────────────────────── │
│        ▼   try { items = ProcessAsync(validatedData, payload, ct) }                         │
│            catch ProcessStatusException e  ► Send Step{Failed|Cancelled|Processing} ──┐     │
│            catch Exception                 ► Send StepFailed                          ─┤     │
│                                              (abort batch, no Post)                    ├──►  │
│        │ normal return (List<ProcessItem>)                                            │     │
│  POST ─┼─── for each item, in order ───────────────────────────────────────────────  │     │
│        ▼                                                                               │     │
│    item.Result==Completed?                                                             │     │
│      ├─ validate data vs OutputDefinition ─ fail ─► item = business failed             │     │
│      ├─ Send KeeperUpdate{…, ValidatedData=data}   (keeper writes composite backup)    │     │
│      ├─ entryId = NewId.NextGuid()                                                     │     │
│      ├─ RetryLoop( write L2[ExecutionData(entryId)], NO TTL )                          │     │
│      │     exhausted ─► item = failed(infra)                                           │     │
│      │     success   ─► Send KeeperCleanup  (delete now-redundant composite backup)    │     │
│      ▼                                                                                 │     │
│    route: not-infra (completed ∪ business-failed) ─► Send StepCompleted/StepFailed     │     │
│           infra ─► Send KeeperInject                                                   │     │
│                                                                                        ▼     │
│  END-DELETE  (finally — every read-succeeded path: happy, pre business-fail, In-exception)  │
│    RetryLoop( delete L2[ExecutionData(entryId)] )  exhausted ─► Send KeeperDelete            │
│    (skipped only on infra(READ)/REINJECT and Guid.Empty source steps)                       │
└────────────────────────────────────────────────────────────────────────────────────────────┘
   every Send / every L2 op is RetryLoop-wrapped; a SEND that exhausts ─► throw ─► UseMessageRetry ─► _error
```

### Recommended Project Structure
```
src/BaseProcessor.Core/Processing/
├── BaseProcessor.cs              # REWRITE: new abstract ProcessAsync(validatedData, payload, ct) → List<ProcessItem>; internal ExecuteAsync forwarder
├── ProcessItem.cs               # ADD: record ProcessItem(ProcessOutcome Result, string Data, Guid ExecutionId)
├── ProcessOutcome.cs            # ADD: enum { Completed, Failed }
├── ProcessStatusException.cs    # ADD: abstract base + ProcessingException / FailedException / CancelledException
├── ProcessorPipeline.cs         # ADD: the Pre/In/Post/end-delete runner (recommended decomposition)
├── EntryStepDispatchConsumer.cs # REWRITE: thin Consume → metric + pipeline.RunAsync
└── ProcessResult.cs             # DELETE (D-06)

src/BaseProcessor.Core/Resilience/  (or Processing/)
└── RetryLoop.cs                 # ADD: static ExecuteAsync(op, limit, ct) — N immediate attempts, surfaces exhaustion

src/Processor.Sample/
└── SampleProcessor.cs           # MIGRATE: override new ProcessAsync → List<ProcessItem>, show one status-exception path
```

### Pattern 1: Pipeline-runner class (recommended over private methods)
**What:** A `ProcessorPipeline` class taking the same collaborators the consumer has today (`IConnectionMultiplexer`, `IProcessorContext`, `BaseProcessor`, `ISendEndpointProvider`, `IOptions<RetryOptions>`, `ILogger`). The consumer keeps only the metric increment + `await pipeline.RunAsync(dispatch, ct)`.
**When to use:** Always for this phase — each terminal (REINJECT/INJECT/DELETE/CLEANUP/UPDATE, the four exhaustion routes, the `Guid.Empty` skip, the `finally`) is independently unit-testable without a MassTransit harness. The existing `DispatchTestKit` already provides the fakes (`FakeProcessor`, `CapturingSendProvider`, `PresentReadWriteFaultL2`).
**Source:** anchored to the current consumer collaborators — `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:46-53`.

### Pattern 2: The new author seam (D-01/D-02/D-03)
```csharp
// Source: derived from current BaseProcessor.cs + CONTEXT D-01/D-02/D-03 (ASSUMED shape — confirm signatures at plan)
public enum ProcessOutcome { Completed, Failed }

public sealed record ProcessItem(ProcessOutcome Result, string Data, Guid ExecutionId);

public abstract class BaseProcessor
{
    // Author overrides ONLY this. Deserializes both strings; mints ExecutionId per item; may throw a status.
    protected abstract Task<List<ProcessItem>> ProcessAsync(
        string validatedData, string payload, CancellationToken ct);

    // Internal forwarder (same-assembly pipeline calls this — current ExecuteAsync at BaseProcessor.cs:31 retyped)
    internal Task<List<ProcessItem>> ExecuteAsync(string validatedData, string payload, CancellationToken ct)
        => ProcessAsync(validatedData, payload, ct);
}
```

### Pattern 3: Status-exception family (D-04/D-05)
```csharp
// Source: CONTEXT D-04/D-05. StepProcessing has NO wire message field (StepProcessing.cs:6-11) — confirmed:
// the processing message is logged only; the author API stays uniform (all three ctors take a message).
public abstract class ProcessStatusException(string message) : Exception(message)
{
    public abstract StepStatus Status { get; }   // or map directly in the catch — discretion
}
public sealed class ProcessingException(string message) : ProcessStatusException(message) { … }
public sealed class FailedException(string message)     : ProcessStatusException(message) { … }
public sealed class CancelledException(string message)  : ProcessStatusException(message) { … }

// In the pipeline:
try { items = await processor.ExecuteAsync(validatedData, payload, ct); }
catch (ProcessStatusException e)
{
    IStepResult result = e switch
    {
        FailedException    => BuildFailed(d, e.Message),          // StepFailed.ErrorMessage = e.Message
        CancelledException => BuildCancelled(d, e.Message),       // StepCancelled.CancellationMessage = e.Message
        ProcessingException => BuildProcessing(d),                // message logged only (no wire field)
        _ => BuildFailed(d, e.Message),
    };
    await SendViaRetryLoop(result);   // exactly ONE result; abort batch (no Post)
    // fall through to end-delete (finally)
}
catch (Exception ex) { await SendViaRetryLoop(BuildFailed(d, ex.Message)); /* unexpected ⇒ failed */ }
```

### Pattern 4: RetryLoop helper (D-08 — exhaustion as typed result, recommended)
```csharp
// Source: CONTEXT D-08; A3 "N immediate attempts, no backoff". Recommended: return a struct so the caller
// branches on exhaustion explicitly (clearer than bool-out or sentinel). Limit from RetryOptions.Limit.
public static class RetryLoop
{
    // Throwing op: succeeds → value; exhausts → returns the last exception so the caller routes the terminal.
    public static async Task<RetryOutcome<T>> ExecuteAsync<T>(
        Func<Task<T>> op, int limit, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < Math.Max(1, limit); attempt++)
        {
            try { return RetryOutcome<T>.Ok(await op()); }
            catch (Exception ex) { last = ex; }   // immediate retry, no delay (A3)
        }
        return RetryOutcome<T>.Exhausted(last!);
    }
}
public readonly record struct RetryOutcome<T>(bool Succeeded, T? Value, Exception? Error) { … }
```
**Note on READ failure (A2):** for the Pre read, "absent/empty key" is NOT an exception — it's `RedisValue.IsNullOrEmpty == true`. The read op must therefore *throw* an internal sentinel (or the loop must treat absent/empty as a retryable failure) so exhaustion routes to `infra(READ)`. Cleanest: the read closure throws `KeyAbsentException` when `IsNullOrEmpty`, so both a Redis exception and absent/empty unify into one exhaustion path. Design §47 + §17 + A2 explicitly: "failure = a Redis exception **or an absent/empty key**".

### Anti-Patterns to Avoid
- **Double retry (D-09 / Pitfall 1):** wrapping a Send in `RetryLoop` AND letting `UseMessageRetry` re-run the whole `Consume` means a send is attempted `limit × limit` times and re-sends already-sent results. The in-code loop owns L2 ops + sends; `UseMessageRetry` is the *outer dead-letter mechanism* that fires only when the in-code send-exhaustion **propagates** (D-10). Do not retry the same op at both layers.
- **Inline `== Guid.Empty`:** always route the source-step skip through `SourceStep.IsSource(entryId)` (SourceStep.cs:8) — the single canonical predicate (already honored at consumer line 82).
- **Writing the composite backup in the processor:** the processor emits `KeeperUpdate{ValidatedData}` only; the keeper writes `L2[CompositeBackup]` (Model B, design §93). Do not call `L2ProjectionKeys.CompositeBackup` write from the pipeline.
- **Keeping the TTL on the data write:** the current consumer writes with `expiry: TtlSeconds` (line 171). Design §16/§64 says `L2[entryId]` data key is **no TTL** — drop the expiry arg in Post.
- **Treating absent/empty input as business-Failed (old behavior):** current consumer line 86-97 returns a business `Failed` on absent input for a required-input step. Phase 44 changes this: absent/empty after retry exhaustion is `infra(READ)` → `REINJECT` (A2), NOT business-Failed. (Input-schema *validation* failure stays business-Failed.)

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Schema validation | a JSON validator / `JsonDocument` field-walk | `ProcessorJsonSchemaValidator.TryValidate(def, data, out errors)` | SSRF-locked, dialect-pinned, error-flattening already correct (validator file) |
| Source-step detection | `entryId == Guid.Empty` | `SourceStep.IsSource(entryId)` | single canonical predicate (SourceStep.cs) — T-43-06 |
| L2 key strings | string interpolation of `skp:data:{id}` | `L2ProjectionKeys.ExecutionData(entryId)` | single source of truth (L2ProjectionKeys.cs:42); writer/reader cannot desync |
| GUID minting | `Guid.NewGuid()` for the data key | `NewId.NextGuid()` (MassTransit) | sequential/COMB GUIDs — already used at consumer line 152 (less index fragmentation) |
| Bus dead-letter on send-exhaustion | a custom DLQ writer | propagate → `UseMessageRetry` → `_error` (D-10) | the `_DLQ1` consolidation is Phase 47; existing bind at `ProcessorStartupOrchestrator.cs:174` already routes to `_error` |
| Retry config | a literal `3` | `RetryOptions.Limit` from `"Retry"` (RetryOptions.cs) | already bound in `AddBaseProcessor` line 89; D-09 shares it |

**Key insight:** Almost every primitive this pipeline needs already exists and is GREEN. Phase 44 is **composition + routing**, not new infrastructure. The one genuinely new mechanism is the `RetryLoop` (a ~15-line bounded loop) and the exception family (three trivial subclasses).

## Runtime State Inventory

> This is a code rewrite of an in-process pipeline, NOT a rename/migration. No stored keys, OS registrations, or secrets carry a renamed string. The relevant "state" is the wire/L2 contract shape, which Phase 43 already reshaped (GREEN).

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data (Redis L2) | `L2[ExecutionData(entryId)]` data key — Phase 44 changes the **write** to drop TTL (was `expiry: TtlSeconds` at consumer line 171) and adds end-delete | Code edit only. No migration of existing keys (close-gate teardown scans clean; data keys are transient per-execution). |
| Live service config | None — no n8n/Datadog/external config embeds this | None — verified: this is in-process consumer logic only. |
| OS-registered state | None | None — no Task Scheduler / pm2 / systemd registration of pipeline internals. |
| Secrets / env vars | None changed — `Retry:Limit` reads an existing `"Retry"` section (RetryOptions bound at AddBaseProcessor:89; appsettings may need a `Retry:Limit` entry if absent) | Verify `appsettings.json` of `Processor.Sample` has a `"Retry"` section, else defaults (Immediate(3)) apply — code edit / appsettings check. |
| Build artifacts | `Processor.Sample` recompiles against the new seam (D-07); deleting `ProcessResult.cs` compile-forces the migration | Rebuild solution; the old `ProcessResult` references break and must be removed. |

**Composite backup key:** the processor never writes/deletes it (Model B). The keeper (Phase 46) owns `L2[CompositeBackup]`. Phase 44's only contact is emitting `KeeperUpdate.ValidatedData` and `KeeperCleanup`. Verified: `KeeperUpdate.cs:11`, design §93.

## Common Pitfalls

### Pitfall 1: Double-retrying sends and L2 ops (D-09)
**What goes wrong:** Both the in-code `RetryLoop` and the bus `UseMessageRetry(Immediate(limit))` retry the same operation. A transient send fault is retried `limit` times in-code, then the whole `Consume` is re-run `limit` times by `UseMessageRetry`, re-sending every already-sent result and re-running every L2 op → duplicate effects amplified `limit²`.
**Why it happens:** `UseMessageRetry` wraps the entire `Consume`; the in-code loop wraps one op. They compose multiplicatively unless deliberately layered.
**How to avoid:** In-code `RetryLoop` owns **per-op** retries (L2 read/write/delete, every send). On send-exhaustion, **propagate** (D-10) so `UseMessageRetry` is the *outer dead-letter trigger* that moves the message to `_error` — it should NOT meaningfully re-succeed the same send. Document that the bus retry is now effectively a dead-letter latch for send-exhaustion, not a second retry of L2 ops. Verify the bind at `ProcessorStartupOrchestrator.cs:172-177` and ensure the comment there is updated to reflect the reconciliation.
**Warning signs:** integration test shows N×limit orchestrator results for one dispatch; `_error` receiving messages whose in-code loop already exhausted.

### Pitfall 2: Absent/empty key not unified with Redis exception on READ (A2)
**What goes wrong:** The retry loop only catches exceptions, so an absent/empty key (no exception, just `RedisValue.Null`) falls through as success with empty data, skipping `REINJECT`.
**Why it happens:** `StringGetAsync` returns `RedisValue.Null` for a missing key — not a throw. The current consumer treats `IsNullOrEmpty` as a separate business branch (line 85).
**How to avoid:** The Pre read closure must convert absent/empty into a retryable failure (throw an internal sentinel) so both Redis-exception and absent/empty unify into the `infra(READ)` exhaustion path → `REINJECT`. Design §47 + A2 are explicit.
**Warning signs:** a missing-input dispatch sends a `StepFailed` (old behavior) instead of a `KeeperReinject`.

### Pitfall 3: End-delete `finally` running on the REINJECT / source-step paths
**What goes wrong:** A naive `finally` deletes `L2[entryId]` even when the read failed (REINJECT — input must be left intact for the keeper) or when it's a `Guid.Empty` source step (no key to delete; would build a malformed key).
**Why it happens:** `finally` runs on every exit path unless guarded.
**How to avoid:** Gate end-delete on a "read succeeded" flag that is set only after a successful Pre read (and false for `Guid.Empty` and for `infra(READ)`). Design §72-74: skipped only on `infra(READ)`/`REINJECT` and `Guid.Empty`. A `bool readSucceeded` local set true just before In-Process is the cleanest guard.
**Warning signs:** close-gate sees the data key deleted on a REINJECT path (keeper then can't recover); or a `KeeperDelete` emitted for a source step.

### Pitfall 4: Per-item ExecutionId provenance (D-03 vs LOG-04 scope)
**What goes wrong:** The framework mints `ExecutionId` (as it does today at consumer line 153) but D-03 says the **author** mints it per `ProcessItem`. The `StepCompleted.ExecutionId` and the LOG-04 `BeginScope` value must be the author-minted one from `item.ExecutionId`, not a framework-minted one.
**Why it happens:** The current code mints `executionId = NewId.NextGuid()` in the framework; the new model moves that to the author.
**How to avoid:** Post-Process reads `item.ExecutionId` (author-minted) for both the `StepCompleted`/`KeeperInject`/`KeeperUpdate` build and the nested `BeginScope` (consumer lines 160-164 pattern). The framework still mints the *entryId* (the new data key) per completed item.
**Warning signs:** the scoped ExecutionId in ES logs differs from the one on the sent `StepCompleted`.

### Pitfall 5: Send ordering UPDATE-before-CLEANUP/INJECT (Phase 46 partition contract)
**What goes wrong:** Phase 46's keeper is partitioned by `corr:wf:proc:exec` and relies on `UPDATE` arriving before `CLEANUP`/`INJECT` for the same exec. If Phase 44 emits them out of order on the same exec, the partition can't fix a send-order inversion.
**Why it happens:** Post-Process sends `UPDATE` then (after write) `CLEANUP`, and on infra sends `INJECT`. The per-item ordering must be UPDATE → (write) → CLEANUP, or UPDATE → INJECT.
**How to avoid:** Emit `KeeperUpdate` for a completed item *before* the L2 write, and `KeeperCleanup`/`KeeperInject` *after* — matching design §64-67 step order. The partitioner (Phase 46) then preserves arrival order. Phase 44's job is just to emit in the right source order.
**Warning signs:** N/A in Phase 44 hermetic tests (keeper not built); surfaces in Phase 49 E2E. Document the send order explicitly so Phase 46 can rely on it.

## Code Examples

### Reading L2 input through RetryLoop, unifying absent/empty (Pre)
```csharp
// Source: current consumer EntryStepDispatchConsumer.cs:81-105 + L2ProjectionKeys.cs:42 + A2.
if (SourceStep.IsSource(dispatch.EntryId))
{
    validatedData = string.Empty;            // skip read; skip end-delete; no input validation
}
else
{
    var read = await RetryLoop.ExecuteAsync(async () =>
    {
        var raw = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(dispatch.EntryId));
        if (raw.IsNullOrEmpty) throw new KeyAbsentException();   // unify absent/empty with Redis fault (A2)
        return raw.ToString();
    }, retryLimit, ct);

    if (!read.Succeeded)                      // infra(READ): Redis fault OR absent/empty, exhausted
    {
        await SendViaRetryLoop(BuildReinject(dispatch), ct);   // KeeperReinject; END (no end-delete)
        return;
    }
    readSucceeded = true;                     // gates the finally end-delete
    validatedData = read.Value!;
    if (!ProcessorJsonSchemaValidator.TryValidate(context.InputDefinition, validatedData, out var errs))
    {
        await SendViaRetryLoop(BuildFailed(dispatch, string.Join("; ", errs)), ct);  // business Failed
        return;                               // finally still runs end-delete (read succeeded)
    }
}
```

### Keeper message construction (exact id-sets, per design §51/64-67/75)
```csharp
// Source: KeeperReinject.cs / KeeperUpdate.cs / KeeperInject.cs / KeeperDelete.cs / KeeperCleanup.cs.
// REINJECT {corr, wf, step, proc, exec, entryId}  (design §51)
new KeeperReinject(d.WorkflowId, d.StepId, d.ProcessorId)
    { CorrelationId = d.CorrelationId, ExecutionId = d.ExecutionId, EntryId = d.EntryId };

// UPDATE {corr, wf, step, proc, exec, validatedData}  (design §64) — exec is the ITEM's author-minted id
new KeeperUpdate(d.WorkflowId, d.StepId, d.ProcessorId)
    { CorrelationId = d.CorrelationId, ExecutionId = item.ExecutionId, ValidatedData = item.Data };

// INJECT {corr, wf, step, proc, exec}  (design §67)
new KeeperInject(d.WorkflowId, d.StepId, d.ProcessorId)
    { CorrelationId = d.CorrelationId, ExecutionId = item.ExecutionId };

// DELETE {corr, wf, step, proc, exec, entryId}  (design §75) — entryId = the inbound dispatch data key
new KeeperDelete(d.WorkflowId, d.StepId, d.ProcessorId)
    { CorrelationId = d.CorrelationId, ExecutionId = d.ExecutionId, EntryId = d.EntryId };

// CLEANUP {corr, wf, step, proc, exec}  (design §65) — exec is the ITEM's author-minted id
new KeeperCleanup(d.WorkflowId, d.StepId, d.ProcessorId)
    { CorrelationId = d.CorrelationId, ExecutionId = item.ExecutionId };
```
**[ASSUMED] which `ExecutionId` rides REINJECT/DELETE:** the inbound dispatch's `ExecutionId` (lineage) vs a per-item id. REINJECT/DELETE concern the *inbound* entry (input not yet item-split), so the inbound dispatch `ExecutionId` is the natural choice; UPDATE/INJECT/CLEANUP concern a *completed item*, so the author-minted `item.ExecutionId`. Confirm against the design's id-set notation at plan time (design §11 lists exec on every message; the round-trip §51/64-67/75 does not disambiguate inbound-vs-item exec explicitly).

**Keeper send target:** `queue:{KeeperQueues.Recovery}` = `queue:keeper-recovery` (KeeperQueues.cs:19). **[ASSUMED]** — confirm the processor sends Keeper messages to `Recovery` (the gate-open consumer queue, Phase 46 binds it) rather than `FaultRecovery`. Design §87-88 has the recovery consumer consume the five states; `KeeperQueues.Recovery` is the documented Phase-46 bind target.

### Post-Process per completed item (write no-TTL, CLEANUP after success)
```csharp
// Source: current consumer EntryStepDispatchConsumer.cs:142-181 (TTL dropped per design §16/64).
foreach (var item in items)
{
    var outcome = item.Result;   // Completed | Failed (author)
    if (outcome == ProcessOutcome.Completed
        && !ProcessorJsonSchemaValidator.TryValidate(context.OutputDefinition, item.Data, out _))
        outcome = ProcessOutcome.Failed;          // business failed (not infra)

    if (outcome == ProcessOutcome.Completed)
    {
        await SendViaRetryLoop(BuildUpdate(dispatch, item), ct);   // KeeperUpdate BEFORE write (Pitfall 5)
        var entryId = NewId.NextGuid();
        var write = await RetryLoop.ExecuteAsync(() =>
            db.StringSetAsync(L2ProjectionKeys.ExecutionData(entryId), item.Data), retryLimit, ct); // NO expiry
        if (!write.Succeeded)                       // output-write exhausted → failed(infra)
        {
            await SendViaRetryLoop(BuildInject(dispatch, item), ct);   // KeeperInject (infra route)
            continue;
        }
        await SendViaRetryLoop(BuildCleanup(dispatch, item), ct);      // KeeperCleanup (composite redundant)
        await SendViaRetryLoop(BuildCompleted(dispatch, item.ExecutionId, entryId), ct);  // StepCompleted
    }
    else // business failed item
    {
        await SendViaRetryLoop(BuildFailed(dispatch, "output failed schema validation"), ct); // StepFailed
    }
}
```
**[ASSUMED] business-failed item routing:** the design (§63-67) describes the `completed` branch in detail; a per-item business `failed` (from `ProcessItem(Failed,…)` or output-validation fail) routes to a `StepFailed` orchestrator result and does NOT write L2 / emit UPDATE/CLEANUP/INJECT. Confirm whether a per-item business-failed sends one `StepFailed` per item or aborts. PIPE-07 says "not-infra (`completed` ∪ business-`failed`) → orchestrator result" per item — supports per-item `StepFailed`.

## State of the Art

| Old Approach (current consumer) | New Approach (Phase 44) | Why |
|--------------------------------|-------------------------|-----|
| Single `ProcessAsync(inputData, config) → IReadOnlyList<ProcessResult>` | `ProcessAsync(validatedData, payload, ct) → List<ProcessItem>`, author mints ExecutionId, may throw status | PIPE-01/04/05; richer per-item outcome + abort semantics |
| `ProcessResult(OutputData)` record (framework owns outcome) | `ProcessItem(ProcessOutcome, Data, ExecutionId)` (author owns outcome + id) | D-03; author distinguishes completed vs failed per item |
| Absent input → business `StepFailed` | Absent/empty input (after retry) → `infra(READ)` → `KeeperReinject` | A2 — input may be a transient L2 outage, recoverable |
| L2 write with `expiry: TtlSeconds` | L2 write NO TTL + end-delete `finally` + exhaustion→`KeeperDelete` | design §16/64/72; explicit lifecycle, no TTL leak |
| Single bus `UseMessageRetry(Immediate(3))` for all faults | In-code `RetryLoop(Retry:Limit)` per op + bus retry as send-exhaustion dead-letter | D-08/D-09; per-op terminal routing |
| No Keeper emission | Emits 5 Keeper states (REINJECT/UPDATE/INJECT/DELETE/CLEANUP) | PIPE-02/06/07/08; recovery handoff |

**Deprecated/outdated (delete this phase):**
- `ProcessResult.cs` — deleted (D-06).
- The old two-arg `ProcessAsync` and `ExecuteAsync` forwarder shape (BaseProcessor.cs:22-32) — retyped.
- The `expiry:` arg on the Post L2 write (consumer line 171) — removed (no TTL).
- The "absent input → StepFailed" business branch (consumer lines 86-97) — replaced by REINJECT.

## Validation Architecture

> nyquist_validation is enabled (config.json `workflow.nyquist_validation: true`). This section is REQUIRED.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 3.2.2 (Microsoft.Testing.Platform / MTP) + NSubstitute + MassTransit.Testing (in-memory harness) |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (+ `xunit.runner.json` for parallelism cap) |
| Quick run command | `dotnet test tests\BaseApi.Tests\BaseApi.Tests.csproj --filter-not-trait Category=RealStack` |
| Full suite command | `dotnet test SK_P.sln --filter-not-trait Category=RealStack` (hermetic); RealStack gated to Phase 49 |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| PIPE-02 | `Guid.Empty` skips read (empty validatedData, no end-delete) | unit | `dotnet test … --filter-method *SourceStep_Skip*` | ❌ Wave 0 |
| PIPE-02 | Redis-exception read exhausted → `KeeperReinject`, no end-delete | unit | `--filter-method *ReadFault_Reinject*` | ❌ Wave 0 |
| PIPE-02 | absent/empty key (A2) read exhausted → `KeeperReinject` | unit | `--filter-method *AbsentKey_Reinject*` | ❌ Wave 0 (new behavior; old `DispatchAckSemanticsFacts.BusinessFailure_DoesNotThrow` must be retired/updated) |
| PIPE-03 | input-schema validation fail → `StepFailed` + end-delete runs | unit | `--filter-method *InputInvalid_Failed*` | ❌ Wave 0 |
| PIPE-04 | author returns N `ProcessItem` → N results | unit | `--filter-method *MultiItem*` | ❌ Wave 0 (adapt `DispatchResultSendFacts`) |
| PIPE-05 | `FailedException`/`CancelledException`/`ProcessingException` → matching Step* record, batch aborts | unit | `--filter-method *StatusException*` | ❌ Wave 0 |
| PIPE-05 | unexpected `Exception` ⇒ `StepFailed` | unit | `--filter-method *UnexpectedException_Failed*` | ❌ Wave 0 (adapt consumer-line-129 case) |
| PIPE-06 | completed item → `KeeperUpdate`, write no-TTL, `KeeperCleanup` on success | unit | `--filter-method *PostCompleted_UpdateCleanup*` | ❌ Wave 0 |
| PIPE-06 | output-write exhausted → item `failed(infra)` → `KeeperInject` | unit | `--filter-method *WriteFault_Inject*` | ❌ Wave 0 (reuse `DispatchTestKit.PresentReadWriteFaultL2`) |
| PIPE-07 | completed result carries entryId+executionId; infra → `KeeperInject` | unit | `--filter-method *CompletedCarriesIds*` | ❌ Wave 0 |
| PIPE-08 | end-delete `finally` deletes on happy/business-fail/In-exception; exhaust → `KeeperDelete` | unit | `--filter-method *EndDelete*` | ❌ Wave 0 |
| PIPE-08 | end-delete SKIPPED on REINJECT + `Guid.Empty` | unit | `--filter-method *EndDelete_Skipped*` | ❌ Wave 0 |
| RESIL-01 | `RetryLoop` runs exactly `Limit` immediate attempts then surfaces exhaustion | unit | `--filter-method *RetryLoop_Exhausts*` | ❌ Wave 0 |
| RESIL-01 | send through `RetryLoop`; send-exhaustion propagates (→ `_error` via bus) | unit | `--filter-method *SendExhaust_Propagates*` | ❌ Wave 0 (adapt `DispatchAckSemanticsFacts`) |
| D-09 | bus `UseMessageRetry` reads `Retry:Limit`; no double-retry of L2/send | unit/contract | `--filter-method *RetryReconcile*` | ❌ Wave 0 (extend `RetryOptionsBindFacts`) |

### Sampling Rate
- **Per task commit:** `dotnet test tests\BaseApi.Tests\BaseApi.Tests.csproj --filter-not-trait Category=RealStack` (hermetic; < 30s for the Processor facts subset via `--filter-namespace *Processor*`).
- **Per wave merge:** full hermetic suite `dotnet test SK_P.sln --filter-not-trait Category=RealStack` + Release 0-warning build.
- **Phase gate:** full hermetic suite GREEN + 0-warning Release before `/gsd-verify-work`. RealStack E2E (full round-trip + recovery) is **Phase 49**, not this phase — Phase 44 proves the five terminals hermetically.

### Mapping the 5 ROADMAP success criteria to tests
1. **SC1 (Pre read + REINJECT + Guid.Empty skip + input-validation business-Failed):** the four PIPE-02/03 unit facts above.
2. **SC2 (In try/catch, status → one result, abort):** the PIPE-05 status-exception + unexpected-exception facts.
3. **SC3 (Post completed: UPDATE, write no-TTL, write-exhaust→failed(infra), CLEANUP on success):** the PIPE-06 facts (reusing `PresentReadWriteFaultL2`).
4. **SC4 (routing: not-infra→result carrying ids, infra→INJECT, N→N):** the PIPE-07 facts.
5. **SC5 (end-delete finally over read-succeeded paths, skip on REINJECT/Guid.Empty, exhaust→DELETE, shared Retry:Limit):** the PIPE-08 + RESIL-01 facts.

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Processor/PipelinePreFacts.cs` — REINJECT (exception + absent/empty), Guid.Empty skip, input-validation Failed (PIPE-02/03)
- [ ] `tests/BaseApi.Tests/Processor/PipelineInFacts.cs` — status-exception mapping + unexpected⇒failed + abort (PIPE-05)
- [ ] `tests/BaseApi.Tests/Processor/PipelinePostFacts.cs` — UPDATE/write-no-TTL/CLEANUP/INJECT/routing (PIPE-06/07)
- [ ] `tests/BaseApi.Tests/Processor/PipelineEndDeleteFacts.cs` — finally semantics, skip paths, DELETE (PIPE-08)
- [ ] `tests/BaseApi.Tests/Processor/RetryLoopFacts.cs` — exhaustion count + outcome surfacing (RESIL-01)
- [ ] **Extend `DispatchTestKit`:** add a `CapturingKeeperSendProvider` (or extend `CapturingSendProvider` to also capture `IKeeperRecoverable` sends by Uri), a `FakeProcessor` overload returning `List<ProcessItem>` / throwing a `ProcessStatusException`, and a `KeyDeleteAsync`-faulting / absent-key Redis fake. `PresentReadWriteFaultL2` already covers the write-fault case.
- [ ] **Retire/rewrite** `DispatchAckSemanticsFacts.BusinessFailure_DoesNotThrow` (absent input is now REINJECT, not StepFailed) and `DispatchInputFacts` (input absence semantics changed).

*(Existing infra that carries over: `DispatchTestKit` fakes, `OrchestratorTestStubs.InfraFaultL2/AbsentL2`, `FakeProcessorContext` with settable `InputDefinition`/`OutputDefinition`, `BuildResultHarness` in-memory MassTransit harness.)*

## Security Domain

> `security_enforcement` is not present in config.json → treat as enabled. This phase is backend messaging; the only validation surface is JSON-schema input/output validation (already SSRF-locked).

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Service is open by project-wide exclusion (REQUIREMENTS.md Out of Scope) |
| V3 Session Management | no | No sessions — message-driven |
| V4 Access Control | no | Internal bus; no external surface added |
| V5 Input Validation | yes | `ProcessorJsonSchemaValidator` (SSRF-locked: `SchemaRegistry.Global.Fetch = (_,_) => null`, dialect pinned) reused for Pre input + Post output — validator file lines 14-20 |
| V6 Cryptography | no | No crypto; the v3.x `H = SHA-256` identity is RETIRED (RETIRE-01, Phase 43) |

### Known Threat Patterns for C# / MassTransit / Redis
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Malicious `$ref` in author/step schema (SSRF) | Information disclosure | already locked: `ProcessorJsonSchemaValidator` returns business-Failed on unresolvable `$ref`, no outbound fetch (validator lines 61-70) |
| Unbounded retry / amplification on poison message | Denial of Service | bounded `RetryLoop` (N immediate, no backoff) + bus `UseMessageRetry` dead-letters to `_error` (D-08/D-10) |
| Duplicate effects from at-least-once redelivery | Tampering (logical) | accepted by design (RESIL-03, no dedup key); orchestrator `ResultConsumer` is L1-idempotent (consumer header lines 39-44) |
| Unvalidated output written to L2 | Tampering | Post output-validation gates the write — a schema-failing blob never reaches `L2[entryId]` (PIPE-06) |

No new secrets, endpoints, or auth surfaces are introduced. The phase is internal pipeline logic.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | REINJECT/DELETE carry the **inbound dispatch** `ExecutionId`; UPDATE/INJECT/CLEANUP carry the **author-minted item** `ExecutionId` | Code Examples (Keeper construction) | Phase 46 partitioner keys on `corr:wf:proc:exec`; a wrong exec on UPDATE/INJECT/CLEANUP would mis-partition or fail the composite-backup match. MEDIUM risk — confirm against design §51/64-67/75 at plan. |
| A2 | Processor sends the 5 Keeper messages to `queue:keeper-recovery` (`KeeperQueues.Recovery`), not `keeper-fault-recovery` | Code Examples | Wrong target → Phase 46 consumer never receives them. LOW risk (Recovery is the documented Phase-46 gate-open bind). Confirm. |
| A3 | A per-item business `failed` sends one `StepFailed` per item and writes nothing (no UPDATE/CLEANUP/INJECT) | Post code example | Wrong → either a missing result or a spurious Keeper emit. LOW-MEDIUM. PIPE-07 wording supports per-item StepFailed. |
| A4 | `StepProcessing` has no wire message field → processing message logged only | Pattern 3 / D-05 | Confirmed by file (StepProcessing.cs:6-11 — no message member). LOW risk; D-05 already anticipates this. |
| A5 | The new `ProcessAsync` returns `List<ProcessItem>` (concrete `List`, not `IReadOnlyList`) and signature includes `CancellationToken ct` | Pattern 2 | Cosmetic; CONTEXT D-03/specifics use `List<ProcessItem>` and the existing seam carries `ct`. LOW. |
| A6 | `Retry:Limit` config key already resolves (RetryOptions bound from `"Retry"` at AddBaseProcessor:89); Sample appsettings may need a `"Retry"` entry or it defaults to Immediate(3) | Runtime State Inventory | If absent, behavior is the default-3 — acceptable but should be made explicit. LOW. |
| A7 | The composite backup is written by the keeper (Model B), processor only emits `KeeperUpdate.ValidatedData` | Summary / Runtime State | Confirmed: KeeperUpdate.cs:11 + design §93. LOW risk. |
| A8 | `ProcessStatusException.Status` maps Failed→StepFailed, Cancelled→StepCancelled, Processing→StepProcessing | Pattern 3 | Confirmed by design §38 (send-side emits matching record per status). LOW. |

## Open Questions

1. **REINJECT/DELETE ExecutionId provenance (A1)**
   - What we know: every Keeper message carries `ExecutionId`; UPDATE/CLEANUP/INJECT clearly use the per-item author-minted id (they describe a completed item).
   - What's unclear: REINJECT and DELETE concern the *inbound* entry (pre-item-split). The design lists `exec` on every message but doesn't disambiguate inbound-vs-item exec for these two.
   - Recommendation: use the inbound `dispatch.ExecutionId` for REINJECT/DELETE; raise in plan/discuss for a one-line confirmation. Note: inbound `EntryStepDispatch.ExecutionId` defaults to `Guid.Empty` (EntryStepDispatch.cs:15) on an entry-step fire — so REINJECT/DELETE may legitimately carry `Guid.Empty` exec. **This needs explicit confirmation** — a `Guid.Empty` partition key in Phase 46 could collide.

2. **Per-item business-failed in Post (A3)**
   - What we know: PIPE-07 routes "not-infra (`completed` ∪ business-`failed`) → orchestrator result".
   - What's unclear: whether a `ProcessItem(Failed, …)` (author-declared failed) AND an output-validation failure both produce one per-item `StepFailed`, or whether output-validation-fail aborts the batch (the *old* consumer aborted the whole dispatch on output-validation fail — line 148-149).
   - Recommendation: per-item `StepFailed` (matches PIPE-07 "N items → N results" and the new per-item model). Confirm the batch is NOT aborted on a single item's output-validation failure.

3. **Does `KeeperUpdate` send before or only-on-completed?**
   - What we know: design §64 sends UPDATE for a `completed` item.
   - What's unclear: ordering vs the output write — §64 lists UPDATE as step 2 (before the write at step 3). Pattern 5 assumes UPDATE-before-write.
   - Recommendation: emit UPDATE before the L2 write (design order), so the keeper has the validated data backed up before the data key write is attempted.

## Environment Availability

> This phase is code/config-only (in-process pipeline rewrite). No new external dependencies. Existing infra (RabbitMQ, Redis) is exercised only in Phase 49 RealStack E2E — hermetic tests use in-memory MassTransit + NSubstitute Redis fakes.

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | build + test | ✓ (solution builds GREEN through Phase 43) | net8.0 | — |
| xunit.v3 / NSubstitute / MassTransit.Testing | hermetic facts | ✓ (BaseApi.Tests.csproj) | xunit.v3 3.2.2 | — |
| Redis (live) | Phase 49 E2E only | not needed this phase | — | NSubstitute `IConnectionMultiplexer` fakes (DispatchTestKit) |
| RabbitMQ (live) | Phase 49 E2E only | not needed this phase | — | in-memory MassTransit harness (`BuildResultHarness`) |

**Missing dependencies with no fallback:** none.
**Missing dependencies with fallback:** none required for Phase 44 (RealStack is Phase 49).

## Sources

### Primary (HIGH confidence — verified this session)
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` (LOCKED) — §Processor round trip (lines 42-76), §Locked decisions (101-115), §Result contract A15 (23-38), §Identities & L2 (9-19)
- `.planning/phases/44-processor-pre-in-post-process-pipeline/44-CONTEXT.md` — D-01..D-10 + discretion + deferred
- `.planning/REQUIREMENTS.md` — PIPE-01..08 (lines 10-17), RESIL-01 (line 43)
- `.planning/ROADMAP.md` — Phase 44 goal + 5 success criteria (lines 452-462)
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` — current straight-through consumer (the rewrite baseline)
- `src/BaseProcessor.Core/Processing/BaseProcessor.cs` + `ProcessResult.cs` — current seam (to retype/delete)
- `src/BaseProcessor.Core/Validation/ProcessorJsonSchemaValidator.cs` — `TryValidate` signature (lines 30-84)
- `src/BaseProcessor.Core/Identity/IProcessorContext.cs` + `ProcessorContext.cs` — `InputDefinition`/`OutputDefinition`/`Id`
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs:172-177` — `UseMessageRetry(Immediate(retryLimit))` bind (D-09 reconciliation point)
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:89` — `RetryOptions` bound from `"Retry"`
- `src/Messaging.Contracts/{StepCompleted,StepFailed,StepCancelled,StepProcessing,IStepResult,SourceStep}.cs`
- `src/Messaging.Contracts/{KeeperUpdate,KeeperReinject,KeeperInject,KeeperDelete,KeeperCleanup,IKeeperRecoverable,KeeperQueues}.cs`
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `ExecutionData` / `CompositeBackup`
- `src/Messaging.Contracts/Configuration/RetryOptions.cs` — `Limit` (default 3)
- `src/Messaging.Contracts/EntryStepDispatch.cs` — inbound message shape (ExecutionId/EntryId default Guid.Empty)
- `src/Processor.Sample/{SampleProcessor.cs,Program.cs}` — migration target
- `src/Keeper/BackupOptions.cs` — composite-backup TTL owned by keeper (Model B confirmation)
- `tests/BaseApi.Tests/Processor/{DispatchTestKit,DispatchAckSemanticsFacts,FakeProcessorContext}.cs` — test infrastructure
- `tests/BaseApi.Tests/BaseApi.Tests.csproj` — xunit.v3 + NSubstitute framework

### Secondary (MEDIUM confidence)
- `.planning/phases/43-message-contracts-l2-key-reshape/43-CONTEXT.md` — composite-backup `skp:`-prefix divergence (line 125), Model B ownership

### Tertiary (LOW confidence)
- None — all claims are file-anchored or design-doc-anchored. `[ASSUMED]` tags in the Assumptions Log flag the un-disambiguated id-set choices for plan-time confirmation.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new packages; all libraries verified present and GREEN through Phase 43.
- Architecture (pipeline decomposition, RetryLoop, status family): HIGH — directly derived from the current consumer + LOCKED design + CONTEXT decisions.
- Keeper id-set wiring: MEDIUM — records verified; the inbound-vs-item ExecutionId on REINJECT/DELETE (A1, Open Q1) needs one confirmation.
- Pitfalls: HIGH — each is grounded in a specific current-code line or design clause.
- Validation Architecture: HIGH — framework + existing test kit verified; gaps enumerated concretely.

**Research date:** 2026-06-08
**Valid until:** 2026-07-08 (stable internal codebase; no fast-moving external deps). Re-verify only if Phase 43 contracts change.
