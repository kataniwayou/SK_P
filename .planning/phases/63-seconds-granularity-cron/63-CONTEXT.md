# Phase 63: Seconds-Granularity Cron - Context

**Gathered:** 2026-06-14
**Status:** Ready for planning

<domain>
## Phase Boundary

Enable 6-field seconds-granularity cron (`*/30 * * * * *` → every 30s) **end-to-end**:

1. `CronInterval` (Orchestrator) computes next-occurrence + interval math correctly for sub-minute intervals (UTC), and
2. the workflow create/update validator (BaseApi.Service) **accepts** the 6-field form while still accepting the 5-field form.

This is a **product code change only** — it lifts today's `CronFormat.Standard` 1-minute floor. It adds **no new capability**. Per the v8.0.0 scope discipline, the only product changes this milestone are seconds-cron (this phase) + the processor payload/logging (Phase 64); everything else is harness/seeder/analyzer.

Requirements: **CRON-01** (orchestrator fires on 6-field cron; sub-minute next-occurrence + interval math in UTC) and **CRON-02** (validator accepts 6-field; 5-field still accepted). Both locked in REQUIREMENTS.md.

</domain>

<decisions>
## Implementation Decisions

### Format Detection
- **D-01:** Decide 5-field vs 6-field by a **token-count switch** — split the trimmed expression on whitespace and count: **6 tokens → `CronFormat.IncludeSeconds`**, **5 tokens → `CronFormat.Standard`**, any other count → invalid/reject. Cronos has no auto-detect, so the format must be chosen explicitly.
- **D-02:** **No exception-as-control-flow.** Do NOT implement detection by attempting one `Cronos.Parse` and catching `CronFormatException` to retry the other format. The token count selects the format up front; `Cronos.Parse` is then called once with the resolved format. (A genuinely-malformed 6-token expression still surfaces as a parse failure — it is not silently retried as 5-field.)

### Single Source of Truth (anti-desync)
- **D-03:** The detection rule lives in **exactly one place** — a **pure string helper in `Messaging.Contracts`** (e.g. `CronFieldForm.IsSecondsForm(string expr)` or equivalent), token-count logic only, **with NO Cronos dependency added to `Messaging.Contracts`**. Returning a `bool`/field-count keeps the contracts leaf free of the third-party parser.
- **D-04:** Both call sites consume the shared detector to pick the `CronFormat`, then each performs its **own local `Cronos.Parse`** with the resolved format:
  - `CronInterval` (Orchestrator) — `NextOccurrence` + `IntervalSeconds`.
  - `WorkflowCreateDtoValidator` / `WorkflowUpdateDtoValidator` (BaseApi.Service) — `BeValidStandardCron` (currently duplicated in both validators; both must route through the shared detector).
- **D-05:** **Rationale / constraint:** `Messaging.Contracts` is the **only** assembly both sites can share — the Orchestrator has a hard firewall (**D-08**: no `ProjectReference` to any `BaseApi.*` project), so `BaseApi.Core` cannot be the shared home. Centralizing only the *detection rule* (not the Cronos parse) structurally guarantees "validator-accepts ⟺ scheduler-parses-the-same-format" while honoring this codebase's documented anti-desync discipline (cf. Phase 21 `L2ProjectionKeys` hoist) without polluting the frozen-vocabulary contracts leaf with a parser dependency.

### Granularity Floor
- **D-06:** **No minimum-interval floor.** Accept any valid 6-field cron, including `* * * * * *` (every 1s). The milestone scope is "enable 6-field," not "add scheduling policy." The workflow under test uses `*/30`. No new validation rule for intervals.

### UTC (carried, unchanged)
- **D-07:** The existing UTC contract on `CronInterval` is preserved — `nowUtc` MUST be `DateTimeKind.Utc` (Cronos throws otherwise); callers feed `timeProvider.GetUtcNow().UtcDateTime`. The 6-field change does not alter this. `IntervalSeconds` (delta between the next two occurrences) is granularity-agnostic and already yields the correct sub-minute result once the format is resolved.

### Tests & Message
- **D-08:** **Extend `CronIntervalTests`** with a `*/30 * * * * *` case — `IntervalSeconds(...) == 30` and `NextOccurrence(...)` strictly-future + `Kind=Utc` (proves SC#1 sub-minute math). Existing 5-field cases retained (no regression).
- **D-09:** **Add validator unit tests** (none exist today for the cron rule) for **both** `WorkflowCreateDtoValidator` and `WorkflowUpdateDtoValidator`: 5-field still accepted (no regression), 6-field now accepted, malformed (wrong field count / unparseable) still rejected. Covers SC#2 + SC#3.
- **D-10:** **Add a detector unit test** for the new pure `Messaging.Contracts` helper — pins the one shared rule (5 fields → not-seconds, 6 → seconds, other counts → handled as invalid).
- **D-11:** **Update the validator's user-facing error message** from "must be a valid 5-field cron expression…" to reflect that **5- or 6-field** is now accepted (both Create + Update validators).

### Claude's Discretion
- Exact helper name / namespace within `Messaging.Contracts`, signature shape (`bool` vs small enum/int), and how each call site maps the result to `CronFormat`.
- Internal refactor of the duplicated `BeValidStandardCron` (Create vs Update) — may be collapsed to route through the shared detector however reads cleanest, provided behavior is identical across both.
- Test data tables, naming, and whether new validator tests are unit-level (preferred — fast) or added to existing integration coverage.
- Whitespace-handling edge cases in the detector (leading/trailing/multiple spaces) — handle robustly; not a user decision.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

No external specs/ADRs govern this phase — requirements are fully captured in REQUIREMENTS.md + the decisions above. The relevant references are in-repo source + planning docs:

### Requirements & Roadmap
- `.planning/REQUIREMENTS.md` §"Seconds-Granularity Cron (CRON)" — CRON-01, CRON-02.
- `.planning/ROADMAP.md` §"Phase 63: Seconds-Granularity Cron" — goal + 4 success criteria (SC#1–4) + locked scope discipline.

### Code touch points
- `src/Orchestrator/Scheduling/CronInterval.cs` — `NextOccurrence` + `IntervalSeconds`; both currently hardcode `CronFormat.Standard` (lines 22, 31). UTC pitfall documented in the class summary.
- `src/BaseApi.Service/Features/Workflow/WorkflowDtoValidator.cs` — `BeValidStandardCron` duplicated in `WorkflowCreateDtoValidator` (line 60) + `WorkflowUpdateDtoValidator` (line 109); both default to 5-field `CronFormat.Standard` and catch `CronFormatException`. VALID-19 rule + user message at lines 48-51 / 103-106.
- `src/Messaging.Contracts/` — target home for the new pure detector (the only shared assembly; no Cronos dependency today — keep it that way per D-03).

### Existing tests (to extend/add)
- `tests/BaseApi.Tests/Orchestrator/CronIntervalTests.cs` — extend with `*/30` (D-08). Uses `FakeTimeProvider`, pins UTC.
- No validator unit test for the cron rule exists today — D-09 adds it.

### Dependency constraint
- `src/Orchestrator/Orchestrator.csproj` — **D-08 firewall**: NO `ProjectReference` to any `BaseApi.*`; refs are `BaseConsole.Core` + `Messaging.Contracts`. This is *why* the shared detector must live in `Messaging.Contracts` (not `BaseApi.Core`).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `Cronos` package — already referenced by both `Orchestrator` and `BaseApi.Service`. `CronFormat.IncludeSeconds` is the 6-field mode; `CronFormat.Standard` the 5-field mode. No new package needed in those two projects.
- `FakeTimeProvider` (Microsoft.Extensions.Time.Testing) — already used in `CronIntervalTests` for deterministic UTC pinning; reuse for the `*/30` case.

### Established Patterns
- **Anti-desync single-source-of-truth** — this codebase deliberately hoists shared parsing/key logic into one location to prevent writer↔reader drift (Phase 21 `L2ProjectionKeys`). D-03/D-04 apply that pattern to cron-format detection.
- **`Messaging.Contracts` = leaf shared lib** — referenced by both Orchestrator and BaseApi.Service; described as "frozen vocabulary, contracts only." D-03 deliberately keeps Cronos *out* of it (pure string helper only).
- **D-08 dependency firewall** — Orchestrator may not reference `BaseApi.*`; constrains where shared code can live.
- **UTC-only Cronos input** — `nowUtc` must be `DateTimeKind.Utc`; callers use `timeProvider.GetUtcNow().UtcDateTime`.

### Integration Points
- `CronInterval` is consumed by the orchestrator scheduler (`WorkflowScheduler` / `WorkflowFireJob`) — the seconds-cron flows through unchanged once `CronInterval` resolves the format; no scheduler signature change expected.
- The validators run on `POST/PUT /api/v1/workflows` via the Phase 4 FluentValidation → 422/400 mapping path.

</code_context>

<specifics>
## Specific Ideas

- Concrete target expression for this milestone: `*/30 * * * * *` (every 30 seconds) — the cadence the rest of v8.0.0 observes (~10 triggers per 5-minute window).
- The detector centralizes **only** the format-selection rule; the actual `Cronos.Parse` stays local to each call site (deliberate — keeps the Cronos dependency out of the contracts leaf).

</specifics>

<deferred>
## Deferred Ideas

- **Minimum-interval floor / sub-second rate guard** — considered (Area 3) and explicitly declined for this milestone (D-06: no floor). If pathological fast-cron scheduling ever becomes a concern, it would be its own scoped validation-policy change, not part of v8.0.0.

None of the discussion strayed into new capabilities — scope held to "enable 6-field cron."

</deferred>

---

*Phase: 63-seconds-granularity-cron*
*Context gathered: 2026-06-14*
