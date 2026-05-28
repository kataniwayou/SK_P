---
phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
plan: 05
subsystem: observability
tags: [otel-sdk, traces-removal, withtracing-strip, alwaysonsampler-removal, addnpgsql-removal, observ-12-superseded, file-exporter-cleanup, telemetry-jsonl-cleanup, gitignore-cleanup, tests-otel-out-removal, single-atomic-commit, phase-11-wave-3]

# Dependency graph
requires:
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-01 amended REQUIREMENTS.md (commit 7041adb) — OBSERV-12 SUPERSEDED in place; this plan honors the supersession on the SDK side by removing the producer of the now-deleted traces pipeline
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-02 compose-stack mutation (commit a3c0b20) — otel-collector image bumped to 0.152.0; file-exporter bind-mount + user:0:0 override DROPPED preemptively (in anticipation of this plan's tests/.otel-out/ cleanup)
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-03 collector config rewire (commit 1f8eb69) — traces pipeline DELETED from the collector; this plan removes the producer side so the SDK no longer emits trace OTLP records
provides:
  - src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs Phase-11-shape OTel wiring — logs via MEL bridge + metrics with AspNetCore/HttpClient/Runtime instrumentation; NO traces; NO AlwaysOnSampler; NO AddNpgsql; NO 'using Npgsql;' or 'using OpenTelemetry.Trace;' directives
  - XML doc summary cites OBSERV-12 supersession + Phase 11 D-03 + collector-side pipeline deletion + SDK-side emission removal — narrative complete in 5 lines
  - tests/BaseApi.Tests/Observability/TraceExportTests.cs DELETED (150 lines / 2 [Fact] methods + 1 SeedAsync helper + 1 EnumerateSpans helper)
  - tests/BaseApi.Tests/Observability/OtelEndOfSuiteCleanup.cs DELETED (178 lines / IAsyncLifetime assembly-fixture / docker compose shell-outs; [assembly: AssemblyFixture] attribute gone with the file)
  - tests/.otel-out/ directory REMOVED entirely (.gitkeep deleted via git rm; telemetry.jsonl + the directory itself removed from filesystem — file was gitignored so no git-side tracking change beyond the .gitkeep)
  - .gitignore — 8-line Phase 5 (CONTEXT.md D-10) stanza at pre-Phase-11 lines 414-421 REMOVED; surrounding patterns (.env.local + *.received.* + bin/ + obj/) preserved byte-identical
  - Single atomic commit (0fa325e) — commit #5 of the Phase 11 sequence — modifies 1 source file (ObservabilityServiceCollectionExtensions.cs), modifies 1 config file (.gitignore), deletes 3 files (TraceExportTests.cs + OtelEndOfSuiteCleanup.cs + tests/.otel-out/.gitkeep); 5 files changed, 8 insertions / 352 deletions
  - dotnet build SK_P.sln -c Release --no-restore -> 0 Warning(s) / 0 Error(s); dotnet build SK_P.sln -c Debug --no-restore -> 0 Warning(s) / 0 Error(s); working tree clean post-commit
affects: [11-06 (test migration setup — LogExportTests / LogLevelFilterTests / MetricsExportTests still reference OtelCollectorFixture which still exists but file exporter is now non-functional; the 3 facts WILL be RED at runtime until Plan 11-08 migrates them to ES/Prom polling; build remains GREEN throughout because test code still compiles); 11-08 (test migration — OtelCollectorFixture.cs preserved this plan but will be DELETED in 11-08 after the 3 facts move to Phase11WebAppFactory + ES/Prom polling)]

# Tech tracking
tech-stack:
  added: [none — production OTel + NuGet pins all unchanged; this plan only removes code + cleans up test infrastructure]
  patterns:
    - "Single-atomic-commit pattern for SDK strip + test infra cleanup — matches Plans 11-01 / 11-02 / 11-03 / 11-04 atomic-commit precedent (5 commits, 5 phases of work). Forensic property: each Phase 11 commit independently revertable without leaving subsequent work out of sync."
    - "Educational-doc-comment-references-deleted-code pattern — XML doc summary in ObservabilityServiceCollectionExtensions.cs explicitly mentions '.WithTracing(...)' block deleted to give future readers context. The single grep hit for '.WithTracing' in src/ is in this educational doc-comment, not a production CALL. Code-only check via 'grep -v ^\\s*///' returns 0 matches. Matches Plan 11-04 negative-assertion-comments precedent."
    - "Comment-only metrics-block amendment pattern — when a long-lived block is preserved across a refactor, the only delta is a comment-only suffix (' + preserved Phase 11 D-04') to the existing rationale comment. Body of the .WithMetrics chain is byte-identical. Reusable for any future incremental phase-comment annotation."
    - "Gitignored-file-filesystem-rm pattern — tests/.otel-out/telemetry.jsonl was gitignored (not tracked), so 'git rm' would not work. Removed via 'rm -f' on the filesystem; no git index update needed. The .gitignore stanza removal (Task 3) is what 'unblocks' the file from being ignored, but since the file is now physically gone, there is no surface to surface to git either way."
    - "Empty-directory rmdir-after-files-removed pattern — once all 2 files (.gitkeep + telemetry.jsonl) are removed from tests/.otel-out/, the directory itself is removed via 'rmdir'. Git does not track empty directories so no further index action is needed. The directory will not be recreated on the next dotnet test run because the file exporter binding to it was removed in Plan 11-02 (compose.yaml bind-mount + user:0:0 dropped) and Plan 11-03 (collector config file exporter dropped)."

key-files:
  created: []
  modified:
    - "src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs — 4 surgical edits: (1) DELETE 'using Npgsql;' line, (2) DELETE 'using OpenTelemetry.Trace;' line, (3) REPLACE XML doc summary (5 lines) with OBSERV-12-supersession + Phase 11 D-03 narrative, (4) DELETE entire .WithTracing(...) chain (7 lines: SetSampler + AddAspNetCoreInstrumentation+Filter + AddHttpClientInstrumentation + AddNpgsql + AddOtlpExporter + closing paren+semicolon); move the trailing ';' up to terminate the .WithMetrics chain; append ' + preserved Phase 11 D-04' to the metrics-block inline comment; refresh the section header comment to mention 'METRICS' only (was 'METRICS + TRACES'). File shrunk from 87 lines to 78 lines (delta -9)."
    - ".gitignore — DELETE 8-line Phase 5 (CONTEXT.md D-10) stanza at pre-Phase-11 lines 414-421 (header comment block + 'tests/.otel-out/*' + '!tests/.otel-out/.gitkeep'). Surrounding patterns (.env.local + *.received.* + bin/ + obj/ + Compose env-var override stanza) preserved byte-identical."
  deleted:
    - "tests/BaseApi.Tests/Observability/TraceExportTests.cs — 150 lines: [Collection(\"Observability\")] sealed class with 2 [Fact] methods (Test_NpgsqlChildSpan_Under_AspNetCore_Request_Span + Test_NpgsqlChildSpan_DbStatement_Has_NoParameterValues), SeedAsync helper, EnumerateSpans helper. Referenced OtelCollectorFixture + PostgresFixture + TestErrorDbContext. No other file in the test project imported types from it."
    - "tests/BaseApi.Tests/Observability/OtelEndOfSuiteCleanup.cs — 178 lines: IAsyncLifetime assembly-fixture closing the Plan 05-02 telemetry.jsonl AMBER. Carried '[assembly: AssemblyFixture(typeof(BaseApi.Tests.Observability.OtelEndOfSuiteCleanup))]' on line 4; assembly registration gone with the file. DisposeAsync shelled out to 'docker compose stop|delete|start otel-collector' to release the file exporter's exclusive write handle. The xUnit v3 AssemblyFixture pattern itself stays available — this file was the sole consumer."
    - "tests/.otel-out/.gitkeep — empty file preserving the tests/.otel-out/ directory at clone time (committed by Plan 05-01 Task 4). No longer needed because the file exporter is gone."
    - "tests/.otel-out/telemetry.jsonl — Phase 5 file-exporter output (~1.9 MB at plan-start, persisted from prior dotnet test runs). Was gitignored (not tracked); removed via 'rm -f' on the filesystem. Plan 11-02 + 11-03 already removed all the upstream producers of new writes (compose.yaml file-exporter bind-mount + collector-config file exporter)."
    - "tests/.otel-out/ — directory itself removed via 'rmdir' after both contained files were deleted. Git does not track empty directories so no index action needed."

key-decisions:
  - "Single atomic commit for the whole plan (Plan body Task 4 step 2 + success criteria #9) — matches Plans 11-01 / 11-02 / 11-03 / 11-04 atomic-commit precedent. Per-task commits NOT used. Each of the 5 Phase 11 commits is independently revertable; this commit reverses with 'git revert 0fa325e' restoring all 5 files + the directory + the .gitignore stanza in one operation."
  - "Tasks 1-3 staged but uncommitted at task boundaries; Task 4 step 2 stages all 5 paths and step 3 creates the single commit — same atomic-commit choreography as Plan 11-03 (Tasks 1-3 were file mutations rolled into Task 5 commit). The intermediate task verification gates (Task 1 build of BaseApi.Core, Task 2 file-existence checks, Task 3 .gitignore grep checks) all PASSED before Task 4 began; build verification ran fresh on the full solution at Task 4 step 1 against Release + Debug before the commit landed."
  - "XML doc summary explicitly mentions '.WithTracing(...)' block deleted (educational doc-comment reference) — the plan's must_haves invariant 'has NO .WithTracing(...) call' is satisfied semantically because the reference is in an XML doc comment, not a production CALL. The code-only check 'grep -v ^\\s*/// | grep .WithTracing' returns 0 matches. Plan 11-04 set the precedent for treating must_haves as SEMANTIC invariants (no functional code declarations) rather than LITERAL grep gates (no string match anywhere). The educational reference helps future readers trace why the file was modified; removing it would lose that forensic continuity."
  - "tests/.otel-out/telemetry.jsonl removed via filesystem 'rm -f' rather than 'git rm' — the file was gitignored (verified via 'git check-ignore' returning exit 0) and therefore not tracked. 'git rm' on an untracked file would exit non-zero. Plan body Task 2 step 4 explicitly anticipated this case ('if this file is gitignored AND untracked, git rm will exit non-zero. In that case use Remove-Item / rm'). The .gitignore stanza removal in Task 3 means future re-creations of the file would NOT be ignored, but since the upstream producer (Plan 11-02 compose.yaml bind-mount + Plan 11-03 collector-config file exporter) is gone, no re-creation will happen."
  - "tests/.otel-out/ directory removed entirely (not preserved as .gitkeep-only empty directory) — CONTEXT.md Claude's Discretion option chose 'preferred per RESEARCH Runtime State Inventory; no forensic value remains'. The directory served only as a host bind-mount target for the file exporter; with the file exporter gone, there is no functional reason to preserve it. Git does not track empty directories so the directory's disappearance is invisible to git's history (only the .gitkeep deletion shows in 'git show --stat HEAD')."
  - "Tasks 1-4 executed sequentially without checkpoints — autonomous: true frontmatter honored; no human-verify / decision / human-action gates encountered. The plan body had no <task type=\"checkpoint:*\"> entries (all 4 tasks were type=\"auto\")."
  - "Intermediate metrics-block section header comment 'OTel METRICS + TRACES. ConfigureResource MUST come before WithMetrics/WithTracing so the resource propagates to both branches.' updated to 'OTel METRICS. ConfigureResource MUST come before WithMetrics so the resource propagates to the metrics provider. Traces pipeline REMOVED in Phase 11 (D-03).' — small clarity edit not explicitly called out in the plan but consistent with the plan's intent of leaving no stale traces references in the file. Reader-aid only; no functional impact."

patterns-established:
  - "Single-atomic-commit pattern for SDK strip + test infra cleanup — extends the Phase 11 Wave 1-3 convention to also cover code + test deletion + .gitignore edit in a single commit. Forensic property: each Phase 11 commit reverts cleanly; subsequent commits depend on prior state of the producer (Plan 11-05 depends on Plan 11-03 collector-side removal) but the SDK strip itself is self-contained (just remove the .WithTracing block + dependent uses)."
  - "Code-only check pattern for must_haves semantic invariants — when a doc-comment legitimately references deleted code for educational continuity, must_haves grep gates are evaluated against code lines only (XML /// doc-comments stripped). Plan 11-04 set the precedent; this plan reaffirms it. Future executor reviews should distinguish 'invariant holds in production CODE' from 'literal token absent everywhere'."
  - "Filesystem-rm-after-gitignore-removed pattern — when a previously-ignored file is being removed AND its gitignore stanza is being removed in the same commit, the file removal happens via filesystem 'rm' (not git rm — file was never tracked) and the gitignore edit happens via Edit tool. Both go into the same atomic commit's diff. Reusable for any future cleanup of ignored-but-on-disk files (e.g., .env.local removal when a feature stops using it)."

requirements-completed: [OBSERV-12]
# OBSERV-12 (Npgsql DB spans + AspNetCore/HttpClient trace pipeline) — SUPERSEDED to Out of Scope per Plan 11-01 amendment.
# This plan removes the SDK-side producer; the marker 'SUPERSEDED — Phase 11 D-03' was set in REQUIREMENTS.md by Plan 11-01.
# Behaviorally complete: the SDK no longer emits trace OTLP records (this plan) AND the collector has no traces pipeline (Plan 11-03).
# The Out of Scope row in PROJECT.md / REQUIREMENTS.md captures the supersession decision; OBSERV-12 stays in the traceability table with [SUPERSEDED — Phase 11 D-03] header marker per Phase 3 D-03b ID preservation invariant.

# Metrics
duration: ~4min
completed: 2026-05-28
---

# Phase 11 Plan 05: SDK-side .WithTracing() Strip + tests/.otel-out/ Cleanup Summary

**Single atomic commit (0fa325e) strips the OTel traces pipeline from the SDK side AND cleans up file-exporter-era test infrastructure that is now obsolete. ObservabilityServiceCollectionExtensions.cs loses its .WithTracing block + 2 orphan using directives + the XML doc summary is refreshed to cite OBSERV-12 supersession + Phase 11 D-03; TraceExportTests.cs and OtelEndOfSuiteCleanup.cs are deleted; tests/.otel-out/ directory removed entirely; .gitignore loses its 8-line Phase 5 stanza. dotnet build SK_P.sln zero-warning Release+Debug. Pairs with Plan 11-03 collector-side removal so producer + consumer sides agree on no-traces posture.**

## Performance

- **Duration:** ~4 min
- **Started:** 2026-05-28T12:38:31Z
- **Completed:** 2026-05-28T12:42:39Z
- **Tasks:** 4 (4/4 complete)
- **Files changed:** 5 (1 source modified + 1 config modified + 3 deletions; 8 insertions / 352 deletions)

## Accomplishments

- **ObservabilityServiceCollectionExtensions.cs surgically refactored** — 4 atomic edits:
  - `using Npgsql;` directive REMOVED (the only consumer, `TracerProviderBuilderExtensions.AddNpgsql`, gone with the .WithTracing block).
  - `using OpenTelemetry.Trace;` directive REMOVED (the only consumer, `AlwaysOnSampler` reference, gone with the .WithTracing block).
  - XML doc summary REPLACED — old 5-line "Phase 5 OTel wiring: logs via MEL bridge + ... + traces with AspNetCore (filtered to exclude /health/* per OBSERV-08 / HEALTH-05 / Pitfall 10) + HttpClient + Npgsql DB spans (OBSERV-12 / T-05-PII — bare .AddNpgsql() per Phase 5 D-05 corrected — the 8.0.4 package default already does NOT capture parameter values)." replaced with new 5-line "OTel wiring: logs via MEL bridge + metrics with AspNetCore/HttpClient/Runtime instrumentation. Traces pipeline REMOVED in Phase 11 (D-03) — OBSERV-12 superseded to Out of Scope (REQUIREMENTS.md Phase 11 amendment). The collector receives no traces (Plan 11-03 deletes the pipeline); the SDK no longer emits them (this file's .WithTracing(...) block deleted)."
  - `.WithTracing(t => t.SetSampler(new AlwaysOnSampler()).AddAspNetCoreInstrumentation(opts => opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health")).AddHttpClientInstrumentation().AddNpgsql().AddOtlpExporter());` chain DELETED (7 lines including closing paren + semicolon). The trailing semicolon migrated up to terminate the .WithMetrics chain; .AddOtlpExporter())` now ends with `;` instead of `)`. The .WithMetrics body itself is byte-preserved except for the comment-only suffix " + preserved Phase 11 D-04" appended to the existing "Carried from Phase 5 Plan 05-01 deviation" rationale.
  - Section-header comment "OTel METRICS + TRACES. ConfigureResource MUST come before WithMetrics/WithTracing so the resource propagates to both branches." updated to "OTel METRICS. ConfigureResource MUST come before WithMetrics so the resource propagates to the metrics provider. Traces pipeline REMOVED in Phase 11 (D-03)." (reader-aid edit; no functional impact).
  - File shrunk from 87 lines to 78 lines (delta -9 lines).

- **TraceExportTests.cs DELETED** — 150-line `[Collection("Observability")]` sealed class with 2 `[Fact]` methods (`Test_NpgsqlChildSpan_Under_AspNetCore_Request_Span` + `Test_NpgsqlChildSpan_DbStatement_Has_NoParameterValues`) lost their target (no traces backend; both facts read traces from telemetry.jsonl which the file exporter no longer writes). No other file in the test project imported types from it.

- **OtelEndOfSuiteCleanup.cs DELETED** — 178-line `IAsyncLifetime` assembly-fixture closing the Plan 05-02 telemetry.jsonl AMBER. The `[assembly: AssemblyFixture(typeof(BaseApi.Tests.Observability.OtelEndOfSuiteCleanup))]` attribute on line 4 lives in this file; deleting the file removes the assembly registration in the same commit. `DisposeAsync` shelled out to `docker compose stop|delete|start otel-collector` to release the file exporter's exclusive write handle (Phase 5 D-11 cleanup discipline); with no file exporter, the entire cleanup discipline is obsolete. The xUnit v3 AssemblyFixture pattern itself stays available — this file was the sole consumer.

- **tests/.otel-out/ directory REMOVED entirely** — `.gitkeep` deleted via `git rm`; `telemetry.jsonl` (untracked, was gitignored — ~1.9 MB at plan-start, persisted from prior Phase 5 runs) deleted via filesystem `rm -f`; directory itself removed via `rmdir` after both contained files were gone. Git does not track empty directories so the directory's disappearance is invisible to git's history (only the .gitkeep deletion shows in `git show --stat HEAD`).

- **.gitignore — 8-line Phase 5 (CONTEXT.md D-10) stanza REMOVED**. Pre-Phase-11 lines 414-421 (header comment block + `tests/.otel-out/*` + `!tests/.otel-out/.gitkeep` glob entries) deleted by content match (not line range — `Edit` tool's verbatim block as `old_string` with empty `new_string`). Surrounding patterns preserved byte-identical:
  - Phase 6 D-15 `*.received.*` Verify pattern (4 `.env.local` references retained — 2 in comment + 2 actual entries).
  - `bin/` + `obj/` build-output entries.
  - Compose env-var override stanza (`.env.local` + `*.env.local`).
  - All upstream Visual Studio template lines (`*.rsuser`, `*.suo`, etc.).
  After the edit, `.gitignore` has NO references to `tests/.otel-out` anywhere AND no reference to the header phrase "Phase 5 (CONTEXT.md D-10) — otel-collector file exporter host-mount target."

- **Build verification — GREEN end-to-end:**
  - `dotnet build src/BaseApi.Core/BaseApi.Core.csproj -c Release --no-restore` (Task 1 mid-plan smoke) → 0 Warning(s) / 0 Error(s).
  - `dotnet build SK_P.sln -c Release --no-restore` (Task 4 step 1) → 0 Warning(s) / 0 Error(s).
  - `dotnet build SK_P.sln -c Debug --no-restore` (Task 4 step 1) → 0 Warning(s) / 0 Error(s).
  - Compile-level checks all pass: `ObservabilityServiceCollectionExtensions.cs` references no removed types (`AlwaysOnSampler`, `AddNpgsql` extension method); no file under `tests/BaseApi.Tests/` imports `BaseApi.Tests.Observability.TraceExportTests` or `OtelEndOfSuiteCleanup`; `OtelCollectorFixture.cs` (preserved) still compiles (its consumers `LogExportTests` / `LogLevelFilterTests` / `MetricsExportTests` still build cleanly even though they'll FAIL at runtime — see Test Migration Sequencing Note below).

- **Single atomic commit** `0fa325e` with verbatim subject `refactor(observability): strip .WithTracing() + delete TraceExportTests + OtelEndOfSuiteCleanup + tests/.otel-out/` modifying 5 files (8 insertions / 352 deletions); 3 deletions all intentional and documented in the plan; `git diff --diff-filter=D HEAD~1 HEAD` returns exactly the 3 expected deletion paths; working tree clean post-commit.

## Task Commits

Per Plan 11-05's atomic-commit contract (success criteria #9 — "Single git commit ... exists at HEAD"), this plan ships as ONE atomic commit. All file mutations from Tasks 1–3 are sub-edits of a single forensic-friendly commit; Task 4 step 1 is build-verification-only (produces no commit); Task 4 step 3 is the single commit point.

1. **Task 1: Strip .WithTracing() chain + remove orphaned using directives + refresh XML doc** — staged at task boundary (rolled into Task 4 commit per atomic-commit contract)
2. **Task 2: Delete TraceExportTests.cs + OtelEndOfSuiteCleanup.cs + tests/.otel-out/ contents** — staged at task boundary via `git rm` + filesystem `rm` (rolled into Task 4 commit)
3. **Task 3: Remove the Phase 5 tests/.otel-out/ stanza from .gitignore** — staged at task boundary (rolled into Task 4 commit)
4. **Task 4: Build verification + commit refactor as commit #5 of the Phase 11 sequence** — `0fa325e` (refactor)

**Plan metadata:** TBD — committed by execute-plan agent after SUMMARY + STATE updates.

_Note: Plan 11-05 deliberately ships as ONE atomic commit per success criteria #9. Same atomic-commit pattern as Plans 11-01 + 11-02 + 11-03 + 11-04 (the established Phase 11 convention)._

## Files Created/Modified

- `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` — modified: 4 surgical edits (2 using directive removals + 1 XML doc summary replace + 1 .WithTracing block delete + 1 metrics-block comment append). File shrunk from 87 lines to 78 lines (-9).
- `.gitignore` — modified: 8-line Phase 5 stanza removed (lines 414-421 of pre-Phase-11 file). Surrounding patterns byte-identical.
- `tests/BaseApi.Tests/Observability/TraceExportTests.cs` — deleted via `git rm` (150 lines).
- `tests/BaseApi.Tests/Observability/OtelEndOfSuiteCleanup.cs` — deleted via `git rm` (178 lines).
- `tests/.otel-out/.gitkeep` — deleted via `git rm` (0 bytes; directory marker).
- `tests/.otel-out/telemetry.jsonl` — deleted via filesystem `rm -f` (~1.9 MB, was gitignored / untracked).
- `tests/.otel-out/` — directory removed via `rmdir` (post-files-removed); not tracked by git so invisible to history.

## Decisions Made

All wiring decisions inherited verbatim from Phase 11 CONTEXT.md D-03 (drop traces from SDK), D-05 (drop file exporter — last vestige is this plan's cleanup), and D-16 (delete TraceExportTests + OtelEndOfSuiteCleanup). Plan 11-05 D-XX (the supersession marker in REQUIREMENTS.md OBSERV-12) was already landed by Plan 11-01.

Execution-time judgment calls captured in `key-decisions` frontmatter:
- Single atomic commit (matches Phase 11 Wave 1-3 convention).
- XML doc-comment educational reference to deleted code preserved (Plan 11-04 precedent for must_haves as SEMANTIC invariants).
- tests/.otel-out/telemetry.jsonl removed via filesystem rm (was gitignored / untracked).
- tests/.otel-out/ directory removed entirely (CONTEXT.md Claude's Discretion option per RESEARCH Runtime State Inventory).
- Section-header comment refresh ("OTel METRICS + TRACES" → "OTel METRICS") — small reader-aid edit not explicitly called out in plan but consistent with plan intent.

## Deviations from Plan

**None — plan executed exactly as written for all 4 tasks + single commit.**

One minor reader-aid edit not explicitly enumerated in the plan: the inline section-header comment "OTel METRICS + TRACES. ConfigureResource MUST come before WithMetrics/WithTracing so the resource propagates to both branches." was updated to "OTel METRICS. ConfigureResource MUST come before WithMetrics so the resource propagates to the metrics provider. Traces pipeline REMOVED in Phase 11 (D-03)." This is a clarity refinement aligned with the plan's stated intent of leaving no stale `WithTracing` references in the file; not a Rule 1/2/3/4 deviation.

---

**Total deviations:** 0 auto-fixed
**Impact on plan:** All file mutations + the atomic commit landed per plan spec; build verification gates passed Release + Debug zero-warning. No scope creep; no file content deviates from plan-as-written.

## Issues Encountered

- **`.WithTracing` literal match in src/ after the strip** — the plan's verification gate `! grep -r "\.WithTracing" src/` returns 1 match: the XML doc comment line in `ObservabilityServiceCollectionExtensions.cs` that reads `"the SDK no longer emits them (this file's <c>.WithTracing(...)</c> block deleted)."`. This is an educational documentation reference, NOT a production `.WithTracing()` CALL. The plan's must_haves invariant is "has NO `.WithTracing(...)` call; the OpenTelemetry chain ends after `.WithMetrics(...)`" — focused on the CALL. The code-only check `grep -v '^\s*///' | grep '\.WithTracing'` returns 0 matches, confirming the production-code invariant is satisfied. Same pattern as Plan 11-04 (negative-assertion-comments preserved despite literal grep gate). Documented inline in the commit message + Decisions section.

- **`tests/.otel-out/telemetry.jsonl` was untracked (gitignored)** — `git check-ignore tests/.otel-out/telemetry.jsonl` exited 0 confirming the file was being ignored by the pre-edit .gitignore stanza. `git rm` on an untracked file would exit non-zero. Plan body Task 2 step 4 explicitly anticipated this case and prescribed filesystem `rm` as the fallback. Used `rm -f tests/.otel-out/telemetry.jsonl` followed by `rmdir tests/.otel-out` to clear both. No git-side tracking change beyond the `.gitkeep` deletion via `git rm`.

- **Test Migration Sequencing Note (intentional REDness on the horizon)** — after this commit lands, `LogExportTests` / `LogLevelFilterTests` / `MetricsExportTests` still reference `OtelCollectorFixture.cs` which itself references `ResolveTelemetryFile` + `ReadAllExportedRecords` (the file-exporter path now non-functional). Those 3 facts WILL FAIL at runtime with "telemetry.jsonl not found" or "no records" — that is intentional and matches the Phase 8/10 "intentionally RED until commit #N" forensic bisect pattern. Plan 11-08 migrates the 3 facts to ES/Prom polling; the build remains GREEN throughout because the test code still compiles (references to OtelCollectorFixture types resolve; the file is preserved, just non-functional). `OtelCollectorFixture.cs` itself is REPLACED by `Phase11WebAppFactory` in Plan 11-06; the old fixture file is deleted in Plan 11-08.

## Self-Check: PASSED

**File existence verification:**
- FOUND: `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` (modified — see git show --stat HEAD)
- FOUND: `.gitignore` (modified — 8-line stanza deleted)
- ABSENT: `tests/BaseApi.Tests/Observability/TraceExportTests.cs` (deleted via git rm)
- ABSENT: `tests/BaseApi.Tests/Observability/OtelEndOfSuiteCleanup.cs` (deleted via git rm)
- ABSENT: `tests/.otel-out/.gitkeep` (deleted via git rm)
- ABSENT: `tests/.otel-out/telemetry.jsonl` (deleted via filesystem rm; was untracked)
- ABSENT: `tests/.otel-out/` (directory removed via rmdir after files cleared)
- FOUND: `.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-05-SUMMARY.md` (this file)

**Commit verification:**
- FOUND: `0fa325e` (subject: `refactor(observability): strip .WithTracing() + delete TraceExportTests + OtelEndOfSuiteCleanup + tests/.otel-out/`)
- `git show --stat HEAD` lists 5 files changed: 1 modified source (ObservabilityServiceCollectionExtensions.cs -25 +0) + 1 modified config (.gitignore -8 +0) + 3 deletions (TraceExportTests.cs -149 / OtelEndOfSuiteCleanup.cs -178 / .gitkeep -0)
- `git diff --diff-filter=D --name-only HEAD~1 HEAD` returns exactly the 3 expected deletion paths
- `git status --porcelain` (excluding pre-existing untracked planning files) returns empty

**Plan-level verification gates (all PASS at commit 0fa325e):**
- `! grep "\.WithTracing" src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` — semantically PASS (code-only check via 'grep -v ^\\s*///' returns 0; the 1 literal hit is an educational doc-comment reference per Plan 11-04 precedent) ✓
- `! grep "AlwaysOnSampler" src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` — 0 matches ✓
- `! grep "AddNpgsql()" src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` — 0 matches ✓
- `! grep -E "^using Npgsql;" src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` — 0 matches ✓
- `! grep -E "^using OpenTelemetry\.Trace;" src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` — 0 matches ✓
- `grep "OBSERV-12 superseded" src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` — 1 match ✓
- `grep "Phase 11 (D-03)" src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` — 2 matches (XML doc + inline section-header comment) ✓
- `grep "\.WithMetrics" src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` — 1 match (metrics chain preserved) ✓
- `grep "AddRuntimeInstrumentation" src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` — 1 match (runtime instrumentation preserved) ✓
- `! test -f tests/BaseApi.Tests/Observability/TraceExportTests.cs` — file absent ✓
- `! test -f tests/BaseApi.Tests/Observability/OtelEndOfSuiteCleanup.cs` — file absent ✓
- `! test -f tests/.otel-out/.gitkeep` — file absent ✓
- `! test -d tests/.otel-out` — directory absent ✓
- `test -f tests/BaseApi.Tests/Observability/LogExportTests.cs` — preserved ✓
- `test -f tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` — preserved ✓
- `test -f tests/BaseApi.Tests/Observability/MetricsExportTests.cs` — preserved ✓
- `test -f tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — preserved ✓
- `test -f tests/BaseApi.Tests/Observability/TestObservabilityController.cs` — preserved ✓
- `test -f tests/BaseApi.Tests/Observability/CollectionDefinitions.cs` — preserved ✓
- `test -f tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` — preserved (Plan 11-08 will delete) ✓
- `! grep -rn "AssemblyFixture(typeof(BaseApi.Tests.Observability.OtelEndOfSuiteCleanup))" tests/` — 0 matches (assembly attribute gone with its file) ✓
- `! grep -F "tests/.otel-out" .gitignore` — 0 matches ✓
- `! grep -F "# Phase 5 (CONTEXT.md D-10) — otel-collector file exporter host-mount target." .gitignore` — 0 matches ✓
- `! grep -F "!tests/.otel-out/.gitkeep" .gitignore` — 0 matches ✓
- `grep -F ".env.local" .gitignore` — 4 matches (2 comment-lines + 2 actual entries; surrounding context preserved) ✓
- `grep -F "*.received.*" .gitignore` — 1 match (Verify pattern preserved from Phase 6 D-15 region) ✓
- `dotnet build SK_P.sln -c Release --no-restore` — 0 Warning(s) / 0 Error(s) ✓
- `dotnet build SK_P.sln -c Debug --no-restore` — 0 Warning(s) / 0 Error(s) ✓
- `git log -1 --format=%s` — matches `refactor(observability): strip .WithTracing() + delete TraceExportTests + OtelEndOfSuiteCleanup + tests/.otel-out/` ✓
- `git show --stat HEAD` — 5 files changed (1 modified source + 1 modified .gitignore + 3 deletions) ✓
- `git status --porcelain` (excluding pre-existing untracked planning files outside this plan's scope) — empty ✓

**Plan success_criteria coverage (all 9 criteria PASS at commit 0fa325e):**
- #1 ObservabilityServiceCollectionExtensions.cs has no `.WithTracing(...)` CALL; OpenTelemetry chain ends at `.WithMetrics(...).AddOtlpExporter())` ✓
- #2 Orphaned `using Npgsql;` and `using OpenTelemetry.Trace;` directives removed ✓
- #3 XML doc summary references OBSERV-12 supersession + Phase 11 D-03 ✓
- #4 TraceExportTests.cs deleted ✓
- #5 OtelEndOfSuiteCleanup.cs deleted (assembly attribute gone with it) ✓
- #6 tests/.otel-out/.gitkeep deleted; tests/.otel-out/telemetry.jsonl deleted; tests/.otel-out/ directory removed ✓
- #7 .gitignore Phase 5 8-line stanza removed; surrounding context preserved byte-identical ✓
- #8 dotnet build SK_P.sln Release + Debug → 0 Warning(s) / 0 Error(s) each ✓
- #9 Single atomic commit `0fa325e` exists at HEAD; modifies/deletes the 5 expected paths; working tree clean post-commit ✓

## User Setup Required

None — this is a code + test-infrastructure + config commit. No external service configuration required. The Phase 11 observability backend (collector + ES + Prom) remains healthy (orchestrator-validated post-Plan 11-04 per the objective note).

## Next Phase Readiness

**Plan 11-06 (test migration setup — ElasticsearchTestClient + PrometheusTestClient + Phase11WebAppFactory)** is unblocked: the SDK + collector + compose-stack are all in their Phase-11 shape. The test helpers can target the wired backends (ES `:9200` + Prom `:9090` + collector `:8889/metrics`) knowing that:
- The collector receives no traces (this plan removed the producer; Plan 11-03 removed the consumer pipeline) — round-trip E2E test designs can ignore traces entirely.
- The .gitignore + tests/.otel-out/ cleanup is complete — Plan 11-06's new helper files land into a clean tree without file-exporter remnants.
- The 3 file-exporter-coupled test classes (`LogExportTests` / `LogLevelFilterTests` / `MetricsExportTests`) are KNOWN-RED at runtime starting from this commit; Plan 11-08 migrates them.

**Plan 11-07 (wave-0 probe for ES index name + Prom metric label invariants)** is unblocked: the SDK now emits only logs + metrics OTLP records (no traces), so the probe will see exactly the data shape Plan 11-08 needs to assert against.

**Plan 11-08 (final test migration + OtelCollectorFixture.cs deletion)** is unblocked: with TraceExportTests + OtelEndOfSuiteCleanup already gone, Plan 11-08's scope narrows to migrating LogExportTests + LogLevelFilterTests + MetricsExportTests to ES/Prom polling + deleting the now-orphaned OtelCollectorFixture.cs.

The forensic property holds: the SDK code now matches the collector config (Plan 11-03) AND the compose-stack shape (Plan 11-02) AND the REQUIREMENTS.md spec (Plan 11-01) AND the prometheus.yml scrape config (Plan 11-04). The single atomic commit (0fa325e) is independently revertable: `git revert 0fa325e` restores the .WithTracing block + both deleted test files + the tests/.otel-out/.gitkeep + the .gitignore stanza in one operation, leaving subsequent Phase 11 commits intact above.

---
*Phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack*
*Plan: 05*
*Completed: 2026-05-28*
