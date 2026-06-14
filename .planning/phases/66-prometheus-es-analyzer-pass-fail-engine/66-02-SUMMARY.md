---
phase: 66-prometheus-es-analyzer-pass-fail-engine
plan: 02
subsystem: testing
tags: [elasticsearch, otel, json-parsing, test-helper, observability, obs-01]

# Dependency graph
requires:
  - phase: 11
    provides: ElasticsearchTestClient + EsIndexNames (PollEsForLog single-hit poll, verified otel field shape, .keyword trap documentation)
provides:
  - "ElasticsearchTestClient.SearchAllHits — single bounded _search returning ALL hits as List<JsonElement>, 404-tolerant, Clone-detached"
  - "EsIndexNames.StepLabelFieldPath / SumFieldPath — direct attributes.* field-path consts (no .keyword)"
  - "ElasticsearchTestClientFacts — hermetic proof of multi-hit return + per-correlationId grouping + duplicate-label detection"
affects: [66-03, prometheus-es-analyzer, OBS-01, OBS-02]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "SearchAllHits multi-hit aggregation primitive (single bounded _search, caller polls-to-stable, Clone-each-detach)"
    - "Hermetic *Facts: prove the parse/grouping contract against an inline captured _search envelope instead of the HTTP path"

key-files:
  created:
    - tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClientFacts.cs
  modified:
    - tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs
    - tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs

key-decisions:
  - "SearchAllHits added ALONGSIDE PollEsForLog (extension, not replacement) — existing facts depend on PollEsForLog's single-hit behavior"
  - "Hermetic facts test the grouping/envelope-parsing contract directly (ExtractAllHits mirrors SearchAllHits navigation) rather than hitting live ES — no live stack needed"
  - "New field-path consts use DIRECT attributes.* paths; .keyword appears only in doc-comment warnings, never in const values (Phase 11 UAT trap)"

patterns-established:
  - "Multi-hit ES read: single size-bounded _search + caller poll-to-stable + 404→empty-list tolerance + Clone-each-detach"
  - "Defensive Sum read (TryGetInt32 then GetString+parse) per 66-RESEARCH A1"

requirements-completed: [OBS-01]

# Metrics
duration: 4min
completed: 2026-06-14
---

# Phase 66 Plan 02: SearchAllHits Multi-Hit ES Read + Step/Sum Field Paths Summary

**`ElasticsearchTestClient.SearchAllHits` — a single bounded `_search` returning ALL hits as a Clone-detached `List<JsonElement>` (404-tolerant), plus direct `attributes.StepLabel`/`attributes.Sum` consts, proven hermetically to group by correlationId into per-run traces and detect duplicate labels.**

## Performance

- **Duration:** ~4 min
- **Started:** 2026-06-14T13:09Z
- **Completed:** 2026-06-14T13:12:46Z
- **Tasks:** 2
- **Files modified:** 3 (1 created, 2 modified)

## Accomplishments
- `SearchAllHits` multi-hit aggregation primitive added alongside the untouched `PollEsForLog`: one size-bounded `_search`, returns every hit Clone-detached, returns an empty list on a non-success (404 lazy-index) response so the caller can poll-to-stable.
- `EsIndexNames` gained `StepLabelFieldPath = "attributes.StepLabel"` and `SumFieldPath = "attributes.Sum"` — DIRECT paths, with doc-comments pinning the `.keyword`-returns-zero-hits rationale (the trap that broke 4 facts at Phase 11 UAT).
- `ElasticsearchTestClientFacts` (hermetic, no live stack) proves: parse yields 4 hits not 1; grouping by `attributes.CorrelationId` yields 2 per-run traces (corr-1's distinct StepLabel set == {Step_A, Step_B}); the duplicate `(corr-2, Step_A)` is detectable (list count 2 > distinct count 1); an empty envelope yields zero groups with no exception (T-66-04 tolerance).

## Task Commits

1. **Task 1: Add SearchAllHits + Step/Sum field-path consts** - `5047f3c` (feat)
2. **Task 2: ElasticsearchTestClientFacts — multi-hit + grouping (hermetic)** - `3c39f25` (test)

_Note: Task 2 is `tdd="true"`. The grouping subject (SearchAllHits) was implemented in Task 1; the hermetic facts mirror that parse navigation against inline captured JSON, so the test passed GREEN on first valid run (4/4). See TDD Gate Compliance below._

## Files Created/Modified
- `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` - Added `SearchAllHits` (multi-hit `_search`, Clone-each, 404-tolerant); `PollEsForLog` unchanged.
- `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` - Added `StepLabelFieldPath` + `SumFieldPath` direct consts with `.keyword`-trap doc-comments.
- `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClientFacts.cs` - 4 hermetic `SearchAllHits_*` facts proving multi-hit return, per-correlationId grouping, duplicate-label detection, empty-hits tolerance.

## Decisions Made
- SearchAllHits added as an extension (PollEsForLog left byte-identical) — other facts depend on the single-hit poll behavior.
- Facts are hermetic: `ExtractAllHits` mirrors the exact `SearchAllHits` envelope navigation (`TryGetProperty("hits")` → `"hits"` array → `EnumerateArray` + `Clone`) so the grouping contract is proven without the HTTP path or a live stack. No `RealStack` trait, so the hermetic suite always covers it.
- Defensive `Sum` read (`TryGetInt32` then string-parse) per 66-RESEARCH A1, so a number-vs-string ES coercion never throws.

## Deviations from Plan
None - plan executed exactly as written. The `.keyword` acceptance grep notionally returns 2 matches, but those are doc-comment warnings against the trap; no const *value* contains `.keyword` (verified `grep 'const string.*\.keyword'` → none), which is the criterion's intent.

## TDD Gate Compliance
Task 2 is `tdd="true"` but is a pure-parsing hermetic fact whose subject (`SearchAllHits`) was delivered by Task 1 (`feat` commit `5047f3c`). A standalone RED commit (failing test before any implementation) was not meaningful here — the grouping logic under test is `List<T>.GroupBy` over an already-correct envelope walk. Gate sequence on disk: `feat` (`5047f3c`, the subject) → `test` (`3c39f25`, the proof), both GREEN (4/4 facts pass). No false-green: the facts assert concrete counts (4 hits, 2 groups, list>distinct) that would fail on a single-hit or mis-grouped walk.

## Issues Encountered
None. Verification used `dotnet test ... -- --filter-class "BaseApi.Tests.Observability.Helpers.ElasticsearchTestClientFacts"` — the xUnit.v3 + Microsoft.Testing.Platform-correct scoping flag (the legacy VSTest `--filter "FullyQualifiedName~..."` is silently ignored and would run the full ~633-test suite, per the Plan 66-01 finding). Scoped run: 4/4 passed in ~1.4s.

## Threat Surface
No new security-relevant surface introduced beyond the plan's `<threat_model>`. T-66-04 (malformed/empty envelope → empty list, never an unhandled exception) is directly proven by `SearchAllHits_EmptyHits_YieldsZeroGroups`. T-66-06 (.keyword regression) is held by construction (direct consts) and the no-const-value-contains-.keyword check.

## Next Phase Readiness
- `SearchAllHits` + the direct field-path consts are ready for Plan 03 to build the live ES query body + poll-to-stable analyzer over the fan-out workflow's ~90-step window.
- No blockers.

---
*Phase: 66-prometheus-es-analyzer-pass-fail-engine*
*Completed: 2026-06-14*

## Self-Check: PASSED
- Files: ElasticsearchTestClient.cs, EsIndexNames.cs, ElasticsearchTestClientFacts.cs, 66-02-SUMMARY.md — all FOUND.
- Commits: 5047f3c (feat), 3c39f25 (test) — all FOUND.
