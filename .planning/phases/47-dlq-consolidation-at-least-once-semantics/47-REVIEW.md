---
phase: 47-dlq-consolidation-at-least-once-semantics
reviewed: 2026-06-09T00:00:00Z
depth: standard
files_reviewed: 5
files_reviewed_list:
  - tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs
  - tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs
  - tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs
  - docs/design/2026-06-08-processor-keeper-recovery-redesign.md
findings:
  critical: 0
  warning: 2
  info: 2
  total: 4
status: issues_found
---

# Phase 47: Code Review Report

**Reviewed:** 2026-06-09
**Depth:** standard
**Files Reviewed:** 5
**Status:** issues_found

## Summary

Phase 47 is verification-only: four new xUnit fact files plus a single additive amendment
line (A16) to the locked design spec. No production source changed. The review focused on
whether each fact actually proves the invariant it claims (RESIL-02 single-DLQ
consolidation, RESIL-03 at-least-once / no-collapse) and whether any test would false-pass.

Overall the suite is well constructed and the invariant claims hold against the production
code I cross-referenced:

- `AtLeastOnceStructuralFacts` — both structural guards are sound. FACT B correctly
  excludes `KeeperRecoveryHandler.cs` (verified: it is the ONLY in-scope file referencing
  `keeper-dlq` / `KeeperQueues.DeadLetter`), and the directory-existence pre-assertions
  (T-47-01) genuinely close the silently-empty-scan false-pass hole. The reflection-vs-string
  rationale for FACT A is accurate.
- `TypedResultConsumerFacts.Duplicate_StepCompleted_reproduces_effect_no_collapse` — a
  genuine no-collapse proof. `StepCompletedConsumer` carries no state and `StepAdvancement.SelectNext`
  is a pure function (verified `src/Orchestrator/Dispatch/StepAdvancement.cs`), so double-Consume
  on one consumer + one dispatcher legitimately produces `Calls.Count == 2`.
- `KeeperDlqConsolidationTests` — the consolidated-error pipeline is reproduced faithfully
  against `ConsolidatedErrorTransportFilter` / `ConsolidatedFault` (verified symbols), and the
  topology-split const assertion (`skp-dlq-1` vs `keeper-dlq`) matches production.
- The A16 amendment is accurate: `ProcessorPipeline.BuildReinject` does stamp
  `Payload = d.Payload` (verified line 203), and the doc explicitly labels itself additive
  with no source change.
- All four `.cs` files and the `.md` are BOM-less UTF-8 (verified first bytes — no `EF BB BF`).

Two warnings concern tests that PASS for a reason other than the one their documentation
claims (accidental-pass via default mock behavior, and an over-claimed proof scope). Neither
is a hard false-pass of the headline invariant, but both weaken the regression value.

## Warnings

### WR-01: RecoveryDeadLetterFacts.EmptyMux stubs the wrong Redis method — data-gone fires by accident

**File:** `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs:38-45`
**Issue:** `EmptyMux()` configures `db.StringGetAsync(...).Returns(RedisValue.Null)`, but the
production `ReinjectConsumer.HandleAsync` (verified `src/Keeper/Recovery/ReinjectConsumer.cs:33`)
gates the data-gone terminal on `StringLengthAsync`, NOT `StringGetAsync`:

```csharp
if (await Db.StringLengthAsync(L2ProjectionKeys.ExecutionData(m.EntryId)) == 0)
    throw new RecoveryDataGoneException();
```

`StringLengthAsync` is never stubbed in `EmptyMux`, so NSubstitute returns the default
`Task<long>` value (`0`). The `== 0` predicate is true and the exception throws — so the test
passes, but because of the unconfigured-mock default, not because the stub expresses the
absent-key condition. The explicit `StringGetAsync(...).Returns(RedisValue.Null)` line is dead:
deleting it would not change the result. This is the `RESIL-03 / KEEP-05` data-gone gate, so a
silent accidental-pass here undermines the very assertion the file exists to make. Note the
sibling `PresentMux()` (lines 124-131) correctly stubs `StringLengthAsync(...).Returns(7L)`,
which makes the `EmptyMux` mismatch stand out as an oversight rather than intent.

**Fix:** Stub the method the consumer actually calls so the data-gone condition is expressed,
not inherited from the mock default:

```csharp
private static IConnectionMultiplexer EmptyMux()
{
    var db = Substitute.For<IDatabase>();
    // ReinjectConsumer gates data-gone on STRLEN == 0 (absent OR empty), not StringGet.
    db.StringLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(0L);
    var mux = Substitute.For<IConnectionMultiplexer>();
    mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
    return mux;
}
```

### WR-02: Duplicate_Reinject_reproduces_effect_no_collapse bypasses the base gate/retry seam it claims to exercise

**File:** `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs:142-172`
**Issue:** The test docstring frames this as proving the "EntryStepDispatch-family recovery
path … is at-least-once and carries NO dedup." It constructs `ReinjectConsumer` directly and
calls `consumer.Consume(...)` twice. But `RecoveryConsumerBase.Consume`
(`src/Keeper/Recovery/RecoveryConsumerBase.cs:40-60`) first awaits
`gate.WaitForOpenAsync(cts.Token)` then calls `HandleAsync`, and `HandleAsync` wraps the send
in `Guard(...) -> RetryLoop.ExecuteAsync`. The `ContextFor` substitute returns a
`Substitute.For<ConsumeContext<T>>()` whose `CancellationToken` is stubbed but whose other
members are NSubstitute defaults. The test happens to work because `OpenGate()` returns
`Task.CompletedTask` and the capturing send succeeds on the first attempt, so `RetryLoop`
never iterates. The no-dedup claim is real (there genuinely is no dedup key), but the proof is
thin: it exercises the happy single-attempt path only, and the no-collapse guarantee for the
*retry/redelivery* shape (the actual at-least-once trigger) is asserted by structure, not by
the test mechanics. This is acceptable as a no-collapse unit fact, but the docstring over-claims
("documented consumer-level double-Consume … fallback") relative to what is mechanically proven.
**Fix:** Either tighten the docstring to state the scope honestly ("proves no dedup key collapses
two independent Consume calls on the success path; redelivery-under-retry is covered structurally
by the absence of any idempotency key"), or assert the retry seam was not invoked
(`send.Sent.Count == 2` already does this implicitly, but make the intent explicit). No code-behavior
change required — this is a claim-vs-proof gap, not a bug.

## Info

### IN-01: Dlq_TopologyArgs final assertion is a tautology, not a topology check

**File:** `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs:207`
**Issue:** `Assert.Equal(604_800_000, (int)TimeSpan.FromDays(7).TotalMilliseconds)` asserts a
property of `TimeSpan`, not of the SUT. It can never fail for a reason related to DLQ-1's TTL —
the production TTL const is not referenced here. The comment already concedes the live arg proof
defers to Plan 04 / Phase 39. As written this line proves nothing about `skp-dlq-1`.
**Fix:** Assert against the production TTL constant if one is exported (e.g.
`Assert.Equal(TimeSpan.FromDays(7).TotalMilliseconds, BaseConsoleMessagingConstants.Dlq1TtlMs)`),
or drop the line and rely on the `Dlq1` / `DeadLetter` const + `NotEqual` assertions (lines
197-203) which do bind to the real consts.

### IN-02: RESIL-03 duplicate-delivery facts exclude ExecutionId without asserting it actually differs

**File:** `tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs:283-290`
**Issue:** The no-collapse assertions deliberately exclude `ExecutionId` ("regenerated per
dispatch via NewId.NextGuid"), which is correct. But the test never asserts that the two
dispatched `ExecutionId`s are in fact distinct. Adding
`Assert.NotEqual(dispatcher.Calls[0].ExecutionId, dispatcher.Calls[1].ExecutionId)` would
positively confirm the per-dispatch regeneration the comment relies on, strengthening the proof
that the second delivery is an independent dispatch rather than a replayed reference.
**Fix:** Add `Assert.NotEqual(dispatcher.Calls[0].ExecutionId, dispatcher.Calls[1].ExecutionId);`
after line 290.

---

_Reviewed: 2026-06-09_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
</content>
