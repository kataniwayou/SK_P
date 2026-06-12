---
phase: 58-orchestration-gate-integration-proof-close
plan: 04
subsystem: infra
tags: [close-gate, triple-sha, net-zero, powershell, gate-a, config-schema, sourcehash, badconfig, cfg-08, cfg-09]

# Dependency graph
requires:
  - phase: 58-01-processor-badconfig
    provides: "Processor.BadConfig console (distinct embedded SourceHash off src/Processor.BadConfig/bin/Release/net8.0/Processor.BadConfig.dll) — the second hash this close script reads + seeds"
  - phase: 58-02-gate-a-seed-primitives-sc-retag
    provides: "The exact sentinel Names + definitions (gateA-sample-compatible / gateA-badconfig-clash) the E2E seed uses — this close script seeds the SAME rows so the live N=3 run is idempotent"
  - phase: 55-live-proof-close-gate
    provides: "scripts/phase-55-close.ps1 — the proven triple-SHA net-zero protocol cloned verbatim here"
provides:
  - "scripts/phase-58-close.ps1 — Phase 58 triple-SHA close gate (two-schema/two-processor CREATE-IF-ABSENT seed + badconfig profile bring-up reference)"
  - "Operator-runnable D-08/D-09 close gate for the live N=3 GREEN proof (Plan 05 runbook references the same bring-up command + sentinel Names)"
affects: [58-05-human-uat-close]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Dual embedded-hash read: two identical Assembly.Load + AssemblyMetadata('SourceHash') blocks (Sample + BadConfig), each ^[a-f0-9]{64}$ validated with exit 2 on failure"
    - "GET-or-create-by-sentinel-Name schema seed (never PUT — frozen-once-referenced 409) factored into a reusable Get-OrCreateSchemaId helper, used twice"
    - "Gate-A incompatible subject seeded-but-not-awaited: badconfig contributes a DB row (so E2E + gate snapshots converge) but adds NO SHA exclusion and NO liveness pre-flight wait (it never goes Healthy)"

key-files:
  created:
    - scripts/phase-58-close.ps1
  modified: []

key-decisions:
  - "processor-badconfig is seeded (schema + processor row) but is DELIBERATELY excluded from both the $services health-required list AND the post-seed liveness wait — Gate A withholds its MarkHealthy so expecting Healthy would false-fail the gate (D-09b)"
  - "No badconfig redis SHA exclusion added — it writes no liveness key and binds no queue, so a stray badconfig key surfaces as a SHA mismatch rather than being silently masked (T-58-09)"
  - "Triple-SHA invariant block (psql \\l SHA, unfiltered redis --scan SHA with the Sample skp:{procId} + _bus_ exclusions, rabbitmq list_queues name SHA, skp-dlq-1 depth==0, skp:msg:* count==0, N=3 identical-fact-count Smell-A guard) carried VERBATIM from phase-55 apart from title strings"
  - "Seed version stays '3.5.0' for BOTH processors (D-09c — the SourceHash, not the version string, distinguishes identity)"

patterns-established:
  - "Pattern 1: Reusable Get-OrCreateSchemaId / Get-OrCreateProcessorId helpers keep the four CREATE-IF-ABSENT seeds (2 schemas + 2 processors) idempotent against a stable-procId N=3 run"
  - "Pattern 2: Seed-but-don't-await for a Gate-A incompatible subject — converge the snapshot rows without making liveness a pre-flight requirement"

requirements-completed: [CFG-08, CFG-09]

# Metrics
duration: 8min
completed: 2026-06-13
---

# Phase 58 Plan 04: Phase-58 Triple-SHA Close Gate Summary

**A verbatim clone of `scripts/phase-55-close.ps1` that reads BOTH embedded SourceHashes (Sample + BadConfig), GET-or-creates two named config-schema rows + two processor rows (CREATE-IF-ABSENT, never PUT), and runs the unchanged triple-SHA / `skp:msg:*` count==0 / `skp-dlq-1` depth==0 / N=3 net-zero protocol — adding no badconfig SHA exclusion and no badconfig liveness pre-flight.**

## Performance

- **Duration:** ~8 min
- **Started:** 2026-06-12T22:37:39Z
- **Completed:** 2026-06-13T00:00:00Z (approx)
- **Tasks:** 1
- **Files modified:** 1 (1 created)

## Accomplishments
- Created `scripts/phase-58-close.ps1` — a faithful clone of the proven phase-55 triple-SHA close gate with ONLY the D-09 seed deltas.
- Dual embedded-hash read: a second identical `Assembly.Load` + `AssemblyMetadata("SourceHash")` block for `src/Processor.BadConfig/bin/Release/net8.0/Processor.BadConfig.dll` (Debug fallback, build-if-absent, `^[a-f0-9]{64}$` validation, `exit 2`), alongside the unchanged Sample read.
- Two config-schema rows seeded FIRST by sentinel Name via a reusable `Get-OrCreateSchemaId` helper (GET-all `/api/v1/schemas` → filter by Name → reuse-or-POST; NEVER PUT) using the EXACT sentinel Names + definitions from 58-02-SUMMARY (`gateA-sample-compatible` → `value:string`; `gateA-badconfig-clash` → `quantity:string`).
- Two processor rows seeded via a reusable `Get-OrCreateProcessorId` helper (GET-by-source-hash → reuse-or-POST): Sample with `configSchemaId = $compatibleSchemaId` (was `$null`), BadConfig with `configSchemaId = $clashSchemaId`.
- The phase-55 triple-SHA invariant block (psql `\l` SHA, unfiltered redis `--scan` SHA with the Sample `skp:{procId}` + `_bus_` exclusions, rabbitmq `list_queues name` SHA, separate `skp-dlq-1` depth==0, additive `skp:msg:*` count==0, N=3 identical-fact-count Smell-A guard) is carried verbatim; retitled Phase 58 / v6.0.0.

## Task Commits

1. **Task 1: Clone phase-55-close.ps1 to phase-58-close.ps1 with the two-schema/two-processor seed + badconfig deltas** — `5e283bb` (feat)

## Files Created/Modified
- `scripts/phase-58-close.ps1` - Phase 58 triple-SHA close gate. Reads both embedded SourceHashes, GET-or-creates two named schemas + two processors (CREATE-IF-ABSENT, never PUT), carries the phase-55 triple-SHA / DLQ / msg-count / N=3 protocol verbatim, adds no badconfig SHA exclusion and no badconfig liveness pre-flight wait, and documents the `--profile badconfig` bring-up.

## Bring-up command the script header documents
```
docker compose --profile badconfig up -d --build baseapi-service orchestrator processor-sample keeper processor-badconfig
```
Sentinel schema Names it seeds (shared with the E2E): `gateA-sample-compatible`, `gateA-badconfig-clash`. The triple-SHA invariant block is verbatim from phase-55 (only the Phase 55→58 / v5.0.0→v6.0.0 title strings changed).

## Decisions Made
- **processor-badconfig seeded-but-not-awaited.** Its schema + processor rows are seeded so the live E2E and the close-gate snapshots converge on the same rows, but it is absent from both the `$services` health-required list and the post-seed liveness wait — Gate A withholds its MarkHealthy, so requiring Healthy would false-fail the gate (D-09b).
- **No badconfig redis SHA exclusion.** It writes no liveness key and binds no queue, so it is naturally absent from both snapshots; keeping the exclusion set minimal (only the Sample `skp:{procId}`) means a stray badconfig key would surface as a SHA mismatch rather than being silently masked (T-58-09).
- **Reusable seed helpers.** Factored the schema and processor GET-or-create logic into `Get-OrCreateSchemaId` / `Get-OrCreateProcessorId` so the four CREATE-IF-ABSENT seeds are uniform and the stable-procId requirement holds across the N=3 run (T-58-10). This is a structural refactor of the phase-55 inline seed, not a behavior change — the same GET→reuse / 404→POST / never-PUT semantics.

## Deviations from Plan

None - plan executed exactly as written. (The phase-55 inline single-processor seed was lifted into two reusable helper functions to express the now-doubled seed without duplication; the GET-or-create-against-`uq_processor_source_hash` / GET-all-filter-by-Name / never-PUT semantics are byte-for-byte the phase-55 behavior, just parameterized.)

## Issues Encountered
None. The Bash tool mangled `$PSVersionTable`/`$P...` interpolation on a couple of throwaway probe commands; pwsh itself runs cleanly (the AST parse + the actual script use pwsh natively). No impact on the deliverable.

## Threat Surface
No new endpoints, auth, crypto, or input-validation surface (the script uses the existing CRUD + docker-exec snapshot tooling unchanged). T-58-09 (false net-zero via SHA exclusion) mitigated: badconfig adds NO exclusion; the invariant block is verbatim. T-58-10 (state churn via non-idempotent seed) mitigated: GET-or-create-by-Name / by-source-hash, never PUT, keeps the N=3 fact count identical. T-58-11 (identity divergence) mitigated: both hashes read off the BUILT dlls, `^[a-f0-9]{64}$` validated, `exit 2` on mismatch. No new threat flags.

## Known Stubs
None. The script is the complete autonomous deliverable (EXISTS + PARSES). Its live N=3 execution is operator-gated by design (Plan 05 / 58-HUMAN-UAT) — not a stub, an intentional operator gate.

## User Setup Required
None - no external service configuration required for the autonomous deliverable. The live N=3 GREEN run against the rebuilt `--profile badconfig` stack is the operator's Plan-05 runbook step.

## Next Phase Readiness
- `scripts/phase-58-close.ps1` exists, parses (PowerShell AST-valid, zero errors), and embodies the D-09 seed deltas over a verbatim phase-55 triple-SHA protocol.
- Plan 05 (58-HUMAN-UAT) can reference the documented `--profile badconfig` bring-up command + the `gateA-sample-compatible` / `gateA-badconfig-clash` sentinel Names for the operator-gated N=3 GREEN close run.
- No blockers. No live stack was required for any autonomous check (parse + grep only).

## Verification
- AST parse: `PARSE OK` (zero parse errors).
- `Processor.Sample.dll` present + `Processor.BadConfig.dll` present (both hashes read).
- `gateA-sample-compatible` + `gateA-badconfig-clash` sentinels present.
- Never PUTs a schema (zero `Method Put.*schemas` matches).
- Triple-SHA invariant carried: `list_queues`, `skp-dlq-1`, `skp:msg:` all present.
- Retitled: `Phase 58` present, `Phase 55` absent (no stale title).
- No badconfig liveness pre-flight: badconfig appears only in the seed + comments; the wait loop awaits only `processor-sample`; `$services` excludes badconfig.

## Self-Check: PASSED

- FOUND: scripts/phase-58-close.ps1
- FOUND commit 5e283bb (Task 1)

---
*Phase: 58-orchestration-gate-integration-proof-close*
*Completed: 2026-06-13*
