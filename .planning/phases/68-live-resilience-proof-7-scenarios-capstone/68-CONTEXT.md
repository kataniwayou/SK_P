# Phase 68: Live Resilience Proof — 7 Scenarios (Capstone) - Context

**Gathered:** 2026-06-15
**Status:** Ready for planning

<domain>
## Phase Boundary

The **milestone capstone**: run all **7** resilience proofs through the already-built
`scripts/phase-67-harness.ps1` and produce an **automated PASS verdict for each**.

- TEST-01 happy path (no fault) — locked PASS in Phase 67 (reference run)
- TEST-02 processor crash — locked PASS in Phase 67 (reference run)
- TEST-03 orchestrator crash · TEST-04 keeper crash · TEST-05 redis crash · TEST-06 rabbitmq crash · TEST-07 redis+rabbitmq combined crash — **new this phase**

Each scenario runs its 5-minute / 30s-cron window and PASSES iff **zero-missing** (every
triggered correlationId reaches both sinks F1+F2) AND **effect-once** (each step's COMPLETED
effect exactly once per correlationId). Message-level redelivery during the fault is
**reported, not failed**. Truth = **Prometheus + Elasticsearch ONLY** — no human verification,
no triple-SHA infra net-zero gate.

**Scope discipline (locked, inherited from v8.0.0 milestone + Phase 67 D-12):** This phase
adds **NO new product code and NO new recovery logic** — it *proves* the existing machinery
(keeper, exactly-once-effect, slot-array, per-replica liveness). Phase 68 is **"just data"
(5 new scenario-table rows) + a thin sweep wrapper + a roll-up summary**. The harness, seeder,
reset, analyzer, and PASS/FAIL engine are all complete (Phases 65/66/67).

**In scope:**
- 5 new rows (TEST-03..07) in the Phase 67 scenario-table seam (same `{ targetContainers,
  faultType, injectAfterNFires, dwellSeconds, notes }` shape).
- A thin sweep-runner wrapper that drives `phase-67-harness.ps1` across all 7 ids.
- A capstone roll-up summary artifact aggregating the 7 verdicts.
- Running the sweep and proving all 7 produce an automated PASS verdict.

**Out of scope (other phases / locked):**
- The harness machinery + fault-injection mechanism (Phase 67, complete — this phase *feeds it data + drives it*).
- The seeder / reset / bring-up (Phase 65) and the analyzer / PASS-FAIL engine (Phase 66).
- The seconds-cron (Phase 63) and processor payload/`Step_*` logging (Phase 64).
- **No new product code, no new product log/metric, no new recovery logic.**
- The triple-SHA infra net-zero close gate (`psql \l` / `redis-cli --scan` / `rabbitmqctl
  list_queues` BEFORE==AFTER) — **explicitly dropped for v8.0.0**, not a pass criterion.

</domain>

<decisions>
## Implementation Decisions

### Scenario-table rows TEST-03..07 (the "just data" deliverable)
- **D-01:** **Uniform fault params across all 5 new rows — `dwellSeconds = 45`,
  `injectAfterNFires = 4`, `faultType = 'stop-start'`** — mirroring the proven TEST-02
  recipe exactly. No per-fault-class tuning. Rationale: keeps Phase 68 genuinely "just data,"
  reuses the only timing already proven live (TEST-02 Pass 10/10), and 45s already spans ≥1
  full 30s cron fire while the tier is dead (the disruption guarantee, D-08). The remaining
  ~135s of window + the analyzer's 120s drain give recovery room for the stateful tiers.
  - **D-01a — `targetContainers` per row (compose SERVICE names, whole-tier per Phase 67 D-06):**
    - TEST-03 → `@('orchestrator')` — single container `sk-orchestrator`; on restart it
      re-hydrates Quartz crons from the L2 parent index.
    - TEST-04 → `@('keeper')` — **both replicas** (`deploy.replicas: 2`); whole keeper tier
      DOWN during dwell (total liveness blackout, the hardest keeper proof — see Note 1).
    - TEST-05 → `@('redis')` — single container `sk-redis`.
    - TEST-06 → `@('rabbitmq')` — single container `sk-rabbitmq`.
    - TEST-07 → `@('redis','rabbitmq')` — combined; both stopped, dwell, both started.
  - **D-01b — Planner-verify (carry the Phase 67 caveat forward):** 45s/N=4 is proven only
    for the *stateless processor* tier. The planner/executor MUST treat a non-PASS on any
    stateful tier (orchestrator re-hydration, keeper takeover, redis/rabbitmq reconnect +
    redelivery) as a **real finding to investigate first** — NOT auto-bump the dwell. Tuning
    a single row's dwell upward is a permitted *deviation with rationale* only if a verdict
    FAIL is traced to insufficient recovery time, never a blind retry. (See D-04 flake policy.)

### Sweep runner & evidence (Success Criterion 5)
- **D-02:** **Thin wrapper script, run-all + collect.** A new `scripts/phase-68-*.ps1`
  (close-script-family sibling) loops `pwsh -File scripts/phase-67-harness.ps1 -ScenarioId <id>`
  over all 7 ids **in numeric order (TEST-01 → TEST-07)** and runs **every** scenario even if
  an earlier one fails (no fail-fast) — so one sweep yields all 7 results. It re-uses the
  harness verbatim (no harness changes beyond the 5 data rows). Wrapper **final exit is
  non-zero if ANY scenario is non-PASS**; zero only when all 7 PASS. The wrapper does NOT
  re-implement any harness step — it only loops + records.
- **D-03:** **Roll-up summary + the 7 per-scenario JSONs.** Keep the existing
  `analyzer-reports/{scenarioId}.json` (7 of them, one per run) AND emit **one capstone
  summary** (a 7-row table: scenarioId · verdict · zero-missing · effect-once ·
  trigger/complete counts · harness exit code). The summary is the single-glance milestone
  proof. Exact format + path = Claude's discretion (default: a `phase-68` summary `.json` +
  a human-readable `.md`/console table, in the close-script style). The summary is
  **derived from the 7 JSON reports + each harness exit code** — it adds no new scoring.

### Flake / re-run policy (acceptance standard for "proven")
- **D-04:** **Re-run allowed on INFRA-ABORT only; never on a verdict FAIL.** The harness exit
  table already separates a **verdict FAIL (exit 1)** from **infra aborts (exit 10/20/25/30/40/
  50/60/70)** and a **bad-arg (64)**. A scenario is "proven PASS" when it produces an
  analyzer **PASS verdict (exit 0)** on a clean end-to-end run. If a scenario instead exits on
  a distinct **infra code** (e.g. a tier missed the bounded 90s health-wait, ES indexing lag,
  a teardown hiccup), **re-running that one scenario is permitted and must be documented**
  (which code, why). A **verdict FAIL is a real finding to investigate** — it is NEVER retried
  away. **No auto-retry in the runner** (operator re-invokes the single failed scenario); the
  wrapper's job is to run + record + distinguish infra-abort vs verdict-FAIL in its roll-up,
  not to mask flake. This keeps "no human verification *of correctness*" intact while allowing
  operational re-runs of a flaky *infra* step.

### Claude's Discretion
- Exact roll-up summary file format + path + console rendering (D-03) — default to the
  `phase-68` close-script style (JSON + human table).
- Wrapper internals: how it captures each harness exit code, how it tags infra-abort vs
  verdict-FAIL rows in the summary, where its console log lands (D-02/D-04).
- Whether the wrapper accepts an optional id-subset arg (e.g. re-run a single failed scenario)
  vs always all-7 — convenience only; default all-7, single-id re-run is just the bare harness.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase requirements & goal (locked)
- `.planning/ROADMAP.md` → "Phase 68: Live Resilience Proof — 7 Scenarios (Capstone)" (Goal +
  5 Success Criteria) and the **v8.0.0 milestone header** (scope discipline: NO new product
  code / NO new recovery logic; truth = Prometheus + ES only; triple-SHA net-zero gate
  explicitly dropped; pass bar = zero-missing + effect-once, redelivery reported-not-failed).
- `.planning/REQUIREMENTS.md` → TEST-01..TEST-07 + the TEST-section pass bar.

### The harness this phase feeds + drives (Phase 67, complete — DO NOT re-implement)
- `scripts/phase-67-harness.ps1` — **the artifact this phase extends.** The in-script
  `$Scenarios = [ordered]@{...}` table (≈ lines 87-90) is where the 5 new rows go (D-01).
  Note its full flow (STEP A0 image rebuild → A bring-up → B reset → B1 clean-orchestrator →
  C seed → D wf-id → E 204 gate → F observe+crash+recover → H analyze → Z teardown) and the
  **EXIT-CODE TABLE (D-04 / lines 31-42)** the sweep wrapper + flake policy depend on
  (0=PASS, 1=verdict FAIL, 10-70=distinct infra aborts, 64=bad arg).
  - **PLANNER-VERIFY (stale comment):** the analyze step hardcodes
    `--filter-method "*Analyze_HappyPath_Window_Yields_Pass*"` for **every** scenario, and the
    IN-04 comment (≈ lines 340-345) says *"for a fault scenario a FAIL verdict is the EXPECTED
    outcome."* That wording is **stale for this capstone** — a *recovered* fault run MUST
    assert **PASS** (zero-missing + effect-once hold). Confirm the fixture's PASS assertion is
    correct for all 7, and rename the `HappyPath` method (or add a verdict-agnostic alias) if
    the name is misleading — same change must keep the harness `--filter-method` pattern in sync.
- `.planning/phases/67-fault-injection-harness/67-CONTEXT.md` — the locked harness decisions:
  D-05 (`stop`/`start`, not `kill`), **D-06 whole-tier crash for the 2-replica tiers**,
  D-07 (inject after N observed fires), D-08 (dwell ≥ one cron interval), D-10 (TEST-01
  baseline-first ordering), D-12 (scenario-table seam — "68 = just data rows 03-07"),
  D-13 (window-by-timestamps cohort; reset halts fires), D-14 (between-runs reset, stack up),
  D-15 (one `down` at the very end), D-16 (analyzer env-var window/scenario-id seam).

### Analyzer + PASS/FAIL engine (Phase 66, complete — the verdict source)
- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` — the OBS-04 fixture the harness
  invokes; reads `SCENARIO_ID` / `WINDOW_START_UTC` / `WINDOW_END_UTC` (D-16 env seam), writes
  `analyzer-reports/{scenarioId}.json`, exits non-zero on FAIL. Header flags the
  **counter-reset hazard**: a crashed+restarted tier resets its Prom counters mid-window →
  **ES-primary completeness is the binding arbiter, Prom is corroborating only**. The 7
  fault rows all trigger this path — the roll-up must NOT treat a Prom discontinuity as a fault.
- `.planning/phases/66-prometheus-es-analyzer-pass-fail-engine/66-CONTEXT.md` — D-06
  (duplicate-effect vs message-redelivery classifier — the "redelivery reported, not failed"
  guarantee), D-02 (write-then-assert, exit-code verdict), D-09 (per-scenario JSON report).

### Fault-target topology
- `compose.yaml` — service names + replica counts the 5 new rows target: `orchestrator`
  (single `sk-orchestrator`), **`keeper` (`deploy.replicas: 2`)**, `redis` (`sk-redis`),
  `rabbitmq` (`sk-rabbitmq`), **`processor-sample` (`deploy.replicas: 2`)**. Every container
  has `restart: unless-stopped` (drives `stop`-not-`kill`). `processor-badconfig` is excluded
  from the minimal stack.

### Reset / bring-up the harness shells (Phase 65, complete)
- `scripts/phase-65-up.ps1` (10-service-type health gate) and `scripts/phase-65-reset.ps1`
  (FLUSHALL + heal-wait + FK-safe row-scoped DB reset; stack stays up) — the harness's
  per-run clean steps; the sweep relies on reset between each of the 7.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `scripts/phase-67-harness.ps1` — drives **one** scenario end-to-end; the 5 new rows make it
  cover TEST-03..07 with **zero machinery change**. The sweep wrapper just loops it 7×.
- The `scripts/phase-NN-close.ps1` family + `phase-65-up.ps1` / `phase-65-reset.ps1` — the
  PowerShell orchestrator template (docker / `docker compose ps` NDJSON health parse / exit-code
  discipline) the new `phase-68` sweep wrapper + roll-up summary should mirror.
- `analyzer-reports/{scenarioId}.json` (Phase 66 output) — the per-scenario verdict the roll-up
  aggregates; no new scoring needed, the summary just reads + tabulates these 7.

### Established Patterns
- **Scenario-table-as-data seam** (Phase 67 D-12) — TEST-03..07 are added by appending hashtable
  rows of the identical shape; no new control flow in the harness.
- **Whole-tier `docker compose stop <service>`** (Phase 67 D-06) — a 2-replica tier (keeper)
  goes fully DOWN; the same NDJSON-per-replica health-wait gates its recovery.
- **Exit-code-as-verdict + distinct infra-abort codes** (Phase 67 D-04) — lets the sweep wrapper
  + flake policy (D-04 here) cleanly separate "re-runnable infra abort" from "real verdict FAIL."
- **ES-primary arbiter, Prom corroborating** (Phase 66) — every fault row resets Prom counters
  mid-window; correctness still scores cleanly off ES completeness.

### Integration Points
- Wrapper → `pwsh -File scripts/phase-67-harness.ps1 -ScenarioId <id>` (process invocation +
  exit code) ×7; reads the 7 `analyzer-reports/{id}.json`; emits one roll-up summary.
- **No product source touched.** The only file edits: the harness's scenario table (5 rows),
  a new sweep wrapper script, a new roll-up summary artifact, and (planner-verify) a possible
  test-fixture method rename in `AnalyzerE2ETests.cs`.

</code_context>

<specifics>
## Specific Ideas

- **Note 1 — keeper whole-tier (both replicas) is the hard proof, by design.** Stopping
  `keeper` stops both replicas (`deploy.replicas: 2`) → a **total liveness blackout** during
  the 45s dwell, not a single-replica failover. This is the most aggressive keeper proof
  (carried forward from Phase 67 D-06's "whole-tier for the 2-replica tiers"). If planning
  finds the system is designed only to survive *single-replica* keeper loss (failover) and a
  total blackout is out of the recovery envelope, that's a **real finding** to surface to the
  spec owner — NOT silently weakened to single-replica. Default stays whole-tier per D-06.
- 5 new scenario ids are `TEST-03`..`TEST-07` (match the analyzer `-ScenarioId` whitelist
  `^[A-Za-z0-9_-]+$`). Sweep order = TEST-01 → TEST-07 (baseline-first per Phase 67 D-10).
- Uniform recipe per row: `faultType='stop-start'`, `injectAfterNFires=4`, `dwellSeconds=45`
  (TEST-01 stays `faultType='none'`, N=0, dwell=0 — already in the table).
- Re-runs (D-04) are operator-initiated single-scenario re-invocations of the bare harness on
  an infra-abort exit; the wrapper does not auto-retry.

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope. Per-fault-class dwell tuning was considered and
**rejected in favor of the uniform proven recipe** (D-01); it returns only as a permitted
deviation-with-rationale if a verdict FAIL is traced to insufficient recovery time (D-01b).
Bounded auto-retry in the runner was considered and **rejected** in favor of operator-initiated
re-runs on infra-abort only (D-04).

</deferred>

---

*Phase: 68-live-resilience-proof-7-scenarios-capstone*
*Context gathered: 2026-06-15*
