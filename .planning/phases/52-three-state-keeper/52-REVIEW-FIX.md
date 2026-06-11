---
phase: 52-three-state-keeper
fixed_at: 2026-06-11T00:00:00Z
review_path: .planning/phases/52-three-state-keeper/52-REVIEW.md
iteration: 2
findings_in_scope: 7
fixed: 6
skipped: 1
status: partial
---

# Phase 52: Code Review Fix Report

**Fixed at:** 2026-06-11T00:00:00Z
**Source review:** .planning/phases/52-three-state-keeper/52-REVIEW.md
**Iteration:** 2

**Summary:**
- Findings in scope: 7 (fix_scope=all — Critical + Warning + Info)
- Fixed: 6 (3 warnings in iteration 1, 3 info in iteration 2)
- Skipped: 1 (IN-03 — repo-wide config-pattern decision, documented rationale)

## Fixed Issues

### WR-01: `RecoveryEndpointHandle.Handle` is not `volatile` — cross-thread visibility gap

**Files modified:** `src/Keeper/Recovery/RecoveryEndpointHandle.cs`
**Commit:** 68c9f78
**Applied fix (iteration 1):** Replaced the plain auto-property `Handle` with a `volatile HostReceiveEndpointHandle? _handle` backing field plus a pass-through `get/set` property (review Option A). The write site in `RecoveryEndpointBinder.ExecuteAsync` (`holder.Handle = handle`) and the BitHealthLoop reads are unchanged — they now go through the volatile field, establishing the acquire/release fence so the binder's one-time store is promptly visible to the BIT loop's reader thread. Added a doc comment explaining the ECMA CLI memory-model rationale. Verified: `dotnet build src/Keeper/Keeper.csproj` succeeded (0 errors, 0 warnings).

### WR-02: `InjectConsumerFacts` — `Received.InOrder` chain does not include the send

**Files modified:** `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs`
**Commit:** 35c6911
**Applied fix (iteration 1):** Made the write→send→delete three-way ordering explicit. Expanded the comment above `Received.InOrder` to state that NSubstitute's InOrder covers only the Redis substitute (locking write < delete) and that the send between them is captured by `CapturingSendProvider`. Added a belt assertion `Assert.Single(send.Sent)` immediately after the InOrder block to machine-lock that exactly one send was captured before the delete fires, so a future refactor dropping or reordering the send after the delete is caught. Used `Assert.Single` (not `Assert.Equal(1, ...)`) to satisfy the xUnit2013 analyzer. Verified: `dotnet build tests/BaseApi.Tests` succeeded (0 errors, 0 warnings).

### WR-03: `SC2RecoveryPathsE2ETests` — stale comment states gate-wait is still in `RecoveryConsumerBase.Consume`

**Files modified:** `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs`
**Commit:** 63e6bf3
**Applied fix (iteration 1):** Corrected both stale comments. (1) The class-summary "Gate-open precondition" comment (lines 44-45) no longer claims `RecoveryConsumerBase.Consume` awaits `gate.WaitForOpenAsync`; it now describes the Phase 52 D-04/D-09 model — a healthy RealStack keeps the BIT loop from `Stop()`ing the `keeper-recovery` endpoint, and the three consumers process at entry with no Consume-level gate-wait (gating is endpoint Stop/Start). (2) The in-body comment (lines 72-73) was fixed the same way and the incorrect "five-state" was corrected to "three recovery consumers (REINJECT, INJECT, DELETE)". Verified: `dotnet build tests/BaseApi.Tests` succeeded (0 errors, 0 warnings).

### IN-01: `GetSendEndpoint(...)` is not wrapped in `Guard`

**Files modified:** `src/Keeper/Recovery/InjectConsumer.cs`, `src/Keeper/Recovery/ReinjectConsumer.cs`
**Commit:** 641d01d
**Applied fix (iteration 2):** Wrapped `Send.GetSendEndpoint(new Uri(...))` in both consumers in the `Guard(...)` helper (`ISendEndpointProvider.GetSendEndpoint` returns `Task<ISendEndpoint>`, which matches `Guard<T>(Func<Task<T>>, ct)` directly). A transient `GetSendEndpoint` failure now routes through the bounded `RetryLoop` like every other L2/Send op, restoring the "every op goes through Guard" contract documented on the base class. The subsequent `ep.Send(..., CancellationToken.None)` Guard wrappers are unchanged. Added an explanatory comment at each site. Verified: `dotnet build src/Keeper/Keeper.csproj` succeeded (0 errors, 0 warnings).

### IN-02: `RecoveryConsumerBase.Db` calls `redis.GetDatabase()` on every property access

**Files modified:** `src/Keeper/Recovery/RecoveryConsumerBase.cs`
**Commit:** bae584e
**Applied fix (iteration 2):** Changed `protected IDatabase Db => redis.GetDatabase();` (expression-body, re-invoked per access) to a get-only property initialized once from the primary constructor parameter: `protected IDatabase Db { get; } = redis.GetDatabase();`. Because `redis` is a DI singleton, the logical `IDatabase` wrapper is stable for the consumer's lifetime, so `GetDatabase()` is now invoked once per consumer instead of once per property read (twice per INJECT message, once per DELETE). Added a rationale comment. Verified: `dotnet build src/Keeper/Keeper.csproj` succeeded (0 errors, 0 warnings).

### IN-04: `SC2RecoveryPathsE2ETests` — `HostRedis` constant shadowed by `HostRedisFull`

**Files modified:** `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs`
**Commit:** 29d4823
**Applied fix (iteration 2):** Removed the duplicate `private const string HostRedisFull` from the nested `RealStackWebAppFactory` and pointed all three of its former use sites (`redisConnectionStringOverride` base-ctor argument, the `ConnectionStrings__Redis` env-var Set, and the `DisposeAsync` cleanup multiplexer connect) at the outer-class `HostRedis` const. The nested type has access to the outer class's `private const`, so the connection string is now a single source of truth in this file — updating it can no longer silently diverge. Added a rationale comment where the duplicate was removed. Verified: `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj` succeeded (0 errors, 0 warnings).

## Skipped Issues

### IN-03: Hardcoded default RabbitMQ credentials in `appsettings.json`

**File:** `src/Keeper/appsettings.json:22-24`
**Reason:** Skipped as a deliberate engineering decision — applying it to Keeper alone would introduce inconsistency without closing the gap.
**Original issue:** `"Username": "guest"` / `"Password": "guest"` are committed in the Keeper main appsettings file; an inadvertent production deploy using this file without an override would expose the broker with default credentials.
**Rationale for skip:**
- This is a repo-wide convention, not a Keeper Phase-52 artifact: all four services (`src/BaseApi.Service`, `src/Keeper`, `src/Orchestrator`, `src/Processor.Sample`) commit the identical `guest`/`guest` default in their main `appsettings.json`, alongside docker-internal `Host` values (`rabbitmq`, `redis:6379`). Changing only Keeper's file would diverge it from its three siblings and partially obscure the convention rather than fix it.
- The value is the standard RabbitMQ docker default and is broker-rejected over non-localhost connections by default, so it is not usable as a real production credential as-committed.
- The mitigation already exists and is the documented lever: the `RabbitMq__Password` env-var override (used today by the E2E `RealStackWebAppFactory`) supersedes the file for any non-dev target.
- The review classifies this as Info / "pattern risk" with the env override noted as the existing mitigation. A correct fix is a cross-cutting config-hygiene change across all four service configs (or a shared placeholder convention) that belongs to a dedicated config-hardening task, not a single-file Keeper edit in this phase. Forcing the single-file change here trades a real consistency regression for a non-closing partial.

---

_Fixed: 2026-06-11T00:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 2_
