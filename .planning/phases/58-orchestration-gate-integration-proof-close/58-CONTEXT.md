# Phase 58: Orchestration-Gate Integration Proof & Close - Context

**Gathered:** 2026-06-13
**Status:** Ready for planning

<domain>
## Phase Boundary

The **v6.0.0 milestone closeout**. A RealStack end-to-end proof that **Gate A** (the Phase-57 startup config-schema↔config-type compatibility check) correctly **composes** with the *existing, unchanged* orchestration-start `ProcessorLivenessValidator` gate:

- **CFG-08 (positive block):** an orchestration whose graph includes a config-**incompatible** (never-Healthy) processor is blocked at orchestration start with **422** via `ProcessorLivenessValidator` ("absent").
- **CFG-09 (negative control):** a config-**compatible** processor reaches Healthy, writes its L2 liveness, and its orchestrations start normally — proving Gate A is **not** a false-positive blocker.

Sealed behind the milestone close gate (N=3 consecutive GREEN + triple-SHA `psql`/`redis`/`rabbitmq` BEFORE==AFTER net-zero, Release + Debug 0-warning).

**Requirements: CFG-08, CFG-09** (locked in REQUIREMENTS.md). This is the **final phase of v6.0.0** — the live counterpart to the Gate A build shipped in Phases 56–57. It is an **adaptation** of the proven Phase-55/49 close-gate + SC E2E harness to prove a *composition* property (Gate A × liveness gate), NOT new recovery infrastructure (v6 left the v5 slot-array/3-state recovery machinery unchanged).

**Build-before-proof split (Phase 54/55 carry):** the 0-warning dual-config build gate + new/adapted E2E **compiles** is the phase's autonomously-verifiable deliverable; the live N=3×GREEN run is **operator-gated**.

</domain>

<decisions>
## Implementation Decisions

### Config-incompatible processor mechanism (CFG-08 subject)
- **D-01 — Second real container:** Produce the config-incompatible CFG-08 subject as a **new processor console** (e.g. `Processor.BadConfig`) — a trivially-distinct concrete class → distinct embedded SourceHash → its own DB `Processor` row → its own `ConfigSchemaId` — running **concurrently** alongside the existing `Processor.Sample` (which serves as the CFG-09 compatible subject). Rationale: one binary = one SourceHash = one DB row = one `ConfigSchemaId`, so `Processor.Sample` cannot be both compatible and incompatible simultaneously. This is the most faithful to the project's RealStack-container close-gate culture (`Processor.Sample` itself was built this way in Phase 28); the SourceHash/liveness machinery works **unmodified**.
- **D-02 — The clash drives the incompatibility:** `Processor.BadConfig` is seeded with a `ConfigSchemaId` whose `Definition` **clashes** with its `TConfig` (a schema-valid payload that would not deserialize — e.g. a property the schema types `integer` that the CLR config types `string`), so Gate A's covers-check (Phase-57 `ConfigSchemaCoverageCheck.Evaluate`) fails and `MarkHealthy` is withheld. The exact clash shape (which property / which type pair) is **Claude's discretion**, consistent with Phase-57 D-02/D-04 (real type clash on a property present in both; deep/nested modeled).
- **D-03 — `Processor.Sample` is the CFG-09 compatible subject:** Seed `Processor.Sample` with a **non-null** `ConfigSchemaId` whose `Definition` is covered by `SampleConfig` (`SampleProcessor : BaseProcessor<SampleConfig>`). Today the SC tests seed it with `ConfigSchemaId: null` (Gate A skipped) — Phase 58 must seed a **compatible non-null** config-schema so Gate A actually *runs and passes*, proving the positive path. (Both the null-skip and the compatible-pass paths are valid Healthy outcomes; CFG-09 needs the compatible-**pass** to prove Gate A is not a false-positive blocker.)

### Compose placement of the broken processor
- **D-04 — Dedicated tier behind a Compose profile:** Add the new `processor-badconfig` service to `compose.yaml` gated behind a **Compose profile** so the DEFAULT dev / `docker compose up` stack stays clean. The close gate + the Gate-A E2E explicitly bring it up (`--profile <name>`). Keeps the v6 default stack free of a deliberately-broken processor while making the proof subject available on demand.
- **D-05 — It is net-zero-harmless (Phase-57 D-09 stay-up posture):** The incompatible processor still flips `MarkReady()` (booted, concluded it must not serve — **no crash-loop**), so its Docker `/ready` healthcheck passes normally; it withholds `MarkHealthy` (no `skp:{id}` L2 liveness key) and **binds no dispatch queue**. Therefore it contributes **nothing** to the steady-state triple-SHA (no liveness key in the redis scan, no `{procId:D}` queue in the rabbitmq list) and cannot hang the compose stack. The close gate must **expect** its liveness key to be ABSENT as a pre-condition (the opposite of the `Processor.Sample` healthy pre-flight requirement), not treat absence as failure.

### CFG-08 proof assertions (prove Gate A is the *cause*)
- **D-06 — Assert all three (logged clash + absent liveness + 422):** The CFG-08 E2E must positively assert the running incompatible processor (a) emitted the Phase-57 **D-10 Error-level config-clash log** (processor id + `ConfigSchemaId` + the specific clash: property + schema-type vs CLR-type) — poll Elasticsearch via the existing `ElasticsearchTestClient.PollEsForLog` seam; (b) wrote **NO** `skp:{id}` L2 liveness key (absent); and (c) orchestration-start returns **422** via `ProcessorLivenessValidator`. The **log assertion is load-bearing** — it is what distinguishes "Gate A withheld Healthy" from "the processor simply wasn't running," making CFG-08 a true Gate-A causation proof rather than an absence coincidence.

### E2E test surface
- **D-07 — New Gate-A tests + retag the v5 recovery SCs into the phase-58 live run:** Add new Gate-A composition E2E tests (CFG-08 incompatible→422; CFG-09 compatible→Healthy→starts) **AND** retag the existing `SC1`/`SC2`/`SC3` v5 recovery E2E suite `[Trait("Phase","58")]` so the phase-58 close-gate live run is **full regression**. v6 did not change the recovery/slot-array machinery, so the milestone close proves the whole system still holds end-to-end (the milestone-close "seal everything" intent). All stay `[Trait("Category","RealStack")]` (excluded from the hermetic suite; included in the live gate).

### Close gate (clone phase-55, v6 seed deltas)
- **D-08 — Clone `scripts/phase-55-close.ps1` → `scripts/phase-58-close.ps1`, triple-SHA verbatim:** The slot-array net-zero machinery is **unchanged in v6** — keep the proven protocol identical: idempotent steady-state Processor-row seed, compose-health pre-flight, BOTH-config 0-warning build gate, **N=3** consecutive-GREEN identical-fact-count cadence, `psql \l` SHA, unfiltered `redis-cli --scan` SHA, `rabbitmqctl -q list_queues name` SHA, the additive `skp:msg:*` count==0 assertion (A19 active reclaim), the separate `skp-dlq-1` depth==0 check, steady-state exclusions (`skp:{procId:D}` liveness + `_bus_` transients). Retitle to Phase 58.
- **D-09 — v6 seed deltas (the only changes vs phase-55):**
  - **(a)** Seed **two** config-`Schema` rows (one compatible-with-`SampleConfig`, one clashing-with-`Processor.BadConfig`'s config type) + point the two `Processor` rows' `ConfigSchemaId` at them. Seed is **CREATE-IF-ABSENT only** — never re-edit — honoring Phase-57 **D-06 frozen-once-referenced** (a referenced schema's `Definition` is locked; an edit attempt returns **409**). The seed must therefore be GET-or-create against the schema and the processor rows.
  - **(b)** The `Processor.BadConfig` row's procId writes **no** liveness key and binds **no** queue (D-05), so it needs **no** steady-state exclusion in the SHA — it is simply absent from both snapshots.
  - **(c)** Verify the **v6 `Processor.Sample` version string** for the seed (`src/Processor.Sample/appsettings.json`) — phase-49 used `"3.5.0"`; confirm the current value rather than carrying it blindly.
  - **(d)** The gate must bring up the badconfig profile (`docker compose --profile <name> up -d --build`) so the CFG-08 subject is present during the live run.

### Carried-forward locks (precedent / convention — captured, not re-decided)
- **D-10 — N=3 consecutive GREEN** with the identical-fact-count Smell-A guard (phase-39/49/55 precedent).
- **D-11 — Build gate FIRST** (autonomously-verifiable): `dotnet build SK_P.sln -c Release` AND `-c Debug` both 0-warning, AND the new/adapted RealStack E2E tests **COMPILE** (excluded from hermetic run by `Category=RealStack` but must build). `scripts/phase-58-close.ps1` exists and is syntactically valid.
- **D-12 — Live N=3×GREEN run is operator-gated** via a HUMAN-UAT runbook (`58-HUMAN-UAT.md`), mirroring Phase 55/49/39. Requires the rebuilt v6 docker stack incl. the badconfig profile. **CFG-08/09 stay unticked** until the operator's GREEN run. The embedded SourceHash of each processor binary must match its host build or the liveness gate false-passes/times out.
- **D-13 — Stable Processor rows seeded idempotently** (genuine embedded SourceHash via GET-or-create on `uq_processor_source_hash`) so each procId — hence `Processor.Sample`'s `skp:{procId:D}` liveness key + `{procId:D}` dispatch queue (steady-state) — is stable across the whole 3-run gate.

### Claude's Discretion
- The exact config-schema clash shape for `Processor.BadConfig` (which property, which schema-type↔CLR-type pair) — consistent with Phase-57 D-02/D-04 (real deserialize-failing clash; nested/enum/array modeled).
- The `Processor.BadConfig` project shape (minimal concrete class to force a distinct SourceHash; reuse `BaseProcessor.Core` + the `Processor.Sample` Dockerfile/csproj template) and the Compose profile name.
- The compatible config-schema `Definition` for `Processor.Sample`'s CFG-09 seed (any schema `SampleConfig` covers).
- xUnit collection/parallelization shaping for the new Gate-A tests; reuse `RealStackWebAppFactory` + `PollForHealthyLivenessAsync` + `ElasticsearchTestClient.PollEsForLog` precedent. The incompatible-processor liveness-**absence** poll is the inverse of `PollForHealthyLivenessAsync` (poll-until-stably-absent within a bound).
- Host-Redis polling / ES seam-log assertion mechanics — reuse the SC harness precedent.

### Folded Todos
None — no pending todos matched this phase.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase requirements & roadmap (locked)
- `.planning/REQUIREMENTS.md` — **CFG-08** (RealStack: config-incompatible processor blocked 422 via `ProcessorLivenessValidator` "absent"), **CFG-09** (config-compatible processor reaches Healthy → writes liveness → orchestrations start; Gate A not a false-positive blocker), the milestone invariant `payload ⊨ ConfigSchemaId ∧ ConfigSchemaId ⊨ configType ⟹ payload deserializes`. **MUST read.**
- `.planning/ROADMAP.md` §"Phase 58: Orchestration-Gate Integration Proof & Close" — goal + 3 success criteria (incl. close-gate net-zero).
- `.planning/PROJECT.md` §"Current Milestone: v6.0.0" — Gate A / Gate B framing; source of truth is the locked Gate A/Gate B planning analysis (2026-06-12 conversation).

### Gate A being proven (Phase 57)
- `.planning/phases/57-startup-config-schema-fetch-gate-a/57-CONTEXT.md` — Gate A covers-semantics (D-01/02/04), **D-09 stay-up posture** (MarkReady flips, MarkHealthy withheld, no endpoint bind — the basis for D-05 here), **D-10 Error-level clash log** (the CFG-08 D-06 log assertion target), **D-06 frozen-once-referenced** schema (the D-09a seed constraint).
- `.planning/phases/57-startup-config-schema-fetch-gate-a/57-VERIFICATION.md` — what Gate A was verified to do hermetically (the live proof's hermetic counterpart).

### Proven close-gate template (clone source)
- `scripts/phase-55-close.ps1` — the v5.0.0 triple-SHA close gate to clone. Its header documents every steady-state exclusion + net-zero discipline (unfiltered scan, `skp:msg:*` count==0, procId/`_bus_` exclusions, separate DLQ depth check, 3-GREEN cadence). **Clone this.**
- `scripts/phase-49-close.ps1` — the v4.0.0 ancestor (phase-55 is itself a clone of phase-49 ← phase-39).
- `.planning/phases/55-live-proof-close-gate/55-CONTEXT.md` — the decision record for the close-gate clone discipline (D-05..D-11) this phase inherits.

### E2E harness (reuse) + recovery SCs (retag)
- `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` — `RealStackWebAppFactory` host overrides + net-zero teardown (`L2KeysToCleanup`) + `SeedProcessorAsync` (currently `ConfigSchemaId: null` at ~line 325 — the seed Phase 58 makes non-null) + `PollForHealthyLivenessAsync` + ES seam-log poll precedent.
- `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs`, `SC2RecoveryPathsE2ETests.cs`, `SC3PauseResumeOutageE2ETests.cs` — the v5 recovery E2E suite to **retag** `[Trait("Phase","58")]` (D-07).

### Processor + gate code shapes
- `src/Processor.Sample/SampleProcessor.cs` — `SampleProcessor : BaseProcessor<SampleConfig>`; the `SampleConfig` type whose covered-schema is the CFG-09 compatible seed; the csproj/Dockerfile template for the new `Processor.BadConfig`. `src/Processor.Sample/appsettings.json` — the version string for the seed (D-09c).
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` — Gate A placement (Loop B config fetch + check-before-bind + decoupled MarkReady/MarkHealthy) the badconfig processor exercises.
- `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` — the existing orchestration-start gate that returns 422 on absent liveness (the integration seam being proven; **unchanged**).
- `compose.yaml` — the `processor-sample` tier (~line 265) is the template for the new profile-gated `processor-badconfig` tier (D-04).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `RealStackWebAppFactory` host overrides + net-zero teardown (`L2KeysToCleanup`) + `PollForHealthyLivenessAsync` + `ElasticsearchTestClient.PollEsForLog` — reused by all SC/Sample E2E tests; the new Gate-A tests reuse the same harness.
- `SeedProcessorAsync` (`SampleRoundTripE2ETests.cs:300`) — the Processor-row seed (today `ConfigSchemaId: null`); Phase 58 extends it to seed config-`Schema` rows + non-null `ConfigSchemaId` (compatible for Sample, clashing for BadConfig).
- `scripts/phase-55-close.ps1` — verbatim triple-SHA protocol; only the two-schema/two-processor CREATE-IF-ABSENT seed + badconfig-profile bring-up + version-string verify change (D-08/D-09).
- `Processor.Sample` csproj + multi-stage Dockerfile + compose tier — the template for the new `Processor.BadConfig` (a minimal distinct concrete class for a distinct SourceHash).

### Established Patterns
- **Gate A stay-up posture (Phase-57 D-09)** — config-incompatible processor: `MarkReady()` flips (`/ready` green, no crash-loop), `MarkHealthy()` withheld (no L2 liveness key), no dispatch-endpoint bind. Makes the badconfig container net-zero-harmless and compose-safe (D-05).
- **One-way Healthy latch** — there is no `MarkUnhealthy`; "incompatible" = never latching → liveness key never written → `ProcessorLivenessValidator` reads "absent" → 422.
- **Frozen-once-referenced schema (Phase-57 D-06/D-08)** — a referenced schema `Definition` is locked; an edit returns 409. The close-gate seed must be CREATE-IF-ABSENT, never re-edit (D-09a).
- Triple-SHA BEFORE==AFTER (`psql \l` / `redis --scan` / `rabbitmq list_queues name`); additive `skp:msg:*` count==0 + `skp-dlq-1` depth==0; steady-state exclusions; N=3 identical-fact-count cadence.
- xUnit v3 on Microsoft.Testing.Platform: `dotnet test --filter` is ignored (MTP0001); filtered runs use the compiled `BaseApi.Tests.exe` with `--filter-not-trait Category=RealStack` (hermetic) / native MTP flags. (Carry into the close script + runbook.)
- `[Trait("Category","RealStack")]` excludes E2E from the hermetic suite; `[Trait("Phase","58")]` includes them in the close-gate live run.

### Integration Points
- The live stack must run **rebuilt v6 images** + the **badconfig profile** (`docker compose --profile <name> up -d --build baseapi-service orchestrator processor-sample keeper processor-badconfig`); each embedded SourceHash must match its host build or the liveness gate false-passes/times out.
- The CFG-08 422 path is already wired by the existing `ProcessorLivenessValidator` (orchestration validator order, Phase-14: Cycle → SchemaEdge → PayloadConfigSchema (Gate B) → ProcessorLiveness). Gate A is processor-side at startup, NOT in this chain — Phase 58 proves they compose.
- `skp-dlq-1` is the single surviving DLQ (keeper-local since Phase 53); the gate's DLQ loop stays the single-element `@('skp-dlq-1')`.

</code_context>

<specifics>
## Specific Ideas

- This close gate proves a **composition** property, not a round-trip — the headline distinction from Phase 49/55 (which proved forward/recovery). The two new actors are (1) a second, deliberately-broken processor container and (2) a non-null compatible config-schema seed for `Processor.Sample`.
- The **log assertion (D-06)** is the causation linchpin: absent-liveness + 422 alone is observationally identical to "processor not running." Polling the Phase-57 D-10 config-clash Error log in Elasticsearch is what upgrades CFG-08 from "absence coincidence" to "Gate A fired."
- The badconfig processor is net-zero-harmless *by virtue of Gate A's own design* (D-09 stay-up → no liveness key, no queue) — the very mechanism under test is what keeps it out of the triple-SHA. No new steady-state exclusion is needed for it.
- `Processor.Sample` flips from `ConfigSchemaId: null` (Gate-A-skipped, today) to a compatible non-null config-schema (Gate-A-passed) — CFG-09 specifically needs Gate A to *run and pass*, not be skipped.

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within the CFG-08/09 + close-gate scope. This phase IS the v6.0.0 milestone close gate; no further deferral.

(Generalizing Gate A to input/output schema↔type compatibility and per-step operator config diagnostics remain documented Future Requirements from Phase 57 — out of scope here, unchanged.)

</deferred>

---

*Phase: 58-orchestration-gate-integration-proof-close*
*Context gathered: 2026-06-13*
