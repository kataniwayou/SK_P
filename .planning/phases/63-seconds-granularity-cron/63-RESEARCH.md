# Phase 63: Seconds-Granularity Cron - Research

**Researched:** 2026-06-14
**Domain:** Cronos cron parsing (.NET) + FluentValidation rule + anti-desync single-source-of-truth hoist
**Confidence:** HIGH (every CONTEXT.md claim verified against live source this session)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** Token-count switch — split trimmed expression on whitespace; **6 tokens → `CronFormat.IncludeSeconds`**, **5 tokens → `CronFormat.Standard`**, any other count → invalid/reject. Cronos has no auto-detect; format must be chosen explicitly.
- **D-02:** **No exception-as-control-flow.** Do NOT detect by attempting one `Cronos.Parse` and catching `CronFormatException` to retry the other format. Token count selects the format up front; `Cronos.Parse` is called once with the resolved format. A genuinely-malformed 6-token expression still surfaces as a parse failure (not silently retried as 5-field).
- **D-03:** Detection rule lives in **exactly one place** — a **pure string helper in `Messaging.Contracts`** (e.g. `CronFieldForm.IsSecondsForm(string)`), token-count logic only, **with NO Cronos dependency added to `Messaging.Contracts`**.
- **D-04:** Both call sites consume the shared detector to pick `CronFormat`, then each performs its **own local `Cronos.Parse`**: `CronInterval` (Orchestrator) — `NextOccurrence` + `IntervalSeconds`; `WorkflowCreateDtoValidator` / `WorkflowUpdateDtoValidator` (BaseApi.Service) — `BeValidStandardCron`.
- **D-05:** `Messaging.Contracts` is the **only** assembly both sites can share (Orchestrator firewall D-08 blocks `BaseApi.*`). Centralize **only the detection rule**, not the Cronos parse, to keep the contracts leaf parser-free.
- **D-06:** **No minimum-interval floor.** Accept any valid 6-field cron including `* * * * * *` (every 1s). No new interval validation rule.
- **D-07:** Existing UTC contract on `CronInterval` preserved — `nowUtc` MUST be `DateTimeKind.Utc`; callers feed `timeProvider.GetUtcNow().UtcDateTime`.
- **D-08:** Extend `CronIntervalTests` with a `*/30 * * * * *` case — `IntervalSeconds(...) == 30` and `NextOccurrence(...)` strictly-future + `Kind=Utc`. Existing 5-field cases retained.
- **D-09:** Add validator unit tests (none exist today) for **both** Create and Update validators: 5-field still accepted, 6-field now accepted, malformed still rejected.
- **D-10:** Add a detector unit test for the new pure `Messaging.Contracts` helper (5 → not-seconds, 6 → seconds, other counts → invalid).
- **D-11:** Update the validator's user-facing error message from "must be a valid 5-field cron expression…" to reflect **5- or 6-field** now accepted (both Create + Update).

### Claude's Discretion
- Exact helper name / namespace within `Messaging.Contracts`, signature shape (`bool` vs small enum/int), and how each call site maps the result to `CronFormat`.
- Internal refactor of the duplicated `BeValidStandardCron` (Create vs Update) — may collapse to route through the shared detector however reads cleanest, provided behavior is identical across both.
- Test data tables, naming, and whether new validator tests are unit-level (preferred — fast) or added to existing integration coverage.
- Whitespace-handling edge cases in the detector (leading/trailing/multiple spaces) — handle robustly; not a user decision.

### Deferred Ideas (OUT OF SCOPE)
- **Minimum-interval floor / sub-second rate guard** — explicitly declined (D-06). Would be its own scoped validation-policy change, not part of v8.0.0.
- Do NOT research scheduling policy, rate limiting, or sub-second guards.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| CRON-01 | Orchestrator fires on 6-field cron; sub-minute next-occurrence + interval math in UTC | `CronInterval.NextOccurrence`/`IntervalSeconds` (lines 22, 31) hardcode `CronFormat.Standard` today — both must route through the shared detector → `CronFormat.IncludeSeconds` for 6-token. Cronos 0.13.0 `CronFormat.IncludeSeconds` is the verified 6-field enum value. `IntervalSeconds` is granularity-agnostic — once the format resolves, the delta math already yields 30. |
| CRON-02 | Validator accepts 6-field; 5-field still accepted | `BeValidStandardCron` duplicated in `WorkflowCreateDtoValidator` (line 60) + `WorkflowUpdateDtoValidator` (line 109); both call bare `CronExpression.Parse(expr)` (defaults to `Standard`). Route both through shared detector to resolve `CronFormat`, parse once, catch `CronFormatException` for genuinely-malformed. Update message (D-11). |
</phase_requirements>

## Summary

This is a ~5-file surgical change with zero new dependencies. The CONTEXT.md is exhaustive and every factual claim in it was verified against the live source this session — file paths, line numbers, the Cronos API shape, the dependency firewall, and the absence of existing tests all hold. No drift detected.

The mechanism: a new pure string helper in `Messaging.Contracts` decides format by token count (no Cronos), and both call sites (`CronInterval` in Orchestrator, the two validators in BaseApi.Service) consume that single rule to select `CronFormat.Standard` vs `CronFormat.IncludeSeconds`, then run their own local `Cronos.Parse`. This mirrors the established Phase 21 `L2ProjectionKeys` anti-desync hoist precisely — the precedent lives in the same leaf assembly and even has a test file (`L2ProjectionKeysTests.cs`) to model the detector test on.

**Primary recommendation:** Add `Messaging.Contracts/CronFieldForm.cs` (pure token-count helper, no Cronos). Wire both `CronInterval` methods and both validators' `BeValidStandardCron` through it. Add 3 test surfaces: extend `CronIntervalTests` (D-08), add a detector test in `tests/BaseApi.Tests/Contracts/` (D-10), add validator tests in `tests/BaseApi.Tests/Validation/` (D-09). Update both validator error messages (D-11). No new package, no `.csproj` edits.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Cron format detection (token count) | Shared Contracts leaf (`Messaging.Contracts`) | — | Only assembly both Orchestrator + BaseApi.Service reference; firewall D-08 blocks `BaseApi.Core`. Pure string logic, no parser. |
| Fire-time math (next-occurrence, interval) | Orchestrator (`CronInterval`) | — | Scheduler-side; consumes detector then parses locally with Cronos. |
| Cron validation (accept/reject) | API (`BaseApi.Service` validators) | — | Runs on POST/PUT /api/v1/workflows via FluentValidation → 422/400 mapping. Consumes detector then parses locally. |

## CONTEXT.md Verification Results

**All claims VERIFIED. No drift.** [VERIFIED: live source read 2026-06-14]

| Claim | Result |
|-------|--------|
| `CronInterval.NextOccurrence` hardcodes `CronFormat.Standard` at line 22 | ✅ Exact — `CronExpression.Parse(cron, CronFormat.Standard).GetNextOccurrence(nowUtc)` |
| `CronInterval.IntervalSeconds` hardcodes `CronFormat.Standard` at line 31 | ✅ Exact — `var expr = CronExpression.Parse(cron, CronFormat.Standard);` |
| UTC contract documented in class summary, `nowUtc` must be `Kind=Utc` | ✅ Pitfall 3 documented in XML summary (lines 9-13); throws on non-UTC |
| `using Cronos;` import shape | ✅ Line 1; type is `Cronos.CronExpression` |
| `BeValidStandardCron` duplicated in Create (~line 60) + Update (~line 109) | ✅ Create line 60, Update line 109 — byte-identical bodies |
| Both use `CronFormat.Standard` + catch `CronFormatException` | ✅ Both call bare `CronExpression.Parse(expr)` (defaults Standard) inside try/catch `CronFormatException` |
| VALID-19 rule + user message at ~lines 48-51 / 103-106 | ✅ Create rule 48-51 (msg line 51), Update rule 103-106 (msg line 106). Message: *"CronExpression must be a valid 5-field cron expression (e.g., '0 0 * * *')."* |
| `Messaging.Contracts` has NO Cronos dependency | ✅ `.csproj` is pure-POCO, no `ItemGroup`, no `PackageReference` at all |
| Orchestrator NO ProjectReference to `BaseApi.*`; refs `Messaging.Contracts` | ✅ Only `BaseConsole.Core` + `Messaging.Contracts` (lines 53-54); firewall documented in csproj comment (D-08) |
| BaseApi.Service references `Messaging.Contracts` | ✅ ProjectReference line 37 — so both call sites can reach the new helper |
| `CronIntervalTests.cs` exists, uses `FakeTimeProvider`, pins UTC | ✅ `PinnedUtc = new(2026,1,1,0,0,30, DateTimeKind.Utc)`, `FakeTimeProvider` from `Microsoft.Extensions.Time.Testing` |
| No validator unit test for the cron rule today | ✅ No `Workflow*Validator*` test file exists; `tests/BaseApi.Tests/Validation/` has only Base/Test-DTO + endpoint discovery tests |
| Cronos referenced by BOTH Orchestrator + BaseApi.Service | ✅ Orchestrator csproj line 42; BaseApi.Service csproj line 50 |
| `CronFormat.IncludeSeconds` correct 6-field enum in installed version | ✅ Cronos **0.13.0** pinned in `Directory.Packages.props` line 91. `IncludeSeconds` is the canonical 6-field flag in Cronos. [VERIFIED: CPM pin] |

## Standard Stack

**No new packages. No `.csproj` changes.** All required dependencies already present.

| Library | Version | Purpose | Status |
|---------|---------|---------|--------|
| Cronos | 0.13.0 | 5/6-field cron parse + next-occurrence | Already referenced by Orchestrator + BaseApi.Service [VERIFIED: Directory.Packages.props:91] |
| FluentValidation | 12.1.1 | `Must(...)`/`When(...)`/`WithMessage(...)` validator rule | Already used by both validators [VERIFIED: Directory.Packages.props:73] |
| Microsoft.Extensions.TimeProvider.Testing | 8.10.0 | `FakeTimeProvider` UTC pinning in tests | Already used in `CronIntervalTests` [VERIFIED: Directory.Packages.props:128] |
| xunit.v3 | 3.2.2 | Test framework (`[Fact]`/`[Theory]`) | Already the test framework [VERIFIED: Directory.Packages.props:121] |

### Cronos API shape (verified against installed usage)
- `CronExpression.Parse(string)` → defaults to `CronFormat.Standard` (5-field). [VERIFIED: BeValidStandardCron usage]
- `CronExpression.Parse(string, CronFormat)` → explicit format. [VERIFIED: CronInterval usage]
- `CronFormat.Standard` = 5 fields; `CronFormat.IncludeSeconds` = 6 fields (leading seconds token). [VERIFIED: enum present in 0.13.0; Standard already in use]
- `GetNextOccurrence(DateTime)` → returns `DateTime?` with `Kind=Utc`; **throws if input is not `DateTimeKind.Utc`**. [VERIFIED: documented Pitfall 3 + test pins UTC]
- Invalid expression → throws `CronFormatException` (already caught by validators). [VERIFIED: catch block]

## Architecture Patterns

### Data flow (after change)

```
                          POST/PUT /api/v1/workflows          Orchestrator scheduler
                                   │                            (WorkflowFireJob etc.)
                                   ▼                                    │
                    WorkflowCreate/UpdateDtoValidator                   ▼
                          BeValidStandardCron(expr)              CronInterval.{NextOccurrence,
                                   │                                     IntervalSeconds}(cron, nowUtc)
                                   │                                    │
                                   └────────────┬───────────────────────┘
                                                ▼
                          Messaging.Contracts.CronFieldForm          ◄── ONE shared rule
                          IsSecondsForm(expr)  (token count only,         (no Cronos here)
                          NO Cronos dependency)
                                                │
                          returns: 6 → seconds, 5 → standard, other → invalid
                                                │
                  ┌─────────────────────────────┴─────────────────────────────┐
                  ▼  (validator side)                                          ▼ (scheduler side)
        resolve CronFormat → local Cronos.Parse              resolve CronFormat → local Cronos.Parse
        in try/catch CronFormatException                     compute next-occurrence / interval (UTC)
        → accept/reject (422/400)                            → fire time
```

The detector is the single hinge guaranteeing **validator-accepts ⟺ scheduler-parses-the-same-format**. The Cronos parse itself stays local to each side (keeps Cronos out of the contracts leaf).

### Pattern: Anti-desync single-source-of-truth hoist (PRECEDENT EXISTS)

`Messaging.Contracts/Projections/L2ProjectionKeys.cs` (Phase 21) is the exact precedent — a `public static class` of pure string logic in the contracts leaf, consumed by both a BaseApi.Service writer and an Orchestrator reader so they can never desynchronize. Model `CronFieldForm` on it directly, including the XML-doc convention citing the decisions (D-03/D-04/D-05). Its test `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs` is the template for the detector test (D-10).

**Recommended detector shape** (discretion — `bool` reads cleanest):

```csharp
// Source: model on Messaging.Contracts/Projections/L2ProjectionKeys.cs (Phase 21 precedent)
namespace Messaging.Contracts;

/// <summary>
/// Single source of truth (D-03/D-05) for cron field-count → format selection. Pure string
/// logic — NO Cronos dependency (keeps the contracts leaf parser-free). Both the Orchestrator
/// scheduler (CronInterval) and the BaseApi.Service validators consume this ONE rule (D-04) so
/// "validator-accepts ⟺ scheduler-parses-the-same-format" can never desynchronize.
/// </summary>
public static class CronFieldForm
{
    /// <summary>true → 6-field seconds form (CronFormat.IncludeSeconds);
    /// false → 5-field standard form (CronFormat.Standard).</summary>
    public static bool IsSecondsForm(string expr) => FieldCount(expr) == 6;

    /// <summary>true when the trimmed expression has a usable 5- or 6-field count.
    /// Callers reject (return invalid) when this is false BEFORE touching Cronos (D-01/D-02).</summary>
    public static bool IsValidFieldCount(string expr) =>
        FieldCount(expr) is 5 or 6;

    private static int FieldCount(string expr) =>
        string.IsNullOrWhiteSpace(expr)
            ? 0
            : expr.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
```

`Split(null, RemoveEmptyEntries)` collapses leading/trailing/multiple whitespace robustly (discretion item on whitespace). Exact name/signature is discretion — a small enum (`Standard|Seconds|Invalid`) is an equally valid shape.

### Call-site wiring (both sides map the bool to CronFormat)

```csharp
// CronInterval (Orchestrator) — replace both hardcoded CronFormat.Standard
var format = CronFieldForm.IsSecondsForm(cron) ? CronFormat.IncludeSeconds : CronFormat.Standard;
CronExpression.Parse(cron, format) ...

// BeValidStandardCron (both validators) — resolve format up front, then ONE guarded parse
if (!CronFieldForm.IsValidFieldCount(expr)) return false;   // reject non-5/6 without exception (D-02)
var format = CronFieldForm.IsSecondsForm(expr) ? CronFormat.IncludeSeconds : CronFormat.Standard;
try { CronExpression.Parse(expr, format); return true; }
catch (CronFormatException) { return false; }               // genuinely-malformed 6-token still rejected
```

### Anti-Patterns to Avoid
- **Exception-as-control-flow (D-02 violation):** Do NOT `try Parse(Standard) catch → try Parse(IncludeSeconds)`. Token count decides up front.
- **Adding Cronos to `Messaging.Contracts` (D-03/D-05 violation):** The detector is pure string logic. Never add a Cronos `PackageReference` to the contracts leaf.
- **Adding a minimum-interval floor (D-06 violation):** Out of scope.
- **Centralizing the Cronos.Parse itself:** Only the *detection rule* is hoisted; each side parses locally.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Cron field parsing / next-occurrence | Custom cron parser | Cronos `CronExpression.Parse` + `GetNextOccurrence` | DST/leap-year/edge-case correctness; already pinned + in use |
| UTC-pinned test clock | Manual `DateTime.UtcNow` stub | `FakeTimeProvider` | Deterministic, already the established test seam |

**Key insight:** The only thing genuinely hand-built here is the **token-count split** — and that is deliberately hand-built (not delegated to Cronos) precisely so the contracts leaf stays parser-free (D-03).

## Common Pitfalls

### Pitfall 1: Non-UTC DateTime into Cronos (carried, unchanged)
**What goes wrong:** `GetNextOccurrence` throws if `nowUtc.Kind != DateTimeKind.Utc`.
**How to avoid:** Preserve the existing contract — callers pass `timeProvider.GetUtcNow().UtcDateTime`. The 6-field change does NOT touch this. Tests must pin `DateTimeKind.Utc` (existing `PinnedUtc` already does).

### Pitfall 2: Wrong field-count interpretation
**What goes wrong:** Cronos `IncludeSeconds` expects the **seconds** token first (`sec min hour day month dow`), so `*/30 * * * * *` = "every 30 **seconds**". A 6-token expression parsed as Standard would throw; a 5-token parsed as IncludeSeconds would also throw. The token count must drive the format.
**How to avoid:** Detector returns 6 → IncludeSeconds before any parse. Never let count and format diverge.

### Pitfall 3: Forgetting the SECOND validator
**What goes wrong:** `BeValidStandardCron` is duplicated in Create AND Update. Fixing one leaves a regression in the other.
**How to avoid:** Route BOTH through `CronFieldForm`. D-09 mandates tests on both. The discretion note allows collapsing the duplication.

### Pitfall 4: Stale error message
**What goes wrong:** Message still says "valid 5-field cron expression" after 6-field is accepted — misleads users.
**How to avoid:** D-11 — update both messages (Create line 51, Update line 106) to reflect 5- or 6-field.

## Runtime State Inventory

> Code/config-only change. No runtime state migration.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | None — cron expressions are stored as plain strings in `WorkflowEntity.CronExpression`; format is re-detected at parse time, not stored. No schema/key change. | None |
| Live service config | None — verified: no external service stores cron format separately. | None |
| OS-registered state | None — Quartz scheduling is in-process; no OS task registration. | None |
| Secrets/env vars | None. | None |
| Build artifacts | None — no new package, no `.csproj` change, so no stale egg-info/binary concern. | None |

**Canonical question — after every file is updated, what runtime systems still cache the old behavior?** None. Existing stored 5-field crons re-parse identically (5-token → `CronFormat.Standard`, unchanged path). No migration.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 3.2.2 (`[Fact]`/`[Theory]`/`[InlineData]`) |
| Config file | `tests/BaseApi.Tests/xunit.runner.json` |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~CronInterval|FullyQualifiedName~CronFieldForm|FullyQualifiedName~WorkflowCreateDtoValidator|FullyQualifiedName~WorkflowUpdateDtoValidator"` |
| Full suite command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| 0-warning build (SC#4) | `dotnet build -c Release` and `dotnet build -c Debug` (TreatWarningsAsErrors inherited from Directory.Build.props — build fails on any warning) |

### Success Criterion → Test Map (this is what VALIDATION.md derives from)

| SC | Behavior | Test Type | Exact Assertion | Test File | Exists? |
|----|----------|-----------|-----------------|-----------|---------|
| SC#1 | Sub-minute interval math, UTC | unit (FakeTimeProvider) | `CronInterval.IntervalSeconds("*/30 * * * * *", pinnedUtc) == 30` | `tests/BaseApi.Tests/Orchestrator/CronIntervalTests.cs` (extend, D-08) | ❌ Wave 0 (new `[Fact]`) |
| SC#1 | Next-occurrence strictly-future + Kind=Utc | unit | `var n = CronInterval.NextOccurrence("*/30 * * * * *", nowUtc); Assert.NotNull(n); Assert.Equal(DateTimeKind.Utc, n!.Value.Kind); Assert.True(n.Value > nowUtc)` | same file | ❌ Wave 0 |
| SC#1 (regression) | 5-field interval still correct | unit | existing `IntervalSeconds("*/5 * * * *", ...) == 300` | same file | ✅ exists — retain |
| SC#2 | 6-field accepted by Create validator | unit | `new WorkflowCreateDtoValidator().Validate(dto with CronExpression="*/30 * * * * *").IsValid == true` | `tests/BaseApi.Tests/Validation/WorkflowCronValidatorTests.cs` (new, D-09) | ❌ Wave 0 |
| SC#2 | 6-field accepted by Update validator | unit | same assertion against `WorkflowUpdateDtoValidator` | same file | ❌ Wave 0 |
| SC#3 | 5-field still accepted (both validators, no regression) | unit | `Validate(... CronExpression="0 0 * * *").IsValid == true` for Create + Update | same file | ❌ Wave 0 |
| SC#2/3 (negative) | Malformed / wrong field-count rejected | unit | `Validate(... CronExpression="not a cron").IsValid == false`; `"* * *".IsValid == false` (4-token rejected) | same file | ❌ Wave 0 |
| D-10 | Detector rule pinned | unit | `CronFieldForm.IsSecondsForm("*/30 * * * * *") == true`; `IsSecondsForm("0 0 * * *") == false`; `IsValidFieldCount("* * *") == false`; whitespace `" */30   *  * * * "` → seconds | `tests/BaseApi.Tests/Contracts/CronFieldFormTests.cs` (new, model on `L2ProjectionKeysTests.cs`) | ❌ Wave 0 |
| SC#4 | 0-warning build + green hermetic suite | build + suite | `dotnet build -c Release` exit 0, `dotnet build -c Debug` exit 0, full `dotnet test` green | — | gate |

### Sampling Rate
- **Per task commit:** Quick run command above (cron-scoped filter, < 30s).
- **Per wave merge:** Full `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj`.
- **Phase gate:** Release + Debug build 0-warning, full suite green, before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] `Messaging.Contracts/CronFieldForm.cs` — the shared detector (D-03). Net-new production file.
- [ ] `tests/BaseApi.Tests/Contracts/CronFieldFormTests.cs` — detector unit test (D-10). Model on `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs`.
- [ ] `tests/BaseApi.Tests/Validation/WorkflowCronValidatorTests.cs` — validator unit tests, Create + Update (D-09). No validator cron test exists today.
- [ ] Extend `tests/BaseApi.Tests/Orchestrator/CronIntervalTests.cs` — `*/30` case (D-08).
- [ ] No framework install needed — xunit.v3 + FakeTimeProvider already present.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK 8 | build + test | ✓ (assumed — repo targets net8.0) | 8.0.x | — |
| Cronos | CronInterval + validators | ✓ already referenced | 0.13.0 | — |
| FakeTimeProvider | UTC test pinning | ✓ already referenced | 8.10.0 | — |

No external services, DB, or broker required — these are pure unit/hermetic tests. No `dotnet test` integration container needed for this phase's validations.

## Security Domain

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V5 Input Validation | yes | FluentValidation `BeValidStandardCron` — already the gate. Change widens accepted set (5+6 field) but still rejects malformed via `CronFormatException`. No injection surface (cron string is parsed by Cronos, never eval'd/shelled). |
| V6 Cryptography | no | — |
| V2/V3/V4 Auth/Session/Access | no | Phase touches validation rule + scheduler math only; no auth/session/access-control surface. |

**Threat note:** D-06 declines a sub-second floor. A `* * * * * *` (1s) cron is now acceptable — this is an intentional product decision, not a vulnerability in scope. Any future DoS-via-fast-cron concern is the deferred minimum-interval-floor change, explicitly out of scope.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Cronos 0.13.0 `CronFormat.IncludeSeconds` interprets the leading token as **seconds** (so `*/30 * * * * *` = every 30s) | Standard Stack / Pitfall 2 | LOW — this is Cronos's documented and only 6-field mode; `Standard` is already verified in-use. If wrong, the `IntervalSeconds == 30` test fails loudly at Wave 0 (fail-fast, no silent escape). |

All other claims are `[VERIFIED]` against live source this session. A1 is the single item resting on Cronos library behavior rather than this repo's code — and it is directly pinned by the SC#1 test, so it self-verifies during implementation.

## Open Questions

1. **Helper name/signature (`bool` vs enum)** — discretion item; recommended `CronFieldForm.IsSecondsForm`/`IsValidFieldCount` (bool pair) for readability. No blocker.
2. **Collapse the duplicated `BeValidStandardCron`?** — discretion. Both validators may share a single internal helper or keep mirrored copies, provided both route through `CronFieldForm` and both are tested (D-09). No blocker.

## Sources

### Primary (HIGH confidence)
- Live source (read 2026-06-14): `src/Orchestrator/Scheduling/CronInterval.cs`, `src/BaseApi.Service/Features/Workflow/WorkflowDtoValidator.cs`, `src/Orchestrator/Orchestrator.csproj`, `src/Messaging.Contracts/Messaging.Contracts.csproj`, `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs`, `tests/BaseApi.Tests/Orchestrator/CronIntervalTests.cs`, `Directory.Packages.props`.
- `.planning/REQUIREMENTS.md` §CRON — CRON-01, CRON-02.
- `.planning/ROADMAP.md` Phase 63 (lines 853-862) — goal + SC#1-4.
- `.planning/phases/63-seconds-granularity-cron/63-CONTEXT.md` — D-01…D-11.

### Tertiary (LOW confidence)
- Cronos `IncludeSeconds` seconds-first ordering (A1) — training knowledge, self-verified by SC#1 test.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — every package pinned + verified in-use; zero new deps.
- Architecture: HIGH — Phase 21 `L2ProjectionKeys` precedent is exact + present in-repo.
- Pitfalls: HIGH — UTC + duplicate-validator + message-staleness all confirmed in source.
- Cronos seconds semantics: MEDIUM-HIGH — A1 self-verifies at Wave 0.

**Research date:** 2026-06-14
**Valid until:** 30 days (stable — pinned deps, no fast-moving surface)

## RESEARCH COMPLETE

**Phase:** 63 - Seconds-Granularity Cron
**Confidence:** HIGH

### Key Findings
- Every CONTEXT.md claim VERIFIED against live source — no drift (file paths, line numbers, Cronos API, D-08 firewall, absent tests all hold).
- Zero new packages, zero `.csproj` edits — Cronos 0.13.0, FluentValidation, FakeTimeProvider all already referenced by the right projects.
- Exact in-repo precedent for the anti-desync hoist: `Messaging.Contracts/Projections/L2ProjectionKeys.cs` (Phase 21) + its test `L2ProjectionKeysTests.cs` — model `CronFieldForm` + its test on these.
- Wave 0 produces 1 new production file (`CronFieldForm.cs`) + 2 new test files + 1 extended test file; both duplicated validators must be wired + their messages updated.
- Validation Architecture maps all 4 SCs to concrete falsifiable assertions (`IntervalSeconds("*/30 * * * * *")==30`, Kind=Utc strictly-future, both-validator accept of 5+6 field, 0-warning Release+Debug build).

### File Created
`.planning/phases/63-seconds-granularity-cron/63-RESEARCH.md`

### Confidence Assessment
| Area | Level | Reason |
|------|-------|--------|
| Standard Stack | HIGH | All deps pinned + verified in-use |
| Architecture | HIGH | Exact in-repo precedent (Phase 21) |
| Pitfalls | HIGH | UTC + dual-validator + message confirmed in source |

### Open Questions
Both are discretion items (helper signature; collapse duplicate validator) — neither blocks planning.

### Ready for Planning
Research complete. Planner can derive PLAN.md + VALIDATION.md directly from the Validation Architecture SC→test map.
