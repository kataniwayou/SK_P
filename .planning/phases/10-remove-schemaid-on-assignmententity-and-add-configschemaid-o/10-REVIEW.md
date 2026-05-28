---
phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o
reviewed: 2026-05-28T00:00:00Z
depth: standard
files_reviewed: 19
files_reviewed_list:
  - src/BaseApi.Service/Features/Assignment/AssignmentDtoValidator.cs
  - src/BaseApi.Service/Features/Assignment/AssignmentDtos.cs
  - src/BaseApi.Service/Features/Assignment/AssignmentEntity.cs
  - src/BaseApi.Service/Features/Processor/ProcessorDtoValidator.cs
  - src/BaseApi.Service/Features/Processor/ProcessorDtos.cs
  - src/BaseApi.Service/Features/Processor/ProcessorEntity.cs
  - src/BaseApi.Service/Persistence/Configurations/AssignmentEntityConfiguration.cs
  - src/BaseApi.Service/Persistence/Configurations/ProcessorEntityConfiguration.cs
  - src/BaseApi.Service/Persistence/Migrations/20260528074618_InitialCreate.Designer.cs
  - src/BaseApi.Service/Persistence/Migrations/20260528074618_InitialCreate.cs
  - src/BaseApi.Service/Persistence/Migrations/AppDbContextModelSnapshot.cs
  - tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/StopOrchestrationFacts.cs
  - tests/BaseApi.Tests/Features/Processor/GetBySourceHashFacts.cs
  - tests/BaseApi.Tests/Integration/AssignmentsIntegrationTests.cs
  - tests/BaseApi.Tests/Integration/ErrorMappingFacts.cs
  - tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs
  - tests/BaseApi.Tests/Integration/StepsIntegrationTests.cs
  - tests/BaseApi.Tests/Integration/WorkflowsIntegrationTests.cs
findings:
  critical: 0
  warning: 1
  info: 3
  total: 4
status: issues_found
---

# Phase 10: Code Review Report

**Reviewed:** 2026-05-28T00:00:00Z
**Depth:** standard
**Files Reviewed:** 19
**Status:** issues_found

## Summary

Phase 10 cleanly removes `AssignmentEntity.SchemaId` and adds the parallel
`ProcessorEntity.ConfigSchemaId` (nullable Guid). Production source files
(`AssignmentEntity`, `AssignmentDtos`, `AssignmentDtoValidator`,
`AssignmentEntityConfiguration`, `ProcessorEntity`, `ProcessorDtos`,
`ProcessorDtoValidator`, `ProcessorEntityConfiguration`) are internally
consistent. The regenerated EF migration (`InitialCreate`) +
`AppDbContextModelSnapshot.cs` correctly reflect the post-Phase-10 model:
no `assignment.schema_id` column / `fk_assignment_schema_id` constraint,
new `processor.config_schema_id` nullable column with `SetNull` cascade and
the `fk_processor_config_schema_id` constraint name matching the
`PostgresExceptionMapper` Option A regex. Test-suite updates are mechanical
and add two `ConfigSchemaId` round-trip facts that close the suite-count
contract (138 → 140 — assuming pre-existing counts are accurate).

The one **Warning** is pre-existing in `AssignmentDtoValidator` (XML doc claims
DoS prevention via rule ordering that FluentValidation's default cascade mode
does not actually deliver). The 3 **Info** items are documentation/comment
hygiene around the post-Phase-10 model shape.

## Warnings

### WR-01: AssignmentDtoValidator Payload DoS-prevention claim is inaccurate

**File:** `src/BaseApi.Service/Features/Assignment/AssignmentDtoValidator.cs:17-19, 43-60` (and identical block at lines 79-95)
**Issue:** The XML doc says:

> The MaxLength rule fires BEFORE `JsonDocument.Parse` via FluentValidation rule ordering — prevents DoS via pathologically large parse-target strings.

FluentValidation v11+ defaults `RuleLevelCascadeMode` to `Continue`, so all
chained validators within a `RuleFor` run regardless of prior failures.
Concretely, if a client posts a 10 MB `Payload`:

1. `MaximumLength(MaxPayloadBytes)` records a failure on the `Payload` field.
2. `.Custom(...)` still executes and calls `JsonDocument.Parse(payload)` on
   the full 10 MB string — the exact CPU/allocation cost the docstring claims
   to prevent.

The author of `OrchestrationDtoValidator.cs:42-45` was aware of this FV12
behavior and explicitly wrote `.Cascade(CascadeMode.Stop)`. This validator
does not. There is no global `ValidatorOptions.Global.DefaultRuleLevelCascadeMode`
override in the codebase (verified by Grep).

Severity is Warning (not Critical) because Kestrel's
`MaxRequestBodySize` (~30 MB default) still bounds the worst case, and the
oversized-payload outcome remains a 400 (just one that also burns through
a `JsonDocument.Parse` cycle on the way). But the docstring's load-bearing
claim is wrong, and a future reader who edits the rule order may rely on it.

**Fix:** Add `.Cascade(CascadeMode.Stop)` to the chain so the docstring claim
actually holds. Apply to both `AssignmentCreateDtoValidator` and
`AssignmentUpdateDtoValidator`.

```csharp
RuleFor(x => x.Payload)
    .Cascade(CascadeMode.Stop)
    .NotEmpty()
    .MaximumLength(MaxPayloadBytes)
    .WithMessage($"Payload must be at most {MaxPayloadBytes} characters.")
    .Custom((payload, ctx) =>
    {
        if (string.IsNullOrEmpty(payload)) return;
        try
        {
            using var doc = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            ctx.AddFailure(nameof(AssignmentCreateDto.Payload),
                $"Payload is not valid JSON: {ex.Message}");
        }
    });
```

Alternative if `.Cascade(CascadeMode.Stop)` is intentionally avoided: gate
the parse inside the `.Custom(...)` callback on the same length check
(`if (payload.Length > MaxPayloadBytes) return;`). Pick one; either makes
the docstring true.

Note: this Warning pre-dates Phase 10 — the same `.Custom(...)` block existed
before the SchemaId removal. Flagged here because the file is in scope and
the doc comment was edited in Phase 10 (lines 21-27 add the VALID-21 deferral
note).

## Info

### IN-01: Stale `Assignment.SchemaId` reference in `SchemaEntity.cs` doc

**File:** `src/BaseApi.Service/Features/Schema/SchemaEntity.cs:5-7` (out of scope, but adjacent)
**Issue:** The `SchemaEntity` summary still reads:

> the root of the entity FK graph (Processor.InputSchemaId / Processor.OutputSchemaId reference it; Assignment.SchemaId references it).

Post-Phase 10, `Assignment.SchemaId` no longer exists. The doc should mention
`Processor.ConfigSchemaId` instead. This file was NOT in the Phase 10 review
scope (`files` list), so the omission is technically a coverage gap of the
removal sweep rather than a Phase 10 regression — but it leaves a documentation
mention of a property that no longer exists in the codebase.

**Fix:** Replace the parenthetical with:

```csharp
/// Schema domain entity — the root of the entity FK graph (Processor.InputSchemaId /
/// Processor.OutputSchemaId / Processor.ConfigSchemaId reference it).
```

(Phase 10 SPEC line 92 explicitly limited scope to the Assignment + Processor +
test files; this stale doc was not on the cleanup list. Surfacing for awareness
in case a follow-up commit can fold it in.)

### IN-02: `ProcessorEntity` doc summary says "3 new scalar properties" but adds a 4th

**File:** `src/BaseApi.Service/Features/Processor/ProcessorEntity.cs:7-8`
**Issue:** The doc still reads "ENTITY-04 verbatim: 3 new scalar properties on
top of `BaseEntity`." Post-Phase 10 the entity has 4 scalars: `SourceHash`,
`InputSchemaId`, `OutputSchemaId`, **`ConfigSchemaId`**. The ENTITY-04
requirement itself was amended in Phase 10 plan 10-01 to include
ConfigSchemaId, so the count "3" is now stale.

**Fix:**

```csharp
/// Processor domain entity — sits one level below <c>SchemaEntity</c> in the FK topology
/// and one level above <c>StepEntity</c>. ENTITY-04 verbatim: 4 new scalar properties on
/// top of <see cref="BaseEntity"/>.
```

The body paragraph at lines 16-22 already correctly enumerates all four
fields, so only the count needs updating.

### IN-03: `ProcessorDtos.cs` says "7 positional params" — verify against the records (already correct, comment is the only artifact)

**File:** `src/BaseApi.Service/Features/Processor/ProcessorDtos.cs:9, 24, 40`
**Issue:** The three doc summaries say "7 positional params" (Create/Update)
and "12 positional params: Id + 7 from CreateDto + 4 audit" (Read). I count
exactly 7 (Create), 7 (Update), 12 (Read) on the record declarations — the
counts are correct. Flagging only because the prior version of this file
likely said "6 / 11"; verify with `git diff` that the count was bumped in
this commit. (Read line 40 currently reads "12 positional params: Id + 7 from
CreateDto + 4 audit" which is 1 + 7 + 4 = 12 ✓.)

**Fix:** No change required — record matches the comment. This is a
verification touchpoint only; if the diff shows the old text still said
"6/11", a separate doc-only commit may be needed (but the post-Phase-10
state is already correct, so this is non-blocking).

### IN-04: Two integration-test files contain identical `RandomSha256Hex()` helper

**File:** Multiple — `tests/BaseApi.Tests/Integration/AssignmentsIntegrationTests.cs:38-42`, `ProcessorsIntegrationTests.cs:34-38`, `StepsIntegrationTests.cs:39-43`, `WorkflowsIntegrationTests.cs:42-46`, `ErrorMappingFacts.cs:25-29`, `Features/Processor/GetBySourceHashFacts.cs:40-44`, `Features/Orchestration/StartOrchestrationFacts.cs:41-45`, `Features/Orchestration/StopOrchestrationFacts.cs:36-40`
**Issue:** The same `static string RandomSha256Hex()` helper is duplicated
across 8 test files. Each was clearly copy-pasted (the comment in
`GetBySourceHashFacts.cs:37` even says "Copied verbatim from
tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs lines 33-37").
Maintenance cost: any change to the helper (e.g., adopt a cryptographically
strong RNG or shift to `Convert.ToHexString`) must be applied 8 times.

Note: the implementation `Guid.NewGuid().ToByteArray().Concat(...)` is fine
for test uniqueness purposes (32 bytes of process-random data, no security
properties claimed); the concern is purely duplication.

**Fix:** Extract to `tests/BaseApi.Tests/TestHelpers/HashHelpers.cs`
(or similar shared utility namespace) and call it from each test file.
Non-blocking; the duplication is pre-existing and Phase 10 only touched
the existing copies. Surface for the next refactor pass.

---

_Reviewed: 2026-05-28T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
