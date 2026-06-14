# Phase 67: Fault-Injection Harness - Context

**Gathered:** 2026-06-14
**Status:** Ready for planning

<domain>
## Phase Boundary

A **fully-automated PowerShell harness** that drives **one** scenario end-to-end with **no human step**:
`clean → seed → activate → inject fault → observe (5-min window) → analyze → tear down`.

It is the **orchestration layer** that wires together the already-built pieces:
- **Phase 65** — `scripts/phase-65-up.ps1` (bring-up), `scripts/phase-65-reset.ps1` (FLUSHALL + heal-wait + row-scoped DB reset, stack stays up), and the `FanOutSeeder` C# RealStack fixture (`dotnet test --filter ~FanOutSeeder`, idempotent `v8-fanout-proof`).
- **Phase 66** — the analyzer fixture (`dotnet test --filter "Category=RealStack&...~Analyzer"`), which writes `analyzer-reports/{scenarioId}.json` and exits non-zero on a FAIL verdict.

Phase 67 adds the **harness machinery + the per-scenario fault-injection mechanism** and proves it on **two reference runs** (a no-fault baseline + one real processor crash). Satisfies FAULT-01/02/03.

**In scope:**
- The `scripts/phase-67-harness.ps1` orchestrator and its scenario-config seam.
- The fault-injection mechanism (`docker stop → dwell → start`, whole-tier, mid-window).
- The activation gate (`POST /api/v1/orchestration/start`, hard-gate on `204`) and the seed→activate workflow-id handoff.
- The observe→analyze cohort-bounding + drain handoff to the Phase 66 analyzer.
- Proving the harness end-to-end on TEST-01 (baseline) + a TEST-02-shaped processor crash.

**Out of scope (other phases / locked):**
- The 7-scenario resilience sweep + formal PASS assertions for all 7 (Phase 68 — this harness's consumer; 68 is "just data" rows in the scenario table).
- The seeder / reset / bring-up artifacts themselves (Phase 65, complete — the harness *calls* them).
- The analyzer / PASS-FAIL engine (Phase 66, complete — the harness *invokes* it and reads its verdict).
- The processor work + `Step_<label>` log shape (Phase 64, locked) and the seconds-cron (Phase 63, locked).
- **No new product code, no new product log/metric** — v8.0.0 scope discipline limits product changes to Phase 63 (cron) + Phase 64 (processor payload/logging). The harness is test/ops tooling only.

</domain>

<decisions>
## Implementation Decisions

### Harness form & wiring (FAULT-01, FAULT-03)
- **D-01:** The harness is a **PowerShell orchestrator** `scripts/phase-67-harness.ps1`, in the same family as the 18 existing `scripts/phase-NN-close.ps1` + the Phase 65 `phase-65-up.ps1` / `phase-65-reset.ps1`. It **shells the existing pieces** rather than re-implementing them: `phase-65-up` → `phase-65-reset` → `dotnet test --filter ~FanOutSeeder` → resolve wf id → `POST /orchestration/start` → docker fault op → wait window → `dotnet test --filter ~Analyzer` → `docker compose down`. Rationale: every infra/docker/redis-cli/psql op in this repo already lives in PowerShell; a PS orchestrator matches convention with zero new tooling. (C# orchestrator fixture rejected: re-implements the docker/compose orchestration the PS close-scripts already prove.)
- **D-02:** **Seed→activate workflow-id handoff = psql sentinel lookup.** After the seeder runs, the harness resolves the activation target by a `psql SELECT id FROM workflows WHERE name = 'v8-fanout-proof'` (the stable sentinel from Phase 65 D-04). The `FanOutSeeder` fixture stays a **pure seeder** — no new file-emission or folded-in activation responsibility. The id-resolution lives in the harness next to its other psql ops.
- **D-03:** **Activation = hard-gate on `204`.** The harness calls `POST /api/v1/orchestration/start` with `[<wf-id>]` and **requires a `204`** before starting the observation window. The `204` is the cheap proof that seed + liveness heal succeeded (no `422` from `ProcessorLivenessValidator`); a non-`204` fails the run loud before wasting a 5-min window. (Note: `orchestration/start` is currently **validation-only / no-side-effect** — the workflow's cron `*/30 * * * * *` is what actually drives the ~10 fires once the workflow exists in the DB. The harness still calls `start` per FAULT-01's literal contract and uses the `204` as the gate.)
- **D-04:** **Final success signal = mirror the analyzer verdict.** Single entrypoint `phase-67-harness.ps1 -ScenarioId <id>`; the harness's **final exit code == the analyzer's verdict** (non-zero = FAIL), and it prints the `analyzer-reports/{scenarioId}.json` path. Infra-step failures (bring-up / reset / seed / activate / inject) **fail loud with distinct non-zero codes** so an infra abort is never mistaken for a FAIL verdict. Phase 66 remains the single source of truth for PASS/FAIL; the harness only adds orchestration.

### Fault-injection mechanism (FAULT-02)
- **D-05:** **Crash mechanism = `docker stop → dwell → `docker start`** of the targeted tier's container(s). Every container carries `restart: unless-stopped` (`compose.yaml`), so `docker kill` would auto-resurrect in ~1-2s — a non-deterministic, near-zero outage. **`docker stop` is the only mechanism that yields a deterministic, harness-controlled outage** the system must actually survive and recover from. The harness owns the recovery (`docker start`) timing. (kill + restart rejected: no controllable outage window.)
- **D-06:** **Whole-tier crash.** For the 2-replica tiers (`keeper`, `processor-sample`, `deploy.replicas: 2`), the fault crashes **all** replicas so the tier is genuinely DOWN during the dwell — in-flight messages must wait for redelivery when it returns. The roadmap names a "crash of the targeted tier," and a true tier outage is what makes the dedup/redelivery classifier (Phase 66 D-06) meaningful. (Single-replica rejected: tests partial degradation, not a tier outage.)
- **D-07:** **Injection timing = after N observed cron fires (~window midpoint).** The harness first watches the cron fire a few times (confirming a healthy baseline is actually running), then injects near the midpoint, leaving ≥half the window for recovery + drained completion. Triggering off **observed fires** (not blind wall-clock) guarantees the fault lands during real activity even if startup was slow. Exact N + observation mechanism = Claude's discretion (derive from cadence; observe via the same per-fire correlationId signal the Phase 66 analyzer uses — Phase 66 Research item #1).
- **D-08:** **Dwell ≥ one cron interval.** Hold the tier down long enough that **at least one full 30s cron fire happens entirely while it is dead**, so the proof genuinely contains a run that was disrupted and then recovered (visible redelivery / keeper-reinject). Exact dwell value = Claude's discretion (e.g. 45–60s — > one 30s interval, < half the window). (Sub-interval blip rejected: may slip between fires and perturb nothing.)

### Phase-67 reference scenario(s) (FAULT-02, FAULT-03)
- **D-09:** **Canonical fault proof = processor crash (TEST-02-shaped).** Crashing `processor-sample` mid-window is the richest recovery path: in-flight dispatches must be redelivered when it returns, exercising the dedup/redelivery classifier (Phase 66 D-06) that the entire effect-once machinery was built for; it is also the easiest tier to reason about (stateless worker). It is the single most representative proof that fault → recover → still-correct holds.
- **D-10:** **Run the TEST-01 no-fault baseline first, then the crash run.** Run TEST-01 (no fault) through the full harness to confirm the clean pipeline goes green, **then** the processor-crash run. This **isolates harness-wiring bugs from fault-injection bugs** — a red harness is far easier to debug once the no-fault path is proven. Costs one extra ~5-min window; accepted for debuggability. (Phase 66 already proved the analyzer on a happy window via direct `dotnet test`; D-10 additionally proves the *harness* wraps it.)
- **D-11:** **Acceptance bar = end-to-end + verdict; expect PASS.** Phase 67 is "done" when the harness runs **fully automated end-to-end and PRODUCES the analyzer's verdict with no human step** (FAULT-01/02/03). We **expect PASS** (recovery should hold; message-level redelivery during the fault is *reported, not failed* per the milestone carve-out) and treat any non-PASS as a **real finding to investigate** — but the harness's correctness does NOT hinge on the verdict value. Phase 68 formally asserts PASS for all 7. Clean separation: **67 = mechanism, 68 = proof results.**

### Scenario seam & teardown (FAULT-03)
- **D-12:** **Scenario-config table seam.** The harness consumes a `scenarioId → { targetContainers, faultType, injectAfterNFires, dwellSeconds, notes }` mapping. Phase 67 ships the **table reader + the entries it proves** (TEST-01 no-fault + TEST-02 processor crash); **Phase 68 just adds rows 03–07** (orchestrator / keeper / redis / rabbitmq / redis+rabbitmq). The seam IS the deliverable that lets Phase 68 be "just data, no new logic." Exact table format (PowerShell hashtable in-script vs `.psd1`/JSON) = Claude's discretion; default to an in-script hashtable for the close-script self-contained style.
- **D-13:** **Observe→analyze cohort = window-by-timestamps; reset halts fires.** No explicit cron-halt step. The analyzer scores the cohort of correlationIds fired within the recorded 5-min window (+ the Phase 66 drain). The workflow keeps existing until the **next run's `phase-65-reset` deletes its rows** (halting fires) before re-seed. This matches how reset already works and does **not** depend on `orchestration/stop` (also validation-only / no-op — it cannot halt firing). The harness records window start/end timestamps and passes them (with the scenario id) to the analyzer per Phase 66 D-05/D-09.
- **D-14:** **Between-runs = `phase-65-reset`, stack stays up.** Between baseline→crash (Phase 67) and between each of Phase 68's 7, the harness calls `phase-65-reset` (FLUSHALL + heal-wait + row-scoped DB reset) then re-seeds. Stack stays up — exactly what reset was built for; fast; per-run correlationId attribution stays clean. Crashed tiers are already restarted *within* their run (D-05 `docker start`), so the stack is whole before each reset. (Full down/up between runs rejected: far slower across 7 runs, redundant with reset.)
- **D-15:** **Final teardown = `docker compose down` (keep volumes + images).** After the last scenario analyzes, bring the stack down so no containers linger — satisfies the roadmap's explicit "tear down" step. **Keep volumes** (no Postgres/ES re-init) and **images** (no rebuild) so the next invocation's `phase-65-up` is fast; reset handles state cleanliness on the next run. One `down` at the very end, **never between runs**. (`down -v` rejected: forces migration + ES re-init cold start; leave-up rejected: doesn't satisfy "tear down.")

### Claude's Discretion
- Exact N-observed-fires threshold + the per-fire observation mechanism (D-07) and the precise dwell duration (D-08) — derive from cadence + traversal latency at research/planning.
- Scenario-config table file format + field names (D-12) — in-script hashtable vs externalized.
- The exact psql connection/auth invocation for the sentinel id lookup (D-02) — reuse the Phase 65 reset's `docker compose exec ... -d stepsdb` pattern.
- Distinct non-zero exit-code numbering for each infra-abort class (D-04).
- How the harness surfaces the per-step operator trace (console + the analyzer's report) and where harness logs/artifacts land.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase requirements & goal (locked)
- `.planning/ROADMAP.md` → "Phase 67: Fault-Injection Harness" (Goal + 3 Success Criteria) and "Phase 68" (the 7-scenario consumer this harness serves). Also the v8.0.0 milestone header (scope discipline — NO new product code; sources of truth = Prometheus + ES only).
- `.planning/REQUIREMENTS.md` → FAULT-01, FAULT-02, FAULT-03 + the TEST-section pass bar ("zero-missing + effect-once; message-level redelivery during the fault is reported, not failed").

### Pieces the harness orchestrates (DIRECT calls — Phase 65, complete)
- `scripts/phase-65-up.ps1` — minimal-stack bring-up (10 service types, `processor-badconfig` excluded by its profile gate). The harness's first step.
- `scripts/phase-65-reset.ps1` — FLUSHALL + heal-wait (liveness key reappears) + FK-safe row-scoped DB reset (preserves `processors`/`config_schemas`) + processor-set assertion. Stack stays UP. The harness's clean step; also the between-runs reset (D-14) and the workflow-row delete that halts fires (D-13).
- `tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs` — the `FanOutSeeder` RealStack fixture (`dotnet test --filter ~FanOutSeeder`); seeds/idempotently no-ops `v8-fanout-proof` (1 wf / 9 steps / 8 edges / 9 assignments). The build routine resolves the wf id internally but does NOT surface it — hence the harness's psql sentinel lookup (D-02).
- `.planning/phases/65-fan-out-workflow-seeder-clean-state-stack/65-CONTEXT.md` + `65-SPEC.md` — sentinel name `v8-fanout-proof`, the fixed 9-label set / topology / cron `*/30 * * * * *`, and the reset/up/seed contracts the harness depends on.

### Analyzer the harness invokes (Phase 66, complete)
- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` — the OBS-04 analyzer fixture. Invoked via `dotnet test --filter "Category=RealStack&FullyQualifiedName~Analyzer"`. Reads `-ScenarioId` (whitelist `^[A-Za-z0-9_-]+$`), writes `analyzer-reports/{scenarioId}.json`, exits non-zero on FAIL. `DrainMs = 60_000` + `PollToStableBudgetMs = 60_000` (worst-case 120s settle after window close) — the harness must allow this drain before reading the verdict. **Its header explicitly flags the Phase 67 open question:** a crashed+restarted tier resets its Prom counters mid-window, breaking delta continuity — which is WHY ES-primary completeness (counter-independent) is the binding arbiter and Prom reconciliation is corroborating only.
- `.planning/phases/66-prometheus-es-analyzer-pass-fail-engine/66-CONTEXT.md` — D-02 (write-then-assert, exit-code verdict), D-05 (post-window drain — harness controls timing), D-06 (duplicate-vs-redelivery classifier), D-09 (JSON + human report, scenario-id parameterized). The harness reads exit code + JSON per D-02/D-09.

### Activation + fault-injection surfaces
- `src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs` §38 — `POST /api/v1/orchestration/start` accepts a bare `List<Guid>` body, returns `204` (validation-only / no side-effect per the amended Acceptance Criteria). The harness's activation gate (D-03). `stop` is likewise validation-only — do NOT rely on it to halt firing (D-13).
- `compose.yaml` — every container has `restart: unless-stopped` (drives D-05's choice of `stop` over `kill`); `processor-sample` + `keeper` carry `deploy.replicas: 2` (drives D-06 whole-tier); the 10 service types + container naming (`sk-redis` named; `processor-sample` unnamed via `docker compose`) the fault ops target.
- `scripts/phase-62-close.ps1` / `scripts/phase-58-close.ps1` — the proven PowerShell patterns for `docker`/`docker compose exec`/`redis-cli`/`psql`/NDJSON health parsing the harness reuses for its docker fault ops + the sentinel id lookup.

### Carried-forward dependency decisions (locked)
- `.planning/phases/64-processor-work-structured-logging/64-CONTEXT.md` — `SampleProcessor` emits one `Step_<label>` log per execution; the COMPLETED-effect signal the analyzer parses (relevant to the per-fire observation the injection-timing trigger reads, D-07).
- `.planning/phases/63-seconds-granularity-cron/63-CONTEXT.md` — the `*/30 * * * * *` 6-field seconds cron drives the ~10 fires / 5-min window (D-07/D-08 cadence math).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- The **18 `scripts/phase-NN-close.ps1`** family + `phase-65-up.ps1` / `phase-65-reset.ps1` — the PowerShell orchestrator template (docker / `docker compose exec` / `redis-cli` / `psql` / NDJSON health parsing). The harness is a new sibling that *composes* them.
- `FanOutSeederE2ETests.cs` (`~FanOutSeeder` filter) + `AnalyzerE2ETests.cs` (`~Analyzer` filter, `-ScenarioId`) — the two `dotnet test` invocations the harness drives; both are self-verifying RealStack fixtures with stable filters.
- `phase-65-reset.ps1`'s `docker compose exec ... -d stepsdb` psql pattern — directly reusable for the sentinel `SELECT id FROM workflows WHERE name='v8-fanout-proof'` (D-02).

### Established Patterns
- **Shell-native infra ops in PowerShell** (the close-script discipline) — drove D-01 (PS orchestrator, not a C# fixture).
- **`dotnet test --filter` as the runnable-artifact invocation** (Phase 65 seeder, Phase 66 analyzer) — the harness slots both into the same step style.
- **Exit-code-as-verdict** (Phase 66 D-02) — drove D-04 (harness mirrors the analyzer exit code; infra aborts use distinct codes).
- **`restart: unless-stopped` on all containers** — drove D-05 (`stop`, not `kill`, for a deterministic outage).
- **Per-run reset, stack stays up** (Phase 65 ENV-02) — drove D-14 (reset between runs) and D-13 (workflow-row delete halts fires).

### Integration Points
- `phase-65-up.ps1` / `phase-65-reset.ps1` (shell-out), `dotnet test --filter ~FanOutSeeder` / `~Analyzer` (process invocation + exit code), `POST /api/v1/orchestration/start` (HTTP 204 gate), `docker stop/start <tier>` (fault op), `psql` sentinel lookup, `docker compose down` (teardown) — the harness's entire integration surface. **No product source is touched.**
- The Phase 66 analyzer's `analyzer-reports/{scenarioId}.json` + exit code — the harness's verdict input.

</code_context>

<specifics>
## Specific Ideas

- Two reference runs in Phase 67: **TEST-01** (no fault, baseline-first) → **TEST-02-shaped processor crash**. Scenario ids are `TEST-01` / `TEST-02` (match the analyzer's `-ScenarioId` whitelist `^[A-Za-z0-9_-]+$`).
- Fault recipe (D-05/06/07/08): `docker stop` **all** `processor-sample` replicas at ~window midpoint (after N observed fires), hold ≥ one 30s cron interval (~45–60s) so ≥1 fire is fully disrupted, then `docker start`.
- Harness flow (D-01): `phase-65-up` → [per run: `phase-65-reset` → `dotnet test ~FanOutSeeder` → psql wf-id → `POST /orchestration/start` (require 204) → observe → (crash runs: inject mid-window) → window close + drain → `dotnet test ~Analyzer -ScenarioId <id>` → record verdict] → `docker compose down`.
- Scenario-config table (D-12) keys: `targetContainers`, `faultType`, `injectAfterNFires`, `dwellSeconds`, `notes`. Phase 67 fills TEST-01 (no fault) + TEST-02 (processor); Phase 68 adds 03–07.
- Phase 66 analyzer header already names the Phase 67 counter-reset hazard: a crashed+restarted tier resets its Prom counters mid-window → ES-primary completeness is the binding arbiter, Prom reconciliation corroborating only. The harness must NOT treat a counter discontinuity as a fault.

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope. The 7-scenario sweep + formal all-7 PASS assertions are Phase 68 (this harness's consumer); the seeder/reset/up are Phase 65; the analyzer/PASS-FAIL engine is Phase 66 — all already scoped in the roadmap.

</deferred>

---

*Phase: 67-fault-injection-harness*
*Context gathered: 2026-06-14*
