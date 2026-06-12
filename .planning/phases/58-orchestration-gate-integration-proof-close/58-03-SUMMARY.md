---
phase: 58-orchestration-gate-integration-proof-close
plan: 03
subsystem: testing
tags: [e2e, realstack, gate-a, compose, profile, config-schema, cfg-08, cfg-09, xunit, traits, docker]

# Dependency graph
requires:
  - phase: 58-01
    provides: "Processor.BadConfig console + its genuine embedded SourceHash + BadConfig(int Quantity) clashing TConfig — the CFG-08 incompatible subject this plan wires into compose + seeds"
  - phase: 58-02
    provides: "SeedConfigSchemaAsync GET-or-create-by-Name helper + SampleCompatibleSchemaName/Definition consts + SeedProcessorAsync configSchemaId param — the harness seams CFG-09 reuses"
  - phase: 57-startup-config-schema-fetch-gate-a
    provides: "Gate A (ConfigSchemaCoverageCheck + the 'Gate A incompatibility' Error log at ProcessorStartupOrchestrator.cs:187) — the CFG-08 ES-clash-log poll target + the withhold-MarkHealthy mechanism"
provides:
  - "compose.yaml processor-badconfig service behind the 'badconfig' profile (excluded from the default `compose up`; brought up with --profile badconfig by the close gate)"
  - "GateACompositionE2ETests.cs — the RealStack composition proof (CFG-08 incompatible→422 three-signal; CFG-09 compatible→204) the operator's live close run (Plan 05) executes"
  - "RealStackWebAppFactory + Seed*/PollForHealthyLivenessAsync promoted to internal — reusable across both Orchestrator E2E classes (no parallel harness)"
  - "Processor.BadConfig ProjectReference on the test project — the test reads the badconfig genuine embedded SourceHash for the identity loop"
affects: [58-04-close-gate, 58-05-live-proof]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Compose profile gating: profiles: [\"badconfig\"] excludes a deliberately-broken tier from the default stack; --profile badconfig opts it in (close gate + live run only)"
    - "Composition E2E reuses a sibling test's harness via private→internal promotion (RealStackWebAppFactory + Seed* helpers) instead of authoring a parallel factory"
    - "CFG-08 three-signal causation proof: ES clash log polled FIRST (boot+Gate-A-ran) → stably-absent liveness (mechanism) → Start 422 (outcome)"

key-files:
  created:
    - "tests/BaseApi.Tests/Orchestrator/GateACompositionE2ETests.cs (CFG-08 + CFG-09 RealStack composition tests)"
  modified:
    - "compose.yaml (processor-badconfig service behind the badconfig profile)"
    - "tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs (RealStackWebAppFactory + 5 Seed*/Poll helpers promoted private→internal for reuse)"
    - "tests/BaseApi.Tests/BaseApi.Tests.csproj (added Processor.BadConfig ProjectReference for the genuine SourceHash read)"

key-decisions:
  - "PollEsForLog signature is (queryBody, timeoutMs, indexPath=null, ct=default) — indexPath is the 3rd positional param, so the call MUST use the named arg `ct: ct` (the plan/PATTERNS snippet already used the named form; confirmed correct)"
  - "Reuse-by-promotion over re-authoring: promoted RealStackWebAppFactory + PollForHealthyLivenessAsync + SeedProcessor/ConfigSchema/Step/Workflow from private static to internal static so GateACompositionE2ETests calls them directly — the plan mandated 'author NO new harness'"
  - "Added a Processor.BadConfig ProjectReference to the test project (Rule 3) — without it the test cannot read the badconfig genuine embedded SourceHash the same way SampleRoundTrip reads Processor.Sample's; this closes the badconfig identity loop"
  - "CFG-08 workflow seeded with cron '* * * * *' (not null) — the Start liveness gate evaluates every participant regardless of cron, and a non-null cron keeps the seed shape identical to CFG-09 (the gate BLOCKS at Start before any fire)"

patterns-established:
  - "Profile-gated broken-subject compose tier (DoS-safe: absent from default stack, /ready passes so no crash-loop)"
  - "Three-signal causation proof (ES-log-first) to distinguish 'Gate A withheld health' from 'container down'"

requirements-completed: []

# Metrics
duration: 6min
completed: 2026-06-12
---

# Phase 58 Plan 03: Gate-A Composition Proof Harness + Profile-Gated BadConfig Tier Summary

**Wired the `processor-badconfig` container into `compose.yaml` behind a `badconfig` profile (excluded from the default dev stack) and authored `GateACompositionE2ETests.cs` — the 0-warning-compiling RealStack composition proof (CFG-08 incompatible→three-signal→422, CFG-09 compatible→Healthy→204) that the operator's live close run executes.**

## Performance

- **Duration:** ~6 min
- **Started:** 2026-06-12T22:28:54Z
- **Completed:** 2026-06-12T22:35:07Z
- **Tasks:** 2
- **Files modified:** 4 (1 created, 3 modified)

## Accomplishments

- **`compose.yaml` — profile-gated `processor-badconfig` service (D-04/D-05):** a faithful clone of the `processor-sample` tier with four deltas only — service key `processor-badconfig`, `container_name: sk-processor-badconfig` (unique), `dockerfile: src/Processor.BadConfig/Dockerfile`, and a new **`profiles: ["badconfig"]`** key. The `/health/ready` healthcheck, `depends_on`, environment block, and `restart` are byte-identical (MarkReady flips → no crash-loop; withholds MarkHealthy → no liveness key, binds no queue → net-zero-harmless). The default `processor-sample` tier was left untouched.
  - **Profile gating VERIFIED with real docker:** `docker compose -f compose.yaml config --quiet` exits 0; `config --services` (default) lists `processor-sample` but NOT `processor-badconfig`; `--profile badconfig config --services` lists BOTH. The default `compose up` is unaffected.
- **`GateACompositionE2ETests.cs` (NEW) — the composition proof:**
  - **CFG-08** (`BadConfig_GateAIncompatible_ClashLogged_LivenessAbsent_Start422`): seeds the clash schema (`gateA-badconfig-clash`, types `quantity` as string) + the badconfig Processor row with that ConfigSchemaId + a workflow whose graph includes it, then asserts the **three D-06 signals in order** — (a) the ES clash log (`PollEsForLog` scoped to `service.name == "processor-badconfig"` + `match body "Gate A incompatibility"`) polled **FIRST** (causation: proves boot + Gate-A-ran), (b) `skp:{badId}` **stably absent** across 3 windows spanning > one 10s heartbeat interval (the inverse of `PollForHealthyLivenessAsync`), (c) `POST /api/v1/orchestration/start` → **422** UnprocessableEntity.
  - **CFG-09** (`SampleCompatible_GateAPasses_Healthy_Start204`): seeds the compatible schema (`gateA-sample-compatible` via the Plan-02 consts) + the Sample Processor row with that ConfigSchemaId (Gate A **runs and passes** — not skipped), `PollForHealthyLivenessAsync(sampleId)`, then Start → **204** NoContent. Best-effort Stop teardown to cease the cron churn.
  - **Net-zero teardown** registered via the inherited `RealStackWebAppFactory` (`L2KeysToCleanup` / `ParentIndexMembersToSrem`); CFG-08 is minimal (no queue/data residue), CFG-09 gets the full Sample sweep.
- **Harness reuse (no new factory):** promoted `RealStackWebAppFactory` + `PollForHealthyLivenessAsync` + `SeedProcessorAsync` / `SeedConfigSchemaAsync` / `SeedStepAsync` / `SeedWorkflowAsync` from `private` to `internal` on `SampleRoundTripE2ETests` so the new class calls them directly.
- **Build + hermetic gates GREEN:** `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` → **0 warnings / 0 errors** (the RealStack test COMPILES; `Processor.BadConfig.dll` now builds into the test graph). Hermetic suite `-- --filter-not-trait "Category=RealStack"` → **558/558 passed** (unchanged from Plan 02 — the two new RealStack tests are correctly excluded and break nothing).

## Exact ES Query Used (for Plan 05's runbook)

The CFG-08 causation poll targets the shipped Gate-A Error log (`ProcessorStartupOrchestrator.cs:187`):

```json
{
  "size": 5,
  "sort": [ { "@timestamp": { "order": "desc" } } ],
  "query": { "bool": { "must": [
    { "term": { "resource.attributes.service.name": "processor-badconfig" } },
    { "match": { "body": "Gate A incompatibility" } }
  ] } }
}
```

Asserted via `PollEsForLog(query, timeoutMs: 120_000, ct: ct)` → `Assert.NotNull` + `Assert.Contains("Gate A incompatibility", …)`.

## Sentinel Schema Names (shared across Plans 03/04)

| Sentinel Name | Definition shape | Role |
|---------------|------------------|------|
| `gateA-sample-compatible` | object, `value: string` (Plan-02 const) | CFG-09 — `SampleConfig(string? Value)` COVERS it → Gate A passes |
| `gateA-badconfig-clash` | object, `quantity: string` | CFG-08 — clashes with `BadConfig(int Quantity)` → Gate A withholds MarkHealthy |

Compose profile: **`badconfig`**. Service / container: **`processor-badconfig`** / **`sk-processor-badconfig`**. Test class: **`GateACompositionE2ETests`**.

## Task Commits

1. **Task 1: Add the profile-gated processor-badconfig service to compose.yaml** — `41dc100` (feat)
2. **Task 2: Author GateACompositionE2ETests.cs (CFG-08 422 + CFG-09 204) — compiles under RealStack trait** — `e0062a7` (test)

## Files Created/Modified

- `compose.yaml` — added the `processor-badconfig` service (34 insertions) behind `profiles: ["badconfig"]`; cloned from `processor-sample` with the four specified deltas. No other tier touched.
- `tests/BaseApi.Tests/Orchestrator/GateACompositionE2ETests.cs` (NEW, 213 lines) — the two-fact composition proof under the four traits.
- `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` — 6 helper/factory declarations promoted `private`→`internal` for cross-class reuse (no behavior change).
- `tests/BaseApi.Tests/BaseApi.Tests.csproj` — added `Processor.BadConfig` ProjectReference (genuine embedded SourceHash read).

## Decisions Made

- **`PollEsForLog` named-arg call.** The helper's signature is `(string queryBody, int timeoutMs, string? indexPath = null, CancellationToken ct = default)` — `indexPath` is the 3rd positional param. The plan/PATTERNS snippet `PollEsForLog(query, timeoutMs, ct: ct)` is correct precisely because it names `ct` (a bare 3rd positional would bind to `indexPath`). Authored exactly that way.
- **Reuse-by-promotion.** The plan mandated "author NO new harness" and "reuse the SAME factory + helpers." Those were `private` on `SampleRoundTripE2ETests`. Promoting `RealStackWebAppFactory` (nested class) + the 5 `Seed*`/`Poll*` statics to `internal` is the minimal, behavior-preserving way to satisfy that — no parallel factory, no duplicated seed logic.
- **Processor.BadConfig ProjectReference (Rule 3).** See Deviations.
- **CFG-08 non-null cron.** The Start liveness gate (`ProcessorLivenessValidator`) evaluates every participant independent of the cron; a `"* * * * *"` cron keeps the seed shape parallel to CFG-09 and the gate still BLOCKS at Start (no fire occurs before the 422). The badconfig container never goes Healthy regardless.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Test project lacked a Processor.BadConfig reference needed to read the badconfig genuine SourceHash**
- **Found during:** Task 2
- **Issue:** `GateACompositionE2ETests` must seed the badconfig Processor DB row against the live container's identity — i.e. with the GENUINE embedded `SourceHash` read off `Processor.BadConfig.dll` (exactly as `SampleRoundTripE2ETests` reads `Processor.Sample`'s via `typeof(SampleProcessor).Assembly`). The test project (`BaseApi.Tests.csproj`) referenced `Processor.Sample` but NOT `Processor.BadConfig`, so `typeof(global::Processor.BadConfig.BadConfigProcessor)` would not compile.
- **Fix:** Added `<ProjectReference Include="..\..\src\Processor.BadConfig\Processor.BadConfig.csproj" />` to the test csproj (mirroring the existing Sample reference, with a Phase-58 comment). This pulls `Processor.BadConfig.dll` into the test build graph so its embedded SourceHash attribute is readable by reflection.
- **Files modified:** tests/BaseApi.Tests/BaseApi.Tests.csproj
- **Verification:** `dotnet build … -c Release` → 0 warnings / 0 errors; `Processor.BadConfig -> …Processor.BadConfig.dll` now appears in the build output.
- **Committed in:** `e0062a7` (Task 2 commit)

**2. [Rule 3 - Blocking] Reused helpers/factory were private — promoted to internal for cross-class reuse**
- **Found during:** Task 2
- **Issue:** The plan requires reusing `RealStackWebAppFactory` + `PollForHealthyLivenessAsync` + the `Seed*` helpers "wholesale," but all were `private` (the factory a `private sealed` nested class) on `SampleRoundTripE2ETests`, unreachable from the new class.
- **Fix:** Promoted exactly those 6 declarations (`RealStackWebAppFactory`, `PollForHealthyLivenessAsync`, `SeedProcessorAsync`, `SeedConfigSchemaAsync`, `SeedStepAsync`, `SeedWorkflowAsync`) from `private` to `internal`. No body/behavior change; `SampleRoundTripE2ETests`'s own tests are unaffected (proven by the unchanged 558/558 hermetic count).
- **Files modified:** tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs
- **Verification:** 0-warning build; 558/558 hermetic suite unchanged.
- **Committed in:** `e0062a7` (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (both Rule 3 - blocking: test-project wiring needed to reuse the harness + read the badconfig identity as the plan specified). No architectural changes, no scope creep — both are the mechanical wiring the plan's "reuse the harness" + "read the genuine SourceHash" instructions implied.

## Issues Encountered

None beyond the two Rule-3 wiring fixes above. Docker WAS available on the executor host, so the full Task-1 profile-gating acceptance (the `config --services` ±`--profile` checks) ran live rather than falling back to the YAML-grep fallback.

## Known Stubs

None. The two tests carry their full load-bearing assertions (CFG-08 three-signal ES-first + absent + 422; CFG-09 Healthy + 204); they are intentionally hermetic-EXCLUDED (RealStack trait) — their LIVE execution is the operator-gated Plan 05 close run, not a stub.

## Threat Flags

None. The phase threat register is satisfied as designed: T-58-06 (DoS) — the badconfig tier is `profiles: ["badconfig"]` (absent from the default stack) and its `/health/ready` passes (MarkReady, no crash-loop); T-58-07 (false-positive proof) — the CFG-08 ES clash-log poll runs FIRST and is load-bearing (`PollEsForLog` + the service-scoped message text), so "absent liveness" is provably causation. No new endpoints, auth, crypto, or input-validation surface (compose tier clone + a test using existing CRUD seeds; the clash schema is server-side meta-schema-validated on write).

## User Setup Required

None — no autonomous check in this plan required a live docker stack bring-up (the `compose config` parse is static; the two new tests are RealStack-excluded). The LIVE execution of `GateACompositionE2ETests` + the `--profile badconfig` bring-up is the operator-gated Plan 04/05 close gate.

## Next Phase Readiness

- **Plan 04 (close script)** can bring up the stack with `docker compose --profile badconfig up …`, read the badconfig genuine embedded SourceHash off `Processor.BadConfig.dll`, and seed both sentinel schemas (`gateA-sample-compatible`, `gateA-badconfig-clash`) + both processor rows idempotently (GET-or-create) — the test and the close script now share the exact sentinel Names and the ES query.
- **Plan 05 (live proof)** runs `GateACompositionE2ETests` against the rebuilt stack to record the CFG-08/CFG-09 GREEN; the three-signal CFG-08 assertions are authored and compiling.
- No blockers.

## Self-Check: PASSED

- FOUND: .planning/phases/58-orchestration-gate-integration-proof-close/58-03-SUMMARY.md
- FOUND: tests/BaseApi.Tests/Orchestrator/GateACompositionE2ETests.cs
- FOUND: compose.yaml (processor-badconfig service, profile badconfig — verified via `docker compose config`)
- FOUND commit 41dc100 (Task 1)
- FOUND commit e0062a7 (Task 2)

---
*Phase: 58-orchestration-gate-integration-proof-close*
*Completed: 2026-06-12*
