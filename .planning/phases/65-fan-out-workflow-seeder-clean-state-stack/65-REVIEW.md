---
phase: 65-fan-out-workflow-seeder-clean-state-stack
reviewed: 2026-06-14T00:00:00Z
depth: standard
files_reviewed: 3
files_reviewed_list:
  - tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs
  - scripts/phase-65-reset.ps1
  - scripts/phase-65-up.ps1
findings:
  critical: 0
  warning: 2
  info: 4
  total: 6
status: issues_found
---

# Phase 65: Code Review Report

**Reviewed:** 2026-06-14T00:00:00Z
**Depth:** standard
**Files Reviewed:** 3
**Status:** issues_found

## Summary

Reviewed the Phase-65 fan-out seeder test plus the two PowerShell stack-management scripts (reset / bring-up). The code is well-structured, heavily documented, and reuses proven helpers (`SampleRoundTripE2ETests.SeedProcessorAsync`, the NDJSON-per-replica parse, the sentinel-name idempotency pattern from `SeedConfigSchemaAsync`). I verified the supporting facts against source: the `/api/v1/workflows` GET returns a bare `IReadOnlyList<WorkflowReadDto>` (no pagination envelope — `BaseController.List`), so the `List<WorkflowReadDto>` deserialization and `FirstOrDefault(w => w.Name == ...)` match are correct; the DTO field orders match `WorkflowCreateDto`/`WorkflowReadDto`; and the FK-safe DELETE ordering matches migration `Down()`. SQL in the reset script is static literal (no interpolation — no injection surface).

No critical issues. Two warnings concern PowerShell variable-scope and exit-code semantics that could weaken the fail-loud contract the scripts depend on. The remaining items are quality/robustness suggestions.

## Warnings

### WR-01: Heal-wait success message can report a stale `$keys` count from the last failed poll

**File:** `scripts/phase-65-reset.ps1:79-89`
**Issue:** `$keys` is assigned inside the `while` loop. The loop `break`s on success while `$keys` holds the winning value, so the success path at line 89 is correct. However, the variable carries loop-iteration scope and the success log relies on `$keys` surviving the `break`. More importantly, if `redis-cli --scan` transiently emits a partial/errored result on the *winning* iteration but still yields >=1 matching line, the count printed is whatever survived `Where-Object` — there is no guard that the matched keys are well-formed `skp:proc:{id}:{instance}` keys beyond "has a second `:` segment". A malformed key containing an extra colon (e.g. a future `skp:proc:{id}:{instance}:detail` key) would satisfy `-notmatch '^skp:proc:[^:]+$'` and falsely signal liveness reconvergence.
**Fix:** Tighten the inclusion regex to match the exact per-instance shape rather than excluding only the bare index, so unrelated future keys cannot satisfy the gate:
```powershell
$keys = @(docker exec sk-redis redis-cli --scan --pattern 'skp:proc:*' |
          Where-Object { $_ -match '^skp:proc:[^:]+:[^:]+$' })
```

### WR-02: `docker exec` / `docker compose exec` failures inside heal-wait are silently swallowed

**File:** `scripts/phase-65-reset.ps1:79-83`
**Issue:** Unlike STEP 1 (FLUSHALL) and STEP 3 (psql), the heal-wait poll does not check `$LASTEXITCODE` after `docker exec sk-redis redis-cli --scan`. If the Redis container is restarting or the daemon hiccups, `--scan` returns a non-zero exit and empty output; the loop treats that as "not healed yet" and silently burns the full 60s deadline before failing with a misleading "liveness did not reconverge" message rather than surfacing the real "redis-cli scan failed" cause. `$ErrorActionPreference = 'Stop'` does not trap native-exe non-zero exits (only PowerShell cmdlet errors), so these go unnoticed.
**Fix:** After the `--scan` call, distinguish "scan errored" from "no keys yet" so an infrastructure failure fails loud instead of masquerading as a timeout. For example capture the raw output, check `$LASTEXITCODE`, and `Write-Phase`/`exit 2` on a hard scan error rather than continuing to poll.

## Info

### IN-01: `docker ps --format '{{.Names}}'` count check is fragile across newline handling

**File:** `scripts/phase-65-reset.ps1:126` and `scripts/phase-65-up.ps1:76`
**Issue:** The reset script wraps the `docker ps` output in `@(... | Where-Object { $_ -match '\S' })` to filter blank lines before counting (line 126 — good), but `phase-65-up.ps1:76` does NOT: `$bad = @(docker ps --filter 'name=sk-processor-badconfig' --format '{{.Names}}')`. If `docker ps` emits a trailing empty string element, `$bad.Count` can be 1 against an actually-empty result, producing a false ENV-01 violation and a spurious `exit 2`.
**Fix:** Mirror the reset script's blank-line filter for consistency and correctness:
```powershell
$bad = @(docker ps --filter 'name=sk-processor-badconfig' --format '{{.Names}}' |
         Where-Object { $_ -match '\S' })
```

### IN-02: Processor-set assertion logs "expected 2" but never enforces it

**File:** `scripts/phase-65-reset.ps1:114-123`
**Issue:** STEP 4 asserts only that `processor-sample` has `>= 1` replica (`$sample.Count -eq 0` is the only failure), then logs `"replicas present: N (expected 2)"`. The `.NOTES` and inline comments repeatedly assert the invariant is exactly 2 replicas, but a degraded single-replica stack passes silently. Since attribution and the 67/68 fault-injection proof presume 2 replicas, a silently-degraded set could cause confusing downstream results.
**Fix:** If exactly 2 is a hard precondition, assert it (`if ($sample.Count -ne 2) { ...exit 2 }`); if it is only a soft expectation, soften the log wording to avoid implying enforcement. Choose one to match intent.

### IN-03: `LabelRegex` and `NodeNumbers` encode the node set in three places

**File:** `tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs:64-95`
**Issue:** The nine node labels are enumerated independently in `NodeNumbers` (dict keys, 64-75), `LabelRegex` (alternation, 94-95), and implicitly in `ExpectedEdges` (78-88). They are currently consistent, but a future edit to one (e.g. adding a node) that misses another would let a malformed label silently pass `Assert.Matches(LabelRegex, ...)` or vice-versa. This is test-internal so severity is low.
**Fix:** Derive `LabelRegex` from `NodeNumbers.Keys` (e.g. build the alternation from the dictionary keys) so the node set has a single source of truth. Optional hardening only.

### IN-04: Self-verification reads the whole-DB tables, presuming a reset-clean DB but not isolating to the seeded workflow

**File:** `tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs:123-215`
**Issue:** All count/edge/payload assertions query the tables globally (`SELECT count(*) FROM steps`, `FROM assignments`, etc.) rather than scoping to the seeded workflow's graph. This is intentional and documented ("presume a reset-clean DB; the 67/68 harness resets BEFORE the seeder"), and is sound *given* the harness contract. The risk is purely that running this test against a non-reset DB (e.g. a developer running `~FanOutSeeder` without first invoking `phase-65-reset.ps1`) yields confusing count failures rather than a clear "DB not clean" signal. The failure messages already hint at this, which mitigates it.
**Fix:** No change required for the harness path. Optionally, a single pre-flight assertion (e.g. assert `steps == 9` immediately after the first seed, before the idempotency re-run, with a "did you reset?" message) would localize the most common misuse. Documentation-only otherwise.

---

_Reviewed: 2026-06-14T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
