---
phase: 16-idempotency-concurrency-l1-cleanup-3-green-closeout
reviewed: 2026-05-29T18:53:48Z
depth: standard
files_reviewed: 6
files_reviewed_list:
  - tests/BaseApi.Tests/Features/Orchestration/HappyPathE2EFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/GateNoWriteFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/IdempotencyFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/StopScanFacts.cs
  - scripts/phase-16-close.ps1
  - scripts/phase-16-close.sh
findings:
  critical: 0
  warning: 3
  info: 4
  total: 7
status: issues_found
---

# Phase 16: Code Review Report

**Reviewed:** 2026-05-29T18:53:48Z
**Depth:** standard
**Files Reviewed:** 6
**Status:** issues_found

## Summary

Reviewed four xUnit integration fact classes and two close-gate scripts (PowerShell + Bash) for Phase 16. The test files are high quality: well-documented intent, correct async/await usage, defensive null-forgiving on deserialized projections that are immediately asserted `NotNull`, and deliberate avoidance of flaky winner-assertions in the concurrency fact. No security issues (these are test/CI artifacts against ephemeral compose infra; no hardcoded production secrets — `postgres -U postgres` is the documented local compose credential).

The substantive findings are all in the two close-gate scripts and concern the **fact-count parse fallback**, which can let a parse failure silently pass the 3-GREEN cadence reporting "-1 facts", and a **`docker compose ps --format json` shape assumption** in the Bash script that can mis-abort. There is also a deliberate-but-undocumented PS1/SH divergence in the pre-flight service list. The test files yield only minor info-level observations.

## Warnings

### WR-01: Fact-count parse failure silently passes the 3-GREEN gate as "-1 facts"

**File:** `scripts/phase-16-close.ps1:69-97` and `scripts/phase-16-close.sh:64-75`
**Issue:** When the `Passed:\s+(\d+)` regex fails to match (e.g., test runner output format changes, localized output, or `dotnet test` emits a different summary line), `$passed` is set to `-1` (PS1 line 71) / `"-1"` (SH line 64). Because the same fallback fires identically on all three runs, the downstream "all runs equal" check (`$distinctPassed.Count -ne 1` / the `[[ ... ]]` comparison) is satisfied by three identical `-1` values. The gate then prints "3-GREEN cadence passed — -1 facts GREEN across 3 runs" and proceeds to `exit 0`. A green build with an unparseable summary is indistinguishable from a real green, and the operator is instructed (PS1 line 164 / SH line 138) to record `-1` into STATE.md. The exit code of `dotnet test` is checked separately, so this is not a false-pass of a RED run, but it defeats the deterministic fact-count invariant the cadence exists to enforce.
**Fix:** Treat a failed parse as a hard gate failure rather than a sentinel that compares equal to itself:
```powershell
if ($passed -lt 0) {
    Write-Host "Could not parse fact count from run $i output — aborting (cannot verify deterministic cadence)." -ForegroundColor Red
    Write-Host $output -ForegroundColor DarkGray
    exit 1
}
```
And in the Bash script, after computing `PASSED`:
```bash
if [[ "$PASSED" == "-1" ]]; then
    echo "Could not parse fact count from run $i output — aborting." >&2
    exit 1
fi
```

### WR-02: Bash pre-flight assumes `docker compose ps --format json` emits a JSON array

**File:** `scripts/phase-16-close.sh:31`
**Issue:** `docker compose ps "$svc" --format json | jq -r '.[].Health'` indexes the output as an array (`.[]`). Modern Docker Compose v2 emits **newline-delimited JSON objects** (one object per line, NOT a wrapping array) for `ps --format json`. Against that output, `jq -r '.[].Health'` errors ("Cannot index object with number"), the `2>/dev/null || echo "missing"` swallows it to `"missing"`, and the script aborts every healthy service with exit 2. The PS1 sibling (line 33) uses `ConvertFrom-Json` on a single-service query, which tolerates a lone object, so the two scripts can disagree on the same healthy stack.
**Fix:** Read `.Health` directly (works for a single per-service object) and guard for array shape defensively:
```bash
health=$(docker compose ps "$svc" --format json 2>/dev/null | jq -rs '(.[0].Health // .[0] | .Health) // "missing"' 2>/dev/null || echo "missing")
```
Or more simply, since the loop queries one service at a time, use `jq -r '.Health'` (object) with a fallback, and confirm against the installed Compose version. Pin the expected output shape in a comment.

### WR-03: PS1 requires elasticsearch/prometheus healthy but exempts otel-collector; SH omits otel-collector entirely — silent divergence

**File:** `scripts/phase-16-close.ps1:31-38` vs `scripts/phase-16-close.sh:30`
**Issue:** The PS1 service list is `@('postgres','redis','otel-collector','elasticsearch','prometheus')` with a special-case `-and $svc -ne 'otel-collector'` so otel-collector is iterated but never required healthy (line 34). The SH list is `postgres redis elasticsearch prometheus` — otel-collector is simply absent, with no comment explaining the omission. The two scripts are documented as equivalents ("Bash equivalent of scripts/phase-16-close.ps1", SH line 2), but they apply different pre-flight criteria. A reader maintaining one will not know the other intentionally diverges, and the PS1 special-case (iterate-then-skip) is a code smell — listing a service only to exempt it invites a future edit that accidentally drops the `-and` guard and starts failing on otel-collector cold-start.
**Fix:** Make the lists identical and encode the otel-collector exemption the same way in both, with a shared comment explaining *why* otel-collector health is not gating (cold-start flake, per the Run-failure note at PS1 line 84 / SH line 58). E.g. drop otel-collector from both loops and add `# otel-collector intentionally excluded: cold-start health flake is non-gating (see run-failure note below)`.

## Info

### IN-01: Concurrency fact has no synchronization barrier before the parallel POSTs

**File:** `tests/BaseApi.Tests/Features/Orchestration/IdempotencyFacts.cs:169-171`
**Issue:** `ConcurrentStart_SameWorkflow_BothSucceed_FinalStructurallyValid` fires `t1`/`t2` back-to-back and `Task.WhenAll`s them. The XML doc honestly scopes this as observational with no winner assertion, which is the right call. But "genuinely race" (comment line 156) is not guaranteed — the two `PostAsJsonAsync` calls start sequentially, so the second request may not be in flight before the first completes on a fast loopback. This does not make the test *wrong* (its only assertions are both-204 and final-root-round-trips, both of which hold regardless of interleaving), but the test does not reliably exercise the race it documents.
**Fix:** Optional — if a true overlap is desired, gate both clients on a shared `TaskCompletionSet`/barrier or build both `HttpRequestMessage`s and start them via `Task.Run`. Given the deliberate non-flaky design, leaving as-is and softening the "genuinely race" comment to "attempt to overlap" is also acceptable.

### IN-02: Duplicated HTTP-seeding helpers across the four fact classes

**File:** `tests/BaseApi.Tests/Features/Orchestration/HappyPathE2EFacts.cs:40-101`, `GateNoWriteFacts.cs:95-184`, `IdempotencyFacts.cs:40-100`, `StopScanFacts.cs:35-79`
**Issue:** `SeedProcessorAsync` / `SeedStepAsync` / `SeedWorkflowAsync` (and Schema/Assignment variants) are re-implemented in each class with near-identical bodies, differing only in the name prefix (`hp-`/`gnw-`/`idf-`/`ssf-`). This is test-fixture duplication that will drift as the create DTOs evolve (a new required `WorkflowCreateDto` field forces four edits).
**Fix:** Extract a shared `OrchestrationSeedHelpers` static class (or extension methods on `HttpClient`) under `BaseApi.Tests.TestHelpers`, parameterizing the name prefix. Low priority — the duplication is contained and each class stays self-documenting.

### IN-03: `MissingStepGate_NoWrite_StructurallyGuaranteed` is a near-tautological assertion

**File:** `tests/BaseApi.Tests/Features/Orchestration/GateNoWriteFacts.cs:285-293`
**Issue:** The fact SCANs for keys under a freshly generated `Guid.NewGuid()` that was never Started, and asserts the count is 0. As the (excellent) XML doc concedes, this asserts the observable end-state of an invariant rather than driving the gate. A random un-Started GUID having zero keys is true by construction for *any* GUID, so the fact's failure mode is essentially "Redis SCAN itself is broken or the prefix is wrong" rather than "the missingStep gate wrote a key." It is non-vacuous only as a smoke test of the SCAN plumbing.
**Fix:** None required — the design rationale (FK-Restrict makes the HTTP path unreachable; white-box `MissingStepFacts` owns the real drive) is sound and thoroughly documented. Flagged only so reviewers understand the assertion's limited discriminating power.

### IN-04: PS1 locale comment slightly overstates the guarantee

**File:** `scripts/phase-16-close.ps1:51-54`
**Issue:** The comment claims `Sort-Object -CaseSensitive` sorts "via locale-stable [string]::CompareOrdinal". `Sort-Object -CaseSensitive` uses a culture-sensitive case-sensitive comparison, not ordinal `CompareOrdinal`. For the ASCII `test:cls-*` / hex key space in play the practical ordering matches, so the SHA-256 BEFORE/AFTER invariant is unaffected, but the stated mechanism is inaccurate versus the Bash side's genuine `LC_ALL=C sort` (ordinal). 
**Fix:** Either use an explicitly ordinal sort (`Sort-Object -CaseSensitive -Culture ([System.Globalization.CultureInfo]::InvariantCulture)` or sort with `[StringComparer]::Ordinal`) to truly match the Bash `LC_ALL=C` path, or correct the comment to say "case-sensitive invariant-culture sort" rather than `CompareOrdinal`.

---

_Reviewed: 2026-05-29T18:53:48Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
