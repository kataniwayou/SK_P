---
phase: 71-orchestrator-recovery-pipeline
fixed_at: 2026-06-16T00:00:00Z
review_path: .planning/phases/71-orchestrator-recovery-pipeline/71-REVIEW.md
iteration: 1
findings_in_scope: 2
fixed: 2
skipped: 0
status: all_fixed
---

# Phase 71: Code Review Fix Report

**Fixed at:** 2026-06-16
**Source review:** .planning/phases/71-orchestrator-recovery-pipeline/71-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 2 (Critical + Warning; the 3 Info findings are out of scope — no `--all`)
- Fixed: 2
- Skipped: 0

Note on the REVIEW.md WR-02 duplication: the FIRST WR-02 (`SlotTtl()` double-call) was explicitly
WITHDRAWN by the reviewer (lines 91-93: "This is NOT a bug ... Withdrawing as a warning") and was left
untouched. The reassigned WR-02 (lines 97-122) is the actual in-scope warning and was fixed.

## Fixed Issues

### WR-01: `StepProcessing` reinject silently loses `EntryId` (relies on record default, not explicit)

**Files modified:** `src/Orchestrator/Recovery/OrchestratorResultPipeline.cs`
**Commit:** 33bbf6f
**Applied fix:** In `BuildReinject`, replaced the unconditional `EntryId = m.EntryId` with the reviewer's
exact explicit form `EntryId = m is StepCompleted ? m.EntryId : Guid.Empty`. Only a `StepCompleted` carries a
real data key; Failed/Cancelled/Processing now reset to `Guid.Empty` in code rather than by coincidence of the
inbound record's hard-default. Behavior is unchanged today (the contract already guarantees `Guid.Empty` on
non-Completed results) but the coupling is now visible and refactor-safe. Added an explanatory comment.

Verification: Debug + Release builds clean (0 warnings, TreatWarningsAsErrors); `*OrchestratorResultPipeline*`
facts green (9 passed).

### WR-02 (reassigned): `OrchestratorInjectConsumer` wrote the copied data key with no TTL (immortal-key leak)

**Files modified:** `src/Keeper/Recovery/OrchestratorInjectConsumer.cs`, `src/Keeper/RecoveryOptions.cs`,
`tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs`,
`tests/BaseApi.Tests/Keeper/OrchestratorInjectConsumerFacts.cs`,
`tests/BaseApi.Tests/Keeper/KeeperDeleteInvariantFacts.cs`
**Commit:** 4c64553
**Applied fix:** The INJECT-escalation copy `SET L2[EntryId] = origin` now carries a bounded TTL, mirroring the
orchestrator FORWARD Lua path's `SET ... 'PX', ARGV[4]` (== `ExecutionDataTtl`). The TTL is computed in C#
(`TimeSpan.FromSeconds(Math.Max(1, ExecutionDataTtlSeconds))`, floored at 1s) — preserving the D-03 anti-desync
invariant that TTLs are never computed inside Lua. The existing `v.HasValue` origin-present guard is unchanged,
and the consumer still contains ZERO `KeyDeleteAsync` calls (delete invariant preserved).

**Deviation from the review's literal suggestion (and why):** The review suggested injecting
`IOptions<OrchestratorRecoveryOptions>` directly. That type lives in the **Orchestrator** project, and the
Keeper enforces a reference firewall (T-34-01, `KeeperDependencyFirewallTests`): the only permitted
ProjectReferences are `BaseConsole.Core` + `Messaging.Contracts` — "no coupling to the API or processor
projects" (and, by the same rule, none to Orchestrator). Adding a Keeper -> Orchestrator project reference
would be a structural regression. To honor the review's INTENT (source the TTL from the same `IOptions<...>`
`ExecutionDataTtlSeconds` mechanism the pipeline uses, computed in C#), the `ExecutionDataTtlSeconds` knob was
added to the Keeper's OWN `RecoveryOptions` (already bound from the same "Recovery" config section, same 300s
default as `OrchestratorRecoveryOptions`), and `IOptions<RecoveryOptions>` is injected into the consumer.
DI is already wired (`Program.cs` line 35 `Configure<RecoveryOptions>(GetSection("Recovery"))`), so no
composition-root change was required.

**Per the scope clarification, `ProcessorInjectConsumer.cs` was NOT modified** — the review notes it has the
same pre-existing pattern, but it is a different console's shipped code and out of this phase's scope.

Verification: Debug + Release builds clean (0 warnings); `*OrchestratorInjectConsumer*` facts green (2 passed),
`*KeeperDeleteInvariant*` facts green (5 passed, confirms zero deletes), `*KeeperDependencyFirewall*` green
(1 passed, confirms no forbidden reference introduced). The existing fact already asserts the 5-arg
`StringSetAsync(key, value, Expiration, ValueCondition, CommandFlags)` shape that `StringSetAsync(key, value, ttl)`
binds to under SE.Redis 2.13.1, so the TTL addition required no assertion change — only the two consumer
construction sites gained the 4th `RecoveryTestKit.Recovery()` argument.

---

_Fixed: 2026-06-16_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
