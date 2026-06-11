---
phase: 52-three-state-keeper
reviewed: 2026-06-11T00:00:00Z
depth: standard
files_reviewed: 21
files_reviewed_list:
  - src/Keeper/Health/BitHealthLoop.cs
  - src/Keeper/Observability/KeeperMetrics.cs
  - src/Keeper/Program.cs
  - src/Keeper/Recovery/DeleteConsumer.cs
  - src/Keeper/Recovery/InjectConsumer.cs
  - src/Keeper/Recovery/RecoveryConsumerBase.cs
  - src/Keeper/Recovery/RecoveryEndpointBinder.cs
  - src/Keeper/Recovery/RecoveryEndpointHandle.cs
  - src/Keeper/Recovery/ReinjectConsumer.cs
  - src/Keeper/Recovery/ReinjectConsumerDefinition.cs
  - src/Keeper/RecoveryOptions.cs
  - src/Keeper/appsettings.json
  - tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs
  - tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/KeeperPauseAccumulateFacts.cs
  - tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs
  - tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs
  - tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/SustainedOutageFacts.cs
  - tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs
findings:
  critical: 0
  warning: 3
  info: 4
  total: 7
status: issues_found
---

# Phase 52: Code Review Report

**Reviewed:** 2026-06-11T00:00:00Z
**Depth:** standard
**Files Reviewed:** 21
**Status:** issues_found

## Summary

This review covers the full Phase 52 three-state Keeper implementation: `BitHealthLoop`, the three recovery
consumers (`ReinjectConsumer`, `InjectConsumer`, `DeleteConsumer`) and their shared base, the runtime
endpoint binder and handle singleton, `KeeperMetrics`, `RecoveryOptions`, and the corresponding test suite.

The core REINJECT silent-drop correctness (STRLEN vs KeyExists, D-06/D-07), the INJECT forward-only
write→send→delete ordering, the `CancellationToken.None` inner-send convention (IN-01), and the
`SustainedOutageRetryCount` large-finite sizing are all correctly implemented. No data-loss bugs, no
injection vulnerabilities, no hardcoded secrets in production paths.

Three warnings are raised: a missing `volatile` on the shared endpoint handle (cross-thread visibility
gap), an incomplete ordering assertion in `InjectConsumerFacts` (the send between write and delete is not
in the `Received.InOrder` chain), and a stale comment in the E2E test that mis-states the gate-wait
removal. Four informational items cover minor code quality issues.

## Warnings

### WR-01: `RecoveryEndpointHandle.Handle` is not `volatile` — cross-thread visibility gap

**File:** `src/Keeper/Recovery/RecoveryEndpointHandle.cs:24`

**Issue:** `Handle` is a plain auto-property with no `volatile` modifier and no memory barrier at the
write site. It is written once by `RecoveryEndpointBinder.ExecuteAsync` (one hosted-service thread) and
read on every edge-transition by `BitHealthLoop.ExecuteAsync` (a different hosted-service thread). The
.NET memory model does not guarantee that a store from one thread is visible to another thread without a
memory barrier. On architectures with relaxed memory models the BitHealthLoop could observe `null` long
after the binder has set the handle — lengthening the startup window beyond what T-52-11 accepts. In
practice on x86/x64 this is unlikely to manifest, but it is a correctness gap against the ECMA CLI spec.

**Fix:** Mark the backing field `volatile`, or use `Interlocked.Exchange`/`Volatile.Write` + `Volatile.Read`:

```csharp
// Option A — simplest: backing field volatile
public sealed class RecoveryEndpointHandle
{
    private volatile HostReceiveEndpointHandle? _handle;
    public HostReceiveEndpointHandle? Handle
    {
        get => _handle;
        set => _handle = value;
    }
}

// Option B — if the field must stay an auto-property, write site in RecoveryEndpointBinder:
//   Volatile.Write(ref holder._handle, handle);   // requires field to be accessible
// Option A is cleaner.
```

---

### WR-02: `InjectConsumerFacts` — the `Received.InOrder` chain does not include the send, leaving the write→send→delete ordering partially unverified

**File:** `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs:54-60`

**Issue:** The `Received.InOrder` block asserts `StringSetAsync` before `KeyDeleteAsync` but omits the
`ep.Send(completed, ...)` call between them. The design contract is write → send → delete (Pitfall 5),
and the send is the most critical safety step — deleting the source key before the send lands would
silently lose the result. The test correctly verifies that one `StepCompleted` is sent to the right
endpoint (`Assert.Single(send.Sent)`), but the three-way ordering (write < send < delete) is not
machine-locked. A future refactor that swaps send and delete would not be caught.

**Fix:** Include the send in the ordering assertion. `CapturingSendProvider` captures into a list; verify
the send was captured between the two Redis calls by checking `send.Sent.Count` is 1 before the delete
assertion, or restructure the test to use NSubstitute's callback ordering. At minimum, add a comment
flagging that the three-way ordering is intentionally only partially locked (if that is accepted):

```csharp
// Full three-way order: write L2 → send StepCompleted → delete source.
// The send is captured by CapturingSendProvider (Assert.Single above); NSubstitute
// InOrder only covers Redis calls directly, so assert send count before delete fires.
Received.InOrder(() =>
{
    db.StringSetAsync(
        Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
        Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
    db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
});
// Belt: send must have occurred before the delete (captured list populated before delete Guard runs)
Assert.Equal(1, send.Sent.Count);
```

The current test is already there but the comment and the assertion order should make the three-step
guarantee explicit.

---

### WR-03: `SC2RecoveryPathsE2ETests` — stale comment states gate-wait is still in `RecoveryConsumerBase.Consume`

**File:** `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs:44-45` and `73`

**Issue:** Two comments in the E2E test describe the removed gate-wait as still present:

- Line 44–45: "Gate-open precondition: a healthy RealStack keeps the L2 health gate OPEN
  (`RecoveryConsumerBase.Consume` awaits `gate.WaitForOpenAsync` at entry)."
- Line 73: "the five-state recovery consumers process at entry (RecoveryConsumerBase awaits
  gate.WaitForOpenAsync)."

Phase 52 (D-04/D-09) removed the per-`Consume` gate-wait entirely — gating is now at the endpoint level
via `Stop`/`Start`. The comment is factually wrong: `RecoveryConsumerBase.Consume` dispatches straight to
`HandleAsync` with no gate-wait. A future reader debugging a gating issue could be seriously misled.
Additionally, "five-state" (line 73) is incorrect — there are three states (REINJECT, INJECT, DELETE).

**Fix:** Update both comments to reflect the Phase 52 change:

```csharp
// Gate-open precondition: a healthy RealStack keeps the BIT loop from Stop()ing the
// keeper-recovery endpoint (D-04/Phase 52). When the endpoint is running, the three
// recovery consumers (REINJECT, INJECT, DELETE) process at entry with no Consume-level gate-wait
// (the gate-wait was removed in Phase 52 — gating is now endpoint Stop/Start).
```

---

## Info

### IN-01: `GetSendEndpoint(...)` is not wrapped in `Guard` — transient failures bypass the inner retry loop

**File:** `src/Keeper/Recovery/InjectConsumer.cs:34`, `src/Keeper/Recovery/ReinjectConsumer.cs:49`

**Issue:** In both `InjectConsumer` and `ReinjectConsumer`, `Send.GetSendEndpoint(new Uri(...))` is called
outside the `Guard` wrapper. Only the subsequent `ep.Send(...)` is retried. If `GetSendEndpoint` throws a
transient `MassTransitException` (e.g., bus not yet fully started), the exception propagates directly to
the MassTransit pipeline retry rather than through `RetryLoop`. This is not a data-loss risk (the
MassTransit pipeline retry catches it), but it is an inconsistency in the "every op goes through Guard"
contract documented in the base class and `InjectConsumer`'s summary.

**Note:** Because the MassTransit-level retry covers this path, this is not a correctness bug — it is a
pattern inconsistency worth noting but low severity.

---

### IN-02: `RecoveryConsumerBase.Db` calls `redis.GetDatabase()` on every property access

**File:** `src/Keeper/Recovery/RecoveryConsumerBase.cs:29`

**Issue:** `protected IDatabase Db => redis.GetDatabase();` is an expression-body property that invokes
`GetDatabase()` on each call. Within a single `HandleAsync` a consumer (e.g., `InjectConsumer`) accesses
`Db` twice (once per `Guard` call) and `DeleteConsumer` once. In StackExchange.Redis `GetDatabase()` is
cheap (no socket call; returns a cached logical `Database` wrapper), so this is not a functional bug.
However, computing a property side-effect per access rather than per message is a minor code smell that
could surprise future maintainers.

**Fix:** Cache at the start of `HandleAsync` or promote to a constructor-resolved field:

```csharp
// Option: cache in base ctor (since redis is a singleton, db is stable for the consumer's lifetime)
protected IDatabase Db { get; }
// ctor: Db = redis.GetDatabase();
```

---

### IN-03: Hardcoded default RabbitMQ credentials in `appsettings.json`

**File:** `src/Keeper/appsettings.json:22-24`

**Issue:** `"Username": "guest"` and `"Password": "guest"` are committed in the main appsettings file.
While these are well-known defaults intended for local dev/docker-compose, committing default credentials
in a non-sample config file is a pattern risk: if a production deploy inadvertently uses this file
without an override, the broker is accessible with default credentials.

**Fix:** Move to a `appsettings.Development.json` or placeholder (`"Password": "<SET_VIA_ENV>"`), and
rely on environment variable overrides (`RabbitMq__Password`) for all non-dev targets. The existing
`RabbitMq__` prefix env-var override works for this today.

---

### IN-04: `SC2RecoveryPathsE2ETests` — `HostRedis` constant is shadowed by `HostRedisFull` in the inner class

**File:** `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs:363,405`

**Issue:** Two constants carry the same connection string value:
- `private const string HostRedis = "localhost:6380,abortConnect=false,connectTimeout=5000";` (outer class, line 363)
- `private const string HostRedisFull = "localhost:6380,abortConnect=false,connectTimeout=5000";` (inner `RealStackWebAppFactory`, line 405)

`HostRedis` is used only in `PollForKeyValueAsync`/`PollForKeyAbsentAsync`'s direct multiplexer
connect (line 77), while `HostRedisFull` is used in `RealStackWebAppFactory`. If either is updated
the other silently diverges. The outer-class `HostRedis` cannot reference the inner `HostRedisFull`,
but the inner class could expose `HostRedisFull` as `internal const` or both could be unified via a
shared helper.

**Fix:** Consolidate to a single constant (elevate `HostRedisFull` to the outer class level, or vice
versa) and reference it from both use sites:

```csharp
// In the outer class:
private const string HostRedis = "localhost:6380,abortConnect=false,connectTimeout=5000";
// In RealStackWebAppFactory: reference the outer constant (since it is private to the same file)
```

---

_Reviewed: 2026-06-11T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
