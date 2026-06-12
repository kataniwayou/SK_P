# Phase 55: Live Proof & Close Gate - Research

**Researched:** 2026-06-12
**Domain:** Real-stack E2E adaptation (v4 → v5) + triple-SHA net-zero close gate (PowerShell + xUnit v3 / Microsoft.Testing.Platform)
**Confidence:** HIGH — every CONTEXT.md assumption was grounded against the actual current source; the deltas below are file:line-cited.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** Hybrid reshape of the three v4 SC E2E tests:
  - **SC3** (`SC3PauseResumeOutageE2ETests`): retag essentially **as-is** (A14 BIT gate + global pause/resume retained unchanged in v5). Verify the keeper still `bus.Publish`es `PauseAll`/`ResumeAll` on each health edge.
  - **SC1** (`SC1RoundTripE2ETests`): **adapt** to additionally assert the v5 slot-array allocation-index write `skp:msg:{messageId}` (written **before** the data key) and the A19 two-key net-zero at end-of-message.
  - **SC2** (`SC2RecoveryPathsE2ETests`): **rewrite/extend** for the v5 3-state keeper (DELETE becomes the A19 **both-key** delete) AND add the organic recovery-pass proof (D-03).
- **D-02:** Re-tag all SC tests `[Trait("Phase","55")]` (from `"49"`).
- **D-03:** Prove the recovery pass **organically** — leave a populated slot-array index in L2 and drive a recovery (re-fire): HGETALL → re-send each completed step with a fresh `NewId.NextGuid()` exec (SLOT-03 send-before-retire) → retire slot to `Guid.Empty` → A19 two-key delete net-zero. The `if exist L2[messageId]` branch.
- **D-04:** ALSO keep **per-state direct-publish** proofs (as v4 SC2), published straight at `queue:keeper-recovery`: REINJECT data-present, REINJECT data-gone (silent drop, no DLQ), INJECT, DELETE (A19 both-key).
- **D-05:** Clone `scripts/phase-49-close.ps1` → `scripts/phase-55-close.ps1`, keeping the triple-SHA protocol **verbatim**: idempotent Processor-row seed, compose-health pre-flight, BOTH-config 0-warning build gate, **N=3** consecutive-GREEN identical-fact-count cadence, `psql \l` SHA, **unfiltered** `redis-cli --scan` SHA, `rabbitmqctl -q list_queues name` SHA (NOT `name messages`), separate `skp-dlq-1` depth==0 check, steady-state exclusions (`skp:{procId:D}` liveness + `_bus_` transient queues).
- **D-06:** v5 net-zero deltas vs v4 script:
  - **(a)** DROP the retired composite-backup namespace `skp:{corr}:{wf}:{proc}:{exec}` and its no-settle-wait note.
  - **(b)** `skp:msg:*` (index) + `skp:data:*` (data) are net-zero targets, captured by the unfiltered `--scan` SHA.
  - **(c)** ADD an explicit additive **`skp:msg:*` count==0** assertion (parallel to `skp-dlq-1` depth==0) proving the A19 **active** reclaim. Net-zero is proven by the active two-key DELETE, NOT a TTL settle (the `skp:msg` random TTL is 300/600s).
- **D-07:** Net-zero discipline stays in the E2E **teardown** (`factory.L2KeysToCleanup`). NO gate-side destructive flush, NO prefix filter narrowing the scan.
- **D-08:** **Build gate runs FIRST** (the phase's autonomously-verifiable deliverable): `dotnet build SK_P.sln -c Release` AND `-c Debug` both 0-warning, AND the new/adapted RealStack E2E tests COMPILE. The `phase-55-close.ps1` script exists and is syntactically valid.
- **D-09:** The live **N=3×GREEN** triple-SHA close run is **operator-gated** via a HUMAN-UAT runbook (`55-HUMAN-UAT.md`). Requires the rebuilt v5 docker stack. TEST-01/02 stay unticked until the operator's GREEN run.
- **D-10:** N=3 consecutive GREEN with an identical-fact-count Smell-A guard (phase-39/49 precedent).
- **D-11:** Stable Processor row seeded idempotently (genuine embedded SourceHash via GET-or-create on `uq_processor_source_hash`). **Verify the v5 Processor.Sample version** for the seed string.

### Claude's Discretion
- xUnit collection/parallelization shaping for the new organic recovery-pass test (it can share the shared `"Observability"` collection if it does NOT stop redis; SC3 already isolates the redis-outage in `RedisOutageSerialCollection`).
- Host-Redis polling / ES seam-log assertion mechanics — reuse SC1's `RealStackWebAppFactory` + `PollForHealthyLivenessAsync` + `ElasticsearchTestClient.PollEsForLog` precedent.
- The exact seed-string version value (verify against `src/Processor.Sample/appsettings.json`).

### Deferred Ideas (OUT OF SCOPE)
None — discussion stayed within the TEST-01/02 scope. This phase IS the milestone close gate.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| **TEST-01** | A RealStack E2E proves the forward pass + the recovery pass + each keeper state (REINJECT present/absent, INJECT, DELETE) under the new model. | SC1 (forward + slot-index + A19 net-zero) + SC2 (3-state direct-publish + organic recovery pass). Production code paths all verified present: `ProcessorPipeline.RunForwardAsync`/`RunRecoveryAsync`/`DeleteTerminalAsync`; `ReinjectConsumer`/`InjectConsumer`/`DeleteConsumer`. |
| **TEST-02** | The close gate runs N-consecutive-GREEN + triple-SHA (psql `\l` / redis `--scan` / rabbitmq `list_queues`) BEFORE==AFTER net-zero — including the slot-array index keys + data keys — at Release + Debug 0-warning. | `scripts/phase-49-close.ps1` is the verbatim template; deltas D-06(a/b/c) are line-cited below. The A19 active two-key DELETE (`DeleteTerminalAsync`, `DeleteConsumer`) makes `skp:msg:*` net-zero a production property, not a TTL race. |
</phase_requirements>

## Summary

Phase 55 is an **adaptation**, not new infrastructure. The v4.0.0 Phase-49 close gate (`scripts/phase-49-close.ps1`) and the SC1/SC2/SC3 RealStack E2E suite already exist, are hermetically green, and shipped a recorded live 3×GREEN run on 2026-06-10 (`49-HUMAN-UAT.md`). The job is to (a) retag the three SC tests `Phase=55`, (b) teach SC1 to assert the new `skp:msg:{messageId}` allocation index + the A19 two-key net-zero, (c) rewrite/extend SC2 for the 3-state keeper (the DELETE is now a **both-key** delete) and add an organic recovery-pass proof, (d) clone the close script with three small v5 deltas, and (e) write the operator runbook. The build gate (0-warning Release+Debug + E2E compile) is the autonomously-verifiable deliverable; the live N×GREEN run is operator-gated.

The single most important grounded fact: the A19 active two-key delete is **real and shipped** (`ProcessorPipeline.DeleteTerminalAsync` issues one `KeyDeleteAsync(new RedisKey[]{ ExecutionData(d.EntryId), MessageIndex(messageId) })`, and `DeleteConsumer` does the same on escalation). This is what converts the v4 "net-zero by TTL race" into a v5 "net-zero as a deterministic production property" — and is why D-06(c)'s explicit `skp:msg:*` count==0 assertion is meaningful rather than redundant.

**Primary recommendation:** Treat the three v4 SC files + `phase-49-close.ps1` as the authoritative scaffolds and apply the minimal, line-cited deltas in this document. Do NOT re-derive the harness, the seeding flow, the SHA protocol, or the steady-state exclusions — they are proven. Focus implementation effort on the three genuinely v5-new mechanics: the `skp:msg:*` index assertions (SC1), the both-key DELETE proof (SC2), and the organic recovery pass (SC2). Three **landmines** below correct CONTEXT.md against the code (the seed version, the keeper contract type names, and a stale composite-sweep that must be removed).

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Forward pass (dispatch → slot-index write → data write → orchestrator advance) | Processor (`BaseProcessor.Core`) | Orchestrator (consumes result) | `ProcessorPipeline.RunForwardAsync` owns the Post writes; the SC1 E2E observes via host Redis + ES |
| Recovery pass (`exist L2[messageId]` → HGETALL → re-send → retire → two-key DEL) | Processor (`BaseProcessor.Core`) | — | `ProcessorPipeline.RunRecoveryAsync` + `DeleteTerminalAsync`; SC2 organic proof drives a re-fire |
| 3-state keeper (REINJECT/INJECT/DELETE) | Keeper (`src/Keeper/Recovery`) | Redis (L2), RabbitMQ (re-inject/result/DLQ) | `ReinjectConsumer`/`InjectConsumer`/`DeleteConsumer`; SC2 direct-publish drives each |
| BIT health gate + global pause/resume (A14) | Keeper (`BitHealthLoop`) | Orchestrator (`PauseAllConsumer`/`ResumeAllConsumer`) | Unchanged from v4; SC3 retags as-is |
| Net-zero close gate (triple-SHA) | Operator script (`scripts/phase-55-close.ps1`) | E2E teardown (`L2KeysToCleanup`) | The gate snapshots-and-compares; the E2E teardown does the active cleanup |
| Test isolation / liveness gating | Test harness (`RealStackWebAppFactory`) | — | In-proc WebApi pointed at host stack; polls real container heartbeat |

## Standard Stack

### Core
| Component | Version / Identifier | Purpose | Why Standard (verified) |
|-----------|---------------------|---------|--------------------------|
| xUnit v3 | `xunit.v3` (CPM, via `Directory.Packages.props`) | E2E test framework | `tests/BaseApi.Tests/BaseApi.Tests.csproj:64`. xUnit v3 3.2.2 runs under Microsoft.Testing.Platform (MTP). |
| Microsoft.Testing.Platform (MTP) | via `<UseMicrosoftTestingPlatformRunner>true</>` + `<TestingPlatformDotnetTestSupport>true</>` | Test runner host | `BaseApi.Tests.csproj:40,52`; `<OutputType>Exe</OutputType>` at `:28`. |
| StackExchange.Redis | `StackExchange.Redis` (CPM) | Host-Redis polling/scan/teardown in the E2E | `BaseApi.Tests.csproj:113`. Used by all three SC factories. |
| MassTransit / `IBus` | (CPM) | SC2 direct-publish to `queue:keeper-recovery`; the in-proc bus | `SC2RecoveryPathsE2ETests.cs:79` resolves `IBus` from `factory.Services`. |
| PowerShell (`pwsh`) | — | The close-gate script host | `scripts/phase-49-close.ps1` (PowerShell, `Set-StrictMode -Version Latest`). |
| `docker` / `docker compose` / `rabbitmqctl` / `redis-cli` / `psql` | host CLI | Live-stack control + triple-SHA capture | Shelled out from the script + from SC2/SC3 via `System.Diagnostics.Process`. |

### Supporting (production code under proof — all verified present)
| Symbol | File:Line | Role in proof |
|--------|-----------|---------------|
| `ProcessorPipeline.RunAsync` | `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:85` | The `exist L2[messageId]` branch: forward vs recovery |
| `ProcessorPipeline.RunForwardAsync` | `:187` | Post writes the index FIRST (`HashSetAsync` `:262`) then data (`StringSetAsync` `:275`) — allocation-before-data (SC1 D-01 assertion) |
| `ProcessorPipeline.RunRecoveryAsync` | `:121` | HGETALL (`:126`) → classify → send-before-retire (`SendResult` `:157` then `HashSetAsync` retire to `RetiredSlot` `:161`) → tail |
| `ProcessorPipeline.DeleteTerminalAsync` | `:307` | The A19 two-key `DEL`: `KeyDeleteAsync(new RedisKey[]{ ExecutionData(d.EntryId), MessageIndex(messageId) })` (`:311-315`) |
| `ReinjectConsumer.HandleAsync` | `src/Keeper/Recovery/ReinjectConsumer.cs:28` | REINJECT: STRLEN gate (`:33`); present → re-inject `EntryStepDispatch` to `queue:{ProcessorId:D}` (`:43-55`); absent → silent drop + `metrics.ReinjectDropped.Add(1)` (`:38-40`), NO DLQ |
| `InjectConsumer.HandleAsync` | `src/Keeper/Recovery/InjectConsumer.cs:22` | INJECT: write `L2[m.EntryId]=m.Data` (`:25`) → send `StepCompleted` to `queue:orchestrator-result` (`:36-37`) → delete `L2[m.DeleteEntryId]` (`:40`) |
| `DeleteConsumer.HandleAsync` | `src/Keeper/Recovery/DeleteConsumer.cs:19` | DELETE: one `KeyDeleteAsync(new RedisKey[]{ ExecutionData(m.EntryId), MessageIndex(m.MessageId) })` (`:20-24`) — **both-key** |
| `BitHealthLoop.ExecuteAsync` | `src/Keeper/Health/BitHealthLoop.cs:29` | Edge-triggered `bus.Publish(PauseAll)` on close (`:67`) / `ResumeAll` on open (`:57`) — **unchanged from v4** (SC3 retags as-is) |
| `L2ProjectionKeys.ExecutionData` | `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:42` | `skp:data:{entryId:D}` |
| `L2ProjectionKeys.MessageIndex` | `:48` | `skp:msg:{messageId:D}` (Redis HASH of int-slot → entryId; the A19 net-zero target) |

### Constants (verified)
| Constant | Value | File:Line |
|----------|-------|-----------|
| `KeeperQueues.Recovery` | `"keeper-recovery"` | `src/Messaging.Contracts/KeeperQueues.cs:12` |
| `OrchestratorQueues.Result` | `"orchestrator-result"` | `src/Messaging.Contracts/OrchestratorQueues.cs:16` |
| `ConsolidatedErrorTransportFilter.Dlq1` | `"skp-dlq-1"` | `src/BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs:49` |
| `SlotArrayOptions.SlotArrayTtlMinSeconds` | `300` | `src/BaseProcessor.Core/Configuration/SlotArrayOptions.cs:20` |
| `SlotArrayOptions.SlotArrayTtlMaxSeconds` | `600` | `:23` |
| `Processor.Sample` `Service:Version` | `"3.5.0"` | `src/Processor.Sample/appsettings.json:11` |

## Architecture Patterns

### System Architecture Diagram (the proof's data flow)

```
                            ┌──────────────────────────────────────────────────┐
                            │  scripts/phase-55-close.ps1 (operator-gated)       │
                            │  1 seed Processor row  2 compose health pre-flight │
                            │  3 BEFORE triple-SHA   4 build gate (Rel+Dbg 0-w)  │
                            │  5 N=3 GREEN cadence    6 settle  7 AFTER SHA       │
                            │  8 skp-dlq-1==0  +  NEW skp:msg:* count==0 (D-06c)  │
                            └───────────────┬──────────────────────────────────┘
                                            │ runs full suite live (Phase=55 RealStack run)
                            ┌───────────────▼──────────────────────────────────┐
                            │  RealStackWebAppFactory (in-proc WebApi → host)    │
                            │  RMQ:5673  Redis:6380  PG:5433  otel:4317          │
                            └───┬────────────────┬────────────────┬─────────────┘
            SC1 forward         │       SC2       │      SC3       │
   ┌────────────────────────┐  │  ┌───────────┐  │ ┌───────────┐  │
   │ POST /start (cron fire) │  │  │ direct-   │  │ │ docker     │ │
   │  → dispatch → processor │  │  │ publish   │  │ │ stop/start │ │
   │  Post: HSET skp:msg     │  │  │ to        │  │ │ sk-redis   │ │
   │  (slot) FIRST, then SET │  │  │ queue:    │  │ │ → BIT edge │ │
   │  skp:data; advance      │  │  │ keeper-   │  │ │ → Pause/   │ │
   │  → A19 two-key DEL       │ │  │ recovery  │  │ │ ResumeAll  │ │
   └───────────┬────────────┘  │  └─────┬─────┘  │ └─────┬─────┘  │
               │ assert         │        │ assert │       │ assert │
   ┌───────────▼────────────┐  │  ┌──────▼─────┐  │ ┌─────▼──────┐ │
   │ host Redis: new skp:    │  │  │ REINJECT/  │  │ │ ES seam:   │ │
   │ data:* AND skp:msg:*    │  │  │ INJECT/    │  │ │ Global     │ │
   │ then BOTH gone at end   │  │  │ DELETE     │  │ │ PauseAll / │ │
   │ ES: orchestrator advance│  │  │ effects +  │  │ │ ResumeAll  │ │
   │                         │  │  │ ORGANIC    │  │ │ + no-output│ │
   │                         │  │  │ recovery   │  │ │ during     │ │
   │                         │  │  │ pass       │  │ │ pause win. │ │
   └─────────────────────────┘  │  └────────────┘  │ └────────────┘ │
                                 └─────────── net-zero teardown ─────┘
                                   factory.L2KeysToCleanup (active delete)
```

File-to-implementation mapping is in the Standard Stack tables above; the diagram traces data flow only.

### Pattern 1: Truthful liveness gate (reuse verbatim)
**What:** Read the GENUINE embedded `SourceHash` off the built `Processor.Sample.dll`, GET-or-create the Processor row by that hash, then POLL host Redis for the real container's `skp:{procId:D}` heartbeat before driving Start. No synthetic liveness seed.
**Where:** `SC1RoundTripE2ETests.cs:88-105` (`PollForHealthyLivenessAsync` `:191-230`). Cloned identically in SC3.
**Reuse:** unchanged for v5. The new organic recovery test reuses the same harness.

### Pattern 2: Net-zero teardown via `L2KeysToCleanup` (reuse, with one deletion — see Landmine 3)
**What:** Every minted `skp:*` key is registered into `factory.L2KeysToCleanup` and drained in `DisposeAsync`; parent-index members are SREMed.
**Where:** `SC1RoundTripE2ETests.cs:417-453`, `SC2...:434-488`, `SC3...:577-613`.
**v5 change:** The new `skp:msg:{messageId}` index keys minted by the organic recovery test + any SC1 forward run must ALSO be registered. The A19 production path actively deletes them, so in the happy path they self-clean; registration is the belt-and-suspenders that surfaces a leak as a SHA mismatch (D-07).

### Pattern 3: Direct-publish per-state proof (SC2 — reuse the shape, change the contracts)
**What:** Resolve `IBus`, `GetSendEndpoint(new Uri($"queue:{KeeperQueues.Recovery}"))`, `Send` the actual keeper contract, assert the deterministic L2/queue effect.
**Where:** `SC2RecoveryPathsE2ETests.cs:79-219`.

### Anti-Patterns to Avoid
- **Re-deriving the SHA protocol or steady-state exclusions.** They are proven (`phase-49-close.ps1:209,215`). Clone verbatim; change only D-06(a/b/c).
- **Asserting `skp:msg:*` net-zero by waiting out its TTL.** The TTL is 300/600s and "cannot be waited out" the way phase-39's short-TTL keys were. Net-zero is proven by the active two-key DELETE + the explicit count==0 assertion (D-06c).
- **Using `dotnet test --filter`.** It is ignored under MTP (see Pitfall 1). Use `--filter-trait` / `--filter-not-trait`.
- **Leaving the v4 composite sweep in the factory teardown.** Model-B is retired (see Landmine 3).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Real-stack host wiring | A new `WebApplicationFactory` subclass | The existing `RealStackWebAppFactory` (cloned in each SC file) | Env-var-in-ctor host overrides + net-zero teardown are proven |
| Liveness gating | A synthetic `skp:{procId}` seed | `PollForHealthyLivenessAsync` (`SC1...:191`) | A synthetic seed false-passes with the container stopped |
| Queue-depth reads | A direct AMQP client | `docker exec sk-rabbitmq rabbitmqctl -q list_queues name messages` (`SC2...:294`) | Already TAB-parsed; matches the gate's mechanism |
| Triple-SHA capture | A new net-zero scheme | `phase-49-close.ps1` SHA-256 over sorted `--scan` / `list_queues name` / `psql -lqt` | Three milestones of precedent (39/49) |
| ES seam-log read | A bespoke ES query | `ElasticsearchTestClient.PollEsForLog` + the SC1/SC3 query shapes | SC3's `prefix`-on-`body.text` gotcha is already solved (`SC3...:271-284`, GAP-49-4) |

**Key insight:** The harness, the seeding flow, the SHA protocol, the exclusions, and the ES query shapes are all load-bearing and already debugged across the Phase-49 GREEN run (which surfaced and closed GAP-49-1..10). Re-deriving any of them re-opens closed gaps.

## Runtime State Inventory

This phase reshapes tests + a script; it mints/asserts live runtime state but does not rename anything. The relevant runtime state is the keyspace/queue topology the close gate snapshots.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data (Redis) | `skp:data:{entryId:D}` (data) + `skp:msg:{messageId:D}` (slot-array HASH) are minted per round-trip; the steady-state `skp:{procId:D}` liveness key + the `skp:` parent-index SET persist | E2E teardown registers every minted `skp:data:*`/`skp:msg:*` into `L2KeysToCleanup`; the gate excludes only `skp:{procId:D}` from the SHA |
| Live service config | None requiring change — the v5 contract change requires the operator to **rebuild** `baseapi-service orchestrator processor-sample keeper` (D-09), but this is a runbook step, not stored config | Document the rebuild in `55-HUMAN-UAT.md` (mirrors `49-HUMAN-UAT.md` Step 1) |
| OS-registered state | None — no Task Scheduler / pm2 / systemd registrations involved | None |
| Secrets/env vars | None — the E2E factory sets host-pointing env vars in-ctor and restores them in teardown (`Set`/`Restore`, `SC1...:396-408`) | None |
| Build artifacts | `Processor.Sample.dll` embedded `SourceHash` must match the running container (an incremental host build leaving a stale hash bit Phase-49 — see `49-HUMAN-UAT.md` "Operational — RESOLVED"). The seed reads the hash off the **built** dll. | Runbook must instruct a clean `dotnet clean + build` so host hash == container hash before the live run |

**RETIRED state that must NOT appear (Model-B teardown verified in Phases 50/53):** the composite backup key namespace `skp:{corr}:{wf}:{proc}:{exec}` is gone. A grep of `src/` for `CompositeBackup` returns nothing; `54-SPEC.md` confirms "no `KeyDelete(MessageIndex(...))` anywhere" was the pre-A19 state and Model-B was retired. **The v4 factory teardown still contains a composite sweep that must be deleted — see Landmine 3.**

## Common Pitfalls

### Pitfall 1: `dotnet test --filter` is silently ignored (MTP0001)
**What goes wrong:** Using `dotnet test --filter "Category=RealStack"` to scope the suite. Under Microsoft.Testing.Platform the legacy VSTest `--filter` is ignored.
**Why it happens:** `BaseApi.Tests.csproj` sets `<UseMicrosoftTestingPlatformRunner>true</>` + `<TestingPlatformDotnetTestSupport>true</>` (`:40,52`), routing `dotnet test` to the MTP runner.
**How to avoid:** Use the MTP-native trait filters via `dotnet run`:
- Hermetic (exclude RealStack): `dotnet run --project tests/BaseApi.Tests -- --filter-not-trait Category=RealStack` — verified in `49-01-SUMMARY.md:75` (507 passed, RealStack excluded).
- Phase-scoped live: `dotnet run --project tests/BaseApi.Tests -- --filter-trait "Phase=55"`.
**Warning signs:** A `--filter` run that includes RealStack facts and fails against a not-running broker, or that runs the full suite when you expected a subset.
**Note on the close script:** `phase-49-close.ps1:242` runs `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release --no-build` with **NO** filter — the gate deliberately runs the FULL suite (RealStack included) live. The clone keeps this. The filter syntax matters for the runbook's hermetic-compile check + ad-hoc operator runs, NOT the gate's cadence loop.

### Pitfall 2: The `skp:msg:*` index TTL cannot be settle-waited
**What goes wrong:** Assuming the index drains like phase-39's short-TTL keys.
**Why it happens:** `SlotArrayOptions` defaults the whole-HASH random TTL to 300–600s (`SlotArrayOptions.cs:20,23`).
**How to avoid:** Rely on the A19 active two-key DELETE (`DeleteTerminalAsync:311-315`) for net-zero, and add the explicit `skp:msg:*` count==0 assertion (D-06c). A lingering index then surfaces as BOTH a redis SHA mismatch AND count>0 — never a silent TTL pass.

### Pitfall 3: SC3's `docker stop sk-redis` destabilizes siblings
**What goes wrong:** Running SC3 in the shared `"Observability"` collection lets the redis outage break any sibling RealStack test touching L2.
**Why it happens:** SC3 stops the shared host Redis.
**How to avoid:** SC3 already lives in `[CollectionDefinition("RedisOutageSerial", DisableParallelization = true)]` (`SC3...:23-24`). Keep it there. The new organic recovery test does NOT stop redis, so per D-03 discretion it may share `"Observability"`.

### Pitfall 4: `_bus_` transient queue churn on bus reconnect
**What goes wrong:** Folding MassTransit `*_bus_*` auto-delete temporary queues into the rabbitmq name-SHA churns it on any bus reconnect (SC3's outage forces a reconnect with a new random suffix).
**How to avoid:** The gate already excludes them (`phase-49-close.ps1:215` `Where-Object { $_ -notmatch '_bus_' }`). Keep verbatim (GAP-49-9).

### Pitfall 5: Liveness-key flap in the SHA
**What goes wrong:** The `skp:{procId:D}` heartbeat key flaps with the TTL race and briefly vanishes during the SC3 outage, churning the redis SHA.
**How to avoid:** The gate excludes exactly that one key (`phase-49-close.ps1:209` `Where-Object { $_ -ne "skp:$($procId...)" }`). Keep verbatim (GAP-49-10). This is the ONLY `skp:` key excluded — the scan is otherwise unfiltered (D-07).

## Code Examples

### The A19 two-key DELETE under proof (production — SC1 net-zero + SC2 DELETE assert this)
```csharp
// Source: src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:310-315
var del = await RetryLoop.ExecuteAsync(
    () => db.KeyDeleteAsync(new RedisKey[]
    {
        L2ProjectionKeys.ExecutionData(d.EntryId),   // operand 1 (Guid.Empty → drop-on-absent on a source step)
        L2ProjectionKeys.MessageIndex(messageId),    // operand 2 (the index — actively reclaimed)
    }), limit, ct);
```
```csharp
// Source: src/Keeper/Recovery/DeleteConsumer.cs:19-24 (the keeper-side both-key delete, GC-03)
protected override async Task HandleAsync(KeeperDelete m, CancellationToken ct)
    => await Guard(() => Db.KeyDeleteAsync(new RedisKey[]
    {
        L2ProjectionKeys.ExecutionData(m.EntryId),
        L2ProjectionKeys.MessageIndex(m.MessageId),   // KeeperDelete now CARRIES MessageId
    }), ct);
```

### The organic recovery branch the D-03 test must drive (production)
```csharp
// Source: src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:94-103
var exists = await RetryLoop.ExecuteAsync(
    () => db.KeyExistsAsync(L2ProjectionKeys.MessageIndex(messageId)), limit, ct);
if (!exists.Succeeded) { await SendKeeper(BuildReinject(d), limit, ct); return; }
if (exists.Value)
    await RunRecoveryAsync(d, messageId, db, limit, ct);   // ← the if-exist branch D-03 drives
else
    await RunForwardAsync(d, messageId, db, limit, executionDataTtl, ct);
```
```csharp
// Source: ProcessorPipeline.cs:152-171 — send-before-retire (SLOT-03), fresh exec (D-03)
foreach (var t in temp) {
    if (!t.Completed) continue;
    await SendResult(BuildCompleted(d, NewId.NextGuid(), t.EntryId), limit, ct);  // SEND FIRST (fresh exec)
    var retire = await RetryLoop.ExecuteAsync(
        () => db.HashSetAsync(L2ProjectionKeys.MessageIndex(messageId), t.Slot, RetiredSlot), limit, ct);  // RetiredSlot = Guid.Empty
    // ... TTL refresh ...
}
// :174-182 — RECOV-03 tail: anyInfra → REINJECT (no delete) ⊻ all-clear → DeleteTerminalAsync (two-key DEL)
```

### The v5 close-gate deltas (clone of `phase-49-close.ps1`)
```powershell
# DELETE the v4 composite settle-GC block (phase-49-close.ps1:280-296) — Model-B retired (D-06a).
# DELETE the composite mention from the redis-mismatch error text (:345-347).

# ADD after the skp-dlq-1 depth==0 block (clone of :366-383), the D-06c additive index check:
$msgCount = (docker exec sk-redis redis-cli --scan --pattern 'skp:msg:*' | Measure-Object).Count
if ($msgCount -ne 0) {
    Write-Host "skp:msg:* count invariant VIOLATED: $msgCount (expected 0 — A19 active reclaim leaked an index)" -ForegroundColor Red
    $allGood = $false
} else {
    Write-Host "skp:msg:* count invariant HELD: 0 (A19 active two-key DEL reclaimed every index)" -ForegroundColor Green
}
# The unfiltered --scan SHA (:209/:316) already captures skp:msg:*/skp:data:* automatically (D-06b) — no scan change.
# The seed version string (:144) stays '3.5.0' (Landmine 1). The service list (:181) is unchanged (already includes keeper).
```

## State of the Art

| Old (v4.0.0, Phase 49) | New (v5.0.0, Phase 55) | When Changed | Impact on the proof |
|------------------------|------------------------|--------------|---------------------|
| Composite backup key `skp:{corr}:{wf}:{proc}:{exec}` (Model B) | RETIRED — processor-owned `skp:msg:{messageId}` slot array | Phase 50/53 | Drop the composite sweep + settle-GC + error text (D-06a). |
| 5-state keeper (`REINJECT/INJECT/DELETE/UPDATE/CLEANUP`) | 3-state keeper (`REINJECT/INJECT/DELETE`) | Phase 53 (RETIRE-03) | SC2 asserts 3 states, not 5. `UPDATE`/`CLEANUP` consumers gone. |
| Keeper `DELETE` deletes source only (`ExecutionData(entryId)`) | Keeper `DELETE` deletes BOTH keys (A19); `KeeperDelete` carries `MessageId` | Phase 54 (GC-03) | SC2's DELETE proof must seed AND assert BOTH `skp:data:{entryId}` AND `skp:msg:{messageId}` gone. |
| Index reclaimed passively by TTL | Index reclaimed actively by terminal two-key `DEL` (TTL demoted to crash-backstop) | Phase 54 (A19/GC-01) | Net-zero is a production property; D-06c count==0 is provable, not a TTL race. |
| `49-HUMAN-UAT.md`, `scripts/phase-49-close.ps1` | `55-HUMAN-UAT.md`, `scripts/phase-55-close.ps1` | This phase | Clone + retag. |

**Deprecated/outdated assumptions in CONTEXT.md (corrected — see Landmines):**
- D-11's hint that the seed version might be a v5 value: it is still `"3.5.0"`.
- additional_context #5's contract list naming `KeeperReinject, EntryStepDispatch, StepCompleted` as "the message contract types": the **keeper queue contracts** are `KeeperReinject`/`KeeperInject`/`KeeperDelete`; `EntryStepDispatch`/`StepCompleted` are the re-injected/sent payloads.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The live v5 docker images, when rebuilt, embed a `SourceHash` matching a clean host build of `Processor.Sample` | Runtime State / D-11 | If the host build is incremental/stale, the seed registers a hash the container can't resolve → liveness gate times out (this exact issue bit Phase 49; runbook mitigates with clean rebuild). VERIFIED as a historical failure mode, ASSUMED to recur if the runbook step is skipped. |
| A2 | The Phase-55 live `Passed` fact count will be a single stable number across 3 runs (no new flaky sibling) | Validation Architecture | A flaky sibling breaks the identical-fact-count Smell-A guard (bit Phase 49 as GAP-49-6). The cadence guard is designed to catch exactly this; risk is operator time, not a wrong gate. |
| A3 | `dotnet build SK_P.sln` is the correct solution build command and `TreatWarningsAsErrors` is repo-wide | D-08 build gate | If a project opts out of TreatWarningsAsErrors, a 0-warning claim could be hollow. `phase-49-close.ps1:228-234` relies on this; ASSUMED unchanged for v5. |

**Note:** A1–A3 are all operational/runbook risks with existing mitigations, not structural unknowns. Every structural claim (key shapes, contract fields, consumer bodies, recovery branch, BitHealthLoop, constants, seed version) was VERIFIED against source.

## Open Questions

1. **Does SC2's v4 `KeeperDelete` direct-publish need a `MessageId` now?**
   - What we know: `KeeperDelete` gained `MessageId` (`KeeperDelete.cs:13`); `DeleteConsumer` deletes both keys (`:20-24`). The v4 SC2 DELETE (`SC2...:208-213`) sends `KeeperDelete` WITHOUT `MessageId` and seeds/asserts only the data key.
   - What's unclear: Nothing structural — the rewrite must populate `MessageId` and pre-seed BOTH `skp:data:{entryId}` AND `skp:msg:{messageId}` then assert BOTH gone after the single `DEL`.
   - Recommendation: This is a known, scoped rewrite (D-04). Planner: SC2 STATE 4 must add a `MessageId`, seed a `skp:msg:{messageId}` HASH (e.g. `HashSetAsync(MessageIndex(messageId), 0, entryId)`), and assert both keys absent.

2. **Where does the organic recovery test seed its pre-populated slot array, and how does it re-fire?**
   - What we know: The recovery branch fires on `exist L2[messageId]` (`:94-103`). The orchestrator mints `messageId` as the MassTransit broker `MessageId` per dispatch — the test cannot pre-know it for an organic fire.
   - What's unclear: The cleanest organic driver. Options: (a) drive a forward fire, capture the broker MessageId, then re-publish the same `EntryStepDispatch` with that MessageId set (forcing the redelivery branch); or (b) pre-seed a `skp:msg:{messageId}` HASH with a known completed entryId, then publish an `EntryStepDispatch` carrying that MessageId. Option (b) is more deterministic and matches the direct-publish idiom already in SC2.
   - Recommendation: Planner should prefer option (b) — pre-seed the HASH + `skp:data:{entryId}`, publish `EntryStepDispatch` with `MessageId` set, assert: completed re-sent (orchestrator advance / a fresh data key written by the re-send is NOT expected — recovery re-sends the EXISTING entryId), slot retired to `Guid.Empty`, then the two-key DEL leaves net-zero. NOTE: setting the broker `MessageId` on a MassTransit `Send` is done via `SendContext.MessageId` in a send-pipe/observer — confirm the in-proc bus exposes it (the planner should grep for any existing test that sets `MessageId`).

3. **Does the close gate's `dotnet test ... --no-build` correctly pick up the RealStack tests at `Phase=55`?**
   - What we know: The gate runs the full suite with no filter (`:242`), so trait retagging does not affect which tests the gate runs — it runs everything.
   - What's unclear: Nothing — retagging `Phase=49`→`Phase=55` only affects `--filter-trait "Phase=55"` ad-hoc runs and the documentation narrative.
   - Recommendation: No gate change needed for the retag beyond the comment/narrative.

## Environment Availability

The autonomous build-gate deliverable (D-08) needs only the .NET SDK. The operator-gated live run (D-09) needs the full docker stack. This audit cannot probe the live stack from the research sandbox; availability is the operator's pre-flight (the gate's compose-health check enforces it).

| Dependency | Required By | Available (research-time) | Version | Fallback |
|------------|------------|---------------------------|---------|----------|
| .NET SDK (net8.0) | Build gate (D-08) + hermetic compile | Assumed (repo builds) | net8.0 (csproj TFM) | none — blocking for D-08 |
| `pwsh` (PowerShell) | `phase-55-close.ps1` | Assumed (Windows 11 host) | — | none for the gate; build gate can run without it |
| `docker` / `docker compose` | Live N×GREEN run (D-09) | Operator pre-flight | — | none — the gate exits 2 if the stack is unhealthy |
| `redis-cli` / `rabbitmqctl` / `psql` (via `docker exec`) | Triple-SHA capture + index count | Operator pre-flight | — | none — required for TEST-02 |
| Elasticsearch + otel-collector | SC1/SC3 seam-log proofs | Operator pre-flight | — | none — SC1/SC3 fail without ES ingest |

**Blocking with no fallback:** Live-run dependencies are all gated by the operator runbook + the script's compose-health pre-flight (`phase-49-close.ps1:178-198`, exit 2 on unhealthy). The build gate (the phase's autonomous deliverable) needs only the SDK.

## Validation Architecture

> nyquist_validation is enabled (config.json `workflow.nyquist_validation: true`). This section is consumed to build `55-VALIDATION.md`.

This phase has a **build-before-proof split** (D-08/D-09): the hermetic build gate is autonomously verifiable; the live N×GREEN run is operator-gated. The validation strategy reflects both.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit v3 (`xunit.v3`, CPM) on Microsoft.Testing.Platform |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (+ `xunit.runner.json` for capped parallelism) |
| Quick run command (hermetic, RealStack excluded) | `dotnet run --project tests/BaseApi.Tests -c Release -- --filter-not-trait Category=RealStack` |
| Full suite command (live, the gate) | `pwsh -File scripts/phase-55-close.ps1` (runs `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release --no-build` ×3) |
| Phase-scoped live SC run | `dotnet run --project tests/BaseApi.Tests -- --filter-trait "Phase=55"` |

### Phase Requirements → Test/Observation Map
| Req | Behavior proven | Type | Live observation (what proves it) | Automated command | File status |
|-----|------------------|------|-----------------------------------|-------------------|-------------|
| TEST-01 | Forward pass: index-before-data + advance | RealStack E2E | A fresh `skp:msg:{messageId}` HASH appears (slot write) AND a fresh `skp:data:*` key appears, ordered alloc-before-data; ES `Start reload for WorkflowId=` orchestrator seam | (within the gate) `--filter-trait "Phase=55"` selects `SC1RoundTripE2ETests` | ❌ adapt SC1 (+ `skp:msg` assert + A19 net-zero) |
| TEST-01 | A19 net-zero at end-of-message | RealStack E2E | After the round trip, BOTH `skp:data:{entryId}` AND `skp:msg:{messageId}` are gone (the two-key `DEL`) | same | ❌ adapt SC1 |
| TEST-01 | Keeper REINJECT data-present | RealStack E2E | Re-injected `EntryStepDispatch` lands on `queue:{ProcessorId:D}` (depth ≥ 1) | `--filter-trait "Phase=55"` → `SC2...` | ❌ rewrite SC2 |
| TEST-01 | Keeper REINJECT data-gone | RealStack E2E | Origin queue stays empty AND `skp-dlq-1` does NOT increment (silent drop; `keeper_reinject_dropped`) | same | ❌ rewrite SC2 |
| TEST-01 | Keeper INJECT | RealStack E2E | `L2[m.EntryId]=m.Data` written + `L2[m.DeleteEntryId]` deleted | same | ❌ rewrite SC2 |
| TEST-01 | Keeper DELETE (A19 both-key) | RealStack E2E | BOTH `skp:data:{entryId}` AND `skp:msg:{messageId}` gone after ONE `DEL` | same | ❌ rewrite SC2 (v5-NEW: assert both keys) |
| TEST-01 | Organic recovery pass (`if exist L2[messageId]`) | RealStack E2E | Pre-seeded slot array → re-fire → completed re-sent (fresh exec) → slot retired to `Guid.Empty` → two-key DEL net-zero | same | ❌ NEW test (D-03) |
| TEST-01 | BIT-gate pause/resume across outage (A14) | RealStack E2E | ES `Global PauseAll` / `Global ResumeAll` seams; no new `skp:data:*` during the paused window | `--filter-trait "Phase=55"` → `SC3...` (serial collection) | ⚠️ retag SC3 (behavior unchanged) |
| TEST-02 | Net-zero triple-SHA | Close-gate script | `psql \l` / `redis --scan` / `rabbitmq list_queues name` SHA BEFORE==AFTER | `pwsh -File scripts/phase-55-close.ps1` (exit 0) | ❌ clone phase-49-close.ps1 |
| TEST-02 | Active index reclaim | Close-gate script | `skp:msg:*` count==0 (D-06c additive) + `skp-dlq-1` depth==0 | same | ❌ add count==0 block |
| TEST-02 | 0-warning Release+Debug | Build gate | `dotnet build SK_P.sln -c Release` AND `-c Debug` exit 0 (TreatWarningsAsErrors) | (within the gate; also the autonomous D-08 deliverable) | ✅ existing build |

### Sampling Rate (Nyquist cadence)
- **Per task commit (autonomous, D-08):** `dotnet run --project tests/BaseApi.Tests -c Release -- --filter-not-trait Category=RealStack` → 0 failed (the new/adapted RealStack facts COMPILE and are EXCLUDED, not run) + `dotnet build SK_P.sln -c Release` and `-c Debug` both 0-warning.
- **Per wave merge:** the full hermetic suite green + both build configs 0-warning + `scripts/phase-55-close.ps1` is syntactically valid (`pwsh -NoProfile -Command "& { . ./scripts/phase-55-close.ps1 }"` parse-check, or a `-WhatIf`-style dry parse).
- **Phase gate (operator, D-09):** `pwsh -File scripts/phase-55-close.ps1` against the rebuilt v5 stack → exit 0 with **N=3 consecutive GREEN** at an **identical `Passed` fact count** (Smell-A guard, D-10), triple-SHA BEFORE==AFTER, `skp-dlq-1` depth==0, `skp:msg:*` count==0. Record the three SHAs + Passed count + both 0-checks in `55-HUMAN-UAT.md`, then tick TEST-01/02.

### Wave 0 Gaps
- [ ] `scripts/phase-55-close.ps1` — clone of `phase-49-close.ps1` with D-06(a) composite removal + D-06(c) `skp:msg:*` count==0 (covers TEST-02)
- [ ] `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs` — retag `Phase=55`, add `skp:msg:{messageId}` assertion + A19 net-zero (covers TEST-01 forward)
- [ ] `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` — retag `Phase=55`, rewrite for 3-state + both-key DELETE + add organic recovery test (covers TEST-01 recovery/keeper)
- [ ] `tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs` — retag `Phase=55` only (covers TEST-01 BIT gate)
- [ ] `.planning/phases/55-live-proof-close-gate/55-HUMAN-UAT.md` — operator runbook (clone `49-HUMAN-UAT.md` structure; covers the D-09 gate)
- [ ] Remove the v4 composite-sweep teardown block from all three SC factories (Landmine 3)

*No new test framework install — the existing xUnit v3 / MTP infrastructure covers all phase requirements.*

## Security Domain

`security_enforcement` is not present in config.json (absent → enabled). This phase adds no new attack surface: it reshapes existing tests + a PowerShell script, and proves existing production code. The relevant security-adjacent controls are already in place and unchanged.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control (verified) |
|---------------|---------|-----------------------------|
| V5 Input Validation | partial | The Processor input/output JSON-schema validation (`ProcessorJsonSchemaValidator`, `ProcessorPipeline.cs:216,254`) is exercised end-to-end by SC1 but unchanged this phase |
| V6 Cryptography | no | The `SourceHash` is a build-identity hash (`^[a-f0-9]{64}$`), not a security control; read off the assembly, never hand-rolled |
| V2/V3/V4 (Auth/Session/Access) | no | No auth surface touched; the E2E uses the in-proc WebApi CRUD over localhost only |

### Known Threat Patterns for this stack
| Pattern | STRIDE | Standard Mitigation (in place) |
|---------|--------|--------------------------------|
| Stale-image / mixed-version deploy mis-deserializes the v5 wire contract | Tampering / DoS | Operator rebuild of all four contract-changed services + the `SourceHash`==host-build liveness gate (false-pass impossible) — documented in the runbook (D-09) |
| Gate false-pass via synthetic liveness seed | Spoofing | `PollForHealthyLivenessAsync` polls the REAL container heartbeat; no synthetic seed (`SC1...:191`) |
| Net-zero false-pass via TTL race | Repudiation (silent loss) | A19 active two-key DELETE + explicit `skp:msg:*` count==0 (D-06c) — a leak surfaces as SHA mismatch AND count>0 |

## Sources

### Primary (HIGH confidence — read this session)
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — forward + recovery + A19 two-key tail (full read)
- `src/Keeper/Recovery/{DeleteConsumer,ReinjectConsumer,InjectConsumer}.cs` — the 3 keeper states (full read)
- `src/Keeper/Health/BitHealthLoop.cs` — A14 edge-triggered PauseAll/ResumeAll (full read; unchanged)
- `src/Messaging.Contracts/{KeeperDelete,KeeperReinject,KeeperInject,EntryStepDispatch,StepCompleted}.cs` — contract fields
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `skp:data:{entryId:D}` / `skp:msg:{messageId:D}`
- `src/Messaging.Contracts/{KeeperQueues,OrchestratorQueues}.cs` + `src/BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs` + `src/BaseProcessor.Core/Configuration/SlotArrayOptions.cs` — constants
- `src/Processor.Sample/appsettings.json` — seed version `"3.5.0"`
- `scripts/phase-49-close.ps1` — the verbatim triple-SHA template (full read)
- `tests/BaseApi.Tests/Orchestrator/{SC1RoundTripE2ETests,SC2RecoveryPathsE2ETests,SC3PauseResumeOutageE2ETests}.cs` — the three scaffolds (full read)
- `tests/BaseApi.Tests/BaseApi.Tests.csproj` — xUnit v3 / MTP setup
- `.planning/phases/54-terminal-index-delete/54-SPEC.md` — A19 behavior under proof
- `.planning/phases/49-live-proof-close-gate/49-HUMAN-UAT.md` — runbook precedent + GAP-49-1..10 record
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` — A18/A19 LOCKED design (recovery + active GC sections)
- `.planning/REQUIREMENTS.md`, `.planning/config.json`

### Secondary (MEDIUM)
- `.planning/phases/49-live-proof-close-gate/49-0*-PLAN.md` / `49-PATTERNS.md` — MTP filter syntax confirmation + the test-reshaping precedent

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — every symbol/constant/version read from source this session.
- Architecture (forward/recovery/keeper/A19): HIGH — full reads of ProcessorPipeline + all three consumers + BitHealthLoop.
- Close-gate protocol + deltas: HIGH — full read of phase-49-close.ps1; deltas line-cited.
- Pitfalls / landmines: HIGH — corrected directly against CONTEXT.md with file:line evidence.
- Operational risks (A1–A3): MEDIUM — historical failure modes with documented mitigations, not structural unknowns.

**Research date:** 2026-06-12
**Valid until:** 2026-06-26 (14 days — the code is locked v5.0.0 at Phase 54; the only churn risk is a late hotfix to the proven paths)

---

## Landmines (read before planning — CONTEXT.md corrections)

These are points where CONTEXT.md's assumption was checked against the code. Two CONFIRM; three CORRECT.

### Landmine 1 — Seed version is `"3.5.0"`, NOT a new v5 value (CORRECTS the D-11 hint)
CONTEXT.md D-11 says "Verify the v5 Processor.Sample version (phase-49 used `3.5.0`)." **The value is STILL `"3.5.0"`** — `src/Processor.Sample/appsettings.json:11` `"Version": "3.5.0"`. The clone's seed body (`phase-49-close.ps1:144` `version = '3.5.0'`) is **unchanged**. Do not invent a "5.0.0" seed string; it would create a NEW Processor row (different name/version is fine, but the version field is cosmetic — the row is keyed by `uq_processor_source_hash`, and the SourceHash is what changed between v4 and v5 builds). The seed version string stays `3.5.0`.

### Landmine 2 — Keeper queue contracts are `KeeperReinject`/`KeeperInject`/`KeeperDelete` (CORRECTS additional_context #5)
The research brief listed "the message contract types (`KeeperReinject`, `EntryStepDispatch`, `StepCompleted`)." That conflates two layers:
- **Published to `queue:keeper-recovery` (what SC2 direct-publishes):** `KeeperReinject` (`KeeperReinject.cs`), `KeeperInject` (`KeeperInject.cs`), `KeeperDelete` (`KeeperDelete.cs`). All implement `IKeeperRecoverable`.
- **The payloads the keeper RE-EMITS:** REINJECT re-sends an `EntryStepDispatch` to `queue:{ProcessorId:D}` (`ReinjectConsumer.cs:43-55`); INJECT sends a `StepCompleted` to `queue:orchestrator-result` (`InjectConsumer.cs:28-37`).
SC2 must `Send` `KeeperReinject`/`KeeperInject`/`KeeperDelete` (exactly as the v4 SC2 already does, `SC2...:100,174,208`) and ASSERT the re-emitted `EntryStepDispatch`/`StepCompleted` effects. **The v5 delta is `KeeperDelete` now carrying `MessageId` (`KeeperDelete.cs:13`)** + the both-key delete.

### Landmine 3 — The v4 composite-sweep teardown block must be DELETED from all three SC factories (Model-B retired)
Every SC factory's `DisposeAsync` still contains a `GAP-49-8` composite sweep that scans `skp:*:{wfId}:*` and deletes composite backup keys:
- `SC1RoundTripE2ETests.cs:437-448`
- `SC2RecoveryPathsE2ETests.cs:462-473`
- `SC3PauseResumeOutageE2ETests.cs:597-608`

Model-B (the composite backup key `skp:{corr}:{wf}:{proc}:{exec}`) was retired in Phases 50/53 — `grep` of `src/` finds no `CompositeBackup` builder and `54-SPEC.md` confirms the index is now the only allocation key. The composite no longer exists, so the sweep is dead code that scans a namespace that never has members. It is harmless at runtime but is a **landmine for the planner**: leaving it implies Model-B state still exists, and it pattern-scans `skp:*:{wfId}:*` which could accidentally match a future 2-segment `skp:msg:...`-adjacent shape. **Delete the block in all three factories** as part of the v5 reshape (D-06a's "drop the composite namespace" applies to the test teardown too, not just the close script). Replace its intent with `skp:msg:{messageId}` registration into `L2KeysToCleanup` where the tests mint them.

### Landmine 4 (CONFIRM) — SC3 / BitHealthLoop is genuinely unchanged
D-01's "retag SC3 as-is" is correct: `BitHealthLoop.cs:48-68` still `bus.Publish(new ResumeAll{...})` on the healthy edge and `bus.Publish(new PauseAll{...})` on the unhealthy edge, exactly as SC3 asserts via the `Global PauseAll`/`Global ResumeAll` ES seams. The ONLY SC3 change is the `[Trait("Phase","49")]` → `[Trait("Phase","55")]` retag (and deleting the dead composite sweep per Landmine 3).

### Landmine 5 (CONFIRM) — the unfiltered `--scan` SHA already captures both v5 key families
D-06b is correct: `phase-49-close.ps1:209/:316` scans ALL keys (the only `Where-Object` exclusion is the single `skp:{procId:D}` liveness key). `skp:data:*` and `skp:msg:*` are folded into the SHA automatically. The D-06c count==0 is ADDITIVE proof (it makes the A19 reclaim explicit), not a replacement for the SHA.
