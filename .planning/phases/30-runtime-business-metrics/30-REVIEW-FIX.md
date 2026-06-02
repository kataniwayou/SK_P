---
phase: 30-runtime-business-metrics
fixed_at: 2026-06-03T00:55:00Z
review_path: .planning/phases/30-runtime-business-metrics/30-REVIEW.md
iteration: 1
findings_in_scope: 5
fixed: 3
skipped: 0
acknowledged: 2
status: all_fixed
---

# Phase 30: Code Review Fix Report

**Fixed at:** 2026-06-03T00:55:00Z
**Source review:** .planning/phases/30-runtime-business-metrics/30-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 5 (fix_scope: all → 1 Warning + 4 Info)
- Fixed: 3 (WR-01 in a prior pass; IN-03, IN-04 this pass)
- Acknowledged (won't-fix-by-design): 2 (IN-01, IN-02)
- Skipped: 0

All five findings are minor; none blocked the phase. WR-01 was a comment/assertion-message
clarification fixed in a prior pass. The four Info findings were each flagged by the reviewer as
deliberate / by-design; engineering judgment was applied per finding — two received the smallest
correct improvement that adds real value (IN-03 drift guard, IN-04 label pinning) and two are
recorded as acknowledged because the flagged behavior is an intentional design choice the reviewer
themself said requires no change.

## Fixed Issues

### WR-01: Instance-id resolution can diverge across processes / containers in the round-trip E2E label assertion

**Files modified:** `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs`
**Commit:** 684e52d (prior pass — not re-applied this pass)
**Applied fix:** Comment/framing correction (no logic change). Expanded the inline comment into an
explicit SCOPE NOTE stating the assertion proves presence + non-emptiness, NOT per-replica
uniqueness, and recorded that uniqueness holds in practice because Docker sets the container id as
`HOSTNAME` by default (so the `MachineName` fallback is not reached) but is not asserted here.
Tightened the assertion failure message accordingly. The affected test is `[Trait("Category","RealStack")]`
and was not executed live; the change is comment/message-only.

### IN-03: Duplicated `ResolveInstanceId` precedence expression — accepted duplication, but drift-prone

**Files modified:** `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs`, `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs`
**Commit:** 91e17a3
**Applied fix:** Adopted the reviewer's *optional* design-preserving suggestion — a comment-only
drift guard. Added a `DRIFT GUARD (IN-03)` paragraph to each of the two production `ResolveInstanceId`
doc comments that names ALL THREE lock-step copies with their file paths and member names: this file,
the sibling base-lib file, and the hermetic mirror `ResolveInstanceIdFacts.Resolve`. This closes the
missing third link (the production copies previously referenced each other but not the test mirror's
location). The duplication itself was deliberately NOT removed: it exists to preserve the
`BaseConsole.Core ↛ BaseApi.Core` firewall (D-09); introducing a shared lib / cross-project reference
to dedupe a ~6-line helper would break that firewall, which the task explicitly forbids. No code logic
changed — XML doc comments only. The `Metrics()` test-factory duplication (OrchestratorTestStubs /
DispatchTestKit) already cross-references each other in prose and was left as-is for the same
firewall-preserving reason.

**Verification:**
- Tier 1: re-read both modified regions — drift-guard text present, `ResolveInstanceId` expression and
  surrounding registration code intact.
- Tier 2: `dotnet build tests/BaseApi.Tests -c Debug` (transitively builds BaseApi.Core + BaseConsole.Core)
  → Build succeeded, 0 Warning(s) / 0 Error(s).
- Hermetic suite: `dotnet test tests/BaseApi.Tests -- --filter-not-trait "Category=RealStack"` →
  Passed 409, Failed 0, Skipped 0. The `ResolveInstanceIdFacts` precedence mirror tests are in the
  hermetic suite and stayed GREEN.

### IN-04: `outcome` tag value re-computes `ToString().ToLowerInvariant()` on every send

**Files modified:** `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs`
**Commit:** 43e3b87
**Applied fix:** Adopted the reviewer's suggested `switch` expression. Replaced the per-send
`result.Outcome.ToString().ToLowerInvariant()` (which allocated an enum-name string plus a lowercased
copy on every result sent) with a `private static string OutcomeLabel(StepOutcome)` returning the
pinned interned literals `"completed" / "failed" / "cancelled"`. The primary value is correctness/
maintainability, not perf: the emitted Prometheus label vocabulary is now decoupled from the C# enum
member names, so a future enum rename can no longer silently change a live Prometheus label. The
allocation removal is a secondary benefit on the per-message hot path. `StepOutcome.Processing` is
mapped defensively to `"processing"` (never reached on a send path per Pitfall 3, kept for switch
exhaustiveness) and a `_` arm preserves the original `ToString().ToLowerInvariant()` behavior for any
hypothetical future enum value. This is a behavior-preserving change for the three values actually
emitted (`{completed, failed, cancelled}`).

**Verification:**
- Tier 1: re-read the modified `SendResult` + new `OutcomeLabel` region — fix present, surrounding
  Send/increment ordering (Send → counter, D-04/D-15) intact.
- Tier 2: `dotnet build src/BaseProcessor.Core -c Debug` → Build succeeded, 0 Warning(s) / 0 Error(s).
- Hermetic suite: `dotnet test tests/BaseApi.Tests -- --filter-not-trait "Category=RealStack"` →
  Passed 409, Failed 0, Skipped 0. `ProcessorMetricsFacts` (which exercises `processor_result_sent`)
  is in the hermetic suite and stayed GREEN.
- Note: this maps an enum to label strings; the GREEN hermetic `ProcessorMetricsFacts` confirm the
  three live label values, but a human should confirm the `{completed, failed, cancelled}` vocabulary
  matches any downstream PromQL/dashboards that consume the `outcome` label.

## Acknowledged (won't-fix-by-design)

### IN-01: `?? Guid.NewGuid()` fallback in `ResolveInstanceId` is unreachable

**File:** `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs:94-98`
**Also:** `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs:85-89`
**Decision:** Acknowledged — no change. `Environment.MachineName` is effectively non-null (it throws
`InvalidOperationException` rather than returning null per the BCL contract), so the trailing
`?? Guid.NewGuid().ToString("N")` is an intentional, documented (D-10) defensive final fallback. The
reviewer explicitly said "None required. The dead branch is harmless and self-documenting" and that
wrapping `MachineName` in a try/catch would be gold-plating. Forcing a try/catch would add a never-
exercised code path and a behavior the hermetic tests cannot assert. Left as the documented design.

### IN-02: GUID interpolated into PromQL label selector without grammar-level escaping

**File:** `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs:117-138`
**Decision:** Acknowledged — no change. `procId` is a server-minted `Guid` rendered with the `D`
format (hex + hyphens only — no `"`, `{`, `}`, `\`), so it provably cannot break out of the PromQL
label-matcher string; the transport layer additionally `Uri.EscapeDataString`s the whole query. The
reviewer said "None required" and flagged it only to record the invariant, which is already documented
at `PrometheusTestClient.PollPromForQuery` (line 69). Adding PromQL-string escaping now would guard
against a non-GUID interpolation that does not exist in this code; doing so speculatively is not
warranted. The safety reasoning is hereby recorded.

---

_Fixed: 2026-06-03T00:55:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
