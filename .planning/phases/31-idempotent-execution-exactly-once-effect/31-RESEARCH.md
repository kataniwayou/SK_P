# Phase 31: Idempotent Execution Round-Trip (Exactly-Once-Effect) - Research

**Researched:** 2026-06-04
**Domain:** .NET distributed messaging idempotency (MassTransit/RabbitMQ + StackExchange.Redis content-addressed dedup)
**Confidence:** HIGH

This is a VERIFY-not-redesign research. The SPEC is locked (8 reqs, ambiguity 0.13) and CONTEXT.md locks D-01..D-12. Every CONTEXT-named file was read against the current tree; this report confirms the snapshot, enumerates the EntryId Guid->string blast radius the plan's files_modified must cover, flags landmines, and builds the Validation Architecture section.

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions (D-01..D-12 — research THESE, no alternatives)
- **D-01:** `EntryId` changes type `Guid` -> `string` (lowercase 64-hex) across `IExecutionCorrelated`, `EntryStepDispatch`, `ExecutionResult`, `L2ProjectionKeys.ExecutionData`. The `EntryId == Guid.Empty` entry-step sentinel is removed; "skip input read" moves to `InputDefinition == null`.
- **D-02:** Add deterministic-identity field `H` (string, 64-hex) to both `EntryStepDispatch` and `ExecutionResult`. `executionId` stays `Guid`, excluded from `H`.
- **D-03:** `H = SHA-256(canonical(correlationId, workflowId, stepId, processorId, EntryId))`; canonical = delimited UTF-8 text (each Guid lowercase "D", EntryId as its 64-hex string, joined by a reserved unit-separator byte) -> SHA-256 -> lowercase x2 hex. Mirrors SourceHash.targets exactly.
- **D-04:** The hash helper lives in `Messaging.Contracts`, co-located with `L2ProjectionKeys`. The SAME helper produces `EntryId = hash(blob)`, `hash(manifest)`, and `H`.
- **D-05:** Dedup flip via `StringSet(flag[H], "Ack", When.Exists)` — NOT Lua. Only non-Ack value is Pending, so `When.Exists` IS the Pending->Ack transition.
- **D-06:** Sender pre-writes `flag[H_child] = "Pending"`. Receiver is effect-first: `if flag[H]==Ack -> drop`; else produce effect -> `StringSet(flag[H],"Ack",When.Exists)` -> broker-ack. Concurrent duplicates collapse downstream by `H`.
- **D-07 (SPEC req-4 AC amendment):** "atomic CAS" is delivered as "Ack observably set exactly once; concurrent duplicates collapse downstream by H." No literal value-CAS primitive.
- **D-08:** Manifest = JSON array of lowercase-hex strings `["<64hex>", ...]`; `hash(manifest)` over serialized UTF-8 bytes; empty result -> `[]` -> `EntryId = hash("[]")` -> terminal. Orchestrator `JsonSerializer.Deserialize<string[]>` -> fan-out N x M.
- **D-09:** Output-schema validation runs on each result DATA blob pre-write (`ProcessResult.OutputData`), per-result. The manifest is an UNVALIDATED pointer list.
- **D-10:** `RetryOptions { int Limit = 3; RetryStrategy Strategy = Immediate; }` bound from appsettings section `Retry` via `IOptions`, per process. Thread `Limit` into all 4 `Immediate(3)` sites. Only the `Immediate` branch implemented.
- **D-11:** req-8 real-stack proof induces a duplicate test-only: re-publish the same `EntryStepDispatch` and/or a throw-once processor. Reuse the dual-pipeline merge fixture + `SampleRoundTripE2ETests` `PollEsForLog` + net-zero teardown. Broker-level fault injection rejected.
- **D-12:** Close-gate teardown scan-cleans the new `skp:data:{64hex}` and `skp:flag:{64hex}` namespaces; triple-SHA BEFORE==AFTER extends to them.

### Claude's Discretion (planner's call within the locked semantics)
- Exact member names (`H` vs `DeterministicId`; `RetryStrategy` enum value names).
- The precise separator byte.
- Whether `RetryOptions` is one shared record or two per-process copies.

### Deferred Ideas / OUT OF SCOPE (do NOT plan as in-phase work)
- **Cancelled circuit-breaker -> Phase 32** (exhaustion->Cancelled, `cancelled[workflowId]` marker, unschedule via L1 jobId, removal of `StepEntryCondition.PreviousCancelled`, resume). **Phase 31's receiver flow does NOT check a cancel marker.**
- Transactional outbox (deferred).
- `ProcessAsync` external-side-effect idempotency (out of framework scope).
- `predecessorStepId`-in-`H` strict-per-edge merge (content-collapse chosen instead).
- Back-off retry as the default (`Immediate(N)` is the default; back-off is config-only, structured-for but not implemented).
</user_constraints>

<phase_requirements>
## Phase Requirements

There are NO IDEM-* formal IDs in `.planning/REQUIREMENTS.md` (verified: grep for IDEM/idempoten returned no matches). The 8 LOCKED requirements live ONLY in `31-SPEC.md` and are the authoritative IDs. The STATE.md note "formalize IDEM-* requirements" was an intention that the SPEC superseded with `req-1..req-8`.

| ID | Description | Research Support (confirmed-against-code) |
|----|-------------|-------------------------------------------|
| req-1 | Deterministic identity `H = SHA-256(corr, wf, step, proc, EntryId)`, executionId excluded | SourceHash convention confirmed (UTF-8->SHA256->x2, `^[a-f0-9]{64}$`); shared helper goes in Messaging.Contracts. No hashing exists on the message path today. |
| req-2 | Per-fire correlationId (exists) + entry-step `EntryId = hash(corr, stepId)`; source signal -> `InputDefinition == null` | `WorkflowFireJob.cs:54` mints correlationId; `:84` dispatches `entryId: Guid.Empty`. Consumer `EntryStepDispatchConsumer.cs:74` branches on `EntryId == Guid.Empty` — this branch is REMOVED. |
| req-3 | Content-addressed two-level L2; empty result -> terminal | Today `EntryStepDispatchConsumer.cs:153` mints `NewId.NextGuid()` per result, writes to `data[newEntryId]`. Becomes `data[hash(blob)]` + manifest at `data[hash(manifest)]`. |
| req-4 | Effect-first CAS dedup both hops via `flag[H]` | No dedup flag exists today. `StringSet(...,When.Exists)` confirmed as SET XX. |
| req-5 | Merge correctness per-edge via input EntryId, content-collapse | `StepAdvancement.SelectNext` is the fan-out site; different input EntryId -> different H. |
| req-6 | Manifest fan-out, N items x M successors | `ResultConsumer.cs:64-69` is the unbundle+fan-out insertion point. |
| req-7 | Configurable retry + `prefix + 64-hex` key format | 4 `Immediate(3)` sites located (see Retry Config Sites). `L2ProjectionKeys.ExecutionData` currently `{Prefix}data:{entryId:D}` (Guid) — becomes 64-hex; add `Flag(H)`. |
| req-8 | Live real-stack exactly-once proof | `SampleRoundTripE2ETests.cs` is the clone target; merge topology built test-side. |
</phase_requirements>

## Summary

The CONTEXT.md snapshot is ACCURATE against the current tree — every named file matches its described shape. The phase is a wire-contract + receiver-logic rework with one high-risk mechanical change (EntryId Guid->string) that ripples wider than the contract files: it also hits `IStepDispatcher`/`StepDispatcher` signatures, `InboundExecutionScopeConsumeFilter` (a `!= Guid.Empty` guard), and ~12 test files that assert `EntryId` as a Guid.

The decided protocol is sound and confirmed against current best practice. `StringSet(flag[H], "Ack", When.Exists)` maps to Redis `SET ... XX` (set only if key exists, returns bool/null) — so with Pending as the only pre-existing value, `When.Exists` IS the Pending->Ack transition (D-05 verified). The effect-first / sender-pre-writes-Pending protocol (D-06) is the correct shape for exactly-once-EFFECT-with-no-loss; the residual concurrent-duplicate is collapsed downstream by deterministic `H` (D-07). The SourceHash canonicalization (UTF-8 text -> SHA-256 -> `b.ToString("x2")`, `^[a-f0-9]{64}$`) is the exact convention to mirror in the new shared helper.

**Primary recommendation:** Treat the EntryId Guid->string change as its own wave-0 mechanical task with the full file inventory below; build the shared `Messaging.Contracts` hash helper with golden tests pinning exact 64-hex outputs BEFORE any consumer rework so cross-process determinism is locked first; verify the new helper's bytes match the SourceHash convention with a parity test.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Deterministic identity H + EntryId/manifest hashing | Messaging.Contracts (shared leaf) | both processes consume | D-04: one canonical path so cross-process hashes cannot drift |
| Content-addressed L2 data write (blob + manifest) | BaseProcessor.Core (processor receiver) | Redis (L2 store) | producer of result data owns the content-address write |
| Effect-first flag[H] dedup (processor inbound) | BaseProcessor.Core (`EntryStepDispatchConsumer`) | Redis | receiver dedups its own inbound dispatch |
| Effect-first flag[H] dedup (orchestrator inbound) | Orchestrator (`ResultConsumer`) | Redis | receiver dedups its own inbound result |
| Sender pre-write flag[H_child]=Pending | both senders (`StepDispatcher` + processor send loop) | Redis | child arrives with its flag present (D-06) |
| Manifest unbundle + N x M fan-out | Orchestrator (`ResultConsumer` + `StepAdvancement.SelectNext`) | - | SelectNext is the existing successor-selection seam |
| Per-fire correlationId + entry-step EntryId stamp | Orchestrator (`WorkflowFireJob`) | - | fire is where per-fire identity is minted |
| Configurable retry budget | both processes (appsettings bind) | MassTransit `UseMessageRetry` | D-10: per-process IOptions |
| Real-stack exactly-once proof | tests/BaseApi.Tests (E2E) | live compose stack | clone of `SampleRoundTripE2ETests` |

## Standard Stack

No new third-party libraries. Every primitive already exists in the tree.

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| StackExchange.Redis | (in tree) | `StringSet(..., When.Exists)` CAS-equivalent, `IBatch`, content-addressed get/set | already in `RedisProjectionWriter`/consumers; `When` maps to SET NX/XX [CITED: StackExchange/StackExchange.Redis#2071, redis.io/commands/set] |
| MassTransit | (in tree) | `UseMessageRetry(r => r.Immediate(N))`, `IConsumer`, `ISendEndpointProvider` | the existing transport; retry binding is the only config change |
| System.Security.Cryptography.SHA256 | BCL | the hash primitive (mirror SourceHash.targets) | `SHA256.ComputeHash` over UTF-8 bytes -> `b.ToString("x2")` [VERIFIED: SourceHash.targets:54-66] |
| System.Text.Json | BCL | manifest serialize/deserialize (`string[]`) | `JsonSerializer.Serialize/Deserialize<string[]>` (D-08); consistent with existing L2 JSON payloads |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Microsoft.Extensions.Options | (in tree) | bind `RetryOptions` from appsettings `Retry` section per process | D-10 |
| NSubstitute + MassTransit.Testing | (in tree) | hermetic consumer harness (`DispatchTestKit`, `OrchestratorTestStubs`) | unit/integration tiers |
| xUnit | (in tree) | all test tiers | - |

**Installation:** none — all packages present.

## Architecture Patterns

### System data flow (the round-trip with dedup)

```
Quartz fire (WorkflowFireJob)
  | mint correlationId (per fire)                                  [exists, :54]
  | entry-step EntryId = hash(correlationId, stepId)               [NEW, req-2]
  | H_entry = hash(corr, wf, step, proc, EntryId)                  [NEW]
  | sender pre-writes flag[H_entry] = "Pending"                    [NEW, D-06]
  v
EntryStepDispatch  --Send queue:{procId}-->  EntryStepDispatchConsumer (processor)
                                               | if flag[H]==Ack -> DROP             [NEW, D-06]
                                               | read input L2[data(EntryId)]        [exists, key now string]
                                               |   (InputDefinition==null => skip)   [CHANGED from EntryId==Empty]
                                               | ProcessAsync                        [exists]
                                               | per result: validate blob,          [D-09]
                                               |   write data[hash(blob)]            [CHANGED from data[newEntryId]]
                                               | manifest = [hash(r1)..]             [NEW]
                                               |   write data[hash(manifest)]        [NEW]
                                               | send 1 ExecutionResult{EntryId=hash(manifest), H}  [CHANGED from one-per-result]
                                               |   sender pre-writes flag[H_result]=Pending
                                               | StringSet(flag[H], Ack, When.Exists) [NEW]  <-- effect-first CAS
                                               | broker-ack
                                               v
ExecutionResult --Send queue:orchestrator-result--> ResultConsumer (orchestrator)
                                               | if flag[H]==Ack -> DROP             [NEW]
                                               | L1 read (TryGet)                    [exists, :55]
                                               | read manifest L2[data(EntryId)]     [NEW]
                                               | items = Deserialize<string[]>       [NEW, D-08]
                                               | foreach item x foreach SelectNext:  [N x M, req-6]
                                               |   dispatch successor(stepId/procId=successor,
                                               |     EntryId=item hash, exec regenerated,
                                               |     H_child deterministic)          [CHANGED]
                                               |   sender pre-writes flag[H_child]=Pending
                                               | StringSet(flag[H], Ack, When.Exists) [NEW]
                                               | broker-ack
```

### Pattern 1: Shared hash helper in Messaging.Contracts (D-03/D-04)
**What:** One static helper, co-located with `L2ProjectionKeys`, producing 64-hex from canonical UTF-8.
**Mirror exactly** [VERIFIED: src/BaseProcessor.Core/SourceHash.targets:54-66]:
```csharp
// canonical: each Guid as g.ToString("D") lowercase, EntryId as its 64-hex string,
// joined by a reserved separator byte (discretion), -> UTF-8 -> SHA-256 -> x2 hex.
using var sha = SHA256.Create();
var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonicalText));
var sb = new StringBuilder(64);
foreach (var b in bytes) sb.Append(b.ToString("x2"));   // lowercase 64-hex, matches ^[a-f0-9]{64}$
return sb.ToString();
```
Note: `Guid.ToString("D")` is lowercase by default in .NET, matching the convention. The existing L2 keys render Guids hyphenated "D" (`L2ProjectionKeys.Root` uses `:D`) — stay consistent.

### Pattern 2: Effect-first dedup at a receiver (D-06)
**What:** `if (await db.StringGetAsync(Flag(H)) == "Ack") return;` (drop, ack) -> else produce effect -> `await db.StringSetAsync(Flag(H), "Ack", when: When.Exists)` -> return (broker-ack).
**When:** at BOTH `EntryStepDispatchConsumer.Consume` and `ResultConsumer.Consume`, at the top, before the existing logic.
**Note:** the flag-read and flag-flip are INFRA Redis ops (no catch) — they propagate to the retry like the existing output-write does [VERIFIED: EntryStepDispatchConsumer.cs:67-69 "Redis fault is INFRA and propagates"].

### Pattern 3: Sender pre-writes Pending (D-06)
**What:** every place that Sends a child message writes `flag[H_child] = "Pending"` first (no `When` — unconditional set; idempotent on re-send). Sites: `StepDispatcher.DispatchAsync` (orchestrator->processor) and the processor's result-send path (`EntryStepDispatchConsumer.SendResult` / the build loop).

### Anti-Patterns to Avoid (already rejected in CONTEXT — do not re-introduce)
- **Lua value-CAS** — rejected (D-05; zero Lua in codebase, no value-conditional `When`).
- **`{correlationId}:{stepId}` dedup key** — collides merge per-edge executions (req-5).
- **Ack-before-effect** — reintroduces the lost-branch horn (CONTEXT "Why effect-first").
- **Truncating the hash into a Guid** — the whole reason for D-01 (Guid->string).
- **Validating the manifest against the output schema** — D-09: the manifest is an unvalidated pointer list.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Conditional set (set-if-present) | a GET-then-SET race | `StringSet(..., When.Exists)` (SET XX) | atomic at Redis; GET-then-SET reintroduces a window |
| Hex encoding | `Convert.ToHexString` (uppercase) or BitConverter | `b.ToString("x2")` loop | the SourceHash convention is LOWERCASE 64-hex; uppercase breaks `^[a-f0-9]{64}$` and cross-process parity |
| Hashing canonicalization | a second/ad-hoc serialization | the ONE shared `Messaging.Contracts` helper (D-04) | two paths drift -> orchestrator and processor disagree on H |
| Retry strategy | a custom retry loop | MassTransit `UseMessageRetry` | the 4 sites already use it; only the count becomes config |

**Key insight:** the entire phase exists because hand-rolled identity (re-minted `NewId` per attempt) is non-idempotent. The fix is to make identity DERIVED (content + position), not minted, and to dedup at the receiver.

## Runtime State Inventory

This is a type-change + protocol-change phase touching live Redis namespaces and the wire contract. The runtime-state audit:

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | L2 `skp:data:{guid:D}` keys (current Guid-keyed execution data). After the change they become `skp:data:{64hex}`. Old-format keys are per-fire, TTL'd (`ExecutionDataTtlSeconds`), and self-expire — no live long-lived data to migrate. NEW namespace `skp:flag:{64hex}` is introduced. | Code edit (key builder). No data migration — old keys TTL out. Close-gate teardown MUST scan-clean both `skp:data:*` and `skp:flag:*` (D-12). |
| Live service config | The wire contract `EntryStepDispatch`/`ExecutionResult` shape changes (EntryId type + new `H` field). Both processes (orchestrator + processor-sample container) deserialize these. A mixed-version deployment (old processor, new orchestrator) would mis-deserialize. | Both processes rebuilt/redeployed together — no rolling upgrade across the contract change. Verified: both reference `Messaging.Contracts` (single leaf). |
| OS-registered state | None — Quartz jobs are in-memory (L1), rescheduled each fire (`WorkflowFireJob:96-99`). No Task Scheduler / systemd state embeds these fields. | None — verified by `WorkflowFireJob` self-reschedule pattern. |
| Secrets/env vars | None — no secret or env var references EntryId/H. `OTEL_EXPORTER_OTLP_ENDPOINT`, RabbitMq/Redis/Postgres connection envs are unaffected. New `Retry` appsettings section is additive. | None — additive config only. |
| Build artifacts | The `SourceHash` MSBuild embed (Phase 28) re-hashes BaseProcessor.Core + concrete `.cs` on every build; adding the new helper to BaseProcessor.Core OR a consumed file CHANGES the embedded SourceHash. NOTE: the hash helper lives in `Messaging.Contracts` (a SIBLING, EXCLUDED from the SourceHash fold per `SourceHash.targets:77-78` comment), so the helper itself does NOT shift the SourceHash — but editing `EntryStepDispatchConsumer.cs` (in BaseProcessor.Core) DOES. | The processor-sample container must be rebuilt; the E2E registers the genuine embedded hash (`SampleRoundTripE2ETests:98-100`) so the DB row must match the rebuilt container's hash. Standard for this harness. |

**The canonical question — after every repo file is updated, what runtime systems still have old state?** Only TTL'd `skp:data:{guid}` keys, which self-expire; the close gate must scan-clean both new namespaces or the triple-SHA BEFORE==AFTER fails.

## Common Pitfalls

### Pitfall 1: EntryId Guid->string blast radius is wider than the 3 contract files (HIGH risk — D-01)
**What goes wrong:** the plan lists only `EntryStepDispatch`/`ExecutionResult`/`IExecutionCorrelated` and misses the dispatcher signatures and the `!= Guid.Empty` guard, producing a non-compiling tree mid-wave.
**Full production-side inventory (every site that names EntryId or the Guid.Empty sentinel):**
- `src/Messaging.Contracts/IExecutionCorrelated.cs:15` — `Guid EntryId { get; }` -> `string`
- `src/Messaging.Contracts/EntryStepDispatch.cs:14` — `EntryId { get; init; } = Guid.Empty` -> string default
- `src/Messaging.Contracts/ExecutionResult.cs:15` — `Guid EntryId` -> string
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:39` — `ExecutionData(Guid entryId)` -> `ExecutionData(string entryId)` returning `{Prefix}data:{entryId}`; ADD `Flag(string h)` -> `{Prefix}flag:{h}`
- `src/Orchestrator/Dispatch/IStepDispatcher.cs:21` — `Guid entryId` param -> `string`
- `src/Orchestrator/Dispatch/StepDispatcher.cs:15,22` — `Guid entryId` param + `EntryId = entryId` assignment -> string
- `src/Orchestrator/Scheduling/WorkflowFireJob.cs:84` — passes `Guid.Empty` for entryId -> pass `hash(correlationId, stepId)` (req-2)
- `src/Orchestrator/Consumers/ResultConsumer.cs:68` — passes `m.EntryId` through (type flows)
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:74` — `if (dispatch.EntryId == Guid.Empty)` -> REMOVE; branch on `InputDefinition == null`; `:91` `ExecutionData(dispatch.EntryId)`; `:153` `NewId.NextGuid()` newEntryId -> `hash(blob)`; `:171` `ExecutionData(newEntryId)`; `:251-274` BuildCompleted/Failed/Cancelled set `EntryId` (Guid.Empty for Failed/Cancelled -> string equivalent, e.g. `""` or hash of empty)
- `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs:33` — `if (ec.EntryId != Guid.Empty) state[...] = ec.EntryId.ToString();` -> string guard (`!string.IsNullOrEmpty`)
**Test-side inventory (must update assertions — ~12 files):** `EntryStepDispatchTests.cs`, `ExecutionResultContractTests.cs`, `FireDispatchTests.cs` (asserts `Guid.Empty` EntryId on fire), `DispatchOutputWriteFacts.cs` (multiple `Guid.Empty`/`L2ProjectionKeys.ExecutionData(sent.EntryId)`), `DispatchInputFacts.cs`, `DispatchTestKit.cs:148` (`EntryId = entryId`), `EntryStepDispatchScopeTests.cs`, `EntryStepDispatchRuntimeScopeTests.cs`, `ConsoleExecutionScopeFilterTests.cs` (the `record ExecProbeMessage(... Guid EntryId)`), `StopConsumerLifecycleTests.cs:161`, `ResultConsumeTests.cs`. The `Guid.Empty` entry-step assertions in `FireDispatchTests` become "non-empty hash" assertions (req-2 acceptance).
**How to avoid:** make the type change a single mechanical wave-0 task that compiles green (contracts + dispatcher + filter + all tests) BEFORE any protocol logic lands.
**Warning sign:** a wave that edits a contract but leaves `StepDispatcher`/`InboundExecutionScopeConsumeFilter` for "later" — the tree won't build.

### Pitfall 2: Hash determinism across processes (HIGH risk — D-03)
**What goes wrong:** orchestrator and processor compute different `H` for the same logical message -> dedup never fires (or false-dedups).
**Root causes to guard:** (a) culture-sensitive Guid formatting — use `ToString("D")` (invariant, lowercase) not `ToString()` with a culture; (b) hex case — `b.ToString("x2")` LOWERCASE, never `X2`/`Convert.ToHexString` (uppercase); (c) JSON manifest byte-stability — `JsonSerializer.Serialize(string[])` is stable for a hex array but pin it with a golden test; (d) separator collision — use a byte that never appears in Guid "D" or hex text (the unit-separator `` is the CONTEXT's suggestion); (e) field ORDER — fix corr, wf, step, proc, EntryId order in ONE helper.
**How to avoid:** D-04's single shared helper + a golden test pinning the EXACT 64-hex for a fixed input vector, plus a parity assertion that the helper's algorithm matches the SourceHash convention (UTF-8 -> SHA256 -> x2).
**Warning sign:** any second place that builds the canonical string.

### Pitfall 3: `When.Exists` returns false/null, not throw, when the flag is absent (MEDIUM — D-05/D-06)
**What goes wrong:** if the sender's pre-write of `flag[H_child]=Pending` was lost (crash) the receiver's `StringSet(...,When.Exists)` is a no-op (returns false) and the flag never becomes Ack — but the effect already happened, so a redelivery re-produces a collapsed duplicate. This is the DESIGNED residual (effect-first), not a bug.
**Root cause:** SET XX sets only if the key exists; returns nil/false otherwise [CITED: redis.io/commands/set, StackExchange/StackExchange.Redis#1578 confirms NotExists/Exists return false when the condition fails].
**How to avoid:** do NOT treat the false return as an error to retry; the observable property is "Ack is set exactly once when Pending was present" (D-07), and downstream `H` dedup absorbs the residual. Document this in the consumer so a future reader doesn't "fix" it into Ack-first.
**Warning sign:** code that asserts/throws on the `StringSet` bool return.

### Pitfall 4: Manifest empty-result terminal branch (MEDIUM — D-08)
**What goes wrong:** an empty `ProcessAsync` result must produce `manifest = []` -> `EntryId = hash("[]")` -> `data[hash("[]")] = "[]"` -> orchestrator deserializes `[]` -> zero successors -> ack. Today the empty-list path `EntryStepDispatchConsumer.cs:193-194` sends NOTHING and acks; the new path must send ONE result carrying the empty-manifest EntryId so the orchestrator can observe-and-terminate (req-3 acceptance: "empty-result dispatch advances zero successors AND is acked").
**How to avoid:** unify the send path — always one result with a manifest EntryId, even empty; the orchestrator fan-out over `[]` is naturally zero.
**Warning sign:** a special-case early-return for empty results that sends no message (the old shape) — the orchestrator then never sees the terminal and req-3's "is acked" clause is untested.

### Pitfall 5: N x M fan-out regenerates executionId but reuses H per (item, successor) (MEDIUM — req-6)
**What goes wrong:** if `H_child` includes `executionId`, an orchestrator redelivery regenerates executionId -> different H -> NOT deduped -> extra dispatch. `H` MUST exclude executionId (D-02) so `(item EntryId, successor stepId/procId, corr, wf)` fully determines `H_child`.
**How to avoid:** compute `H_child = hash(corr, wf, successorStepId, successorProcId, itemEntryId)` — executionId regenerated freely as lineage. Acceptance: "an orchestrator redelivery reuses the same H per (item, successor) -> deduped."

### Pitfall 6: Close-gate triple-SHA must include the new namespaces (MEDIUM — D-12)
**What goes wrong:** the existing close gate scan-cleans `skp:data:*` + root/step keys; the new `skp:flag:*` keys accumulate per-fire and break BEFORE==AFTER.
**How to avoid:** extend the E2E `L2KeysToCleanup` / scan-clean to `skp:flag:*` AND the new-format `skp:data:*` (64-hex), and confirm the close-gate teardown pattern (`SampleRoundTripE2ETests` `ScanExecutionDataKeys` already scans `skp:data:*` by prefix — add a `skp:flag:*` scan).

## Code Examples (verified current shapes)

### Current SourceHash convention to mirror [VERIFIED: SourceHash.targets:54-66]
```csharp
var text = File.ReadAllText(p);                         // UTF-8 default
text = text.Replace("\r\n","\n").Replace("\r","\n");    // (content-only; not needed for H over Guids)
var h = SHA256.ComputeHash(Encoding.UTF8.GetBytes(text));
foreach (var b in h) sb.Append(b.ToString("x2"));        // lowercase 64-hex
```

### Current per-result mint+write to be replaced [VERIFIED: EntryStepDispatchConsumer.cs:153-175]
```csharp
var newEntryId  = NewId.NextGuid();                      // -> hash(blob)
var executionId = NewId.NextGuid();                      // stays lineage (regenerated)
await db.StringSetAsync(
    L2ProjectionKeys.ExecutionData(newEntryId),          // -> ExecutionData(hash(blob))
    r.OutputData,
    expiry: TimeSpan.FromSeconds(opts.ExecutionDataTtlSeconds));
built.Add(BuildCompleted(dispatch, executionId, newEntryId));  // one-per-result -> ONE manifest result
```

### Current fan-out seam to extend [VERIFIED: ResultConsumer.cs:64-69]
```csharp
foreach (var (stepId, step) in advancement.SelectNext(m.Outcome, completed, wf.Steps))
{
    await dispatcher.DispatchAsync(
        m.WorkflowId, stepId, step.ProcessorId, step.Payload,
        m.CorrelationId, m.ExecutionId, m.EntryId, context.CancellationToken);
}
// -> wrap in: foreach (item in manifest) foreach (SelectNext) dispatch(EntryId=item, H_child computed)
```

### Current 4 retry sites to bind [VERIFIED]
```csharp
// 1. ResultConsumerDefinition.cs:31           r.Immediate(3)
// 2. StartOrchestrationConsumerDefinition.cs:31  r.Immediate(3)  (+ Ignore<WorkflowRootNotFoundException>)
// 3. StopOrchestrationConsumerDefinition.cs:26   r.Immediate(3)  (+ Ignore<WorkflowRootNotFoundException>)
// 4. ProcessorStartupOrchestrator.cs:151         cfg.UseMessageRetry(r => r.Immediate(3))  -- NOTE: this one is
//    an inline ConnectReceiveEndpoint config, NOT a ConsumerDefinition. It binds IOptions<RetryOptions>
//    from the BaseProcessor.Core process; sites 1-3 bind from the Orchestrator process (D-10 per-process).
```
**Landmine:** site 4 lives inside the runtime endpoint-bind (`ConnectReceiveEndpoint`), so it needs `IOptions<RetryOptions>` injected into `ProcessorStartupOrchestrator` (DI ctor), not a ConsumerDefinition. Sites 1-3 are ConsumerDefinitions in the Orchestrator process and need IOptions injected into each definition ctor (or read from a shared accessor).

## State of the Art

| Old Approach | Current Approach | When | Impact |
|--------------|------------------|------|--------|
| Mint `NewId` per result/attempt | Content-addressed `EntryId = hash(blob)` | this phase | retries reproduce the same key (idempotent overwrite) |
| One `ExecutionResult` per result | One result carrying a manifest EntryId | this phase | orchestrator fans out N x M from the manifest |
| Hard-coded `Immediate(3)` x4 | `IOptions<RetryOptions>` per process | this phase | retry budget is config; feeds Phase 32's final-attempt check |
| `EntryId == Guid.Empty` source sentinel | `InputDefinition == null` | this phase | EntryId is now a real 64-hex content address |

**Deprecated/outdated by this phase:** the `EntryId == Guid.Empty` branch in `EntryStepDispatchConsumer`; the per-result one-by-one Send loop; the `data[newEntryId]` random-key write.

## Validation Architecture

> Nyquist validation is ENABLED (`workflow.nyquist_validation: true` in config.json).

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (in `tests/BaseApi.Tests`) + NSubstitute + MassTransit.Testing |
| Config file | none separate — project-level; RealStack/E2E gated by `[Trait("Category","RealStack")]` / `"E2E"` |
| Quick run command | `dotnet test tests/BaseApi.Tests --filter "Category!=RealStack&Category!=E2E"` (hermetic only) |
| Full suite command | `dotnet test tests/BaseApi.Tests` (incl. real-stack; requires the live compose stack up) |
| Close gate | `phase-31-close.ps1` (clone the existing close gate): 3-consecutive-GREEN full run + triple-SHA BEFORE==AFTER over `skp:data:*` + `skp:flag:*` |

### Validation tiers (what to sample at each)

**Tier 1 - Hermetic / unit (fast, no containers):**
- H determinism: compute H for a fixed 5-field vector -> golden 64-hex; recompute after "simulated retry" (same fields) -> byte-identical; change each of the 5 fields -> H changes; change executionId -> H unchanged (req-1 acceptance).
- Key-builder golden tests: `ExecutionData(hex)` -> `skp:data:{hex}`; `Flag(hex)` -> `skp:flag:{hex}`; both match `^skp:(data|flag):[a-f0-9]{64}$` (req-7 acceptance).
- Manifest empty->terminal: `hash("[]")` golden; `Deserialize<string[]>("[]")` -> zero successors (req-3).
- SourceHash-parity: the new helper's UTF-8->SHA256->x2 matches the SourceHash convention bytes (cross-process determinism guard).
- CAS property (hermetic, NSubstitute IDatabase): receiver with `flag==Ack` does NOT produce effect (drop); receiver with `flag==Pending` produces effect then `StringSet(...,When.Exists)` called once (D-07 observable).
- Retry binding: `RetryOptions` binds `Limit` from a test appsettings; default is `Immediate(3)`.

**Tier 2 - Integration (consumer harness, real-ish Redis fake or Testcontainers Redis):**
- Effect-first dedup with simulated crash window: receiver produces effect, then the flag flip is skipped (simulate crash) leaving Pending; redelivery re-produces a collapsed duplicate (same H downstream), NOT a loss (req-4 acceptance).
- Merge distinct-H vs collapse: two predecessors with DIFFERENT outputs -> two distinct H for the merge step (both execute, distinct output keys); two predecessors with IDENTICAL output -> same H -> one execution (req-5 acceptance).
- N x M fan-out: a multi-result manifest yields N items; orchestrator dispatches one successor per (item, NextStep) with successor stepId/procId + item EntryId; a re-deliver reuses the same H_child -> no extra dispatch (req-6 acceptance).
- Empty-result: one result with empty-manifest EntryId -> zero successors AND acked (req-3).

**Tier 3 - Real-stack E2E (clone `SampleRoundTripE2ETests`):**
- The StepB4-x2 INVERSE: build a merge topology test-side (two predecessor entry steps feeding one successor step via `NextStepIds`), run across cron fires PLUS an induced duplicate (re-publish the same `EntryStepDispatch`, and/or a throw-once test processor forcing `Immediate(N)` re-run).
- Assert: per `CorrelationId`, the ES downstream-effect set equals the expected per-fire set with ZERO duplicates, even with the induced retry (req-8 acceptance).
- Close gate: 3-consecutive-GREEN + triple-SHA BEFORE==AFTER holds with the new `skp:data:` (64-hex) and `skp:flag:` namespaces covered by scan-clean teardown (req-8 + D-12).

### Observable signals to sample
- **ES (Elasticsearch via `PollEsForLog`):** the set of advanced/executed downstream effects per `CorrelationId` (term on `attributes.WorkflowId`/`CorrelationId`, scoped by `resource.attributes.service.name`). The StepB4 line must appear EXACTLY the expected number of times.
- **Redis scan-clean:** `skp:data:*` and `skp:flag:*` empty after teardown (net-zero; triple-SHA BEFORE==AFTER).
- **Attempt counts:** for the retry-config test, count `Consume`/attempt invocations against the configured `Limit`.
- **Flag transition:** `flag[H]` observably becomes `Ack` exactly once (Tier-1/2 assertion).

### Phase Requirements -> Test Map
| Req | Behavior | Tier | Automated command (representative) | File exists? |
|-----|----------|------|-------------------------------------|-------------|
| req-1 | H deterministic, executionId-invariant | unit | `dotnet test --filter "FullyQualifiedName~HashHelper"` | Wave 0 (new) |
| req-2 | entry-step EntryId = hash(corr,stepId); InputDefinition==null source | unit+integration | `--filter "FullyQualifiedName~FireDispatch"` (update) | exists (update) |
| req-3 | content-addressed two-level + empty terminal | unit+integration | `--filter "FullyQualifiedName~DispatchOutputWrite"` (update) | exists (update) |
| req-4 | effect-first CAS dedup both hops | integration | `--filter "FullyQualifiedName~DedupCas"` | Wave 0 (new) |
| req-5 | merge distinct-H / collapse | integration | `--filter "FullyQualifiedName~Merge"` | Wave 0 (new) |
| req-6 | manifest N x M fan-out + redeliver dedup | integration | `--filter "FullyQualifiedName~ManifestFanout"` | Wave 0 (new) |
| req-7 | configurable retry + 64-hex key golden | unit | `--filter "FullyQualifiedName~RetryOptions|KeyBuilder"` | Wave 0 (new) |
| req-8 | live real-stack exactly-once proof | E2E | `dotnet test --filter "Category=RealStack&FullyQualifiedName~ExactlyOnce"` | Wave 0 (clone of SampleRoundTripE2ETests) |

### Sampling Rate
- **Per task commit:** hermetic filter (`Category!=RealStack&Category!=E2E`).
- **Per wave merge:** full hermetic + integration suite.
- **Phase gate:** full suite (incl. real-stack) green + `phase-31-close.ps1` 3xGREEN + triple-SHA before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Contracts/HashHelperGoldenFacts.cs` (or similar) — H determinism + key-builder golden + SourceHash parity (req-1, req-7).
- [ ] `tests/BaseApi.Tests/Processor/EffectFirstDedupFacts.cs` — CAS property + crash-window (req-4).
- [ ] `tests/BaseApi.Tests/Orchestrator/ManifestFanoutFacts.cs` — N x M + redeliver dedup + empty terminal (req-3, req-6).
- [ ] `tests/BaseApi.Tests/Orchestrator/MergeCollapseFacts.cs` — distinct-H vs collapse (req-5).
- [ ] `tests/BaseApi.Tests/Orchestrator/RetryOptionsBindFacts.cs` — appsettings bind + attempt count (req-7/D-10).
- [ ] `tests/BaseApi.Tests/Orchestrator/IdempotentExactlyOnceE2ETests.cs` — clone of `SampleRoundTripE2ETests` with merge topology + induced duplicate (req-8).
- [ ] `phase-31-close.ps1` — clone close gate; extend scan-clean to `skp:flag:*` + 64-hex `skp:data:*` (D-12).
- [ ] UPDATE existing EntryId-Guid assertions across ~12 test files (Pitfall 1 inventory) as part of the Guid->string wave-0 task.

## Security Domain

`security_enforcement` is not set to `false` in config.json, but this phase is internal framework messaging with no new external trust boundary, auth, or PII. The relevant ASVS surface is unchanged from prior phases:

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V5 Input Validation | yes | output-schema validation runs on each result DATA blob (D-09, existing `ProcessorJsonSchemaValidator`); the manifest is internally generated hex (not user input) |
| V6 Cryptography | partial | SHA-256 used for CONTENT-ADDRESSING and dedup identity, NOT for security (no secret, no auth) — collision-resistance of SHA-256 is the only property relied on; do not hand-roll the digest (use BCL `SHA256`) |
| V7 Info Disclosure | yes | existing precedent: never interpolate ids into log templates; H/EntryId go only into scope VALUES under fixed keys (`InboundExecutionScopeConsumeFilter` / T-18-04). The `SourceHashProvider` names KEY only, never value — keep this for any new flag/hash logging. |

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Hash input ambiguity (delimiter injection) | Tampering | reserved separator byte that cannot appear in Guid "D"/hex text (D-03) |
| Redis key injection via EntryId | Tampering | EntryId is a derived 64-hex from a server-side hash, never user-supplied free text -> `^[a-f0-9]{64}$` |

No new auth/session/access-control surface (V2/V3/V4 unchanged — this is intra-cluster messaging behind the existing boundary).

## Open Questions

1. **Failed/Cancelled result EntryId after Guid->string (Discretion-adjacent).**
   - What we know: today `BuildFailed`/`BuildCancelled` set `EntryId = Guid.Empty` (no data written). After the type change there is no `Guid.Empty`.
   - What's unclear: the planner must pick the string sentinel for "no output" on a Failed/Cancelled result (empty string `""` vs `hash("[]")`). The orchestrator `ResultConsumer` must handle it (a Failed result fans to no successor via `SelectNext` outcome-match anyway).
   - Recommendation: use `hash("[]")` (the empty-manifest EntryId) for consistency — a Failed result then naturally has a terminal manifest; OR `""` if the receiver short-circuits on outcome before reading the manifest. Planner's call; pin it in the plan and test it.

2. **Per-process RetryOptions: one shared record or two copies (explicit Discretion in D-10/CONTEXT).**
   - What we know: Orchestrator (3 ConsumerDefinition sites) and BaseProcessor.Core (1 inline bind) are separate processes with separate appsettings.
   - Recommendation: a shared `RetryOptions` record type in a leaf both reference (e.g. `Messaging.Contracts` or a config leaf), bound independently per process via `IOptions`. The record SHAPE is shared; the BOUND VALUES are per-process. This is the single-source-of-truth D-10 wants for Phase 32's `GetRetryAttempt()==Limit` check.

3. **Merge topology fixture for req-8 (test-construction detail).**
   - What we know: there is NO literal "StepB4" named fixture in the tree — it is a live-stack observation. `SampleRoundTripE2ETests` seeds a single-entry workflow.
   - Recommendation: the E2E seeds TWO entry steps feeding ONE successor (`NextStepIds` from both predecessors -> the merge step), mirroring the dual-pipeline merge the SPEC describes; assert the merge step's downstream effect appears exactly once per fire (collapse) or per-distinct-input (distinct H), per req-5/req-8.

## Environment Availability

The real-stack E2E (req-8) depends on the live compose stack (same as `SampleRoundTripE2ETests`):

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Redis (host :6380) | L2 data/flag + scan-clean | assumed (E2E precedent) | - | hermetic Redis-fake for Tier 1/2 |
| RabbitMQ (host :5673) | round-trip transport | assumed | - | MassTransit in-memory harness for Tier 1/2 |
| Postgres (host :5433) | processor row seed | assumed | - | — (E2E only) |
| OTLP/Elasticsearch (:4317) | `PollEsForLog` proof | assumed | - | — (E2E only) |
| processor-sample container | genuine SourceHash round-trip | assumed (rebuilt with new contract) | - | — (E2E only) |

**Missing dependencies with no fallback:** none for the hermetic/integration tiers (the bulk of validation). The real-stack tier requires the full compose stack up — standard for this repo's E2E and gated by `Category=RealStack`.
**Note:** the processor-sample container MUST be rebuilt with the new wire contract before the E2E (contract change is not backward-compatible across a mixed deployment — see Runtime State Inventory).

## Project Constraints (no CLAUDE.md present)

There is NO `./CLAUDE.md` at the repo root or anywhere in the tree (verified via Glob `**/CLAUDE.md` -> no files). Project conventions are instead encoded in the code and prior-phase decisions; the load-bearing ones for this phase:
- L2 key shapes are owned by ONE source of truth (`L2ProjectionKeys` in Messaging.Contracts), forwarded by thin shims (`RedisProjectionKeys`, `OrchestratorL2Keys`) — add the `Flag` builder and change `ExecutionData` HERE, never in a shim.
- Hex is LOWERCASE 64-hex (`^[a-f0-9]{64}$`, `b.ToString("x2")`) — the SourceHash convention.
- Redis INFRA faults propagate (no catch) -> retry; only ProcessAsync/business outcomes are caught-and-acked.
- Ids go into log SCOPE values under fixed keys, never into message templates (T-18-04).
- No Lua in the codebase; `StringSet When.Exists/NotExists` + `IBatch` are the Redis primitives.

## Sources

### Primary (HIGH confidence)
- Codebase (read this session): `EntryStepDispatch.cs`, `ExecutionResult.cs`, `IExecutionCorrelated.cs`, `L2ProjectionKeys.cs`, `SourceHash.targets`, `AssemblyMetadataSourceHashProvider.cs`, `HashHelpers.cs`, `EntryStepDispatchConsumer.cs`, `ResultConsumer.cs`, `StepAdvancement.cs`, `WorkflowFireJob.cs`, `StepDispatcher.cs`/`IStepDispatcher.cs`, the 4 retry sites, `RedisProjectionWriter.cs`, `SampleRoundTripE2ETests.cs`, `InboundExecutionScopeConsumeFilter.cs`, `StepOutcome.cs`, `StepEntryCondition.cs`, `StepDtoValidator.cs`, `ProcessResult.cs`, `StepProjection.cs`, `ICorrelated.cs`. Grep inventories for EntryId/Guid.Empty/IDEM.
- `31-SPEC.md`, `31-CONTEXT.md`, `31-DISCUSSION-LOG.md`, `.planning/config.json`, `.planning/STATE.md`.

### Secondary (MEDIUM confidence, verified)
- [redis.io SET command](https://redis.io/docs/latest/commands/set/) — NX/XX flag semantics (XX = set only if exists).
- [StackExchange/StackExchange.Redis#1578](https://github.com/StackExchange/StackExchange.Redis/issues/1578) — `When.Exists`/`When.NotExists` return false when the condition fails (not throw).
- [StackExchange/StackExchange.Redis#2071](https://github.com/StackExchange/StackExchange.Redis/issues/2071) — `StringSet` `when` parameter + bool return behavior.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all primitives in-tree, verified by reading.
- Architecture / decided approach: HIGH — every CONTEXT-named file matches its snapshot; protocol confirmed sound.
- `When.Exists` semantics: HIGH — Redis SET XX + StackExchange.Redis issue confirmation.
- Blast radius (EntryId Guid->string): HIGH — enumerated by grep across src + tests.
- Pitfalls: HIGH — grounded in the actual current shapes.

**Research date:** 2026-06-04
**Valid until:** ~2026-07-04 (stable internal stack; only StackExchange.Redis `When` semantics could shift, and that is long-stable).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The live compose stack (Redis/RMQ/PG/OTLP/processor-sample) is available for the req-8 E2E, as the existing `SampleRoundTripE2ETests` assumes | Environment Availability | E2E tier cannot run; hermetic+integration tiers still cover reqs 1-7 |
| A2 | `JsonSerializer.Serialize(string[])` of a lowercase-hex array is byte-stable across both processes (default STJ, no custom options) | Pitfall 2 | hash(manifest) could differ cross-process — mitigated by the golden test (Tier 1) which would catch it before integration |

All other claims are VERIFIED against code or CITED to Redis/StackExchange.Redis docs.

## RESEARCH COMPLETE
