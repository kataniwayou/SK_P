---
phase: 21-v3.4.0-closeout-hygiene
reviewed: 2026-05-31T00:00:00Z
depth: standard
files_reviewed: 6
files_reviewed_list:
  - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
  - src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs
  - src/Orchestrator/Messaging/OrchestratorL2Keys.cs
  - tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs
  - tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs
  - scripts/phase-21-close.ps1
findings:
  critical: 0
  warning: 0
  info: 2
  total: 2
status: clean
---

# Phase 21: Code Review Report

**Reviewed:** 2026-05-31
**Depth:** standard
**Files Reviewed:** 6
**Status:** clean

## Summary

Phase 21 is a behavior-preserving HARDEN-03 refactor that hoists the three L2
(Redis) projection key builders into a new shared `public static
L2ProjectionKeys` in the `Messaging.Contracts` leaf, and converts the writer
(`RedisProjectionKeys`) and reader (`OrchestratorL2Keys`) into thin forwarders.
The central correctness requirement — keys MUST be byte-identical before/after —
holds, and I verified it directly against the pre-refactor source at `424a5a1`:

- **Root:** pre-refactor writer used `$"{prefix}{workflowId}"` (bare); pre-refactor
  reader used `$"{prefix}{workflowId:D}"`. The shared `L2ProjectionKeys.Root` uses
  `$"{prefix}{workflowId:D}"`. In .NET, `Guid.ToString()` defaults to the "D"
  (hyphenated) format, so bare interpolation and `:D` render byte-identically.
  The refactor unifies the writer and reader onto the single `:D` form with no
  output change on either side — this is exactly the desync class HARDEN-03 closes.
- **Step / Processor:** the shared builders are character-for-character copies of
  the pre-refactor writer expressions (`$"{prefix}{workflowId}:{stepId}"` and
  `$"{prefix}{processorId}"`).

All four real call sites remain wired through the forwarders
(`RedisProjectionWriter`, `RedisL2Cleanup`, `OrchestrationService` →
`RedisProjectionKeys`; `StartOrchestrationConsumer`, `StopOrchestrationConsumer`
→ `OrchestratorL2Keys`). Both `Orchestrator.csproj` and `BaseApi.Service.csproj`
already reference `Messaging.Contracts`, and the new type is `public`, so both
hosts can consume it. The `RedisProjectionKeys` writer shim correctly stays
`internal` (preserving the existing `InternalsVisibleTo("BaseApi.Tests")` access
surface), while `OrchestratorL2Keys` remains `internal` and exposes only `Root`
(the only shape the reader needs) — no over-exposure.

The new `L2ProjectionKeysTests` golden strings are byte-identical to the existing
writer-side `RedisProjectionKeysTests` golden strings, so the shared source of
truth is provably pinned to the same values both prior sides emitted.

The `CorrelationPropagationE2ETests` change is confirmed **prose-only**: the diff
against `424a5a1` touches only two XML-doc (`///`) hunks, correcting stale
`skp:wf:{id}:root` references to the flat `skp:{id}` form. No executable line,
assertion, query string, or seeding helper changed. This matches the production
reality (the flat scheme has no `wf:`/`:root` discriminator). No defect.

`scripts/phase-21-close.ps1` is a well-structured triple-SHA close gate with
correct `$ErrorActionPreference='Stop'` + `Set-StrictMode`, fail-closed exit
codes, a hard-failure guard against unparseable fact counts (Smell A), and
locale-stable (`Sort-Object -CaseSensitive`) snapshot hashing. No correctness or
security issues.

No bugs, security vulnerabilities, or behavior changes were found. The two Info
items below are cosmetic observations only — neither warrants a change.

## Info

### IN-01: Root uses explicit `:D` while Step/Processor use bare interpolation (intra-class stylistic inconsistency)

**File:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:26-30`
**Issue:** `Root` renders its GUID with the explicit `:D` format specifier
(`$"{prefix}{workflowId:D}"`), whereas `Step` and `Processor` rely on the bare
default interpolation (`{stepId}`, `{processorId}`). All three are byte-identical
because "D" is the `Guid.ToString()` default — and the class doc comment (lines
13-14) already explains the `:D` is "byte-identical to a bare interpolation." The
mixed style is intentional and harmless (it documents the format-sensitivity at
the one seam that previously diverged), but a future reader could misread it as
two different formats.
**Fix:** Optional only. Either leave as-is (the doc comment justifies it), or for
uniformity apply `:D` to all three (`$"{prefix}{workflowId:D}:{stepId:D}"`,
`$"{prefix}{processorId:D}"`) — provably byte-identical, so safe under the
golden-string tests. Do NOT change without re-running the close gate; given the
phase already passed, leaving it is the lower-risk choice.

### IN-02: Writer/reader forwarder doc comments are near-duplicated boilerplate

**File:** `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs:5-10`,
`src/Orchestrator/Messaging/OrchestratorL2Keys.cs:5-11`
**Issue:** Both forwarder shims carry substantial, partially overlapping XML-doc
prose describing the HARDEN-03 rationale that is also (more fully) stated on the
shared `L2ProjectionKeys`. This is intentional per-host context and is accurate,
but it is the kind of triplicated narrative that can drift out of sync over time
(the exact class of drift HARDEN-03 set out to eliminate at the code level).
**Fix:** Optional only. Consider trimming each shim's doc to a one-line "Forwarder
to `<see cref="L2ProjectionKeys"/>` — see there for the authoritative shapes
(HARDEN-03)" and letting the shared class own the full rationale. No behavior
impact; purely a maintenance-surface reduction.

---

_Reviewed: 2026-05-31_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
