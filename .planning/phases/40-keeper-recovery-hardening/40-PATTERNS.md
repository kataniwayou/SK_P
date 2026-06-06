# Phase 40: Keeper Recovery Hardening - Pattern Map

**Mapped:** 2026-06-06
**Files analyzed:** 8 (3 NEW concerns, 5 MODIFY)
**Analogs found:** 8 / 8 (every KHARD concern recombines an in-repo precedent)

> This is a **refactor-and-harden** phase. The two dominant shapes are (1) the **delegation/extraction** of two verbatim-duplicate consumer bodies into one injected generic helper (mirror `L2ProbeRecovery`), and (2) the **single-winner atomic-write** counter (mirror the `flag[H]` `When.Exists` / `keepTtl` dedup gate). No new domain — see `40-RESEARCH.md` for the current-state map; this doc pins the concrete excerpts each new/modified file copies from.

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| **NEW** `src/Keeper/Recovery/KeeperRecoveryHandler.cs` | service (injected helper) | event-driven (fault intake → recover/park) | `src/Keeper/Recovery/L2ProbeRecovery.cs` | role+flow exact (same injected-singleton-helper shape, same namespace) |
| **MODIFY** `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` | consumer | event-driven | (itself, pre-refactor) + the helper it delegates to | self / extraction |
| **MODIFY** `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` | consumer | event-driven | `FaultEntryStepDispatchConsumer.cs` (verbatim twin) | exact |
| **NEW** per-`H` attempt counter `skp:keeper:attempts:{H}` | utility (L2 key + atomic op) | CRUD (atomic INCR / EXPIRE / DEL on Redis) | `flag[H]` single-winner gate (`StepDispatcher.cs:59`, `EntryStepDispatchConsumer.cs:225`, `ResultConsumer.cs:115`) + key factory `L2ProjectionKeys.cs:51-54` | role+flow strong (same `skp:` family, same single-winner / `keepTtl` discipline) |
| **MODIFY** `src/Keeper/Program.cs` | config (DI composition root) | — | the existing `AddSingleton<L2ProbeRecovery>()` block (`Program.cs:29-33,50-54`) | exact |
| **NEW** hermetic cap test in `tests/BaseApi.Tests/Keeper/` | test | event-driven (harness `Sent`/`Consumed` assertions) | `KeeperProbeLoopTests.cs` (`BuildHarness` + `Probe_GiveUp_ParksToDlq`) | exact |
| **MODIFY** `tests/BaseApi.Tests/Keeper/FakeRedis.cs` | test (Redis double) | CRUD | itself — extend the `BuildDatabase()` NSubstitute config to back counter ops | self / extension |
| **MODIFY** `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs` (give-up teardown) | test (E2E teardown) | request-response (RabbitMQ mgmt API poll) | the gate's settle-drain loop `scripts/phase-39-close.ps1:262-283` (port to C#) | flow-match (poll-until-baseline shape) |

**Also-modify (config + tags, small):** `src/Keeper/ProbeOptions.cs` (+`RecoverAttemptCap`), `src/Keeper/appsettings.json` (`Probe` section), `src/Keeper/Observability/KeeperMetrics.cs` (`KeeperMetricTags.ReasonRecoverCap`), `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` (+`KeeperRecoverAttempts(h)`).

---

## Pattern Assignments

### NEW `src/Keeper/Recovery/KeeperRecoveryHandler.cs` (service, event-driven) — KHARD-03 keystone

**Analog:** `src/Keeper/Recovery/L2ProbeRecovery.cs` (injected-singleton-helper shape) + the verbatim consumer body in `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs`.

**Injected-helper ctor + namespace pattern to copy** (`L2ProbeRecovery.cs:19`):
```csharp
public sealed class L2ProbeRecovery(IConnectionMultiplexer redis, IOptions<ProbeOptions> opts, KeeperMetrics metrics)
```
The new handler mirrors this exactly — primary-constructor, `sealed`, ctor-injects `ILogger<KeeperRecoveryHandler>`, `L2ProbeRecovery recovery`, `KeeperMetrics metrics`, and (NEW for KHARD-01) `IConnectionMultiplexer redis` + `IOptions<ProbeOptions> opts` for the cap counter. Lives in `namespace Keeper.Recovery`.

**The verbatim body to extract** (identical in both consumers, `FaultEntryStepDispatchConsumer.cs:36-103`). The body the handler absorbs, with the **3 per-type deltas** that become parameters (verified against `FaultExecutionResultConsumer.cs:37-104` — byte-identical except these three):

| Delta | Dispatch consumer | Result consumer | Handler param |
|-------|-------------------|-----------------|---------------|
| `fault_type` tag | `KeeperMetricTags.FaultTypeDispatch` (`FaultEntryStepDispatchConsumer.cs:45,82,97`) | `KeeperMetricTags.FaultTypeResult` (`FaultExecutionResultConsumer.cs:46,83,98`) | `string faultTypeTag` |
| re-inject endpoint | `new Uri($"queue:{inner.ProcessorId:D}")` (`:71`) | `new Uri($"queue:{OrchestratorQueues.Result}")` (`:72`) | `Func<T, Uri> reinjectEndpoint` |
| generic type | `EntryStepDispatch` | `ExecutionResult` | `where T : class` (+ see generic-bound note below) |

**Intake block to copy verbatim** (`FaultEntryStepDispatchConsumer.cs:38-53`) — double-unwrap, stopwatch, `FaultConsumed.Add`, the **load-bearing manual CorrelationId scope** + `ExecutionLogScope.BuildState(inner)`, one Information log:
```csharp
var inner = context.Message.Message;   // double .Message — verbatim inner IExecutionCorrelated
var ex    = context.Message.Exceptions is { Length: > 0 } exs ? exs[0] : null;
var procId = inner.ProcessorId.ToString("D");
var sw     = Stopwatch.StartNew();
metrics.FaultConsumed.Add(1, KeeperMetricTags.FaultTags(faultTypeTag, procId));
using (logger.BeginScope(new Dictionary<string, object> { [CorrelationKeys.LogScope] = inner.CorrelationId.ToString() }))
using (logger.BeginScope(ExecutionLogScope.BuildState(inner)))
{
    logger.LogInformation(
        "Keeper fault intake: {FaultType} for H={H} — {ExceptionType}: {ExceptionMessage}",
        typeof(T).Name, inner.H, ex?.ExceptionType, ex?.Message);
}
```

**Pause / probe-await / recover-or-park block to copy** (`FaultEntryStepDispatchConsumer.cs:58-103`) — the Publish(Pause), `recovery.RunAsync`, the `Recovered` branch (re-inject Send + Publish(Resume) + 3 metrics) and the `GaveUp` branch (park `context.Message` to `KeeperQueues.DeadLetter` + 2 metrics). **KHARD-01's cap check is inserted at the TOP of the `Recovered` branch, before the re-inject Send** (see counter pattern below).

**Generic-bound note (refines RESEARCH A6 — VERIFIED):** `IExecutionCorrelated` (`src/Messaging.Contracts/IExecutionCorrelated.cs:10-17`) declares `ExecutionId`, `WorkflowId`, `StepId`, `ProcessorId`, `EntryId` and (via `ICorrelated`) `CorrelationId` — **but NOT `H`**. `H` is declared only on the concrete records (`EntryStepDispatch.cs:17`, `ExecutionResult.cs:18`). The body reads `inner.H` (`:52,59,71,76`). So `where T : class, IExecutionCorrelated` **cannot** read `inner.H` directly. Planner must pick one: (a) add `string H { get; }` to `IExecutionCorrelated` (both records already have it — lowest-risk, one-line interface add), (b) a narrower new interface, or (c) pass `H` via a `Func<T,string>` accessor. Option (a) is cleanest and matches the existing pattern. Confirm in PLAN.

**Consumers shrink to (delegation target shape):**
```csharp
public Task Consume(ConsumeContext<Fault<EntryStepDispatch>> context) =>
    handler.HandleAsync(context, KeeperMetricTags.FaultTypeDispatch,
        inner => new Uri($"queue:{inner.ProcessorId:D}"), context.CancellationToken);
```

---

### MODIFY `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` + `FaultExecutionResultConsumer.cs` (consumer, event-driven)

**Current state (the duplicated body being removed):** both files, `Consume` is ~67 lines (`FaultEntryStepDispatchConsumer.cs:36-103`; `FaultExecutionResultConsumer.cs:37-104`). After the refactor each `Consume` is a one-line `=> handler.HandleAsync(...)` expression body (see shape above).

**Ctor change:** `(ILogger<T>, L2ProbeRecovery recovery, KeeperMetrics metrics)` → `(ILogger<T>, KeeperRecoveryHandler handler)`. The `L2ProbeRecovery`/`KeeperMetrics` deps move INTO the handler.

**DO-NOT-TOUCH constraint (Pitfall 4):** the asymmetric retry-owner seam in the two `*ConsumerDefinition.cs` files stays exactly as-is — `FaultEntryStepDispatchConsumerDefinition` owns `UseMessageRetry`, `FaultExecutionResultConsumerDefinition` is an intentional no-op. The refactor extracts only the consumer *bodies*, never the definitions.

---

### NEW per-`H` attempt counter `skp:keeper:attempts:{H}` (utility, CRUD) — KHARD-01

**Analog 1 — the single-winner `flag[H]` dedup gate.** Three call sites prove the convention:
- `src/Orchestrator/Dispatch/StepDispatcher.cs:59` — first writer seeds with TTL:
```csharp
await db.StringSetAsync(L2ProjectionKeys.Flag(h), "Pending", expiry: FlagTtl);
```
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:223-225` — the **`keepTtl` landmine note + the `When.Exists` single-winner flip** (copy this discipline verbatim):
```csharp
// keepTtl: SET XX without KEEPTTL would CLEAR the sender's 300s TTL, making every deduped flag a
// permanent skp:flag:* key (unbounded Redis growth). KEEPTTL preserves the bound so Ack flags drain.
await db.StringSetAsync(L2ProjectionKeys.Flag(dispatch.H), "Ack", expiry: null, keepTtl: true, when: When.Exists);
```
- `src/Orchestrator/Consumers/ResultConsumer.cs:113-115` — identical `keepTtl: true, when: When.Exists` flip (the orchestrator-hop twin).

**Analog 2 — the key factory.** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:50-54`:
```csharp
/// <summary>D-05: effect-first dedup flag key — <c>skp:flag:{64hex}</c>.</summary>
public static string Flag(string h) => $"{Prefix}flag:{h}";

/// <summary>D-03: probe scratch key — short-TTL write-then-delete; the TTL is the crash net-zero net.</summary>
public static string KeeperProbe(string h) => $"{Prefix}keeper:probe:{h}";   // "skp:keeper:probe:{h}"
```
Add `KeeperRecoverAttempts(h) => $"{Prefix}keeper:attempts:{h}"` (`skp:keeper:attempts:{h}`) right after `KeeperProbe` — same family, same `Prefix` const.

**Recommended algorithm (the single-winner crossing pattern, mirrors `flag[H]` first-writer-wins; A1):**
1. `var n = await db.StringIncrementAsync(L2ProjectionKeys.KeeperRecoverAttempts(h));` — atomic `INCR`, race-free across the 2 keeper replicas (the whole point — see "Don't Hand-Roll" in RESEARCH).
2. On first create set TTL **without clobbering** (the `keepTtl` landmine, MEMORY): `await db.KeyExpireAsync(key, ttl, ExpireWhen.HasNoExpiry);` (or seed via `StringSetAsync(..., When.NotExists)` then `INCR`).
3. Gate the park on the **crossing increment only** — `if (n == cap + 1)` parks once (the single winner), `if (n > cap + 1)` → already parked, do nothing; `n <= cap` → re-inject as today.
4. On park: `await db.KeyDeleteAsync(key);` (DEL — converges to a single park, key gone). Register the key in the E2E `factory.L2KeysToCleanup` + the close-gate settle-drain set (TTL self-cleans a missed DEL → net-zero triple-SHA).

**Config home** — extend `ProbeOptions` (`src/Keeper/ProbeOptions.cs:8-12`, currently `DelaySeconds`/`MaxAttempts`) with `RecoverAttemptCap` (default `3`); bound from the existing `"Probe"` section in `appsettings.json` (no new `Configure<T>` line — already wired at `Program.cs:29`).

**New give-up reason tag** — add `KeeperMetricTags.ReasonRecoverCap = "recover_cap"` beside `ReasonProbeExhausted` (`KeeperMetrics.cs:124-126`), so `keeper_dlq_pushed{reason}` distinguishes flood-cap from L2-never-returned.

---

### MODIFY `src/Keeper/Program.cs` (config / DI composition root)

**Analog:** the existing recovery-helper registration block (`Program.cs:31-33`):
```csharp
// PROBE-01 — the shared bounded probe-loop helper (stateless; ctor-injects the singleton multiplexer +
// IOptions<ProbeOptions>). Both fault consumers depend on it.
builder.Services.AddSingleton<Keeper.Recovery.L2ProbeRecovery>();
```
Add one line directly after it: `builder.Services.AddSingleton<Keeper.Recovery.KeeperRecoveryHandler>();`. The consumer-registration block (`Program.cs:50-54`) and the `ProbeOptions` Configure (`:29`) are **unchanged** — the handler ctor-injects the already-registered `IConnectionMultiplexer` (transitive via `AddBaseConsole`, `:21`), `IOptions<ProbeOptions>`, `L2ProbeRecovery`, `KeeperMetrics`.

---

### NEW hermetic cap test in `tests/BaseApi.Tests/Keeper/` (test, event-driven) — KHARD-01 deliverable

**Analog:** `tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs`.

**Harness-build pattern to copy** (`KeeperProbeLoopTests.cs:108-122`) — must add `.AddSingleton<KeeperRecoveryHandler>()` once the handler exists:
```csharp
private static ServiceProvider BuildHarness(
    FakeRedis fake, IOptions<ProbeOptions> opts, Action<IBusRegistrationConfigurator> addConsumers) =>
    new ServiceCollection()
        .AddLogging()
        .AddMetrics()
        .AddSingleton<KeeperMetrics>()
        .AddSingleton<IConnectionMultiplexer>(fake.Multiplexer)
        .AddSingleton(opts)
        .AddSingleton<L2ProbeRecovery>()
        // KHARD-03: + .AddSingleton<KeeperRecoveryHandler>()
        .AddMassTransitTestHarness(x =>
        {
            addConsumers(x);
            x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
        })
        .BuildServiceProvider(true);
```

**Send/park-assertion pattern to copy** (`KeeperProbeLoopTests.cs:170-192`, `Probe_GiveUp_ParksToDlq`) — the cap test is a richer variant:
```csharp
await harness.Bus.Publish<Fault<EntryStepDispatch>>(new { Message = dispatchInner }, ct);
Assert.True(await harness.Consumed.Any<Fault<EntryStepDispatch>>(ct));
Assert.True(await harness.Sent.Any<Fault<EntryStepDispatch>>(ct));   // PARKED envelope
Assert.False(await harness.Sent.Any<EntryStepDispatch>(ct));         // NO bare-inner re-inject
```
**Cap-test extension:** publish the SAME `H` `cap+1` times (each with `FakeRedis(RedisHealth.Up)` so the probe recovers); assert `harness.Sent` count of bare inner == `cap`, then exactly ONE `Sent<Fault<T>>` to keeper-dlq on the `cap+1`-th, and the counter key is DEL'd (fake-counter assertion). The `Opts`/`SampleInner`/`NewMetrics` helpers (`:40-54`) are reused. Place in a sibling `KeeperRecoverCapTests.cs` or a new fact in `KeeperProbeLoopTests.cs`.

**Test-safety constraint (MEMORY landmine):** hermetic only (in-memory harness + FakeRedis, no live keeper). A RealStack cap test is FORBIDDEN — it would flood the live stack (~67 cyc/s/replica).

---

### MODIFY `tests/BaseApi.Tests/Keeper/FakeRedis.cs` (test Redis double, CRUD) — Wave-0 gap

**Analog:** itself — the current `BuildDatabase()` NSubstitute config (`FakeRedis.cs:109-127`) backs ONLY Get/Set/Delete:
```csharp
private IDatabase BuildDatabase()
{
    var db = Substitute.For<IDatabase>();
    db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
        .Returns(_ => Task.FromResult(OnRead()));
    db.StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
            Arg.Any<When>(), Arg.Any<CommandFlags>())
        .Returns(_ => Task.FromResult(OnWrite()));
    db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
        .Returns(_ => Task.FromResult(OnWrite()));
    return db;
}
```
**Extension (match this style):** add `db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())` backed by an in-double `Dictionary<RedisKey,long>` (so consecutive INCRs on the same `H` return 1,2,3,...), and `db.KeyExpireAsync(...)` returning `true`. The double must track DEL so the cap test can assert the counter key was removed. Keep the `Substitute.For<IDatabase>()` + `.Returns(_ => ...)` idiom; do NOT introduce a new mocking library (NSubstitute is the repo standard — `FakeRedis.cs:27`).

---

### MODIFY give-up E2E teardown in `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs` (test, request-response) — KHARD-02

**Current state (the audit-flagged drain to replace):**
- The fragile fixed wait (`KeeperRecoveryE2ETests.cs:379-384`): `await Task.Delay(TimeSpan.FromSeconds(10), ct);` — banks on both replicas parking within 10s.
- The one-shot purge (`:396`): `await PurgeKeeperDlqAsync(ct);` calling (`:512-520`):
```csharp
private static async Task PurgeKeeperDlqAsync(CancellationToken ct)
{
    using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri("http://localhost:15673") };
    var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes("guest:guest"));
    http.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);
    try { await http.DeleteAsync("/api/queues/%2F/keeper-dlq/contents", ct); }
    catch { /* best-effort terminal-queue drain */ }
}
```
The race (`:391-395`): the synthetic Fault is **Published** → both keeper replicas give up + park >10s apart → a late park lands after the one-shot purge → `keeper-dlq` depth==1 → gate `GATE_EXIT=1`.

**Analog (port to C#):** the gate's settle-drain loop `scripts/phase-39-close.ps1:262-283` — poll-to-baseline with a bounded deadline:
```powershell
$settleDeadline = (Get-Date).AddSeconds(330)
$settled = $false
while ((Get-Date) -lt $settleDeadline) {
    $nowRedis = (docker exec sk-redis redis-cli --scan | ...).Trim()
    if ($nowRedis -ceq $beforeRedis) { $settled = $true; break }
    Start-Sleep -Seconds 5
}
```

**New helper `DrainKeeperDlqUntilStablyEmptyAsync(ct)`:** loop until a max-timeout deadline — (1) `PurgeKeeperDlqAsync` (reuse the existing mgmt-API DELETE), (2) poll depth via `GET /api/queues/%2F/keeper-dlq` → `messages`, (3) when depth has been `0` for a continuous **stability window** (must exceed the ">10s apart" inter-replica gap — RESEARCH A5: poll 2s, window ≥15s, max 90s), return; else fail loudly. **Keep** the `KeeperDlqProbe`-based park PROOF (`:374-377`) — KHARD-02 changes only the net-zero teardown.

**Pitfall 2 (MEMORY + gate header):** do NOT add a purge to `phase-39-close.ps1` — the gate is snapshot-only by contract so a teardown regression surfaces as depth>0. The drain stays in the E2E teardown.

---

## Shared Patterns

### Injected-stateless-helper (DI symmetry)
**Source:** `src/Keeper/Recovery/L2ProbeRecovery.cs:19` (ctor) + `src/Keeper/Program.cs:33` (`AddSingleton`).
**Apply to:** the NEW `KeeperRecoveryHandler` — `sealed` primary-ctor class in `Keeper.Recovery`, registered as a singleton beside `L2ProbeRecovery`, ctor-injected by both consumers. The existing hermetic tests `new` such helpers directly (`KeeperProbeLoopTests.cs:63,79,94`), so the handler stays trivially testable.

### Single-winner atomic-write + `keepTtl`/TTL discipline
**Source:** `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:223-225` (the `keepTtl` landmine note + `When.Exists` flip), `src/Orchestrator/Consumers/ResultConsumer.cs:113-115` (twin), `src/Orchestrator/Dispatch/StepDispatcher.cs:59` (TTL seed).
**Apply to:** the KHARD-01 counter — atomic `StringIncrementAsync`, gate the park on the crossing increment (`n == cap+1`), set TTL only on first create without clobbering it, DEL on park, register the key for net-zero cleanup. **MEMORY landmine:** a `SET` that drops the TTL churns the close-gate SHA forever.

### Stable `H` as the cross-message key
**Source:** `MessageIdentity.ComputeH(...)` (`StepDispatcher.cs:44`); `H` is deliberately stable across re-injects (per-execution lineage excluded). `H` lives on the concrete records (`EntryStepDispatch.cs:17`, `ExecutionResult.cs:18`), NOT on `IExecutionCorrelated`.
**Apply to:** the counter key `skp:keeper:attempts:{H}` — the SAME logical message keeps the SAME `H` on every recover→reinject→refault cycle, which is exactly what makes the per-`H` counter the correct cap key (and what forces the generic-bound decision in the handler).

### Hermetic harness (no RealStack)
**Source:** `KeeperProbeLoopTests.cs:108-122` (`BuildHarness`) + `FakeRedis.cs`.
**Apply to:** the KHARD-01 cap test AND the FakeRedis counter-op extension. No `[Trait("Category","RealStack")]` — runs in the fast hermetic suite (`dotnet test ... --filter "Category!=RealStack"`).

---

## No Analog Found

None. Every KHARD concern has a blessed in-repo precedent (the keystone insight of RESEARCH.md §"Don't Hand-Roll").

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| — | — | — | All 8 files map to an existing analog. |

---

## Metadata

**Analog search scope:** `src/Keeper/**`, `src/Messaging.Contracts/**`, `src/BaseProcessor.Core/Processing/`, `src/Orchestrator/{Dispatch,Consumers}/`, `tests/BaseApi.Tests/Keeper/`, `scripts/phase-39-close.ps1`.
**Files scanned (read in full or targeted):** 11 source + 1 gate script.
**Pattern extraction date:** 2026-06-06

**Refinement of RESEARCH Assumptions:**
- **A6 (RESOLVED, refined):** `IExecutionCorrelated` declares 5 members + `CorrelationId` (via `ICorrelated`) but **NOT `H`** — `H` is on the concrete records only. The generic handler bound needs a decision (add `H` to `IExecutionCorrelated` recommended). Planner must resolve in PLAN.
- **A1 (counter single-winner):** the `flag[H]` `When.Exists` first-writer-wins gate (`EntryStepDispatchConsumer.cs:225`) is the exact precedent — gate the park on the atomic `INCR == cap+1` crossing.
- **A2/A3/A4/A5:** discretion defaults (cap=3 in `ProbeOptions`, TTL≈300s, `reason="recover_cap"`, drain 2s/15s/90s) — planner locks or routes through discuss.

---

## PATTERN MAPPING COMPLETE

**Phase:** 40 - Keeper Recovery Hardening
**Files classified:** 8 (+4 small also-modify: ProbeOptions, appsettings, KeeperMetricTags, L2ProjectionKeys)
**Analogs found:** 8 / 8

### Coverage
- Files with exact analog: 6
- Files with role-match (cross-tier) analog: 2 (counter → `flag[H]`; E2E drain → gate settle-loop)
- Files with no analog: 0

### Key Patterns Identified
- **Delegation/extraction:** two verbatim-duplicate consumer bodies (`FaultEntryStepDispatchConsumer.cs:36-103` ≡ `FaultExecutionResultConsumer.cs:37-104`, only 3 data deltas) collapse into one injected `KeeperRecoveryHandler` that mirrors the `L2ProbeRecovery` singleton-helper shape; consumers become one-line `=> handler.HandleAsync(...)` delegators. Keep the asymmetric `*ConsumerDefinition` retry seam untouched (Pitfall 4).
- **Single-winner counter:** the KHARD-01 cap reuses the `flag[H]` atomic-write / `keepTtl` / `When.Exists` first-writer-wins discipline (`EntryStepDispatchConsumer.cs:223-225`) on a new `skp:keeper:attempts:{H}` key (factory beside `KeeperProbe`); park on the crossing `INCR == cap+1`, DEL on park, TTL-bounded for net-zero.
- **Poll-until-stably-empty drain:** the KHARD-02 E2E teardown ports the gate's bounded settle-drain loop (`phase-39-close.ps1:262-283`) into C#, replacing `Task.Delay(10s)` + one-shot `PurgeKeeperDlqAsync`; the gate stays snapshot-only (Pitfall 2).
- **Generic-bound caveat:** `IExecutionCorrelated` lacks `H` — a planner decision (add `H` to the interface recommended).

### File Created
`.planning/phases/40-keeper-recovery-hardening/40-PATTERNS.md`

### Ready for Planning
Pattern mapping complete. Planner can reference each analog (file:line) directly in PLAN.md action sections; the dependency order T1 KHARD-03 (extract) → T2 KHARD-01 (cap in the one helper) → T3 KHARD-02 (E2E drain) from RESEARCH still holds.
