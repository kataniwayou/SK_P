# Phase 40: Keeper Recovery Hardening - Research

**Researched:** 2026-06-06
**Domain:** .NET 8 / C# distributed workflow orchestration — MassTransit fault consumers + StackExchange.Redis L2 probe recovery + PowerShell close-gate
**Confidence:** HIGH (this is a ground-the-existing-code phase; all findings are `[VERIFIED: codebase]` reads of the real Keeper sources, tests, and gate script)

---

## Summary

Phase 40 is a pure hardening/refactor of an already-live, already-proven Keeper recovery engine. The three KHARD requirements are tightly coupled and all land in the same small surface area: the two near-identical fault consumers (`FaultEntryStepDispatchConsumer`, `FaultExecutionResultConsumer`), the give-up RealStack E2E teardown, and (for KHARD-02) the close-gate script. There is **no new domain to discover** — the mechanisms are pinned by ROADMAP success criteria. The job is to map the real code so the planner writes file-accurate tasks.

Three concrete findings drive the plan:

1. **KHARD-03 (extract) is the keystone and should be done FIRST.** The two consumers (`src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` and `FaultExecutionResultConsumer.cs`) are *verbatim duplicates* except for: the generic type param (`EntryStepDispatch` vs `ExecutionResult`), one `fault_type` tag literal (`dispatch` vs `result`), and the re-inject endpoint URI (`queue:{ProcessorId:D}` vs `queue:{OrchestratorQueues.Result}`). Everything else — intake log, pause, probe-await, recover/reinject/resume, give-up/park, all metrics — is byte-identical. This is the perfect shape for a **shared generic helper/service** the cap lands inside.

2. **KHARD-01 (cap) is a NEW cross-message concept, not an extension of `ProbeOptions.MaxAttempts`.** `MaxAttempts=12` is the *inner probe-loop* iteration count inside a single `Consume` (`L2ProbeRecovery.RunAsync`). The unbounded flood is the *outer* cycle: recover → reinject verbatim inner → it re-faults at the receiver → a fresh `Fault<T>` with the SAME `H` is published → Keeper recovers again → reinjects again, forever (~67 cyc/s/replica). There is **no per-`H` memory** today. KHARD-01 needs a NEW per-`H` recover-attempt counter (a Redis key with TTL is the right fit, mirroring the existing `skp:` convention) checked at the top of the recover branch.

3. **KHARD-02 (drain) replaces an existing one-shot purge + fixed `Task.Delay(10s)`.** The give-up E2E (`KeeperRecoveryE2ETests.KeeperRecovery_GivesUp_ParksToDlq`) already calls `PurgeKeeperDlqAsync` (a single RabbitMQ mgmt-API `DELETE .../keeper-dlq/contents`) preceded by a fixed 10s wait. The race the audit caught: a SECOND keeper replica's give-up park lands *after* that one-shot purge. KHARD-02 replaces purge-once with **poll-until-stably-empty** (drain, re-check depth==0, hold stable for a window, bounded by a max timeout).

**Primary recommendation:** Plan three tasks in dependency order — (T1) KHARD-03 extract a shared `KeeperRecoveryHandler<TFault>` generic helper both consumers delegate to; (T2) KHARD-01 add a configurable `RecoverAttemptCap` + per-`H` Redis counter *inside that one helper*, with a hermetic test proving exactly cap-many reinjects then one park; (T3) KHARD-02 replace the one-shot DLQ purge with a bounded poll-until-stably-empty drain helper in the E2E teardown. Re-run `scripts/phase-39-close.ps1` (or a Phase-40 clone) for the live close-gate proof.

---

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Recover-attempt cap enforcement (KHARD-01) | Keeper console (consumer/helper) | Redis (L2) — per-`H` counter store | The give-up decision is Keeper business logic; the counter is durable cross-message state, so it lives in L2 like every other `skp:` key |
| Shared recovery body (KHARD-03) | Keeper console (`src/Keeper/`) | — | Pure in-process refactor; no other tier touched |
| keeper-dlq deterministic drain (KHARD-02) | Test harness (E2E teardown) | RabbitMQ mgmt API + close-gate script | Net-zero discipline lives in the E2E teardown by design (the gate snapshots, it does NOT purge — see gate script lines 42-43) |
| Probe loop (existing) | Keeper console (`L2ProbeRecovery`) | Redis (L2) | Unchanged this phase |

---

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| KHARD-01 | Configurable recover→reinject attempt cap; at cap, park original `Fault<T>` to `keeper-dlq` (single park, idempotent give-up). Hermetic test proves cap honored. | New per-`H` counter in L2 (no existing counter — verified). Config home = `ProbeOptions` (existing `Probe` section) or a new option. Hermetic pattern = `KeeperProbeLoopTests` harness facts. See "KHARD-01" + "Validation Architecture". |
| KHARD-02 | Give-up E2E teardown drains `keeper-dlq` via bounded poll-until-stably-empty; close-gate `keeper-dlq depth==0` holds across 3× cadence. | Replaces existing `PurgeKeeperDlqAsync` one-shot + `Task.Delay(10s)` in `KeeperRecoveryE2ETests`. Gate assertion at `phase-39-close.ps1:349-359`. See "KHARD-02". |
| KHARD-03 | Extract shared recover/probe/reinject/park/pause/resume logic into one helper/base; both consumers delegate; cap exists in exactly one place. Hermetic GREEN, Release 0-warning. | Two consumers are verbatim duplicates except 3 deltas (type param, fault_type literal, reinject endpoint). See "KHARD-03". |
</phase_requirements>

---

## Current-State Map (real files, line refs, excerpts)

### The two fault consumers — near-total duplicates (drives KHARD-03)

Both at `src/Keeper/Consumers/`. Confirmed verbatim except three deltas:

| Aspect | `FaultEntryStepDispatchConsumer.cs` | `FaultExecutionResultConsumer.cs` |
|--------|-------------------------------------|-----------------------------------|
| Message type | `Fault<EntryStepDispatch>` | `Fault<ExecutionResult>` |
| `fault_type` tag | `KeeperMetricTags.FaultTypeDispatch` (`"dispatch"`) | `KeeperMetricTags.FaultTypeResult` (`"result"`) |
| Re-inject endpoint | `queue:{inner.ProcessorId:D}` (line 71) | `queue:{OrchestratorQueues.Result}` (line 72) |
| Everything else | IDENTICAL | IDENTICAL |

The identical body (both files, ~lines 38-103): unwrap `context.Message.Message`; start `Stopwatch`; `FaultConsumed.Add`; manual CorrelationId log scope + `ExecutionLogScope.BuildState`; one Information log; `Publish(PauseWorkflow)` + `WorkflowPaused.Add`; `await recovery.RunAsync(inner.EntryId, inner.H, procId, ct)`; on `Recovered` → `GetSendEndpoint(...).Send(inner)` + `Publish(ResumeWorkflow)` + `WorkflowResumed`/`Recovered`/`RecoveryDuration(recovered)`; on `GaveUp` → `GetSendEndpoint(queue:keeper-dlq).Send(context.Message)` + `DlqPushed(probe_exhausted)`/`RecoveryDuration(gave_up)`.

The audit's IN-01 ("near-total duplicates; future fixes must be applied in two places") is exactly this. `[VERIFIED: codebase]`

**Both ctors take the identical deps:** `(ILogger<T>, L2ProbeRecovery recovery, KeeperMetrics metrics)`.

### Consumer definitions — the asymmetric retry-owner seam (DO NOT break in the refactor)

- `FaultEntryStepDispatchConsumerDefinition.cs` — **owns** the endpoint retry. Binds `EndpointName = KeeperQueues.FaultRecovery` ("keeper-fault-recovery"); `ConfigureConsumer` registers `UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit))`. Ctor takes `IOptions<RetryOptions>`.
- `FaultExecutionResultConsumerDefinition.cs` — `ConfigureConsumer` is an **intentional no-op** (both consumers colocate on the ONE shared endpoint; retry is per-endpoint not per-consumer; only the first definition may register it — "Pitfall 3"). Ctor takes no deps.

`[VERIFIED: codebase]` **Constraint for KHARD-03:** the refactor must NOT change this definition asymmetry. The two `*ConsumerDefinition` classes can stay as-is (they configure the endpoint, not the consumer body). Only the consumer *bodies* get extracted.

### DI registration — `src/Keeper/Program.cs`

```csharp
builder.Services.Configure<ProbeOptions>(builder.Configuration.GetSection("Probe"));   // line 29
builder.Services.AddSingleton<Keeper.Recovery.L2ProbeRecovery>();                       // line 33
builder.Services.AddSingleton<KeeperMetrics>();                                         // line 41
builder.Services.AddBaseConsoleMessaging(builder.Configuration, x =>                     // lines 50-54
{
    x.AddConsumer<FaultEntryStepDispatchConsumer, FaultEntryStepDispatchConsumerDefinition>();
    x.AddConsumer<FaultExecutionResultConsumer,   FaultExecutionResultConsumerDefinition>();
});
```

`IConnectionMultiplexer` (Redis singleton) is already registered transitively by `AddBaseConsole` (line 21). `[VERIFIED: codebase]`

### The probe helper — `src/Keeper/Recovery/L2ProbeRecovery.cs`

`public async Task<ProbeOutcome> RunAsync(string entryId, string h, string procId, CancellationToken ct)`. `enum ProbeOutcome { Recovered, GaveUp }`. Inner loop iterates `opts.Value.MaxAttempts` times; each iteration READs `skp:data:{entryId}` + WRITE-then-delete `skp:keeper:probe:{h}`; `catch (RedisException)` → `L2ProbeFailed.Add` + delay `DelaySeconds`. Bumps `InFlight` ±1. **This is the INNER cap; KHARD-01 is a different, OUTER cap.** `[VERIFIED: codebase]`

### Config home — `src/Keeper/ProbeOptions.cs` + `src/Keeper/appsettings.json`

```csharp
public sealed class ProbeOptions {
    public int DelaySeconds { get; set; } = 5;
    public int MaxAttempts  { get; set; } = 12;   // INNER probe-loop iterations, NOT the recover cap
}
```
```json
"Probe": { "DelaySeconds": 5, "MaxAttempts": 12 }   // appsettings.json:29-32
```
The convention: a typed options class bound from a named section via `builder.Services.Configure<T>(GetSection("X"))`, injected as `IOptions<T>`. `RetryOptions`/`"Retry"` follows the same shape. `[VERIFIED: codebase]`

### Key derivation — `src/Messaging.Contracts/Hashing/MessageIdentity.cs` + `Projections/L2ProjectionKeys.cs`

- `H` = `MessageIdentity.ComputeH(corr, wf, step, proc, entryId)` → lowercase 64-hex. It is **deliberately stable across re-injects** (per-execution lineage excluded, D-02) — so the SAME logical message keeps the SAME `H` on every recover→reinject→refault cycle. **This is precisely what makes a per-`H` counter the correct cap key.** `[VERIFIED: codebase]`
- Existing `skp:` key factories: `Flag(h)` → `skp:flag:{h}`, `KeeperProbe(h)` → `skp:keeper:probe:{h}`, `ExecutionData(entryId)` → `skp:data:{entryId}`. A new `KeeperRecoverAttempts(h)` → `skp:keeper:attempts:{h}` would slot in here cleanly. `[VERIFIED: codebase]`

### The close gate — `scripts/phase-39-close.ps1`

- The `keeper-dlq depth==0` assertion: lines 343-359 (DELTA 2). Reads `rabbitmqctl list_queues name messages`, asserts both `keeper-dlq` and `skp-dlq-1` depth==0.
- **Critical gate design (lines 42-43):** "the net-zero DLQ drain stays in the E2E teardown (D-10), NOT this gate (NO gate-side purge_queue) — so a teardown regression surfaces here as depth>0." → **KHARD-02 must harden the E2E teardown, NOT add a purge to the gate.** `[VERIFIED: codebase]`
- 3× cadence: lines 227-260. Redis settle-drain helper precedent (poll-to-baseline with bounded timeout): lines 262-283 — a good shape to mirror for the DLQ poll-until-empty.

### The give-up E2E teardown — `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs`

The current (audit-flagged) drain, `KeeperRecovery_GivesUp_ParksToDlq`:
- After proving the park: `await Task.Delay(TimeSpan.FromSeconds(10), ct);` (lines 379-384) — the fragile fixed wait for the 2nd replica.
- Then `await PurgeKeeperDlqAsync(ct);` (line 396) — a ONE-SHOT mgmt-API delete (`PurgeKeeperDlqAsync`, lines 512-520: `http.DeleteAsync("/api/queues/%2F/keeper-dlq/contents")`).
- The race: 2 keeper replicas (the synthetic Fault is **Published**, so BOTH replicas give up + park independently, >10s apart per the in-code comment lines 379-395). A late park after the one-shot purge leaves depth==1 → gate `GATE_EXIT=1`. `[VERIFIED: codebase]`

### Existing hermetic test patterns — `tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs` + `FakeRedis.cs`

The KHARD-01 hermetic cap test extends these directly:
- `FakeRedis` (NSubstitute-backed): flippable health Down/HalfOpen/Up + `SetFailuresBeforeUp(n)`. **Note (gap for KHARD-01):** `FakeRedis` currently configures ONLY `StringGetAsync`/`StringSetAsync`/`KeyDeleteAsync`. A per-`H` attempt counter likely uses `StringIncrementAsync` + `KeyExpireAsync` (or `StringSetAsync ... When.NotExists` + `StringGetAsync`) — **`FakeRedis` will need extending** (Wave-0 gap) to back those ops, OR the cap-counter store is abstracted behind a tiny interface the test fakes separately. `[VERIFIED: codebase]`
- `BuildHarness(...)` (lines 108-122): wires SUT consumer + `FakeRedis.Multiplexer` + `ProbeOptions` + `L2ProbeRecovery` + `KeeperMetrics` into an in-memory MassTransit harness. `Probe_GiveUp_ParksToDlq` (lines 170-192) already asserts: `harness.Sent.Any<Fault<EntryStepDispatch>>` true (parked) AND `harness.Sent.Any<EntryStepDispatch>` false (no reinject). The KHARD-01 test is a richer variant of this. `[VERIFIED: codebase]`

---

## KHARD-03 — Extraction Recommendation (do FIRST)

**Recommendation: a shared, injected generic recovery helper `KeeperRecoveryHandler<TFault>` (a class, NOT an abstract base consumer).** Both consumers become thin delegators.

**Why injected helper over abstract base consumer:**
- The only per-type differences are *data*, not *behavior*: the `fault_type` tag string and the re-inject endpoint URI. These pass cleanly as constructor params or a per-type descriptor — no behavioral override (no `abstract` method) is needed, so an abstract base earns nothing.
- The repo already prefers the injected-stateless-helper shape: `L2ProbeRecovery` is exactly this (a `[VERIFIED]` singleton helper both consumers ctor-inject). KHARD-03's helper mirrors that precedent → DI symmetry, easy hermetic instantiation (the existing tests `new` the helper directly).
- The asymmetric `*ConsumerDefinition` retry-owner seam stays untouched (it configures the endpoint, not the body) — a base-consumer refactor risks entangling that.

**Suggested shape** (planner refines):

```csharp
// src/Keeper/Recovery/KeeperRecoveryHandler.cs  (NEW)
// One body for both fault types. The two per-type deltas are parameters.
public sealed class KeeperRecoveryHandler(
    ILogger<KeeperRecoveryHandler> logger, L2ProbeRecovery recovery, KeeperMetrics metrics /*, attempt-counter dep */)
{
    public async Task HandleAsync<T>(
        ConsumeContext<Fault<T>> context,
        string faultTypeTag,                              // "dispatch" | "result"
        Func<T, Uri> reinjectEndpoint,                   // inner => queue:{procId} | queue:orchestrator-result
        CancellationToken ct)
        where T : class, IExecutionCorrelated
    {
        // ... the verbatim body, with KHARD-01's cap check in the Recovered branch ...
    }
}
```
Consumers shrink to:
```csharp
public Task Consume(ConsumeContext<Fault<EntryStepDispatch>> ctx) =>
    _handler.HandleAsync(ctx, KeeperMetricTags.FaultTypeDispatch,
        inner => new Uri($"queue:{inner.ProcessorId:D}"), ctx.CancellationToken);
```

**Register** `builder.Services.AddSingleton<KeeperRecoveryHandler>();` in `Program.cs` alongside `L2ProbeRecovery` (line 33). Update the two consumer ctors to take the handler instead of `(recovery, metrics)`.

**Existing hermetic tests stay GREEN:** `KeeperProbeLoopTests.BuildHarness` registers `L2ProbeRecovery` + `KeeperMetrics`; it will additionally need `.AddSingleton<KeeperRecoveryHandler>()`. The harness *assertions* (Sent reinject / Sent park) are unchanged because the observable bus behavior is identical. `[VERIFIED: codebase — confidence HIGH]`

**Verify `IExecutionCorrelated` is the shared inner constraint** before finalizing the generic bound — both inners (`EntryStepDispatch`, `ExecutionResult`) carry `ProcessorId`, `WorkflowId`, `CorrelationId`, `H`, `EntryId` (used in the body). The consumer doc-comments name them `IExecutionCorrelated`. `[CITED: consumer doc-comments]` — planner should grep `IExecutionCorrelated` to confirm it declares all five members the body reads.

---

## KHARD-01 — Cap-Tracking Recommendation

**The flood, precisely:** recover branch reinjects the verbatim inner (same `H`) → receiver re-faults on a *persistent* (non-transient) cause → MassTransit publishes a fresh `Fault<T>` (same `H`) → Keeper consumes again → probe recovers again (the probe reads `skp:data`/`skp:keeper:probe`, which are NOT the persistently-broken key) → reinjects again. No memory of prior cycles → unbounded. `[VERIFIED: code + audit item Phase-36]`

**Recommendation: a per-`H` recover-attempt counter in Redis (L2), checked in the Recovered branch INSIDE the shared helper, before the reinject Send.**

Algorithm (in the helper's Recovered branch):
1. `n = INCR skp:keeper:attempts:{H}` and `EXPIRE skp:keeper:attempts:{H} <ttl> NX` (set TTL only on first create — or use the `StringIncrementAsync` then conditional `KeyExpireAsync`).
2. If `n > Cap` → **give up**: park `context.Message` to `keeper-dlq` (the existing GaveUp park path), increment `DlqPushed{reason="recover_cap"}` (NEW reason value), record `RecoveryDuration{outcome=gave_up}`, and **`DEL skp:keeper:attempts:{H}`** so the counter does not leak (it converges to a SINGLE park, then the key is gone). Do NOT reinject.
3. Else → reinject as today. **On a genuinely successful recovery** (the receiver does NOT re-fault), the counter simply ages out via its TTL (no explicit reset needed unless the same `H` recurs legitimately — TTL handles leak prevention).

**Idempotent single-park guarantee:** because `H` is stable and `INCR` is atomic, the FIRST replica/cycle to cross `Cap` parks once and deletes the key; any concurrent crosser sees the key already gone or re-creates it (edge: two replicas crossing simultaneously). To make "exactly one park" robust against the 2-replica race, the planner should consider gating the park on the atomic `INCR` result equalling `Cap+1` (only the transition crosser parks) — this is the same single-winner pattern the existing `flag[H]` dedup uses. `[ASSUMED: A1 — the exact single-winner mechanism needs design confirmation]`

**Counter-leak / TTL discipline (cross-reference MEMORY close-gate flag-key churn):** the known landmine is *clearing a TTL on update*. Use `INCR` (creates with no TTL) followed by `KeyExpireAsync(..., ExpireWhen.HasNoExpiry)` on first set, OR `StringSetAsync(..., keepTtl: true)` on any rewrite. Mirror the gate's note (`phase-39-close.ps1` header + MEMORY: "SET XX needs keepTtl or it clears the TTL"). The counter key MUST be TTL-bounded so a missed `DEL` self-cleans → close-gate redis-scan SHA stays net-zero. Register it in the E2E `L2KeysToCleanup` too. `[VERIFIED: MEMORY + gate header]`

**Config home — recommendation: add `RecoverAttemptCap` to the existing `ProbeOptions`** (bound from the `"Probe"` section), default `3`.

| Decision | Recommendation | Rationale |
|----------|---------------|-----------|
| Config key | `Probe:RecoverAttemptCap` (extend `ProbeOptions`) | Reuses the exact existing convention; one options class for all recovery knobs; no new `Configure<T>` line needed. `[ASSUMED: A2]` |
| Default value | `3` | Matches the existing `Retry.Limit=3` immediate-retry budget; small enough to converge a persistent fault fast, large enough to ride a few transient flaps. `[ASSUMED: A2]` |
| Counter store | Redis key `skp:keeper:attempts:{H}` with TTL (e.g. 300s, matching the data/flag TTL window) | Mirrors `skp:flag`/`skp:keeper:probe` families; survives cross-message; auto-cleans. `[ASSUMED: A3 — TTL value needs confirmation]` |
| Give-up reason tag | NEW `KeeperMetricTags.ReasonRecoverCap` (e.g. `"recover_cap"`) distinct from `probe_exhausted` | Lets operators distinguish "L2 never came back" from "persistent fault hit the cap" on `keeper_dlq_pushed{reason}`. `[ASSUMED: A4]` |

**Hermetic cap test (the KHARD-01 deliverable):** extend `KeeperProbeLoopTests` style. Because the in-memory harness does not actually re-fault a reinjected message, prove the cap at the **helper level** against a fake counter:
- Drive the helper/consumer `Cap+1` times for the SAME `H` (each time with `FakeRedis` Up so the probe recovers).
- Assert: exactly `Cap` reinject Sends (`EntryStepDispatch` Sent count == Cap), then on the `Cap+1`-th intake exactly ONE park (`Fault<T>` Sent to keeper-dlq) and NO further reinject.
- Assert the counter key is deleted after the park (no leak).
- **Wave-0 prerequisite:** `FakeRedis` must back the counter ops (`StringIncrementAsync`/`KeyExpireAsync` or equivalent), OR the cap-counter is abstracted behind a small fakeable interface. `[VERIFIED: FakeRedis surface gap]`

**CRITICAL test-safety constraint (MEMORY landmine):** the hermetic cap test MUST NOT leave a poison armed against a live keeper that can reinject. Hermetic tests use the in-memory harness + FakeRedis (no live keeper), so this is naturally safe — but the test must arm, observe exactly cap-many reinjects then one park, and disarm. Do NOT add a RealStack cap test that leaves a persistent poison while the deployed keeper can reinject (it would flood the live stack at ~67 cyc/s/replica). `[VERIFIED: MEMORY project_keeper_recovery_unbounded_reinject_loop]`

---

## KHARD-02 — Drain-Hardening Recommendation

**Replace** the `Task.Delay(10s)` + one-shot `PurgeKeeperDlqAsync` in `KeeperRecoveryE2ETests.KeeperRecovery_GivesUp_ParksToDlq` **with a bounded poll-until-stably-empty drain helper.**

**Recommendation: a reusable helper method in the E2E test (NOT in the gate script).** The gate must stay snapshot-only (gate header lines 42-43 are explicit: NO gate-side purge). Mirror the gate's own settle-drain shape (`phase-39-close.ps1:262-283`).

Algorithm `DrainKeeperDlqUntilStablyEmptyAsync(ct)`:
1. Loop until a max-timeout deadline:
   - Purge `keeper-dlq` (mgmt-API DELETE, reuse `PurgeKeeperDlqAsync`).
   - Poll depth via mgmt API (`GET /api/queues/%2F/keeper-dlq` → `messages`), wait the poll interval.
   - When depth has been observed `0` for a continuous **stability window** (covering the worst-case inter-replica park gap), return success.
2. If the deadline passes without a stable-empty window, fail loudly (so a real regression surfaces, not a silent pass).

| Knob | Recommendation | Rationale |
|------|---------------|-----------|
| Poll interval | 2s | Fast enough to catch a late park; cheap mgmt-API call. `[ASSUMED: A5]` |
| Stability window | ≥15s | The in-code comment says replicas park ">10s apart"; window must exceed that worst case. `[ASSUMED: A5]` |
| Max timeout | 90s | Covers 2× the deployed give-up window (~60s) + park latency + stability window. `[ASSUMED: A5]` |
| Location | Private helper in `KeeperRecoveryE2ETests` (reusable across both DLQ-touching facts) | Keeps net-zero discipline in the E2E teardown per gate contract. `[VERIFIED: gate header]` |

**Why poll-and-re-purge, not just poll:** the parked messages are terminal (no consumer drains `keeper-dlq` in production — its depth IS the operator alert). So each late replica park must be actively purged; polling alone never reaches 0. The loop purges, then confirms stability. `[VERIFIED: KeeperQueues.DeadLetter doc-comment "no consumer, operator alert"]`

**Note:** the `_parkedDlqHashes` in-test probe (`KeeperDlqProbe`) already proves the park happened (the functional assertion). KHARD-02 only changes the *net-zero teardown*, not the park-proof. Keep the probe-based park assertion; replace only the drain. `[VERIFIED: codebase]`

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Per-`H` atomic counter | A read-modify-write over `StringGet`/`StringSet` | Redis `StringIncrementAsync` (atomic `INCR`) | Race-free across 2 keeper replicas; the whole point is convergence under concurrency |
| Setting a TTL without clobbering it | `StringSet(..., expiry)` on every update | `INCR` + `KeyExpire(..., ExpireWhen.HasNoExpiry)` once, or `keepTtl:true` | MEMORY landmine: SET XX clears the TTL → key churns the close-gate SHA forever |
| Stable hash `H` for the cap key | A new hash over the fault | `MessageIdentity.ComputeH` / the inner's existing `H` | D-04: exactly ONE canonicalization may exist; `H` is already stable across reinjects |
| Per-type consumer behavior | Two copies / abstract base with overrides | One generic helper + per-type *data* params | The deltas are data (tag, endpoint), not behavior — IN-01 is the cost of duplication |
| DLQ drain | `Task.Delay` + one-shot purge | Bounded poll-until-stably-empty loop | The 2-replica late-park race is the exact bug being fixed |

**Key insight:** every piece KHARD needs already has a blessed in-repo precedent — `L2ProbeRecovery` (injected helper), `flag[H]` (atomic single-winner dedup), the gate's settle-drain (poll-to-baseline), `ProbeOptions` (typed options). KHARD-01/02/03 are recombinations of existing patterns, not new inventions.

---

## Common Pitfalls

### Pitfall 1: Confusing `ProbeOptions.MaxAttempts` with the recover cap
**What goes wrong:** Reusing `MaxAttempts` (the inner probe-loop iteration count) as the KHARD-01 cap.
**Why:** Both are "attempts" but at different scopes — inner (probe iterations in one Consume) vs outer (recover→reinject cycles across messages).
**How to avoid:** KHARD-01 is a NEW, separate, per-`H` counter. `MaxAttempts` is untouched.
**Warning sign:** A plan task that edits the probe loop's `for` bound for KHARD-01.

### Pitfall 2: Adding a purge to the close-gate script
**What goes wrong:** "Fixing" KHARD-02 by purging keeper-dlq in `phase-39-close.ps1`.
**Why:** The gate is deliberately snapshot-only so a teardown regression surfaces as depth>0 (gate header lines 42-43).
**How to avoid:** Harden the E2E *teardown*; leave the gate's assertion untouched.

### Pitfall 3: Clearing the counter-key TTL on update (close-gate SHA churn)
**What goes wrong:** A `SET` that drops the TTL → the `skp:keeper:attempts:{H}` key persists → redis-scan SHA mismatch → gate exit 1.
**How to avoid:** `INCR` + one-time `EXPIRE NX` (or `keepTtl`), register in `L2KeysToCleanup`, and `DEL` on park. MEMORY-tracked landmine.

### Pitfall 4: Breaking the asymmetric retry-owner seam in the KHARD-03 refactor
**What goes wrong:** Moving retry config or making both definitions symmetric → double-registered retry filter on the one shared endpoint.
**How to avoid:** Extract only the consumer *bodies*; leave both `*ConsumerDefinition` classes exactly as-is.

### Pitfall 5: A RealStack cap test that floods the live stack
**What goes wrong:** A live test arms a persistent poison while the deployed keeper can still reinject → ~67 cyc/s/replica flood.
**How to avoid:** Prove the cap HERMETICALLY (in-memory harness + FakeRedis, no live keeper). MEMORY-tracked.

### Pitfall 6: Container rebuild before the live close gate
**What goes wrong:** A stale keeper image (old recovery body, no cap) false-passes/RED-fails the gate.
**How to avoid:** Rebuild `baseapi-service orchestrator processor-sample keeper` before any live gate run (embedded SourceHash must match). MEMORY-tracked (`reference_close_gate_container_rebuild_and_flag_churn`).

---

## Validation Architecture

`nyquist_validation: true` (`.planning/config.json`). Test framework = xUnit (`tests/BaseApi.Tests/BaseApi.Tests.csproj`); hermetic suite excludes `Category=RealStack`.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (v3 — `TestContext.Current.CancellationToken` in use) + MassTransit.Testing in-memory harness + NSubstitute |
| Hermetic test home | `tests/BaseApi.Tests/Keeper/` (`KeeperProbeLoopTests.cs`, `FakeRedis.cs`) |
| Quick run (hermetic) | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release --filter "Category!=RealStack"` |
| Full suite + live gate | `pwsh -File scripts/phase-39-close.ps1` (or a Phase-40 clone) |

### Requirement → Observable Signal Map
| Req | Behavior | Test Type | Observable signal (proves it) | File |
|-----|----------|-----------|-------------------------------|------|
| KHARD-01 | Cap honored: exactly `Cap` reinjects then 1 park, no reinject after | unit/hermetic | Harness `Sent.Any<TInner>` count == Cap; then exactly one `Sent<Fault<T>>` to keeper-dlq; `Sent<TInner>` count stays == Cap after | NEW fact in `KeeperProbeLoopTests.cs` |
| KHARD-01 | Single idempotent park (persistent fault converges) | unit/hermetic | Driving `H` `Cap+N` times yields exactly ONE park, not N | NEW fact |
| KHARD-01 | Counter does not leak | unit/hermetic | `skp:keeper:attempts:{H}` deleted after park (fake-counter assertion) | NEW fact |
| KHARD-02 | Deterministic `keeper-dlq depth==0` across 3× | live (RealStack) | `phase-39-close.ps1:349-359` DLQ depth==0 GREEN ×3 (eliminates the lone `GATE_EXIT=1`) | `KeeperRecoveryE2ETests` teardown + gate |
| KHARD-03 | No duplication; cap in one place | static/build | One `KeeperRecoveryHandler`; both consumers delegate; Release build 0-warning; hermetic suite GREEN | consumers + `Program.cs` |

### Sampling Rate
- **Per task commit:** hermetic quick run (above) — seconds.
- **Per wave merge:** full hermetic suite GREEN.
- **Phase gate:** `scripts/phase-39-close.ps1` 3× consecutive GREEN, triple-SHA net-zero, both DLQs depth==0, Release+Debug 0-warning.

### Wave 0 Gaps
- [ ] **Extend `FakeRedis`** to back the per-`H` counter ops (`StringIncrementAsync` + `KeyExpireAsync`, or `StringGet`/`StringSet When.NotExists`) — current double only configures Get/Set/Delete. *(Alternative: abstract the counter behind a small fakeable interface.)*
- [ ] **NEW hermetic cap fact(s)** in `KeeperProbeLoopTests.cs` (or a sibling `KeeperRecoverCapTests.cs`).
- [ ] **Update `BuildHarness`** to register `KeeperRecoveryHandler` once it exists.
- [ ] *(No framework install needed — xUnit + harness + NSubstitute all present.)*

---

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK 8 | build + hermetic tests | ✓ (assumed — repo builds net8.0) | 8.0 | — |
| Docker compose stack (redis, rabbitmq, postgres, otel, ES, prometheus, orchestrator, processor-sample, baseapi, keeper ×2) | KHARD-02 live close gate | runtime (operator-started) | per compose | hermetic KHARD-01/03 proof needs NO stack |

**Skip note:** KHARD-01 and KHARD-03 are fully hermetic (no external stack). Only KHARD-02's live close-gate proof needs the running compose stack (and a rebuilt keeper image — Pitfall 6).

---

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | "Exactly one park" under 2-replica race needs a single-winner gate on the atomic `INCR == Cap+1` transition | KHARD-01 | Two replicas could double-park a persistent fault → close-gate depth==2 flake; needs design confirmation |
| A2 | Cap config = new `Probe:RecoverAttemptCap`, default `3` | KHARD-01 | User may want a separate options class or a different default; low risk (Claude's-discretion area) |
| A3 | Counter key TTL = 300s (matching data/flag window) | KHARD-01 | Too short → a slow-recovering legit fault resets its count; too long → key lingers (TTL still self-cleans the SHA) |
| A4 | New `reason="recover_cap"` distinct from `probe_exhausted` | KHARD-01 | If reused, operators can't distinguish flood-give-up from L2-never-returned; cosmetic/observability only |
| A5 | KHARD-02 drain knobs: 2s poll, ≥15s stability window, 90s max | KHARD-02 | Too-short stability window re-introduces the late-park race; must exceed the ">10s apart" inter-replica gap |
| A6 | `IExecutionCorrelated` declares all five members the shared body reads (`ProcessorId`, `WorkflowId`, `CorrelationId`, `H`, `EntryId`) | KHARD-03 | If not, the generic bound needs a richer interface or per-type accessors; verifiable by one grep |

**These are the "Claude's discretion" gray areas** — the planner should either lock A2/A3/A4/A5 as defaults in PLAN or route them through discuss-phase. A1 and A6 are *technical* confirmations the planner should resolve during planning (A6 by grep; A1 by adopting the existing `flag[H]` single-winner pattern).

---

## Open Questions

1. **2-replica exactly-once park (A1).**
   - What we know: the synthetic give-up Fault is Published → both replicas independently give up; `H` + `INCR` are atomic.
   - What's unclear: which replica "owns" the single park when both cross the cap.
   - Recommendation: gate the park on `INCR` returning exactly `Cap+1` (only the crossing increment parks), mirroring the existing `flag[H]` first-writer-wins dedup. Confirm in PLAN.

2. **Does the receiver actually re-publish a fresh `Fault<T>` with the SAME `H` on a persistent fault?**
   - What we know: `H` is stable by design; the flood is documented (audit Phase-36).
   - What's unclear (for test fidelity): the exact MassTransit republish path is in the *receiver* (processor/orchestrator), not Keeper.
   - Recommendation: the hermetic cap test does NOT need the real republish — it drives the SAME `H` `Cap+1` times directly. The live behavior is already audit-confirmed; no new RealStack test required (and a live cap test is FORBIDDEN — Pitfall 5).

---

## Project Constraints (from CLAUDE.md)

No `./CLAUDE.md` in the repo (verified — Glob found none). Constraints derive from MEMORY.md + planning docs:
- Release build MUST stay 0-warning (`TreatWarningsAsErrors` makes a warning fatal — gate lines 219-224).
- Hermetic suite MUST stay GREEN.
- No live test may leave a poison armed while the deployed keeper can reinject (flood landmine).
- Counter key MUST be TTL-bounded + net-zero (close-gate redis-scan SHA invariant).
- Do NOT add a purge to the gate script; net-zero stays in the E2E teardown.
- Rebuild `baseapi-service orchestrator processor-sample keeper` before any live gate run.
- GSD subagents corrupt STATE.md encoding (BOM/mojibake) — relevant to the planner's doc writes, not the code phase, but flagged.

---

## Sources

### Primary (HIGH confidence — codebase reads, this session)
- `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` + `FaultExecutionResultConsumer.cs` — the two duplicate bodies (KHARD-03)
- `src/Keeper/Consumers/FaultEntryStepDispatchConsumerDefinition.cs` + `FaultExecutionResultConsumerDefinition.cs` — asymmetric retry-owner seam
- `src/Keeper/Recovery/L2ProbeRecovery.cs` + `src/Keeper/ProbeOptions.cs` — inner probe loop + config convention
- `src/Keeper/Observability/KeeperMetrics.cs` — 8 instruments + `KeeperMetricTags` (reason/fault_type/outcome enums)
- `src/Keeper/Program.cs` — DI registration
- `src/Keeper/appsettings.json` — `Probe`/`Retry` sections
- `src/Messaging.Contracts/Hashing/MessageIdentity.cs` + `Projections/L2ProjectionKeys.cs` + `KeeperQueues.cs` — `H`/key derivation
- `scripts/phase-39-close.ps1` — close gate (DLQ depth assertion lines 343-359; settle-drain precedent 262-283; gate-is-snapshot-only lines 42-43)
- `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs` — give-up teardown + `PurgeKeeperDlqAsync` (the KHARD-02 target)
- `tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs` + `FakeRedis.cs` — hermetic test patterns (the KHARD-01 base)
- `.planning/REQUIREMENTS.md` (KHARD-01/02/03, lines 68-75), `.planning/ROADMAP.md` (Phase 40, lines 312-320), `.planning/v3.7.0-MILESTONE-AUDIT.md` (tech-debt items + IN-01)

### Secondary
- MEMORY.md entries: keeper unbounded reinject loop; close-gate flag-key churn (SET XX keepTtl); RealStack fault-trip mechanics; container rebuild before gate.

### Tertiary
- None (no web/Context7 needed — this is a closed-world codebase-grounding phase).

---

## Metadata

**Confidence breakdown:**
- Current-state map (consumers, gate, tests, config): HIGH — direct reads of every cited file.
- KHARD-03 extraction shape: HIGH — duplication is verbatim; precedent (`L2ProbeRecovery`) is in-repo.
- KHARD-01 cap mechanism: MEDIUM-HIGH — the per-`H`-counter approach is sound and pattern-matched to `flag[H]`; the exactly-one-park-under-2-replicas detail (A1) and the config defaults (A2-A4) need PLAN/discuss confirmation.
- KHARD-02 drain: HIGH — the target code + gate contract are explicit; only the timing knobs (A5) are tunable.
- Pitfalls: HIGH — all sourced from MEMORY + the gate's own header comments.

**Research date:** 2026-06-06
**Valid until:** stable (closed-world codebase phase; only invalidated by edits to the cited Keeper files)

---

## RESEARCH COMPLETE

**Phase:** 40 - Keeper Recovery Hardening
**Confidence:** HIGH

### Key Findings
- The two fault consumers are verbatim duplicates except 3 data deltas (type param, `fault_type` literal, reinject endpoint) → KHARD-03 = one injected generic `KeeperRecoveryHandler` helper (mirrors the existing `L2ProbeRecovery` precedent); do it FIRST so the cap lands in one place.
- KHARD-01's cap is a NEW cross-message per-`H` counter (Redis `skp:keeper:attempts:{H}`, atomic INCR, TTL-bounded, DEL-on-park) — NOT the existing inner-loop `ProbeOptions.MaxAttempts`. `H` is stable across reinjects by design, making it the correct cap key.
- KHARD-02 replaces an existing one-shot `PurgeKeeperDlqAsync` + `Task.Delay(10s)` in `KeeperRecoveryE2ETests` with a bounded poll-until-stably-empty drain (the gate stays snapshot-only by contract — NO gate-side purge).
- Hermetic cap test extends `KeeperProbeLoopTests` + `FakeRedis`; Wave-0 gap = `FakeRedis` must be extended to back the counter ops. A live cap test is FORBIDDEN (flood landmine).
- All three requirements recombine existing in-repo patterns; no new external dependency, no web research needed.

### File Created
`.planning/phases/40-keeper-recovery-hardening/40-RESEARCH.md`

### Ready for Planning
Research complete. The planner has file-accurate current-state maps, an extraction recommendation, a cap-tracking design, a drain-hardening design, the hermetic test approach, a Validation Architecture, and a flagged Assumptions Log (A1-A6) for the discretion gray areas.
