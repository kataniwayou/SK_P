# Phase 10: Remove SchemaId on AssignmentEntity and add ConfigSchemaId on ProcessorEntity - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-28
**Phase:** 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o
**Areas discussed:** Edit ordering / commit strategy, Test fixture & ConfigSchemaId round-trip design, Migration regeneration mechanics & verification, Mapperly drift probe

---

## Edit ordering / commit strategy (Area 1)

### Q1: When should REQUIREMENTS.md amendments be committed relative to code?

| Option | Description | Selected |
|--------|-------------|----------|
| Doc-first (Phase 3 D-03b precedent) | Commit #1 amends REQUIREMENTS.md before any code; forensic-friendly if execution stops mid-phase | ✓ |
| Concurrent (one commit per logical area) | REQUIREMENTS.md amend rows land in the same commit as their corresponding code change | |
| Doc-last (after code lands) | Amend REQUIREMENTS.md after all code/migration/tests are GREEN | |

**User's choice:** Doc-first (Phase 3 D-03b precedent)
**Notes:** Mirrors Plan 03-01's PERSIST-16 introduction pattern verbatim. Half-state safety preserved if execution interrupts.

### Q2: How should the 7-requirement workload split into atomic commits?

| Option | Description | Selected |
|--------|-------------|----------|
| Per logical area: asm-remove + proc-add + migration + tests | 4 code commits aligned to natural seams; independently revertable | ✓ |
| Per file type: all entities first, then all DTOs, etc. | Sequential by layer; each intermediate commit is RED for build; bisect-hostile | |
| Single feat commit (everything bundled) | One large commit; atomic but unreviewable | |

**User's choice:** Per logical area
**Notes:** Bisect-friendly; per-commit `dotnet build` zero-warning gate; per-commit revertability.

### Q3: Where does the migration regeneration step sit relative to code changes?

| Option | Description | Selected |
|--------|-------------|----------|
| After all code changes, before tests | Only ordering that produces a correct migration in one pass | ✓ |
| Before code changes (cannot work) | Would re-emit current InitialCreate (no model delta to capture) | |
| Paired with each code change (two migrations) | Conflicts directly with SPEC (regenerate-in-place, not additive) | |

**User's choice:** After all code changes, before tests
**Notes:** EF needs post-Phase-10 model to generate correct migration; alternative orderings produce wrong artifacts.

---

## Test fixture & ConfigSchemaId round-trip design (Area 2)

### Q1: Where do the 2 new ConfigSchemaId facts live and how do they seed the Schema FK target?

| Option | Description | Selected |
|--------|-------------|----------|
| In ProcessorsIntegrationTests.cs, each fact seeds its own Schema inline | Mirrors Plan 09-03 D-19 independent-[Fact]s precedent; clean failure attribution | ✓ |
| New file tests/BaseApi.Tests/Features/Processor/ConfigSchemaIdFacts.cs | Phase 9 D-19 feature-folder pattern; cons: ConfigSchemaId is a FIELD round-trip, not a NEW ENDPOINT | |
| Shared Schema POST helper in ProcessorsIntegrationTests | Borderline helper extraction; SPEC excluded helper extraction for refactor cleanup | |

**User's choice:** In ProcessorsIntegrationTests.cs, each fact seeds its own Schema inline
**Notes:** Feature-folder pattern reserved for new endpoints; field round-trips belong alongside the implicit Input/OutputSchemaId round-trips.

### Q2: How does AssignmentsIntegrationTests.CreatePrereqAsync simplify?

| Option | Description | Selected |
|--------|-------------|----------|
| Drop the Schema POST entirely; return only stepId | After Phase 10, AssignmentEntity has no SchemaId; chain collapses to Processor → Step | ✓ |
| Keep the Schema POST but stop returning the schemaId tuple | Dead-weight HTTP call; defeats the simplification | |

**User's choice:** Drop the Schema POST entirely; return only stepId
**Notes:** 3 call sites at lines ~95/~140/~180 update from `(stepId, schemaId)` tuple to `stepId` single value.

### Q3: Should the 2 new facts use IClassFixture<Phase8WebAppFactory>?

| Option | Description | Selected |
|--------|-------------|----------|
| Reuse Phase8WebAppFactory | Canonical integration-test factory; serves all Wave B smokes + Phase 9 facts | ✓ |
| New Phase10WebAppFactory subclass | Zero behavioral delta; pure cosmetic split with maintenance burden | |

**User's choice:** Reuse Phase8WebAppFactory
**Notes:** Per-class throwaway DB fixture is shape-agnostic; new fixture would be pure cosmetic.

---

## Migration regeneration mechanics & verification (Area 3)

### Q1: How is the teardown sequence captured?

| Option | Description | Selected |
|--------|-------------|----------|
| CONTEXT.md D-XX decision + Plan Task 0 explicit step | Both layers: decision reviewable in CONTEXT.md; plan turns it into executable steps | ✓ |
| Pitfall note only (no D-XX) | Pitfalls are easier to skim past; decisions more enforced | |

**User's choice:** CONTEXT.md D-XX decision + Plan Task 0 explicit step
**Notes:** Both layers required — CONTEXT preserves WHY; Plan turns WHAT into actionable steps. Without the plan task, executor may invoke `dotnet ef migrations add` before deleting old files (silently violates SPEC).

### Q2: How is "exactly one InitialCreate file" enforced?

| Option | Description | Selected |
|--------|-------------|----------|
| Grep + file-count gate in the migration plan's verification step | test-existence + file count + grep assertions; fails commit atomically | ✓ |
| Trust EF tooling — no explicit gate | If executor forgets teardown, second migration file silently lands | |

**User's choice:** Grep + file-count gate
**Notes:** Forensic-friendly; no silent failure mode. Plan verification fails commit #4 atomically if any check fails.

### Q3: Sidecar SQL script for diff review?

| Option | Description | Selected |
|--------|-------------|----------|
| No snapshot | Migration file IS source of truth; commit #4 diff IS the review surface | ✓ |
| Sidecar SQL script in .planning/phases/10-.../migration-script.sql | Duplicates content; rots independently; not consumed by any downstream tool | |

**User's choice:** No snapshot
**Notes:** Avoids duplicate-source-of-truth rot; deferred to separate docs phase if external SQL-review consumers ever emerge.

---

## Mapperly drift probe (Area 4)

### Q1: Should Phase 10 include a Mapperly drift probe?

| Option | Description | Selected |
|--------|-------------|----------|
| Skip the probe — trust Phase 6 enforcement | RMG012/RMG020/RMG089 promoted globally; safety net proven across Phases 6-9; Phase 10 changes are symmetric (no asymmetry to catch) | ✓ |
| Include a probe task as a Phase 10 sanity check | Reproves safety net at exact moment surface changes; ~3 minutes extra confidence | |

**User's choice:** Skip the probe — trust Phase 6 enforcement
**Notes:** Probe's diagnostic value is for asymmetric changes; Phase 10 introduces no asymmetry. Probe lives in `/gsd-validate-phase` for retroactive audit if drift confidence erodes.

---

## Claude's Discretion

- Exact wording of REQUIREMENTS.md amendments (shape locked in SPEC.md REQ-5 + CONTEXT D-01..D-03; prose at planner discretion)
- Exact `[Trait]` tag values on the 2 new ConfigSchemaId facts (D-06 says inherit from class; planner may diverge if Phase 8 convention dictates otherwise, with documentation)
- Exact wording of new XML `<summary>` doc-comments on `ProcessorEntity.ConfigSchemaId` and updated `AssignmentEntity` (must preserve source/sink/config wording pattern)

## Deferred Ideas

- Negative-path facts for ConfigSchemaId (Guid.Empty → 400, non-existent FK → 422, Schema-DELETE-while-referenced SetNull) — SPEC out-of-scope; defer to hardening phase or `/gsd-validate-phase 10`
- `MaximumCount` cap on WorkflowIds validator — Phase 9 D-08 deferred; not Phase 10's concern
- Pagination/filtering/sorting on list endpoints (HTTP-17/18/19) — v2 backlog
- VALID-21 dynamic schema conformance — Assignment loses SchemaId structurally; future revival needs new design
- Helper extraction for 8 ProcessorCreateDto call sites — SPEC out-of-scope
- Sidecar SQL script snapshot — D-09 rejection to avoid duplicate-source rot
