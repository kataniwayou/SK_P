---
phase: 30-runtime-business-metrics
fixed_at: 2026-06-03T00:10:15Z
review_path: .planning/phases/30-runtime-business-metrics/30-REVIEW.md
iteration: 1
findings_in_scope: 1
fixed: 1
skipped: 0
status: all_fixed
---

# Phase 30: Code Review Fix Report

**Fixed at:** 2026-06-03T00:10:15Z
**Source review:** .planning/phases/30-runtime-business-metrics/30-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 1 (Critical + Warning only; the 4 Info findings IN-01..IN-04 are out of scope for this pass)
- Fixed: 1
- Skipped: 0

## Fixed Issues

### WR-01: Instance-id resolution can diverge across processes / containers in the round-trip E2E label assertion

**Files modified:** `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs`
**Commit:** 684e52d
**Applied fix:** The reviewer noted the runtime-metric assertion (METRIC-01/02)
only checks that `service_instance_id` is present and non-empty, while the
surrounding doc comment's framing implied the per-replica *uniqueness* property
of the resource attribute. Since `service.instance.id` resolves per-process via
`POD_NAME → HOSTNAME → MachineName → GUID`, two containers on the same Docker
host could collide under the `MachineName` fallback — the assertion still passes
because it only checks non-emptiness, so this is not a test defect.

The smallest correct change is a comment/framing correction (no logic change).
Expanded the inline comment into an explicit SCOPE NOTE stating that the
assertion proves presence + non-emptiness, NOT per-replica uniqueness; recorded
that uniqueness holds in practice because Docker sets the container id as
`HOSTNAME` by default (so the `MachineName` fallback is not reached) but is not
asserted here. Also tightened the assertion failure message to say
"presence/non-emptiness only; per-replica uniqueness is not asserted here."

**Verification:**
- Tier 1: re-read the modified region — fix text present, surrounding assertion
  logic intact.
- Tier 2: `dotnet build tests/BaseApi.Tests -c Debug` → Build succeeded, 0
  Warning(s) / 0 Error(s).
- Hermetic suite: `dotnet test tests/BaseApi.Tests -- --filter-not-trait
  "Category=RealStack"` → Passed 409, Failed 0, Skipped 0.
- The affected test carries `[Trait("Category","RealStack")]` and requires a
  live compose stack; it was NOT executed live in this environment. The change
  is a comment/assertion-message edit only (no behavioral change to the test
  logic), verified via compile + the hermetic suite staying GREEN.

---

_Fixed: 2026-06-03T00:10:15Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
