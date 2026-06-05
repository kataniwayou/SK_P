---
phase: 34-keeper-console-foundation
fixed_at: 2026-06-05T00:00:00Z
review_path: .planning/phases/34-keeper-console-foundation/34-REVIEW.md
iteration: 1
findings_in_scope: 2
fixed: 2
skipped: 0
status: all_fixed
---

# Phase 34: Code Review Fix Report

**Fixed at:** 2026-06-05T00:00:00Z
**Source review:** .planning/phases/34-keeper-console-foundation/34-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope (Critical + Warning): 2
- Fixed: 2
- Skipped: 0
- Out of scope (Info, intentionally not touched): IN-01, IN-02

Both in-scope warnings describe patterns that exist IDENTICALLY in the shipped
`src/Orchestrator/` and `src/Processor.Sample/` consoles and their analogous tests.
The prime directive for this phase was to improve Keeper WITHOUT diverging it from the
established cross-console conventions or breaking the green build/tests. Both fixes are
therefore documentation/clarifying-comment changes (zero behavioral change), chosen over
the reviewer's heavier code-rewrite alternatives precisely to avoid divergence.

## Fixed Issues

### WR-01: `RetryOptions.Strategy` Is Bound From Config But Always Silently Ignored

**Files modified:** `src/Keeper/Consumers/PlaceholderConsumerDefinition.cs`
**Commit:** 7c920e6
**Applied fix:** Added a clarifying code-comment at the `UseMessageRetry` call site documenting
that `Immediate` is intentionally the ONLY supported retry strategy at this milestone, that
`RetryOptions.Strategy` (Interval/Exponential) is structured-for but deliberately NOT wired, and
that this is a shared deferral across ALL consoles — so the config key binding is intentional, not
misleading. The comment explicitly warns that wiring `Strategy` must be done uniformly across every
console (Orchestrator/Processor too), not piecemeal in Keeper.

**Rationale for fix choice (not a code-behavior change):** The reviewer offered two alternatives —
(a) add a `Strategy switch` branch at the point of use, or (b) clarify the deferral with a comment.
Option (a) was rejected: the `RetryOptions` type is shared (`src/Messaging.Contracts/Configuration/RetryOptions.cs`)
and its own doc-comment states "Only the Immediate branch is implemented this phase ... back-off is
structured-for, deferred." The Orchestrator and Processor consumer definitions hard-wire `Immediate`
the exact same way. Adding Strategy-switching to Keeper alone would diverge it from every other console
and introduce behavioral inconsistency. The minimal non-diverging fix is the clarifying comment, which
removes the "silently misleading config key" hazard the reviewer flagged without changing runtime behavior.
The underlying Strategy-wiring remains an accepted cross-console deferral that must be resolved uniformly
across all consoles (a future-phase, project-wide task), not in Keeper in isolation.

### WR-02: `KeeperDependencyFirewallTests` Reflects Only Direct References

**Files modified:** `tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs`
**Commit:** 8d26729
**Applied fix:** Added a `NOTE — DIRECT references only` paragraph to the test's `<summary>`
doc-comment, clarifying that `GetReferencedAssemblies()` returns only the references in `Keeper.dll`'s
own manifest and does NOT walk the transitive closure; that a forbidden assembly pulled in indirectly
via `BaseConsole.Core` or `Messaging.Contracts` would not trip the guard; that a transitive scan is
deferred; and that "closure" in this test means Keeper's own manifest, not the recursive dependency graph.

**Rationale for fix choice (documentation disclaimer, not a recursive walk):** The reviewer offered
two alternatives — (a) replace `GetReferencedAssemblies()` with a recursive transitive walk, or
(b) document the known gap. Option (a) was rejected: the analogous `ConsoleDependencyFirewallTests`
(the Orchestrator/Processor firewall guard) is also direct-reference-only by design and uses the same
shallow reflection. Converting Keeper's test to a recursive `Assembly.Load` walk would diverge it from
that established sibling test and risks flakiness (assemblies not in the load context). The disclaimer
keeps the test consistent with its sibling, green, and honest about the depth it actually enforces.

## Verification

Per-fix verification (3-tier) and pre-commit gate:
- **Tier 1:** Re-read both modified files; clarifying comments present, surrounding code intact.
- **Tier 2 (build):** `dotnet build SK_P.sln -c Release` → 0 Warnings / 0 Errors (TreatWarningsAsErrors honored);
  `dotnet build SK_P.sln -c Debug` → 0 Warnings / 0 Errors.
- **Tests:** `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Keeper" -c Release` →
  Passed! Failed: 0, Passed: 460, Skipped: 0 (full hermetic suite green, including the firewall test
  whose comment was changed).

Both changes are comment-only with no behavioral or structural divergence from the established
cross-console conventions.

## Skipped Issues

None — both in-scope findings were fixed.

(Note: IN-01 and IN-02 are Info-severity and out of the critical_warning fix scope; they were
intentionally not addressed in this iteration.)

---

_Fixed: 2026-06-05T00:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
