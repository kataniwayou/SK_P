# Phase 63: Seconds-Granularity Cron - Pattern Map

**Mapped:** 2026-06-14
**Files analyzed:** 6 (2 new product, 1 modified product, 2 new test, 1 modified test)
**Analogs found:** 6 / 6 (all exact or role-match — every analog lives in-repo and was read this session)

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| **NEW** `src/Messaging.Contracts/CronFieldForm.cs` | utility (pure detector, contracts leaf) | transform (string → field count → bool) | `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` | exact (same assembly, same anti-desync hoist pattern, Phase 21 precedent) |
| **MOD** `src/Orchestrator/Scheduling/CronInterval.cs` | service (scheduler fire-time math) | transform (cron+nowUtc → DateTime?/int) | itself (in-place rewire of `NextOccurrence`/`IntervalSeconds`) | exact (self — extract current bodies) |
| **MOD** `src/BaseApi.Service/Features/Workflow/WorkflowDtoValidator.cs` | validator (FluentValidation rule) | request-response (DTO → accept/reject) | itself (in-place rewire of duplicated `BeValidStandardCron`) | exact (self — extract both current bodies) |
| **NEW** `tests/BaseApi.Tests/Contracts/CronFieldFormTests.cs` | test (detector unit) | transform-under-test | `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs` | exact (test of the precedent class) |
| **MOD** `tests/BaseApi.Tests/Orchestrator/CronIntervalTests.cs` | test (scheduler unit) | transform-under-test | itself (extend with `*/30` `[Fact]`) | exact (self — mirror existing `[Fact]`) |
| **NEW** `tests/BaseApi.Tests/Validation/WorkflowCronValidatorTests.cs` | test (validator unit) | request-response-under-test | `tests/BaseApi.Tests/Validation/BaseDtoValidatorRuleTests.cs` | role-match (FluentValidation instantiate-and-`Validate`; no cron validator test exists today) |

---

## Pattern Assignments

### NEW `src/Messaging.Contracts/CronFieldForm.cs` (utility, transform)

**Analog:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — the SAME-assembly Phase 21 anti-desync hoist. Mirror its class shape exactly: `public static class`, no `using` for any third-party parser (**Cronos MUST NOT appear** — D-03), XML-doc summary that cites the governing decisions, expression-bodied static helpers.

**Class shape to copy** (`L2ProjectionKeys.cs:1-31`):
```csharp
namespace Messaging.Contracts.Projections;   // CronFieldForm may sit in Messaging.Contracts or .Projections (discretion D-03)

/// <summary>
/// Single source of truth for the L2 (Redis) projection key formats ...
/// Hoisted into the Messaging.Contracts leaf (Phase 21) so the writer
/// (<c>BaseApi.Service.RedisProjectionKeys</c>) and reader (<c>Orchestrator.OrchestratorL2Keys</c>)
/// consume ONE shape — a future GUID-format/suffix change cannot silently desynchronize them.
/// ...
/// </summary>
public static class L2ProjectionKeys
{
    public const string Prefix = "skp:";

    public static string ParentIndex() => Prefix;
    public static string Root(Guid workflowId) => $"{Prefix}{workflowId:D}";
```

Key conventions to replicate in `CronFieldForm`:
- `public static class` with expression-bodied `public static` members.
- XML `<summary>` that names the decisions it pins (cite D-03/D-04/D-05 + the "validator-accepts ⟺ scheduler-parses-the-same-format" invariant — see the RESEARCH.md recommended shape, lines 133-159).
- ZERO third-party `using` — the file uses only `System` / `string` APIs. **No `using Cronos;`.**

**Recommended body** (from RESEARCH.md §"Recommended detector shape", lines 143-158 — discretion-confirmed; `bool` pair reads cleanest):
```csharp
public static bool IsSecondsForm(string expr) => FieldCount(expr) == 6;

public static bool IsValidFieldCount(string expr) => FieldCount(expr) is 5 or 6;

private static int FieldCount(string expr) =>
    string.IsNullOrWhiteSpace(expr)
        ? 0
        : expr.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
```
`Split(null, RemoveEmptyEntries)` collapses leading/trailing/multiple whitespace robustly (discretion item, D-01/D-02 whitespace handling). Exact name/signature is discretion — a small `Standard|Seconds|Invalid` enum is equally valid.

**Constraint reminder:** Do NOT add a Cronos `PackageReference` to `src/Messaging.Contracts/Messaging.Contracts.csproj` (verified pure-POCO today, RESEARCH.md:72). This file is the structural guarantee the contracts leaf stays parser-free.

---

### MOD `src/Orchestrator/Scheduling/CronInterval.cs` (service, transform)

**Analog:** itself. Rewire both methods from hardcoded `CronFormat.Standard` to detector-resolved format. Keep the UTC contract + XML doc (update the doc to say "5- or 6-field" instead of "5-field").

**Current imports** (`CronInterval.cs:1-3`):
```csharp
using Cronos;

namespace Orchestrator.Scheduling;
```
Add `using Messaging.Contracts.Projections;` (or whichever namespace `CronFieldForm` lands in). **Do NOT add any `BaseApi.*` using** — D-08 firewall (Orchestrator refs only `BaseConsole.Core` + `Messaging.Contracts`, RESEARCH.md:73).

**Current `NextOccurrence` body** (`CronInterval.cs:21-22`) — the hardcoded format to replace:
```csharp
public static DateTime? NextOccurrence(string cron, DateTime nowUtc) =>
    CronExpression.Parse(cron, CronFormat.Standard).GetNextOccurrence(nowUtc);
```

**Current `IntervalSeconds` body** (`CronInterval.cs:29-35`) — the hardcoded format to replace:
```csharp
public static int IntervalSeconds(string cron, DateTime nowUtc)
{
    var expr = CronExpression.Parse(cron, CronFormat.Standard);
    var n1 = expr.GetNextOccurrence(nowUtc);
    var n2 = n1 is { } a ? expr.GetNextOccurrence(a) : null;
    return (n1, n2) is ({ } x, { } y) ? (int)Math.Round((y - x).TotalSeconds) : 0;
}
```

**Rewire pattern** (RESEARCH.md §"Call-site wiring", lines 166-168) — resolve format from the shared detector, then parse locally:
```csharp
var format = CronFieldForm.IsSecondsForm(cron) ? CronFormat.IncludeSeconds : CronFormat.Standard;
CronExpression.Parse(cron, format) ...
```
Apply to BOTH methods. The interval delta-math (n1/n2/Round) is granularity-agnostic — unchanged once `format` resolves (yields 30 for `*/30 * * * * *`). UTC pitfall (`nowUtc` must be `Kind=Utc`) is preserved — do not touch the input contract (D-07).

---

### MOD `src/BaseApi.Service/Features/Workflow/WorkflowDtoValidator.cs` (validator, request-response)

**Analog:** itself. `BeValidStandardCron` is **byte-identical** in `WorkflowCreateDtoValidator` (line 60) and `WorkflowUpdateDtoValidator` (line 109). Both must route through `CronFieldForm`. Both user-facing messages (lines 51 / 106) must be updated (D-11). Pitfall 3: fixing only one leaves a regression — fix BOTH (RESEARCH.md:202-204).

**Current imports** (`WorkflowDtoValidator.cs:1-3`):
```csharp
using BaseApi.Core.Validation;
using Cronos;
using FluentValidation;
```
Add `using Messaging.Contracts.Projections;` (BaseApi.Service already references `Messaging.Contracts` — RESEARCH.md:74).

**Current rule shape — Create** (`WorkflowDtoValidator.cs:48-51`) and **Update** (`103-106`) are identical except the validator class. This is the `RuleFor(...).Must(...).When(...).WithMessage(...)` shape to preserve, updating only the message string:
```csharp
RuleFor(x => x.CronExpression)
    .Must(BeValidStandardCron)
    .When(x => !string.IsNullOrWhiteSpace(x.CronExpression))
    .WithMessage("CronExpression must be a valid 5-field cron expression (e.g., '0 0 * * *').");
```
→ D-11: change message to reflect 5- **or** 6-field, e.g. `"CronExpression must be a valid 5- or 6-field cron expression (e.g., '0 0 * * *' or '*/30 * * * * *')."` — in BOTH validators.

**Current `BeValidStandardCron` body** (Create `60-72`, Update `109-121` — byte-identical):
```csharp
private static bool BeValidStandardCron(string? expr)
{
    if (string.IsNullOrWhiteSpace(expr)) return true;
    try
    {
        CronExpression.Parse(expr);   // defaults to CronFormat.Standard (5 fields)
        return true;
    }
    catch (CronFormatException)
    {
        return false;
    }
}
```

**Rewire pattern** (RESEARCH.md §"Call-site wiring", lines 171-174) — resolve format up front (reject non-5/6 WITHOUT exception, D-02), then ONE guarded parse:
```csharp
private static bool BeValidStandardCron(string? expr)
{
    if (string.IsNullOrWhiteSpace(expr)) return true;          // null/blank is valid (ENTITY-08), unchanged
    if (!CronFieldForm.IsValidFieldCount(expr)) return false;  // reject non-5/6 up front — no exception (D-02)
    var format = CronFieldForm.IsSecondsForm(expr) ? CronFormat.IncludeSeconds : CronFormat.Standard;
    try { CronExpression.Parse(expr, format); return true; }
    catch (CronFormatException) { return false; }              // genuinely-malformed 6-token still rejected
}
```
**Discretion (D, RESEARCH.md:292):** the two duplicated copies may be collapsed into one shared private helper, provided behavior is identical across both validators and both are still tested. Method name may be renamed (e.g. `BeValidCron`) but is not required.

---

### NEW `tests/BaseApi.Tests/Contracts/CronFieldFormTests.cs` (test, detector)

**Analog:** `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs` — the test of the precedent class. Mirror its structure: `sealed class`, `[Fact]`/`[Theory]` per rule, direct static-method assertion, no DI/HTTP.

**Imports + class shape to copy** (`L2ProjectionKeysTests.cs:1-17`):
```csharp
using System;
using Messaging.Contracts.Projections;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration.Projection;   // new file uses BaseApi.Tests.Contracts

/// <summary>
/// Pins the shared <see cref="L2ProjectionKeys"/> output ...
/// </summary>
[Trait("Phase", "22")]    // new file: [Trait("Phase", "63")] (optional, follows convention)
public sealed class L2ProjectionKeysTests
{
```

**`[Fact]` assertion style to mirror** (`L2ProjectionKeysTests.cs:24-28`, expression-bodied variant `45-48`):
```csharp
[Fact]
public void ParentIndex_Returns_Bare_Prefix()
{
    Assert.Equal("skp:", L2ProjectionKeys.ParentIndex());
}
```
This file currently uses per-case `[Fact]`s (no `[InlineData]`). For the detector, a `[Theory]/[InlineData]` table is the cleaner fit (and is the style of `BaseDtoValidatorRuleTests`, lines 49-72) — either is acceptable. Pin (D-10 / RESEARCH.md:246):
- `CronFieldForm.IsSecondsForm("*/30 * * * * *") == true`
- `CronFieldForm.IsSecondsForm("0 0 * * *") == false`
- `CronFieldForm.IsValidFieldCount("* * *") == false` (4-or-fewer token rejected)
- whitespace `" */30   *  * * * "` → seconds (robust trim/collapse).

---

### MOD `tests/BaseApi.Tests/Orchestrator/CronIntervalTests.cs` (test, scheduler)

**Analog:** itself. Add a `*/30 * * * * *` `[Fact]` mirroring the existing ones; reuse the `FakeTimeProvider` + `PinnedUtc` seam. Retain all 5-field cases (no regression, D-08).

**Imports + UTC pin to reuse** (`CronIntervalTests.cs:1-17`):
```csharp
using Microsoft.Extensions.Time.Testing;
using Orchestrator.Scheduling;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

public sealed class CronIntervalTests
{
    // 2026-01-01T00:00:30Z — a fixed UTC instant 30s past the minute ...
    private static readonly DateTime PinnedUtc = new(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc);
```

**Existing `[Fact]` to mirror — interval** (`CronIntervalTests.cs:19-27`):
```csharp
[Fact]
public void IntervalIsDeltaSecondsBetweenNextTwoOccurrences()
{
    var time = new FakeTimeProvider(new DateTimeOffset(PinnedUtc));

    var interval = CronInterval.IntervalSeconds("*/5 * * * *", time.GetUtcNow().UtcDateTime);

    Assert.Equal(300, interval); // every-5-minutes -> 300s between the next two fires
}
```

**Existing `[Fact]` to mirror — next-occurrence + UTC** (`CronIntervalTests.cs:29-40`):
```csharp
[Fact]
public void NextOccurrence_IsStrictlyFuture_AndUtc()
{
    var time = new FakeTimeProvider(new DateTimeOffset(PinnedUtc));
    var nowUtc = time.GetUtcNow().UtcDateTime;

    var next = CronInterval.NextOccurrence("*/5 * * * *", nowUtc);

    Assert.NotNull(next);
    Assert.Equal(DateTimeKind.Utc, next!.Value.Kind);
    Assert.True(next.Value > nowUtc, "next occurrence must be strictly in the future");
}
```

**New case to add** (D-08 / RESEARCH.md:239-240): copy the two `[Fact]`s above, swap the cron to `"*/30 * * * * *"`, assert `IntervalSeconds(...) == 30` and `NextOccurrence(...)` not-null, `Kind=Utc`, strictly-future. Same `PinnedUtc` + `FakeTimeProvider` setup.

---

### NEW `tests/BaseApi.Tests/Validation/WorkflowCronValidatorTests.cs` (test, validator)

**Analog:** `tests/BaseApi.Tests/Validation/BaseDtoValidatorRuleTests.cs` — the in-repo FluentValidation unit-test pattern (no cron validator test exists today, D-09). Mirror its instantiate-validator → `Validate(dto)` → assert `IsValid` / per-property error membership; private factory helpers for validator + DTO.

**Imports + factory pattern to copy** (`BaseDtoValidatorRuleTests.cs:1-17`):
```csharp
using BaseApi.Core.Validation;
using Xunit;

namespace BaseApi.Tests.Validation;

/// <summary>
/// Verifies the ... rules by directly instantiating the validator and
/// asserting <c>ValidationResult.IsValid</c> + per-property error membership.
/// No DI, no HTTP wire — pure unit test.
/// </summary>
public sealed class BaseDtoValidatorRuleTests
{
    private static BaseDtoValidator<TestUpdateDto> NewValidator() => new();

    private static TestUpdateDto Dto(string name = "ok", string version = "1.0.0", string? description = null, string note = "n")
        => new(name, version, description, note);
```
For the new file, import `using BaseApi.Service.Features.Workflow;` (for the validators + DTOs) instead of `BaseApi.Core.Validation`.

**Accept assertion style** (`BaseDtoValidatorRuleTests.cs:38-45`):
```csharp
[Fact]
public void Test_Name_200Chars_Accepted()
{
    var name200 = new string('a', 200);
    var result = NewValidator().Validate(Dto(name: name200));
    Assert.DoesNotContain(result.Errors, e => e.PropertyName == "Name");
}
```

**Reject assertion + `[Theory]/[InlineData]` table style** (`BaseDtoValidatorRuleTests.cs:49-61`):
```csharp
[Theory]
[InlineData("")]
[InlineData("01.0.0")]   // leading zero
...
public void Test_Version_BadShape_Rejected(string version)
{
    var result = NewValidator().Validate(Dto(version: version));
    Assert.False(result.IsValid);
    Assert.Contains(result.Errors, e => e.PropertyName == "Version");
}
```

**DTO constructor shape** (verified `src/BaseApi.Service/Features/Workflow/WorkflowDtos.cs:20-26` and `34-40` — both positional records, IDENTICAL signature):
```csharp
public sealed record WorkflowCreateDto(
    string Name, string Version, string? Description,
    List<Guid> EntryStepIds, List<Guid>? AssignmentIds, string? CronExpression) : IBaseDto;
public sealed record WorkflowUpdateDto(
    string Name, string Version, string? Description,
    List<Guid> EntryStepIds, List<Guid>? AssignmentIds, string? CronExpression) : IBaseDto;
```
A VALID baseline DTO (so the cron rule is the only variable) needs: `Version` = strict SemVer (e.g. `"1.0.0"`), `EntryStepIds` = a single non-empty Guid list (e.g. `new() { Guid.NewGuid() }`) — VALID-06/VALID-17 also run. Factory helper recommended:
```csharp
private static WorkflowCreateDto CreateDto(string? cron) =>
    new("wf", "1.0.0", null, new() { Guid.NewGuid() }, null, cron);
private static WorkflowUpdateDto UpdateDto(string? cron) =>
    new("wf", "1.0.0", null, new() { Guid.NewGuid() }, null, cron);
```
Cases (D-09 / RESEARCH.md:242-245), run against BOTH `new WorkflowCreateDtoValidator()` and `new WorkflowUpdateDtoValidator()`:
- 5-field `"0 0 * * *"` → `IsValid == true` (no `CronExpression` error) — regression guard.
- 6-field `"*/30 * * * * *"` → `IsValid == true` — new capability.
- malformed `"not a cron"` → has `CronExpression` error.
- wrong-field-count `"* * *"` (4-token) → has `CronExpression` error.

---

## Shared Patterns

### Anti-desync single-source-of-truth hoist (the spine of this phase)
**Source:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` (Phase 21)
**Apply to:** `CronFieldForm.cs` (the new detector) — and structurally, to both call sites that consume it.
```csharp
public static class L2ProjectionKeys   // public static class, pure string logic, contracts leaf, NO parser dep
{
    public static string Root(Guid workflowId) => $"{Prefix}{workflowId:D}";
}
```
The detection rule lives in exactly ONE place; both the Orchestrator scheduler and the BaseApi.Service validators consume it, so "validator-accepts ⟺ scheduler-parses-the-same-format" can never drift (D-03/D-04/D-05).

### Detector-resolves-format, parse-stays-local
**Source:** RESEARCH.md §"Call-site wiring" (lines 166-174)
**Apply to:** `CronInterval` (both methods) AND `BeValidStandardCron` (both validators).
```csharp
var format = CronFieldForm.IsSecondsForm(expr) ? CronFormat.IncludeSeconds : CronFormat.Standard;
CronExpression.Parse(expr, format);   // Cronos.Parse stays LOCAL to each side — never hoisted into contracts
```
Only the *detection rule* is shared; each side keeps its own `Cronos.Parse`. Never centralize the parse (would pull Cronos into the contracts leaf — D-03/D-05 violation).

### Cronos UTC contract (carried, unchanged)
**Source:** `src/Orchestrator/Scheduling/CronInterval.cs:9-13` (XML Pitfall 3) + `CronIntervalTests.cs:17` (`PinnedUtc`)
**Apply to:** every Cronos call site + every test that pins "now".
```csharp
// nowUtc MUST be DateTimeKind.Utc — Cronos throws on non-UTC. Callers feed timeProvider.GetUtcNow().UtcDateTime.
private static readonly DateTime PinnedUtc = new(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc);
```

### FluentValidation unit test (instantiate → Validate → assert)
**Source:** `tests/BaseApi.Tests/Validation/BaseDtoValidatorRuleTests.cs`
**Apply to:** `WorkflowCronValidatorTests.cs`.
```csharp
var result = NewValidator().Validate(Dto(...));
Assert.False(result.IsValid);
Assert.Contains(result.Errors, e => e.PropertyName == "Name");   // → "CronExpression" for the cron rule
```

---

## No Analog Found

None. Every file has an exact or strong in-repo analog (all read this session). No file falls back to RESEARCH.md generic patterns.

---

## Constraints Honored (cross-check for the planner)

| Constraint | Where it binds | Pattern guarantee |
|------------|----------------|-------------------|
| **D-03** — NO Cronos in `Messaging.Contracts` | `CronFieldForm.cs` | Modeled on `L2ProjectionKeys` (zero parser dep); no `using Cronos;`; no `PackageReference` edit to the contracts `.csproj`. |
| **D-08** — Orchestrator firewall (no `BaseApi.*` ref) | `CronInterval.cs` | Detector reached via `Messaging.Contracts` (the only shared assembly); no `BaseApi.*` using added. |
| **D-02** — no exception-as-control-flow | `BeValidStandardCron` | `IsValidFieldCount` rejects non-5/6 up front; ONE guarded `Parse` with resolved format. |
| **D-06** — no minimum-interval floor | validators | No new interval rule added; `* * * * * *` accepted. |
| **D-11** — message update | validator lines 51 / 106 | Both messages rewritten to "5- or 6-field". |
| **Pitfall 3** — fix BOTH validators | `WorkflowDtoValidator.cs` | Create (60) + Update (109) both rewired; D-09 tests both. |

---

## Metadata

**Analog search scope:** `src/Messaging.Contracts/`, `src/Orchestrator/Scheduling/`, `src/BaseApi.Service/Features/Workflow/`, `tests/BaseApi.Tests/Validation/`, `tests/BaseApi.Tests/Orchestrator/`, `tests/BaseApi.Tests/Features/Orchestration/Projection/`.
**Files read this session:** 6 analogs + `WorkflowDtos.cs` (DTO signature).
**Pattern extraction date:** 2026-06-14
