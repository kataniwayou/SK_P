---
phase: 34-keeper-console-foundation
fixed_at: 2026-06-05T00:00:00Z
review_path: .planning/phases/34-keeper-console-foundation/34-REVIEW.md
iteration: 2
findings_in_scope: 4
fixed: 3
skipped: 1
status: partial
---

# Phase 34: Code Review Fix Report

**Fixed at:** 2026-06-05T00:00:00Z
**Source review:** .planning/phases/34-keeper-console-foundation/34-REVIEW.md
**Iteration:** 2

**Summary:**
- Findings in scope: 4 (fix_scope = all: Critical + Warning + Info)
- Fixed: 3 (WR-01 + WR-02 in iteration 1; IN-01 this iteration)
- Skipped: 1 (IN-02 — sound rationale, accepted cross-console dev posture, no action required)

This is the cumulative iteration-2 report. The two Warnings (WR-01, WR-02) were fixed in
iteration 1 and are verified still-present below (not re-touched). The new work this iteration
is the two Info findings: IN-01 fixed, IN-02 skipped-with-rationale. Prime directive honored —
every change improves Keeper WITHOUT diverging from the established cross-console conventions or
breaking the green build/tests.

## Fixed Issues

### IN-01: Test Fixture Does Not Inject a `Retry` Config Section — `RetryOptions` Resolves to Defaults Silently

**Files modified:** `tests/BaseApi.Tests/Keeper/KeeperHostBootFixture.cs`
**Commit:** 808b750
**Applied fix:** Added an explicit in-memory `Retry` section (`Retry:Limit = 3`,
`Retry:Strategy = Immediate`) to `ConfigureBuilder`, immediately before the existing
`Configure<RetryOptions>(builder.Configuration.GetSection("Retry"))` bind. The bind is now
exercised against a real config value mirroring the live `src/Keeper/appsettings.json`, instead
of silently resolving to `RetryOptions`' property-initializer defaults when the section is absent.
If `RetryOptions` later gains a required or range-validated key, the boot fixture now carries the
section rather than masking the gap.

**Rationale for fix choice (non-divergent):** Per the per-finding guidance, I first checked the
sibling host-boot precedent. The base `ConsoleTestHostFixture.BuildConfig()` does NOT register
`RetryOptions` at all — so there is nothing in the base fixture to mirror, and adding Retry to the
base config would itself be the divergent move. The Keeper sibling test `KeeperRoundRobinTests`
already sets a real value via `.Configure<RetryOptions>(o => o.Limit = 3)`, establishing that the
Keeper suite expects the binding to resolve to `Limit = 3`, not silent defaults. This fix aligns the
Keeper boot fixture with that established Keeper-local intent and with the live appsettings.json,
without diverging from any sibling console.

### WR-01: `RetryOptions.Strategy` Is Bound From Config But Always Silently Ignored

**Files modified:** `src/Keeper/Consumers/PlaceholderConsumerDefinition.cs` (iteration 1)
**Commit:** 7c920e6 (iteration 1)
**Applied fix:** Already addressed in iteration 1 via a clarifying code-comment at the
`UseMessageRetry` call site, documenting that `Immediate` is intentionally the ONLY supported
strategy at this milestone, that `RetryOptions.Strategy` (Interval/Exponential) is structured-for
but deliberately NOT wired (a shared deferral across ALL consoles), and that wiring it must be done
uniformly across every console — not piecemeal in Keeper. Verified still present this iteration
(lines 32-36). Carried as already-fixed; not re-touched (per scoped-commit + no-double-touch rule).

### WR-02: `KeeperDependencyFirewallTests` Reflects Only Direct References

**Files modified:** `tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs` (iteration 1)
**Commit:** 8d26729 (iteration 1)
**Applied fix:** Already addressed in iteration 1 via a `NOTE — DIRECT references only` paragraph
in the test's `<summary>` doc-comment, clarifying that `GetReferencedAssemblies()` returns only the
references in `Keeper.dll`'s own manifest (not the transitive closure), that a forbidden assembly
pulled in indirectly via `BaseConsole.Core` or `Messaging.Contracts` would not trip the guard, that
a transitive scan is deferred (and would diverge from the analogous direct-reference-only
`ConsoleDependencyFirewallTests`), and that "closure" here means Keeper's own manifest. Verified
still present this iteration (lines 14-22). Carried as already-fixed; not re-touched.

## Verification

Per-fix verification (3-tier) and pre-commit gate for IN-01:
- **Tier 1:** Re-read the modified `KeeperHostBootFixture.cs`; the injected `Retry` section is present,
  the existing `Configure<RetryOptions>` bind and surrounding seam intact.
- **Tier 2 (build):** `dotnet build SK_P.sln -c Release` → 0 Warnings / 0 Errors (TreatWarningsAsErrors
  honored); `dotnet build SK_P.sln -c Debug` → 0 Warnings / 0 Errors.
- **Tests:** `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Keeper" -c Release` →
  Passed! Failed: 0, Passed: 460, Skipped: 0 (the MTP/VSTest filter quirk runs the full hermetic
  suite — all 460 green, including the Keeper boot + round-robin + firewall tests).

## Skipped Issues

### IN-02: Hardcoded `guest/guest` RabbitMQ Credentials in `appsettings.json`

**File:** `src/Keeper/appsettings.json:22-24`
**Reason:** Skipped with rationale — accepted cross-console dev posture, no action required. The
reviewer's own Fix note states "No immediate action" and frames the optional self-documenting comment
as purely cosmetic. I verified the sibling consoles' config files (`src/Orchestrator/appsettings.json`,
`src/Processor.Sample/appsettings.json`) are plain JSON with NO `//` comments and carry the identical
`guest/guest` values. Introducing a `//` comment in Keeper's appsettings.json alone would diverge from
the established cross-console convention — and the prime directive for this phase is to mirror the
sibling consoles, not diverge. Prod deployments already override these via environment variables
(`RabbitMq__Username` / `RabbitMq__Password`) or a secrets provider; the dev-only intent is documented
in the compose comment (T-34-07). No source change made.
**Original issue:** `RabbitMq.Username` / `RabbitMq.Password` are `guest/guest` in the committed
`appsettings.json` — the established dev posture matching Orchestrator and Processor.Sample; prod
overrides via env/secrets.

---

_Fixed: 2026-06-05T00:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 2_
