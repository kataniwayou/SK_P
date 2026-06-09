---
phase: 48-v3-x-teardown
reviewed: 2026-06-09T00:00:00Z
depth: standard
files_reviewed: 13
files_reviewed_list:
  - src/Keeper/ProbeOptions.cs
  - src/Keeper/Program.cs
  - src/Keeper/Recovery/L2ProbeRecovery.cs
  - src/Keeper/appsettings.json
  - src/Messaging.Contracts/KeeperQueues.cs
  - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
  - tests/BaseApi.Tests/BaseApi.Tests.csproj
  - tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs
  - tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs
  - tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs
  - tests/BaseApi.Tests/Keeper/KeeperHostBootFixture.cs
  - tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs
  - tests/BaseApi.Tests/Resilience/ReactivePathRetiredFacts.cs
findings:
  critical: 0
  warning: 0
  info: 5
  total: 5
status: issues_found
---

# Phase 48: Code Review Report

**Reviewed:** 2026-06-09T00:00:00Z
**Depth:** standard
**Files Reviewed:** 13
**Status:** issues_found

## Summary

Phase 48 is a v3.x reactive-recovery teardown (RETIRE-03): deletions of the `Fault<T>` recovery
consumers + `KeeperRecoveryHandler`, the retired `keeper-dlq`/`keeper-fault-recovery` queue consts, a
partial-delete reducing `L2ProbeRecovery` to the single `ProbeOnceAsync` BIT-probe helper, the
`Program.cs` unwiring, and a set of negative-guard tests that make the teardown self-verifying.

The teardown is correct and internally consistent. I verified the load-bearing concerns directly:

- **Wiring is intact.** `L2ProbeRecovery` (now just `ProbeOnceAsync`) is registered in both
  `Program.cs:37` and the parallel `KeeperHostBootFixture.cs:48`, and is the live ctor-dependency of
  `BitHealthLoop` (`BitHealthLoop.cs:13`, called at line 32). No caller invokes a now-deleted member.
- **No dangling references to deleted types in compiled code.** A whole-tree scan for the retired
  symbols (`FaultEntryStepDispatchConsumer`, `FaultExecutionResultConsumer`, `KeeperRecoveryHandler`,
  `KeeperQueues.FaultRecovery`, `KeeperQueues.DeadLetter`, `keeper-dlq`, `keeper-fault-recovery`) found
  zero references in active code paths — the only hits are XML-doc `<c>` prose (see IN-01).
- **Contract surface is clean.** `KeeperQueues` retains exactly `Recovery` (the sole surviving queue);
  the retired consts are gone. `L2ProjectionKeys.ExecutionData` is the single Guid-only overload.
- **The guard tests are sound.** `ReactivePathRetiredFacts` and `AtLeastOnceStructuralFacts` use a
  correct reflection + source-scan idiom with an explicit false-pass guard (`Directory.Exists` before
  enumerate) and a `[CallerFilePath]`-anchored repo-root resolver. The `BitHealthLoop` `ScriptedRedis`
  double is deterministic (park-then-release) and its edge-trigger assertions correctly account for the
  intentional `prev=null` startup `ResumeAll`.

No Critical or Warning findings. All five findings are Info-level: teardown-completeness loose ends
(orphaned test helper, stale doc references) and one pre-existing latent issue surfaced by the review.
None block the phase.

## Info

### IN-01: Stale `<c>` doc references to the deleted `FaultEntryStepDispatchConsumerDefinition`

**File:** `src/Keeper/Recovery/UpdateConsumerDefinition.cs:13`, `ReinjectConsumerDefinition.cs:9`, `InjectConsumerDefinition.cs:9`, `DeleteConsumerDefinition.cs:9`, `CleanupConsumerDefinition.cs:9`; also `src/Orchestrator/Consumers/StepCompletedConsumerDefinition.cs:18`
**Issue:** Six XML-doc comments still cite `<c>FaultEntryStepDispatchConsumerDefinition</c>` as the
"single-owner / no-op mirror" precedent, but that type was deleted in this teardown. Because the
reference is inside a `<c>` (inline-code) tag rather than a `<see cref="...">`, it does NOT break the
build or doc generation — it is purely a stale prose pointer to a type that no longer exists, which will
mislead a future reader looking for the precedent.
**Fix:** Re-point the prose at a surviving precedent. The current single-owner endpoint definition is
`UpdateConsumerDefinition` (per `Program.cs:51` comment, it owns the endpoint retry + the five
`UsePartitioner` calls while the other four no-op). Replace `FaultEntryStepDispatchConsumerDefinition`
with `UpdateConsumerDefinition` in those `<c>` tags (and drop the cross-reference entirely from
`StepCompletedConsumerDefinition.cs` or re-point it likewise).

### IN-02: Orphaned test helper `FakeRedis.cs` after the probe-loop test deletion

**File:** `tests/BaseApi.Tests/Keeper/FakeRedis.cs:33`
**Issue:** `FakeRedis` is a substantial (~214-line) Wave-0 test double built for the now-deleted
`KeeperProbeLoopTests` (its own class doc, line 8, says "consumed by Plan 02's `KeeperProbeLoopTests`").
A whole-tree scan for `FakeRedis` finds only its own definition — no remaining test references it. The
`BitHealthLoopTests` introduced in this phase rolls its own `ScriptedRedis` double instead. `FakeRedis`
is now dead test code, and its `KeyExpire`/`ScriptEvaluate`/`StringIncrement` machinery models the
deleted `KeeperRecoveryHandler`'s per-H attempt-counter behavior (lines 149-191) that no longer exists.
**Fix:** Delete `tests/BaseApi.Tests/Keeper/FakeRedis.cs` (and `BackupOptionsBoundTests`/
`ProbeOptionsBoundTests` are unaffected — they instantiate plain options objects). If the double is
intentionally retained for a future probe test, add an `// intentionally retained (unused)` note so the
next teardown pass does not re-flag it. Confirm no other Keeper test references it before removal (this
review found none).

### IN-03: `ProbeOnceAsync` accepts a `CancellationToken` it never uses

**File:** `src/Keeper/Recovery/L2ProbeRecovery.cs:27`
**Issue:** `ProbeOnceAsync(CancellationToken ct, ...)` receives `stoppingToken` from
`BitHealthLoop.cs:32`, but `ct` is never threaded into the three Redis calls
(`StringGetAsync`/`StringSetAsync`/`KeyDeleteAsync` at lines 32-35 take no token). StackExchange.Redis
does not expose a `CancellationToken` on these overloads, so the parameter is structurally unusable here
and is dead. A probe op cannot be cancelled mid-flight; cancellation is only observed at the loop's
`Task.Delay` between ticks (`BitHealthLoop.cs:63`). This is a pre-existing latent issue (not introduced
by the teardown), but the partial-delete left it as the method's only signature.
**Fix:** Either drop the unused `ct` parameter to make the no-cancellation contract explicit
(`public async Task<bool> ProbeOnceAsync(Guid? entryId = null, string? h = null)` — update the single
caller at `BitHealthLoop.cs:32`), or keep it and add a one-line comment noting SE.Redis has no
token-bearing overload so the token bounds only the inter-tick delay. Prefer dropping it; the loop
already handles shutdown via the delay's `OperationCanceledException`.

### IN-04: `appsettings.json` Service.Version pinned to `3.7.0` during the v3.x teardown

**File:** `src/Keeper/appsettings.json:11`
**Issue:** `Service.Version` reads `"3.7.0"`, the version of the reactive path this phase is tearing
down. Sibling source already documents the post-teardown world as v4 (e.g.
`L2ProjectionKeys.cs:24,41` cite "removed in v4.0.0"; `AtLeastOnceStructuralFacts.cs:9` is "the v4.0.0
at-least-once invariants"). Shipping the Keeper console still self-reporting `3.7.0` after the surface
that defined 3.7.0 is gone will mislabel metrics/health output for the v4 build.
**Fix:** Confirm the intended milestone version for this teardown and bump `Service.Version` to match
(likely `4.0.0`). If the version bump is deliberately owned by a later phase, no change is needed here —
flagging only so the stale value is a conscious decision, not an oversight.

### IN-05: FACT 4 manifest-scan uses `Name.Contains("Manifest")` (broad substring)

**File:** `tests/BaseApi.Tests/Resilience/ReactivePathRetiredFacts.cs:145`
**Issue:** The "no result-manifest type survives" guard asserts
`t.Name.Contains("Manifest")` across the Orchestrator + BaseProcessor.Core assemblies. A substring match
is intentionally broad (it is the point — catch any resurrected manifest type under any name), but it
would also false-positive on an unrelated future type whose name merely contains the word "Manifest"
(e.g. a hypothetical `AssemblyManifestReader`), turning an unrelated addition into a spurious teardown
regression failure. The companion `AtLeastOnceStructuralFacts` FACT A deliberately uses exact
`t.Name == "MessageIdentity"` for its name guard, so this is also a slight idiom inconsistency.
**Fix:** Low priority — accept as-is given the deliberate breadth, OR tighten to the known retired
type name(s) if RETIRE-02 deleted a specifically-named manifest type (e.g.
`t.Name == "ExecutionResultManifest"`). Document the choice with a one-line comment so a future reader
knows the broad match is intentional. No action required for correctness of the current build.

---

_Reviewed: 2026-06-09T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
