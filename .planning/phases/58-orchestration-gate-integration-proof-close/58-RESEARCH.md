# Phase 58: Orchestration-Gate Integration Proof & Close - Research

**Researched:** 2026-06-13
**Domain:** RealStack E2E composition proof (Gate A × orchestration-start liveness gate) + triple-SHA milestone close gate
**Confidence:** HIGH (every claim grounded in current `master` source; no external libraries to verify)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

- **D-01 — Second real container:** Produce the CFG-08 subject as a NEW processor console (`Processor.BadConfig`) — a trivially-distinct concrete class → distinct embedded SourceHash → its own DB `Processor` row → its own `ConfigSchemaId` — running CONCURRENTLY alongside `Processor.Sample` (the CFG-09 compatible subject). One binary = one SourceHash = one row = one ConfigSchemaId.
- **D-02 — The clash drives incompatibility:** `Processor.BadConfig` seeded with a `ConfigSchemaId` whose `Definition` clashes with its `TConfig`, so Gate A's covers-check fails and `MarkHealthy` is withheld. Exact clash shape is Claude's discretion (a property the schema types `integer` that the CLR types `string`, or nested/enum/array).
- **D-03 — `Processor.Sample` is the CFG-09 compatible subject:** Seed it with a NON-NULL `ConfigSchemaId` whose `Definition` is covered by `SampleConfig`. Today SC tests seed `ConfigSchemaId: null` (Gate A skipped) — Phase 58 must seed a compatible non-null schema so Gate A RUNS AND PASSES.
- **D-04 — Dedicated tier behind a Compose profile:** Add `processor-badconfig` to `compose.yaml` gated behind a Compose profile; default `docker compose up` stays clean. Close gate + Gate-A E2E bring it up with `--profile <name>`.
- **D-05 — Net-zero-harmless (Phase-57 D-09 stay-up posture):** The incompatible processor flips `MarkReady()` (no crash-loop, `/ready` green), withholds `MarkHealthy` (no `skp:{id}` key), binds NO dispatch queue. Contributes NOTHING to the triple-SHA. The close gate must EXPECT its liveness key ABSENT, not treat absence as failure.
- **D-06 — Assert all three (logged clash + absent liveness + 422):** CFG-08 E2E positively asserts (a) the Phase-57 D-10 Error-level config-clash log via `ElasticsearchTestClient.PollEsForLog`; (b) NO `skp:{id}` L2 key (absent); (c) orchestration-start returns 422. The LOG ASSERTION is load-bearing — it distinguishes "Gate A withheld Healthy" from "processor wasn't running."
- **D-07 — New Gate-A tests + retag the v5 recovery SCs:** Add new Gate-A composition E2E (CFG-08 incompatible→422; CFG-09 compatible→Healthy→starts) AND retag `SC1`/`SC2`/`SC3` `[Trait("Phase","58")]`. All stay `[Trait("Category","RealStack")]`.
- **D-08 — Clone `scripts/phase-55-close.ps1` → `scripts/phase-58-close.ps1`, triple-SHA verbatim:** Keep the proven protocol identical; retitle to Phase 58.
- **D-09 — v6 seed deltas (the only changes vs phase-55):** (a) Seed TWO config-`Schema` rows + point the two `Processor` rows' `ConfigSchemaId` at them, CREATE-IF-ABSENT only (Phase-57 D-06 frozen-once-referenced → 409 on edit). (b) BadConfig writes no liveness key / binds no queue — no SHA exclusion needed. (c) Verify the v6 `Processor.Sample` version string. (d) Bring up the badconfig profile (`docker compose --profile <name> up -d --build`).
- **D-10 — N=3 consecutive GREEN** with identical-fact-count Smell-A guard.
- **D-11 — Build gate FIRST (autonomously-verifiable):** `dotnet build SK_P.sln -c Release` AND `-c Debug` both 0-warning, AND the new/adapted RealStack E2E COMPILE. `scripts/phase-58-close.ps1` exists + syntactically valid.
- **D-12 — Live N=3×GREEN run is operator-gated** via `58-HUMAN-UAT.md`. CFG-08/09 stay UNTICKED until the operator's GREEN run. Each embedded SourceHash must match its host build.
- **D-13 — Stable Processor rows seeded idempotently** (GET-or-create on `uq_processor_source_hash`) so each procId is stable across the 3-run gate.

### Claude's Discretion

- The exact config-schema clash shape for `Processor.BadConfig` (which property, which schema-type↔CLR-type pair) — real deserialize-failing clash; nested/enum/array modeled.
- The `Processor.BadConfig` project shape (minimal concrete class to force a distinct SourceHash; reuse `BaseProcessor.Core` + `Processor.Sample` Dockerfile/csproj template) and the Compose profile name.
- The compatible config-schema `Definition` for `Processor.Sample`'s CFG-09 seed (any schema `SampleConfig` covers).
- xUnit collection/parallelization shaping for the new Gate-A tests; reuse `RealStackWebAppFactory` + `PollForHealthyLivenessAsync` + `ElasticsearchTestClient.PollEsForLog`. The liveness-ABSENCE poll is the inverse (poll-until-stably-absent within a bound).
- Host-Redis polling / ES seam-log assertion mechanics — reuse the SC harness precedent.

### Deferred Ideas (OUT OF SCOPE)

- Generalizing Gate A to input/output schema↔type compatibility — documented Future Requirement, unchanged.
- Per-step operator config diagnostics — documented Future Requirement, unchanged.
- No other deferrals; this phase IS the v6.0.0 milestone close gate.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| **CFG-08** | An orchestration whose graph includes a config-incompatible (never-Healthy) processor is blocked at orchestration start with **422** via the existing `ProcessorLivenessValidator` ("absent"), proven E2E against the real stack. | `ProcessorLivenessValidator.cs:34-35` throws `ProcessorNotLive(id,"absent")` → 422 when `skp:{procId}` is absent. `Processor.BadConfig` never writes that key because Gate A withholds `MarkHealthy` (`ProcessorStartupOrchestrator.cs:184-192`). The D-10 clash log is emitted at `ProcessorStartupOrchestrator.cs:187-189` — the ES poll target. The covers-check that fails is `ConfigSchemaCoverageCheck.Evaluate` (clash rule table §"Clash Shape Options"). |
| **CFG-09** | A config-compatible processor reaches Healthy, writes its L2 liveness, and its orchestrations start normally — Gate A is not a false-positive blocker. | `Processor.Sample` with a `SampleConfig`-covered non-null `ConfigSchemaId` passes `ConfigSchemaCoverageCheck` → binds queue → `MarkHealthy()` (`ProcessorStartupOrchestrator.cs:194-214`) → heartbeat writes `skp:{procId}` → `ProcessorLivenessValidator` passes → Start returns 204. Proven today (sans non-null schema) by `SampleRoundTripE2ETests`. |
</phase_requirements>

## Summary

Phase 58 is the **v6.0.0 milestone close**. It adds **zero new product code** to the gate machinery — it proves a *composition* property of code already shipped in Phases 56–57: that the processor-side **Gate A** (the startup config-schema↔config-type covers-check in `ProcessorStartupOrchestrator`) correctly drives the WebApi-side **orchestration-start liveness gate** (`ProcessorLivenessValidator`, unchanged since Phase 14) to a 422 for an incompatible processor, and does NOT false-block a compatible one. The deliverable splits cleanly: an **autonomously-verifiable build gate** (0-warning Release+Debug, new E2E COMPILES, `phase-58-close.ps1` syntactically valid) and an **operator-gated live N=3×GREEN run** (`58-HUMAN-UAT.md`).

The work is mechanical adaptation of three proven assets: (1) a **new `Processor.BadConfig` console** cloned from `Processor.Sample` (its distinct project directory yields a distinct embedded SourceHash automatically — the `SourceHash.targets` fold is `BaseProcessor.Core/**/*.cs` + `$(MSBuildProjectDirectory)/**/*.cs`); (2) **two-schema seed extension** to `SeedProcessorAsync` (CREATE-IF-ABSENT against the schema-list, then non-null `ConfigSchemaId` on both processor rows); and (3) a **verbatim clone of `phase-55-close.ps1`** with the documented D-09 seed deltas only. The covers-check `ConfigSchemaCoverageCheck.Evaluate` already implements a locked STJ type-clash rule table — the BadConfig clash shape must simply hit one of its CONFIRMED-clash rows (#13 string-enum→CLR-enum, #5 number→int, #8 string→int, #22 nullable-null→non-nullable). The simplest robust clash is **schema `type:"integer"` vs CLR `string`** (rule #3 inverse, line 213/220 of the coverage check), or **schema `type:"string"` vs CLR `int`** (rule #8, line 213).

The **causation linchpin (D-06)** is the Elasticsearch log assertion: absent-liveness + 422 alone is observationally identical to "processor not running," so the CFG-08 test MUST poll ES for the Phase-57 D-10 Error log `"Gate A incompatibility for processor {ProcessorId} config schema {ConfigSchemaId}: {Clash}"` (emitted at `ProcessorStartupOrchestrator.cs:187`) scoped to the `processor-badconfig` service name. This upgrades CFG-08 from absence-coincidence to a true Gate-A-fired proof.

**Primary recommendation:** Clone `Processor.Sample` → `Processor.BadConfig` with a single distinct config record (e.g. `BadConfig(int Quantity)`) and seed it against a schema definition typing `Quantity` as `"string"` (CONFIRMED-clash rule #8); extend `SeedProcessorAsync` to GET-or-create two named schemas; clone `phase-55-close.ps1` verbatim adding only the two-schema/two-processor seed + badconfig-profile bring-up; add new Gate-A E2E tests in the `"Observability"` collection (so `RealStackNetZeroSweepFixture` covers them); mechanically retag SC1/2/3 `[Trait("Phase","58")]`.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Config-incompatibility detection (Gate A) | Processor startup (`BaseProcessor.Core`) | — | `ConfigSchemaCoverageCheck.Evaluate` runs in `ProcessorStartupOrchestrator` AFTER Loop B, BEFORE bind. It is NOT in the WebApi validator chain. |
| Withholding `MarkHealthy` / no liveness key | Processor heartbeat (`BaseProcessor.Core`) | — | Heartbeat writes `skp:{id}` only when `IsHealthy`; Gate A clash never latches Healthy → key never written. One-way latch (no `MarkUnhealthy`). |
| Orchestration-start 422 block | WebApi (`BaseApi.Service`) | — | `ProcessorLivenessValidator.ValidateAsync` reads each participating proc's `skp:{procId}`; absent → `ProcessorNotLive(id,"absent")` → 422. UNCHANGED. |
| Validator chain order | WebApi (`BaseApi.Service`) | — | Cycle → SchemaEdge → PayloadConfigSchema (Gate B) → ProcessorLiveness (async, last, before UpsertAsync). Gate A composes from OUTSIDE this chain. |
| Two-schema + two-processor seed | Test harness / close script (host process) | WebApi CRUD | Seeded via WebApi over host port 8080 (close script) / in-proc `HttpClient` (E2E). CREATE-IF-ABSENT honors frozen-once-referenced (409). |
| Distinct code identity (SourceHash) | Build (`SourceHash.targets`) | — | The fold over `BaseProcessor.Core/**` + the concrete project dir means a separate `Processor.BadConfig` project = distinct hash automatically; no per-file effort. |
| Net-zero snapshot/compare | Close script (host process, docker exec) | E2E teardown + `RealStackNetZeroSweepFixture` | Triple-SHA BEFORE==AFTER; active reclaim (A19 two-key DEL) + test teardown keep it net-zero. |
| Causation proof (clash log) | Elasticsearch (otel pipeline) | E2E test | `PollEsForLog` term-query on `processor-badconfig` service + the Error message text. |

## Standard Stack

This phase adds **no new libraries**. It reuses the existing, version-pinned (CPM `Directory.Packages.props`) stack. The relevant in-repo components:

### Core (reused, unchanged)
| Component | Location | Purpose | Why Standard |
|-----------|----------|---------|--------------|
| `ProcessorLivenessValidator` | `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` | 422 on absent/stale/malformed `skp:{procId}` liveness at orchestration start | The integration seam under test; UNCHANGED (Phase 14, refined Phase ~50 WR-01). |
| `ProcessorStartupOrchestrator` | `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` | Loop A identity → Loop B definitions → Gate A → bind → MarkHealthy | Hosts Gate A (lines 184-192) + the D-10 clash log (lines 187-189). |
| `ConfigSchemaCoverageCheck.Evaluate` | `src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs` | `schema ⊨ TConfig` structural covers-check; locked STJ clash rule table | The mechanism the BadConfig clash must trip. |
| `ElasticsearchTestClient.PollEsForLog` | `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` | Polls ES `_search` (localhost:9200) with exp backoff; returns first hit or null | The D-06 log-assertion seam. |
| `RealStackWebAppFactory` | `SampleRoundTripE2ETests.cs:373-464` | In-proc WebApi → host stack (RMQ 5673 / Redis 6380 / PG 5433 / otel 4317) + net-zero teardown | Reused wholesale by every RealStack E2E. |
| `RealStackNetZeroSweepFixture` | `tests/BaseApi.Tests/Orchestrator/RealStackNetZeroSweepFixture.cs` | `ICollectionFixture` net-zero sweep after the `"Observability"` + `"RedisOutageSerial"` collections | New Gate-A tests should join `"Observability"` to inherit it. |
| `scripts/phase-55-close.ps1` | (384 lines) | Triple-SHA close gate template | The D-08 clone source. |

### Supporting (reused)
| Component | Location | Purpose |
|-----------|----------|---------|
| `SourceHash.targets` | `src/BaseProcessor.Core/SourceHash.targets` | Computes/embeds the distinct SourceHash; imported by each concrete csproj. |
| `IConfigTypeProvider` / `BaseProcessorConfigTypeProvider` | `src/BaseProcessor.Core/Configuration/` | Supplies the concrete `TConfig` type to Gate A (`GetType().BaseType!.GenericTypeArguments[0]`). |
| Schema CRUD (`SchemasController`) | `src/BaseApi.Service/Features/Schema/SchemaController.cs` | `/api/v1/schemas` 5 verbs (GET-all, GET-by-id, POST, PUT, DELETE) for the two-schema seed. |
| `ProcessorCreateDto`/`ProcessorReadDto` | `src/BaseApi.Service/Features/Processor/ProcessorDtos.cs` | 7-field create DTO incl. `ConfigSchemaId`. |
| Processor by-source-hash lookup | `ProcessorController.cs:53` `GET /api/v1/processors/by-source-hash/{sourceHash}` | The idempotent GET-or-create lookup (404 when absent). |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| New `Processor.BadConfig` container (D-01) | Reuse `Processor.Sample` with a clashing schema | REJECTED by D-01: one binary = one SourceHash = one row = one ConfigSchemaId, so Sample can't be both compatible AND incompatible. The container approach is faithful to the RealStack close-gate culture. |
| Schema `type:"integer"` vs CLR `string` | Schema `type:"string"` vs CLR `int` / string-enum vs CLR-enum / nested-array clash | All are CONFIRMED-clash rows in the rule table; pick the simplest that reads clearly in the log. See §"Clash Shape Options". |

**Installation:** N/A — no new packages. A new `Processor.BadConfig.csproj` must be added to `SK_P.sln`.

**Version verification:** No npm/NuGet version checks apply (CPM-pinned, no new refs). The ONE version string to verify per D-09c — the `Processor.Sample` seed version — is **`"3.5.0"`** ([VERIFIED: `src/Processor.Sample/appsettings.json` `Service.Version` = `"3.5.0"`, current on master]). The SourceHash (not this string) is what moves v5→v6 — so the Sample seed version stays `3.5.0`.

## Architecture Patterns

### System Architecture Diagram

```
                       ┌─────────────────── Host stack (compose, rebuilt v6 images) ───────────────────┐
                       │                                                                                │
  E2E test process     │   baseapi-service ──(GetProcessorBySourceHash / GetSchemaDefinition over bus)─┐│
  (in-proc WebApi)     │        ▲                                                                      ││
        │              │        │ seed Schema×2 + Processor×2 (CREATE-IF-ABSENT)                       ││
        │ HTTP 8080    │        │                                                                      ││
        ▼              │   ┌────┴─────────────────────────────────────────────┐                       ││
   /api/v1/schemas ────────│ Postgres: 2 schema rows + 2 processor rows        │                       ││
   /api/v1/processors      │   (sample→compatibleSchemaId, badconfig→clashId)  │                       ││
        │              │   └──────────────────────────────────────────────────┘                       ││
        ▼              │                                                                                ││
  POST /orchestration/start                                                                            ││
        │              │                                                                                ││
   ┌────┴──── validator chain (BaseApi.Service) ────┐                                                  ││
   │ Cycle → SchemaEdge → PayloadConfigSchema(GateB) │                                                 ││
   │   → ProcessorLiveness ──reads skp:{procId}──────┼──► Redis (host 6380) ◄──heartbeat (IsHealthy)──┐││
   └─────────────────────────────────────────────────┘                                              │││
        │                                                                                            │││
   ┌────┴─── CFG-09 (Sample) ──┐        ┌──── CFG-08 (BadConfig) ────┐                                │││
   │ skp:{sampleId} PRESENT     │       │ skp:{badId} ABSENT          │                               │││
   │ → 204 NoContent (starts)   │       │ → 422 ProcessorNotLive      │                               │││
   └────────────────────────────┘       │   ("absent")                │                               │││
                                         └─────────────────────────────┘                               │││
                                                                                                       │││
  Processor.Sample (compatible) ──Gate A PASS──► bind {sampleId} queue ─► MarkHealthy ─► heartbeat ────┘││
  Processor.BadConfig (clash) ──Gate A CLASH──► MarkReady (no crash) ─► WITHHOLD MarkHealthy ───────────┘│
        │ Error log "Gate A incompatibility for processor {id} config schema {csid}: {clash}"            │
        └──────────────────────► otel-collector ──► Elasticsearch (host 9200) ◄── PollEsForLog (D-06) ───┘
```

Trace CFG-08: BadConfig boots → resolves identity → fetches its clashing config-schema def → `Evaluate` returns `(Covered:false, ClashDetail)` → logs ONE Error (D-10) + `MarkReady()` + RETURNS (no bind, no MarkHealthy) → `skp:{badId}` never written → orchestration whose graph includes badId hits `ProcessorLivenessValidator` → `raw.IsNullOrEmpty` → `ProcessorNotLive(badId,"absent")` → 422. Test asserts all three: ES log present, `skp:{badId}` stably absent, Start returns 422.

### Recommended Project Structure
```
src/
├── Processor.Sample/         # CFG-09 compatible subject (existing; flip ConfigSchemaId null→non-null in seed)
└── Processor.BadConfig/      # NEW — CFG-08 incompatible subject (clone of Processor.Sample)
    ├── Processor.BadConfig.csproj   # clone; import SourceHash.targets; ref BaseProcessor.Core + Messaging.Contracts
    ├── Dockerfile                   # clone; swap publish target + ASSEMBLY name + Service.Name
    ├── Program.cs                   # clone; AddSingleton<BaseProcessor, BadConfigProcessor>
    ├── BadConfig.cs                 # NEW record : ProcessorConfig — the clashing TConfig (distinct .cs → distinct hash)
    ├── BadConfigProcessor.cs        # NEW : BaseProcessor<BadConfig> — minimal transform
    └── appsettings.json             # clone; Service.Name="processor-badconfig", ConsoleHealth port

tests/BaseApi.Tests/Orchestrator/
├── SampleRoundTripE2ETests.cs       # extend SeedProcessorAsync → two-schema seed helpers
├── GateAComposition E2E (NEW)       # CFG-08 + CFG-09 tests; [Collection("Observability")]
├── SC1/SC2/SC3 ...E2ETests.cs       # retag [Trait("Phase","55")] → "58" (mechanical)

scripts/
├── phase-55-close.ps1               # clone source
└── phase-58-close.ps1               # NEW — verbatim + D-09 deltas

compose.yaml                          # add processor-badconfig service behind a profile (D-04)
```

### Pattern 1: Distinct SourceHash via a separate project (D-01)
**What:** The embedded SourceHash is a SHA-256 fold over `BaseProcessor.Core/**/*.cs` PLUS `$(MSBuildProjectDirectory)/**/*.cs` (the importing concrete project's own dir), excluding `obj/bin/*.g.cs/GlobalUsings/AssemblyInfo`.
**When to use:** Always — a separate `Processor.BadConfig` project directory with its own (even trivially different) `.cs` files yields a distinct hash with zero extra effort.
**Source:** `src/BaseProcessor.Core/SourceHash.targets` lines 80-85 (`<ImplFiles Include="$(MSBuildThisFileDirectory)**\*.cs;$(MSBuildProjectDirectory)\**\*.cs" Exclude=.../>`).
```xml
<!-- Processor.BadConfig.csproj MUST import the targets the same way Sample does: -->
<Import Project="..\BaseProcessor.Core\SourceHash.targets" />
```
The runtime reader is `AssemblyMetadataSourceHashProvider` reading `[assembly: AssemblyMetadata("SourceHash", "<64-hex>")]` off `GetEntryAssembly()` — so the attribute lands on `Processor.BadConfig.dll`, distinct from `Processor.Sample.dll`.

### Pattern 2: GET-or-create idempotent seed (D-09a / D-13)
**What:** Seeding the same row on every run must be idempotent. Processors key on `uq_processor_source_hash`; schemas have NO uniqueness constraint (only FK indexes on Input/Output/ConfigSchemaId), so schema idempotency must be by GET-all-then-filter-by-Name.
**Processor (existing, reuse verbatim):**
```csharp
// SampleRoundTripE2ETests.cs:311-329 — GET by source-hash, 200→reuse Id, 404→POST.
var lookup = await client.GetAsync($"/api/v1/processors/by-source-hash/{sourceHash}", ct);
if (lookup.StatusCode == HttpStatusCode.OK) return (await lookup.Content.ReadFromJsonAsync<ProcessorReadDto>(ct:ct))!.Id;
// else POST ProcessorCreateDto with the (now NON-NULL) ConfigSchemaId.
```
**Schema (NEW helper — GET-all-then-filter):**
```csharp
// Schemas have no unique index → a blind POST creates a duplicate each run. GET the list,
// match a fixed sentinel Name (e.g. "gateA-sample-compatible" / "gateA-badconfig-clash"),
// reuse its Id; POST only if absent. The Definition is FROZEN once a processor references it
// (Phase-57 D-06 → PUT returns 409), so NEVER PUT-edit — CREATE-IF-ABSENT only.
var all = await client.GetFromJsonAsync<List<SchemaReadDto>>("/api/v1/schemas", ct);
var existing = all!.FirstOrDefault(s => s.Name == sentinelName);
if (existing is not null) return existing.Id;
var dto = new SchemaCreateDto(sentinelName, "1.0.0", null, definitionJson);
var resp = await client.PostAsJsonAsync("/api/v1/schemas", dto, ct); resp.EnsureSuccessStatusCode();
return (await resp.Content.ReadFromJsonAsync<SchemaReadDto>(ct:ct))!.Id;
```
**Source:** `SchemaController.cs` (5 inherited CRUD verbs, `/api/v1/schemas`); `SchemaDtos.cs` (`SchemaCreateDto(Name, Version, Description, Definition)`); `BaseController.cs:45-48` (`[HttpGet] List`). Frozen-once-referenced: `SchemaService.cs:37-57` + `SchemaDefinitionFrozenException` → 409.

### Pattern 3: Inverse liveness-absence poll (CFG-08 / Claude's discretion)
**What:** `PollForHealthyLivenessAsync` (`SampleRoundTripE2ETests.cs:201-240`) polls UNTIL the key appears + is fresh. CFG-08 needs the inverse: assert `skp:{badId}` stays ABSENT within a bound (the badconfig container booted but withheld Healthy). A bare "absent right now" is racy (the container may not have finished booting); the robust pattern is **wait until the container is demonstrably booted (its `/ready` is green / it logged Gate A), THEN assert the key is absent and STAYS absent across a short stability window.**
```csharp
// Robust shape: first confirm Gate A FIRED (poll ES for the D-10 Error log — this proves the
// container booted AND concluded incompatible), THEN assert skp:{badId} is absent and remains
// absent across N consecutive reads spanning > one heartbeat interval (Interval=10s default).
//   1. await PollEsForGateAClashLog(badId, badConfigSchemaId);   // proves boot + clash (causation)
//   2. assert StringGet(skp:{badId}) IsNullOrEmpty across ~3 reads over ~15s (stably absent)
```
The ES log poll is the load-bearing causation proof (D-06) AND the boot-confirmation — do the ES poll FIRST so "absent" can't be confused with "not yet booted."

### Pattern 4: Gate-A clash log ES query (D-06 causation linchpin)
**Source log:** `ProcessorStartupOrchestrator.cs:187-189`:
```csharp
logger.LogError(
    "Gate A incompatibility for processor {ProcessorId} config schema {ConfigSchemaId}: {Clash}",
    context.Id, context.ConfigSchemaId, coverage.ClashDetail);
```
This is `LogError` (Error level) → otel → ES. Structured attributes available: `attributes.ProcessorId`, `attributes.ConfigSchemaId`, `attributes.Clash`, plus `resource.attributes.service.name`. The `{Clash}` text is `ConfigSchemaCoverageCheck.Detail`: `"property '{name}': schema {schemaType} clashes with CLR {clrType.Name}"` (line 288-289).
**ES query (mirror `SampleRoundTripE2ETests.cs:151-165`):** term on `attributes.ProcessorId` == badId (or `attributes.ConfigSchemaId` == clashSchemaId) AND `resource.attributes.service.name` == `"processor-badconfig"`. Assert the hit's raw text `Contains("Gate A incompatibility")`. Use `PollEsForLog(query, timeoutMs: ~120_000, ct)` (otel export is async).

### Pattern 5: New Gate-A tests join the `"Observability"` collection
**What:** `SampleRoundTripE2ETests` is `[Collection("Observability")]` (line 71). That collection is `DisableParallelization = true` AND wires `ICollectionFixture<RealStackNetZeroSweepFixture>` (`CollectionDefinitions.cs`). The sweep actively reclaims `skp:data:*` / `skp:msg:*` residue + purges `skp-dlq-1` after the LAST test in the collection — required for the close-gate net-zero snapshot.
**When to use:** The new CFG-08/CFG-09 tests SHOULD declare `[Collection("Observability")]` so they (a) serialize against the shared ES/Redis backends and (b) are covered by the net-zero sweep. The CFG-08 test seeds no cron round-trip and binds no queue (badconfig is harmless), so it adds little teardown burden; the CFG-09 test (Sample, with cron) needs the sweep exactly like `SampleRoundTripE2ETests`.

### Anti-Patterns to Avoid
- **Asserting "absent right now" without boot-confirmation:** racy — the badconfig container may simply not have finished Loop A/B yet. Confirm Gate A fired (ES log) first, then assert stably-absent. (Pattern 3.)
- **Blind POST of the seed schema each run:** schemas have no unique constraint → duplicates accumulate → churns the close-gate state. GET-all-filter-by-Name first. (Pattern 2.)
- **PUT-editing a referenced schema Definition:** returns 409 (frozen-once-referenced, `SchemaService.cs`). Seed is CREATE-IF-ABSENT only.
- **Folding the badconfig liveness key into a SHA exclusion:** unnecessary — badconfig writes NO key and binds NO queue, so it is simply absent from both snapshots (D-05/D-09b). Do NOT add a `Where-Object` exclusion for it (the Sample procId exclusion stays; badconfig needs none).
- **Treating badconfig's absent liveness as a pre-flight failure:** the close-gate pre-flight requires `processor-sample` healthy, but `processor-badconfig` is intentionally NEVER healthy. Do NOT add it to the `$services` health-required list; its Docker `/ready` healthcheck DOES pass (MarkReady flips), but its liveness key + queue must NOT be expected.
- **Mixed-version stack:** the live run MUST use rebuilt v6 images incl. the badconfig profile; a stale image's embedded SourceHash diverges from the seeded row → identity never resolves → liveness false-pass/timeout (D-12).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Distinct code identity for BadConfig | Manual hash / GUID / random SourceHash | A separate `Processor.BadConfig` project importing `SourceHash.targets` | The fold auto-includes the new project dir; a synthetic hash would diverge from the container's embedded hash → identity never resolves. |
| ES log polling | New HTTP+backoff loop | `ElasticsearchTestClient.PollEsForLog` | Already handles 404/empty-hits/backoff/Clone detach. |
| Host-Redis liveness check | New StackExchange.Redis plumbing | `PollForHealthyLivenessAsync` precedent (+ its inverse) | Connection string, freshness model, ConfigurationOptions all solved. |
| Net-zero teardown | New cleanup logic | `L2KeysToCleanup` + `ParentIndexMembersToSrem` + `RealStackNetZeroSweepFixture` | The composite-key sweep + cron-residue reclaim is subtle (GAP-49-8); reuse it. |
| Triple-SHA close protocol | New close script | Clone `phase-55-close.ps1` verbatim | The steady-state exclusions, `--` MTP nuances, Smell-A guard, DLQ depth, `skp:msg:*` count are all hard-won. |
| Covers-check / clash detection | New schema↔type comparator | `ConfigSchemaCoverageCheck.Evaluate` (already shipped) | The STJ rule table (#1-#26) is locked + spike-confirmed; the test just needs to TRIP a confirmed-clash row, not reimplement the logic. |

**Key insight:** This phase's correctness rests entirely on code that already exists and is hermetically verified (Phase 57). Phase 58 adds only *test subjects* (a clashing schema + a broken container) and *proof harness* (E2E + close script). Every hand-roll temptation has a proven in-repo asset.

## Runtime State Inventory

> This is a milestone-close phase that ADDS a container + schemas + processor rows; it is not a rename/refactor. The inventory below captures the runtime state the close gate must account for (analogous concern: what persists across the N=3 run).

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | (1) Two NEW Postgres `Schema` rows (sample-compatible + badconfig-clash) seeded CREATE-IF-ABSENT. (2) Two `Processor` rows with non-null `ConfigSchemaId` (Sample flips from null → compatible; BadConfig → clash). (3) `Processor.Sample`'s `skp:{sampleId}` liveness key (steady-state, EXCLUDED from redis SHA). (4) `Processor.BadConfig` writes NO key (D-05). | Seed code (E2E helper + close script). Schema rows are row-level (don't move the `psql \l` db-list SHA). Sample liveness exclusion stays; badconfig needs NONE. |
| Live service config | `compose.yaml` gains a `processor-badconfig` service behind a profile (D-04). Default `docker compose up` unaffected (profile-gated). | Compose edit. The close script + Gate-A E2E bring it up with `--profile <name>`. |
| OS-registered state | None — no Task Scheduler / launchd / pm2. The close gate runs via `pwsh -File scripts/phase-58-close.ps1`. | None — verified: no scheduler registration in this project (manual `pwsh` invocation, per `55-HUMAN-UAT.md` precedent). |
| Secrets/env vars | None new. BadConfig reuses Sample's env (RMQ/Redis/otel hosts via compose). No new SOPS keys / .env. | None — verified: env is compose-inline (`compose.yaml:278-284`), no secret store. |
| Build artifacts | NEW: `Processor.BadConfig.dll` carrying a distinct embedded SourceHash; `Processor.BadConfig` Docker image. The close script reads the genuine hash off the BUILT dll (`bin/Release/net8.0/Processor.BadConfig.dll`) the same way it reads Sample's. `SK_P.sln` must include the new project. | Add project to `SK_P.sln`; close script reads both Sample + BadConfig embedded hashes for the two-row seed. |

## Common Pitfalls

### Pitfall 1: Absent-liveness mistaken for "not running" (the D-06 raison d'être)
**What goes wrong:** CFG-08 asserts only "skp:{badId} absent + Start 422" — which passes EVEN IF the badconfig container is simply down. The test then proves nothing about Gate A.
**Why it happens:** Absence is observationally identical to non-existence.
**How to avoid:** Poll ES for the Phase-57 D-10 Error log FIRST (proves the container booted, ran Gate A, and concluded incompatible), THEN assert absent + 422. Scope the ES query to `service.name == "processor-badconfig"` + the specific `ProcessorId`/`ConfigSchemaId`.
**Warning signs:** Test green with the badconfig container stopped.

### Pitfall 2: Schema seed not idempotent → close-gate state churn
**What goes wrong:** A blind `POST /api/v1/schemas` each run creates a NEW schema row (no unique constraint), accumulating rows and (if the processor row's ConfigSchemaId is re-pointed) churning state across the N=3 run.
**Why it happens:** Unlike processors (`uq_processor_source_hash`), schemas have only FK indexes — no name/version uniqueness.
**How to avoid:** GET-all → filter by a fixed sentinel Name → reuse Id; POST only if absent (Pattern 2). Never PUT (409 frozen).
**Warning signs:** `SELECT count(*) FROM "Schemas"` grows across runs; redis/rmq SHA mismatch from re-pointed ConfigSchemaId.

### Pitfall 3: Clash shape that doesn't actually trip the rule table
**What goes wrong:** A "clash" that the covers-check classifies as FINE (e.g. schema `type:"string"` vs CLR `string`, or schema `type:"integer"` vs CLR `int`) → Gate A PASSES → BadConfig goes Healthy → CFG-08 false-fails (expects absent, gets present).
**Why it happens:** The rule table treats many pairs as compatible (string→string/Guid/DateTime FINE; integer→any-numeric FINE).
**How to avoid:** Use a CONFIRMED-clash row (see §"Clash Shape Options"). Simplest: schema `type:"string"` on a property the CLR types `int` (rule #8, line 213 `return Detail(name, "string", declared)`), OR schema `type:"integer"` on a CLR `string` (rule #3 inverse, line 220).
**Warning signs:** BadConfig reaches Healthy; `skp:{badId}` appears; no Gate A Error log in ES.

### Pitfall 4: Stale image / divergent SourceHash (D-12)
**What goes wrong:** The seeded row's SourceHash doesn't match the running container's embedded hash → identity never resolves → for Sample, liveness never appears (CFG-09 false-fail/timeout); for BadConfig, it never even reaches Gate A.
**Why it happens:** Running a pre-built image against a freshly-built host dll, or forgetting `--build`.
**How to avoid:** `docker compose --profile <name> up -d --build baseapi-service orchestrator processor-sample keeper processor-badconfig`. The close script reads each genuine embedded hash off the BUILT dll (mirror `phase-55-close.ps1:99-120`).
**Warning signs:** `processor-sample never wrote a fresh Healthy liveness key` (the existing fail message at `SampleRoundTripE2ETests.cs:235`).

### Pitfall 5: `dotnet test --filter` silently ignored (xUnit v3 / MTP)
**What goes wrong:** A hermetic-filtered dev run using the VSTest `--filter` form errors (MSB1001 / MTP0001) or runs nothing.
**Why it happens:** xunit.v3 3.2.2 runs under Microsoft.Testing.Platform (MTP), not VSTest; `--filter` is a VSTest switch.
**How to avoid:** The hermetic dev-iteration form is the `--` passthrough: `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-not-trait "Category=RealStack"` [VERIFIED: Phase 50-02 SUMMARY confirms the `--`-passthrough form]. The CLOSE script runs the FULL suite (no filter) against the live stack, so this only matters for dev iteration + the build-gate compile check.
**Warning signs:** "0 tests ran" or MSB1001/MTP0001 in a filtered run.

## Code Examples

### Clash schema definition (CFG-08 seed — recommended shape)
```json
// Source: definition stored in the Schema row; tripped by ConfigSchemaCoverageCheck.ClassifyScalar
// (ConfigSchemaCoverageCheck.cs:200-213, String case). CLR BadConfig.Quantity is `int`; schema types it `string`.
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "properties": {
    "quantity": { "type": "string" }
  }
}
// With:  public sealed record BadConfig(int Quantity) : ProcessorConfig;
// Evaluate(...) → ClassifyScalar String vs int → effective is numeric, not string/Guid/Date →
//   Detail("quantity","string", Int32) → (Covered:false, "property 'quantity': schema string clashes with CLR Int32")
```

### Gate-A clash log ES query (CFG-08 D-06 assertion)
```csharp
// Source: mirrors SampleRoundTripE2ETests.cs:151-167 (term + service-scope), targeting the D-10 Error log.
var clashLogQuery = $$"""
  {
    "size": 5,
    "sort": [ { "@timestamp": { "order": "desc" } } ],
    "query": {
      "bool": {
        "must": [
          { "term": { "attributes.ProcessorId": "{{badId}}" } },
          { "term": { "resource.attributes.service.name": "processor-badconfig" } }
        ]
      }
    }
  }
  """;
using var es = new ElasticsearchTestClient();
var clash = await es.PollEsForLog(clashLogQuery, timeoutMs: 120_000, ct: ct);
Assert.NotNull(clash);
Assert.Contains("Gate A incompatibility", clash!.Value.GetRawText());
```

### CFG-08 Start → 422 assertion
```csharp
// ProcessorLivenessValidator.cs:34-35 → ProcessorNotLive(badId,"absent") → 422.
var startResp = await client.PostAsJsonAsync(
    "/api/v1/orchestration/start", new List<Guid> { badWorkflowId }, ct);
Assert.Equal(HttpStatusCode.UnprocessableEntity, startResp.StatusCode);   // 422
```

### Two-processor seed delta (close script, D-09a) — sketch
```powershell
# Clone the phase-55 single-Processor seed (phase-55-close.ps1:92-156). For PHASE 58:
#   1. Read BOTH embedded hashes (Processor.Sample.dll + Processor.BadConfig.dll).
#   2. GET-or-create TWO schema rows by sentinel Name ("gateA-sample-compatible","gateA-badconfig-clash").
#   3. GET-or-create the Sample processor row with configSchemaId=$compatibleSchemaId (was $null).
#   4. GET-or-create the BadConfig processor row with configSchemaId=$clashSchemaId.
#   5. Wait for processor-sample healthy (UNCHANGED). Do NOT wait for processor-badconfig liveness —
#      it intentionally never goes Healthy; only require its Docker /ready (MarkReady flips).
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `Processor.Sample` seeded `ConfigSchemaId: null` (Gate A skipped) | CFG-09 seeds a `SampleConfig`-covered non-null schema (Gate A runs + passes) | Phase 58 | CFG-09 needs Gate A to RUN and PASS, not be skipped. |
| Single processor container in the close gate | Two concurrent containers (Sample compatible + BadConfig clash) | Phase 58 | Proves composition, not just round-trip. |
| `phase-55-close.ps1` (v5 single-Processor seed) | `phase-58-close.ps1` (two-schema + two-processor seed, badconfig profile bring-up) | Phase 58 | Only the seed + profile bring-up change; triple-SHA verbatim. |
| `[Trait("Phase","55")]` on SC1/2/3 | `[Trait("Phase","58")]` | Phase 58 | Mechanical retag → SCs run in the phase-58 close (full regression). |

**Deprecated/outdated:** None introduced. v6 left the v5 slot-array/3-state recovery machinery UNCHANGED (the SC retag is a tag-only change, not a behavior change).

## Clash Shape Options (Claude's Discretion — D-02)

All are CONFIRMED-clash rows in `ConfigSchemaCoverageCheck` (spike-confirmed per the class doc):

| Option | Schema | CLR `TConfig` property | Rule (line) | Detail string |
|--------|--------|------------------------|-------------|---------------|
| **A (recommended)** | `{"quantity":{"type":"string"}}` | `int Quantity` | #8, line 213 | `property 'quantity': schema string clashes with CLR Int32` |
| B | `{"count":{"type":"integer"}}` | `string Count` | #3 inverse, line 220 | `property 'count': schema integer clashes with CLR String` |
| C | `{"price":{"type":"number"}}` | `int Price` | #5, line 226 | `property 'price': schema number clashes with CLR Int32` |
| D (nested, D-04 fidelity) | `{"items":{"type":"array","items":{"type":"string"}}}` | `List<int> Items` | array recurse, line 240/280 | `property 'items[]': schema string clashes with CLR Int32` |
| E (enum) | `{"mode":{"enum":["A","B"]}}` | `SomeEnum Mode` | #13, line 203 | `property 'mode': schema string-enum clashes with CLR SomeEnum` |

**Recommendation: Option A** — simplest single-property record (`BadConfig(int Quantity)`), reads clearly in the ES log, one distinct `.cs` file (drives the distinct SourceHash). Option D demonstrates the nested/array fidelity the CONTEXT calls out, if a richer proof is wanted — but A is sufficient for CFG-08.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | otel exports `attributes.ProcessorId` / `attributes.ConfigSchemaId` as queryable ES term fields (the D-10 log uses structured logging params). | Pattern 4, Code Examples | LOW — the existing `SampleRoundTripE2ETests` already term-queries `attributes.WorkflowId` successfully (line 158), confirming structured-attribute → ES term works. If the exact field path differs, fall back to a `match` on the message body text `"Gate A incompatibility"` scoped to the service name. |
| A2 | A compose profile cleanly excludes `processor-badconfig` from the default `docker compose up` while `--profile <name> up` includes it. | D-04, compose | LOW — standard Compose `profiles:` behavior; verify the exact profile-key placement at plan time against the compose version in use. |
| A3 | The new Gate-A E2E in `[Collection("Observability")]` will not be starved by the existing serial collection's multi-minute ES polls. | Pattern 5 | LOW — adds runtime to the serial collection but no correctness risk; the close-gate `start_period`/timeouts already tolerate multi-minute windows. |

**Note:** No claim about Gate A / liveness-validator BEHAVIOR is assumed — all are VERIFIED against current source. The three assumptions above are about test-harness wiring details, all low-risk with documented fallbacks.

## Open Questions

1. **ES structured-attribute field path for `ProcessorId`/`ConfigSchemaId`.**
   - What we know: `attributes.WorkflowId` is a proven queryable term field (`SampleRoundTripE2ETests.cs:158`). The D-10 log uses `{ProcessorId}`/`{ConfigSchemaId}` structured params.
   - What's unclear: whether otel maps them to `attributes.ProcessorId` exactly (casing/path) — Guids may serialize as strings.
   - Recommendation: Plan the CFG-08 ES assertion to term on `service.name == "processor-badconfig"` + a `match` on the message text `"Gate A incompatibility"` as the robust primary; add the `attributes.ProcessorId` term as a tightening filter once confirmed during execution. (Both narrow to the right log; the service-scope + message text is sufficient for causation.)

2. **Whether `processor-badconfig` needs a distinct ConsoleHealth port / container name in compose.**
   - What we know: `processor-sample` uses port 8082 + `container_name: sk-processor-sample`.
   - What's unclear: two processors in the same compose network can share the internal port 8082 (no host publish on processor-sample), but `container_name` must be unique.
   - Recommendation: Give `processor-badconfig` `container_name: sk-processor-badconfig` + `Service.Name: processor-badconfig`; the internal health port can stay 8082 (not host-published). Confirm no host port collision at plan time.

## Environment Availability

> The live N=3 run is operator-gated (D-12). These are the host-stack dependencies the close gate / E2E require; on THIS research machine they are not probed (the live run is an operator action), but they are the documented contract.

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Docker + compose | live stack (rebuilt v6 images + badconfig profile) | operator-provided | — | none — blocks the live run (D-12) |
| `pwsh` (PowerShell) | `scripts/phase-58-close.ps1` | ✓ (Windows 11 host) | — | none |
| .NET 8 SDK | build gate (Release+Debug 0-warning), test compile | ✓ | net8.0 (per `Directory.Build.props`) | none |
| Host ports 5673/6380/5433/9200/4317/8080 | `RealStackWebAppFactory` + ES client | operator-provided (compose-published) | — | none for the live run; the BUILD gate (D-11) needs none of these |

**Missing dependencies with no fallback:** The live run requires the full rebuilt v6 compose stack incl. the badconfig profile — an operator action (D-12), not autonomously satisfiable.
**Build-verifiable subset (D-11, no stack needed):** `dotnet build -c Release` + `-c Debug` 0-warning; the new/adapted RealStack E2E COMPILE (excluded from the hermetic run by `Category=RealStack` but must build); `phase-58-close.ps1` parses.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit v3 (xunit.v3 3.2.2) on Microsoft.Testing.Platform (MTP) |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (`OutputType=Exe`, `UseMicrosoftTestingPlatformRunner=true`) |
| Quick run command (hermetic, dev) | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-not-trait "Category=RealStack"` (the `--`-passthrough MTP form) |
| Full suite command (close gate, live) | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release --no-build` (NO filter — RealStack E2E run live) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| CFG-08 | BadConfig → Gate A clash log (ES) + `skp:{badId}` absent + Start 422 | RealStack E2E (operator-gated live) | full-suite via `phase-58-close.ps1` (N=3) | ❌ Wave 0 — new Gate-A test |
| CFG-09 | Sample (non-null compatible schema) → Gate A passes → Healthy → `skp:{sampleId}` present → Start 204 | RealStack E2E (operator-gated live) | full-suite via `phase-58-close.ps1` (N=3) | ❌ Wave 0 — new Gate-A test (or extend `SampleRoundTripE2ETests` seed) |
| CFG-08/09 (regression) | v5 recovery SCs still hold end-to-end | RealStack E2E | full-suite via `phase-58-close.ps1` | ✅ SC1/2/3 exist — retag `[Trait("Phase","58")]` only |
| D-11 build gate | Release+Debug 0-warning; new E2E COMPILES | unit/build (autonomous) | `dotnet build SK_P.sln -c Release; dotnet build SK_P.sln -c Debug; dotnet test ... -- --filter-not-trait "Category=RealStack"` | partial — build infra exists; new project + tests are Wave 0 |
| D-11 close script | `phase-58-close.ps1` exists + parses | smoke (autonomous) | `pwsh -NoProfile -Command "& { . scripts/phase-58-close.ps1 }"` parse check / `Get-Command -Syntax` style validation | ❌ Wave 0 — new script |

### Sampling Rate (minimal observable signals — no redundant over-testing)
CFG-08 is fully proven by exactly **three observable signals** on the badconfig subject (D-06): (1) the Gate A Error log in ES (causation), (2) `skp:{badId}` stably absent (mechanism), (3) Start 422 (outcome). CFG-09 by **three**: Gate A passes (no clash log + key appears), `skp:{sampleId}` present (heartbeat), Start 204 (outcome). No further per-property assertions are needed — the covers-check itself is hermetically tested in Phase 57; Phase 58 samples only the *composition*.

- **Per task commit:** `dotnet build SK_P.sln -c Release` + hermetic `--filter-not-trait "Category=RealStack"` (RealStack tests excluded — they need the live stack, but MUST compile).
- **Per wave merge:** both-config build + full hermetic suite green.
- **Phase gate (operator-gated, D-12):** `pwsh -File scripts/phase-58-close.ps1` — N=3 consecutive GREEN, triple-SHA BEFORE==AFTER, `skp-dlq-1` depth==0, `skp:msg:*` count==0, both-config 0-warning. Recorded in `58-HUMAN-UAT.md`.

### Wave 0 Gaps
- [ ] `src/Processor.BadConfig/` project (csproj + Dockerfile + Program.cs + `BadConfig.cs` + `BadConfigProcessor.cs` + appsettings.json) — distinct SourceHash subject for CFG-08.
- [ ] `SK_P.sln` — add `Processor.BadConfig` project.
- [ ] `compose.yaml` — `processor-badconfig` service behind a profile (D-04).
- [ ] New Gate-A composition E2E test(s) — CFG-08 (incompatible→clash-log+absent+422) and CFG-09 (compatible→Healthy→204); `[Collection("Observability")]` + `[Trait("Category","RealStack")]` + `[Trait("Phase","58")]`. Covers CFG-08, CFG-09.
- [ ] Two-schema seed helpers (GET-or-create by sentinel Name) — extend `SeedProcessorAsync` / add `SeedConfigSchemaAsync`; flip Sample's seed `ConfigSchemaId: null` → compatible non-null.
- [ ] `scripts/phase-58-close.ps1` — clone of `phase-55-close.ps1` + D-09 deltas (two-schema/two-processor seed, version verify, badconfig-profile bring-up).
- [ ] `58-HUMAN-UAT.md` runbook — operator N=3 GREEN-run record (mirror `55-HUMAN-UAT.md`).
- [ ] SC1/SC2/SC3 retag `[Trait("Phase","55")]` → `[Trait("Phase","58")]` (mechanical).
- [ ] No framework install needed — xUnit v3 / MTP infra already present.

## Security Domain

> `security_enforcement` is not present in `config.json` (treated as enabled), but this phase introduces **no new attack surface**: no new endpoints, no new input parsing, no new auth/session/crypto. It adds a deliberately-broken processor container (behind a profile) + test seeds. The relevant security note is already handled by shipped code.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | No auth surface touched. |
| V3 Session Management | no | N/A. |
| V4 Access Control | no | No new endpoints; seed uses existing CRUD. |
| V5 Input Validation | yes (existing) | The config-schema definition is operator-authored + meta-schema-validated on write (VALID-08) + frozen-once-referenced. `ConfigSchemaCoverageCheck` is SSRF-safe (T-57-03): it NEVER calls `JsonSchema.Evaluate`, never resolves an external `$ref` (`ConfigSchemaCoverageCheck.cs` class doc, lines 32-38). No new validation introduced by Phase 58. |
| V6 Cryptography | no (SourceHash is integrity, not secrecy) | SourceHash is SHA-256 over source for code-identity, computed by the shipped `SourceHash.targets`; not hand-rolled in this phase. |

### Known Threat Patterns for this stack
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Malicious config-schema with external `$ref` (SSRF) | Information Disclosure | Already mitigated: the covers-check walks declared `properties`/`items` only, never `Evaluate`/`$ref` resolution (shipped Phase 57). |
| Broken processor crash-looping / hanging the stack | Denial of Service | Gate A stay-up posture (D-05): MarkReady flips (no crash-loop), no queue bound, no liveness key → net-zero-harmless by design. |
| Clash-log leaking sensitive config values | Information Disclosure | The D-10 log emits only ProcessorId + ConfigSchemaId + the structural clash (property name + type pair), not config values (`ConfigSchemaCoverageCheck.Detail`). |

## Sources

### Primary (HIGH confidence — current `master` source)
- `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` — 422 absent-liveness gate (lines 33-35).
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` — Gate A placement + D-10 clash log (lines 184-192, message at 187-189) + MarkReady/MarkHealthy decoupling.
- `src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs` — covers-check + locked STJ clash rule table (clash rows at lines 200-256, Detail at 288).
- `src/BaseProcessor.Core/Configuration/ProcessorConfig.cs` / `IConfigTypeProvider.cs` — marker base + TConfig provider.
- `src/BaseProcessor.Core/SourceHash.targets` — fold definition (lines 80-85), reader contract.
- `src/Processor.Sample/` — `SampleProcessor.cs`, `SampleConfig.cs`, `Processor.Sample.csproj`, `Dockerfile`, `Program.cs`, `appsettings.json` (`Service.Version` = `"3.5.0"`).
- `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` — `RealStackWebAppFactory` (373-464), `SeedProcessorAsync` (300-330, `ConfigSchemaId: null` at 325), `PollForHealthyLivenessAsync` (201-240), ES query precedent (151-189), net-zero teardown.
- `tests/BaseApi.Tests/Orchestrator/RealStackNetZeroSweepFixture.cs` + `tests/BaseApi.Tests/Observability/CollectionDefinitions.cs` + `Helpers/ElasticsearchTestClient.cs` — sweep + collection + ES poll.
- `tests/BaseApi.Tests/Orchestrator/SC{1,2,3}*E2ETests.cs` — `[Trait("Phase","55")]` (mechanical retag targets).
- `scripts/phase-55-close.ps1` — full triple-SHA close template (clone source).
- `src/BaseApi.Service/Features/Schema/{SchemaController,SchemaDtos,SchemaService}.cs` + `SchemaDefinitionFrozenException.cs` — `/api/v1/schemas` CRUD + 409 frozen-once-referenced.
- `src/BaseApi.Service/Features/Processor/{ProcessorDtos,ProcessorController}.cs` — `ProcessorCreateDto`/`ProcessorReadDto` (ConfigSchemaId), by-source-hash lookup.
- `compose.yaml` — `processor-sample` tier (265-290) template.
- `.planning/REQUIREMENTS.md` — CFG-08/09 + milestone invariant.
- `.planning/phases/58-orchestration-gate-integration-proof-close/58-CONTEXT.md` — D-01..D-13.

### Secondary (MEDIUM — in-repo planning docs)
- `.planning/phases/50-contracts-slot-array-l2-key-reshape/50-02-SUMMARY.md` — confirms the `--`-passthrough MTP filter form (`dotnet test ... -- --filter-not-trait`).
- `.planning/phases/57-startup-config-schema-fetch-gate-a/57-CONTEXT.md` (referenced via CONTEXT) — Gate A D-09 stay-up posture, D-10 log, D-06 frozen schema.

### Tertiary (LOW — none)
- None. All claims grounded in source or current planning docs.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — every component read in current source; no external libraries.
- Architecture (composition seam, 422 path, Gate A, SourceHash, seed): HIGH — code paths traced line-by-line.
- Clash shape (rule table): HIGH — `ConfigSchemaCoverageCheck` rule table read in full; recommended Option A maps to line 213.
- Close-gate clone deltas: HIGH — `phase-55-close.ps1` read in full; deltas are localized to the seed block + service list + profile bring-up.
- ES field-path for ProcessorId/ConfigSchemaId: MEDIUM — `attributes.WorkflowId` proven; exact path for the new attributes flagged as Open Question 1 with a robust message-text fallback.

**Research date:** 2026-06-13
**Valid until:** 2026-07-13 (stable — internal source, no fast-moving external deps; re-verify only if Phase 57 Gate A code or `phase-55-close.ps1` changes before planning).
