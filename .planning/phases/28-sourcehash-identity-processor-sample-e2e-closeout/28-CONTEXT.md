# Phase 28: SourceHash Identity + Processor.Sample + E2E Closeout - Context

**Gathered:** 2026-06-02
**Status:** Ready for planning

<domain>
## Phase Boundary

Phase 28 delivers three things and nothing more — it is the v3.5.0 milestone closeout:

1. **Build-time SourceHash identity (IDENT-01/02)** — an MSBuild target that computes the implementation SourceHash and embeds it as `[assembly: AssemblyMetadata("SourceHash", "<64-hex>")]` onto the **concrete** entry assembly.
2. **First concrete `Processor.Sample` (SAMPLE-01/02)** — a minimal POC console implementing `ProcessAsync`, carrying no infrastructure/id/L2/bus code (all inherited from `BaseProcessor.Core`), plus a multistage Dockerfile and a compose tier mirroring the Orchestrator.
3. **Real-stack E2E + close gate (TEST-01/02)** — a live proof of the orchestrator→`Processor.Sample`→orchestrator round-trip and the liveness-gated Start path, behind the 3-GREEN / triple-SHA BEFORE=AFTER close gate with scan-clean teardown.

New capabilities (real transform logic, a second concrete, NuGet packaging of the base) belong in future milestones — not this phase.

</domain>

<decisions>
## Implementation Decisions

### SourceHash MSBuild Target (IDENT-01/02)
- **D-01:** The hash-and-embed logic is authored **once** as a shared `.targets` file that lives in `BaseProcessor.Core`, and each concrete `Processor.*` project **explicitly `<Import>`s** it. This is the chosen reuse model because `BaseProcessor.Core` is consumed by **ProjectReference, not PackageReference** — a `build/*.targets` convention file would NOT auto-flow over a ProjectReference, so implicit package-style import is not available. Authoring it in `BaseProcessor.Core` keeps the hash logic versioned next to the code it describes.
  - Rejected: `Directory.Build.targets` under `src/Processor.*/` (implicit inheritance risks hashing non-processor projects if the folder layout shifts).
  - Rejected: inline target in `Processor.Sample.csproj` (copy-paste debt for every future `Processor.<Purpose>`).
- **D-02:** The hash is computed by an **inline `RoslynCodeTaskFactory` MSBuild task** (no new compiled build tool, no external script, no NuGet dependency). Deterministic and unit/build-testable. The algorithm itself is already locked by IDENT-01 (SHA-256, lowercase 64-hex, LF-normalized, per-file hashes folded over an **ordinal path sort** of `BaseProcessor.Core` + the concrete's `.cs`, excluding generated files, `BaseConsole.Core`, and `Messaging.Contracts`); `BeforeTargets=CoreCompile`; re-runs on implementation-source change (no stale hash on incremental builds).
- **D-03:** Embed target constraint — the runtime reader `AssemblyMetadataSourceHashProvider.Get()` resolves the hash from `Assembly.GetEntryAssembly()`. Therefore the target MUST emit the attribute onto the **concrete (entry) assembly** (`Processor.Sample`), even though the hash content folds in `BaseProcessor.Core`'s sources.

### Processor.Sample Behavior (SAMPLE-01/02)
- **D-04:** `ProcessAsync` returns a **single, fixed, deterministic dummy result** — the smallest output that proves the live pipe. The multi-result / fan-out minting path is already unit-proven in Plan 27-02, so the live POC does not need to emit ≥2 results.
- **D-05:** `Processor.Sample` runs **schema-less** — `InputSchemaId`, `OutputSchemaId`, and `ConfigSchemaId` are all null on its registered Processor row. SC#4 only requires "output written to L2 + orchestrator advances"; input/output validation is already unit-covered in Phase 27, so null schemas keep the E2E minimal and seeding trivial. The processor still reads its L2 input key (input data resolution is exercised), it just skips validation.
- **D-06:** Dockerfile + compose tier **mirror the Orchestrator tier** verbatim in shape (multistage build → runtime image; a `processor-sample` service joining `compose.yaml` alongside `sk-orchestrator`). The `ComposeYamlFacts` guard test is extended to cover the new service.

### Real-Stack E2E Topology (TEST-01)
- **D-07:** **Both** the Orchestrator and `Processor.Sample` run as **real containers** in the host compose stack; the E2E test drives the round-trip **only** via `POST /api/v1/orchestration/start` (an in-process WebApi pointed at the host stack — RMQ `localhost:5673`, Redis `localhost:6380`, Postgres `localhost:5433`, otel `localhost:4317`) and asserts the orchestrator advances on the returned `ExecutionResult`. This is the established `CorrelationPropagationE2ETests` pattern + one additional container.
  - Rationale: a containerized, heartbeating `Processor.Sample` is **required** to prove SC#4's liveness-gated Start — the Start liveness gate only passes when a real processor is writing its `skp:{id:D}` Healthy heartbeat into Redis. Hosting the processor or orchestrator in-process would not exercise that gate truthfully.
  - Rejected: Sample-container + orchestrator-in-process; both-in-process (neither proves the live liveness gate end-to-end).
- **D-08:** The E2E proves the **real embedded hash** (SC#3): the test extracts `Processor.Sample`'s **actual built** SourceHash (reflect the built assembly / read the build artifact) and registers THAT exact value as the Processor DB row via CRUD — so identity resolution closes against the genuine embedded value (which must also satisfy the DB `^[a-f0-9]{64}$` validator). Registering a hardcoded/known hash was rejected as not proving the embed.
- **D-09:** The new E2E lives alongside `CorrelationPropagationE2ETests` in `BaseApi.Tests` (Orchestrator/Processor E2E area), reusing the host-stack helper conventions.

### Close Gate (TEST-02)
- **D-10:** The phase-close gate is unchanged in discipline (locked since Phase 3 D-15/D-18): 3-consecutive-GREEN cadence + triple-SHA (`psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues`) BEFORE=AFTER. The scan-clean teardown is **extended** to cover the new processor-liveness (`skp:{id:D}`) and execution-data (`L2ProjectionKeys.ExecutionData`) keys so no leaked keys mask under the SHA.

### Claude's Discretion
- Exact `.targets` file name and the RoslynCodeTaskFactory task internals (algorithm is locked; implementation form is open).
- The dummy result's concrete payload shape (any single deterministic value satisfying the outcome-only result contract).
- E2E helper/fixture structure and container readiness-wait mechanics, consistent with existing host-stack E2E helpers.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase requirements & success criteria
- `.planning/REQUIREMENTS.md` — IDENT-01/02, SAMPLE-01/02, TEST-01/02 acceptance text + the "Sample Concrete" / "Testing & Closeout" / non-goals tables (the DB `^[a-f0-9]{64}$` validator constraint, the "dummy list" POC boundary, the "only `Processor.Sample` ships" non-goal).
- `.planning/ROADMAP.md` §"Phase 28: SourceHash Identity + Processor.Sample + E2E Closeout" — Goal + the 5 numbered Success Criteria (the authoritative SC list).

### SourceHash embed/read seam
- `src/BaseProcessor.Core/Identity/AssemblyMetadataSourceHashProvider.cs` — the runtime reader (reads `SourceHash` from `Assembly.GetEntryAssembly()`); **constrains the embed target to emit onto the concrete entry assembly** (D-03).
- `src/BaseProcessor.Core/Identity/ISourceHashProvider.cs` — the seam contract.

### Concrete + container tier to mirror (SAMPLE-02)
- `src/Orchestrator/Dockerfile` — the multistage Dockerfile shape to mirror.
- `compose.yaml` — the compose stack the `processor-sample` service joins (alongside `sk-orchestrator`, `sk-rabbitmq`, etc.).
- `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` — the compose-guard test to extend for the new service.

### Round-trip code under live test (TEST-01)
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` — the consumer the E2E exercises end-to-end (Plan 27-02 output).
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` — bind-then-MarkHealthy startup (Plan 27-03 output) the live container runs.
- `tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs` — the host-stack E2E pattern to mirror (in-process WebApi driver against real containers; host ports 5673/6380/5433/4317).
- `src/BaseApi.Service/Features/Processor/ProcessorService.cs` + `ProcessorDtoValidator.cs` — the CRUD path + `^[a-f0-9]{64}$` SourceHash validator the embedded hash must satisfy (D-08 / SC#3).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `AssemblyMetadataSourceHashProvider` (Phase 26): already reads the embedded attribute via reflection over the entry assembly — Phase 28 only needs to *produce* what it reads.
- `EntryStepDispatchConsumer` + `ProcessorStartupOrchestrator` (Phase 27): the full processor-half round-trip + bind-before-Healthy startup — `Processor.Sample` is just a thin concrete on top.
- `Orchestrator/Dockerfile` + `compose.yaml` + `ComposeYamlFacts`: a proven multistage-Dockerfile + compose-tier + guard-test triad to mirror for the processor.
- `CorrelationPropagationE2ETests`: the host-stack E2E harness (in-process WebApi → real containers) to mirror for TEST-01.

### Established Patterns
- `BaseProcessor.Core` is consumed by **ProjectReference** (not NuGet) — this is why the reusable MSBuild target must be an explicit `<Import>` of a `.targets` file in `BaseProcessor.Core`, not a package `build/` convention file (D-01).
- Real-stack E2E = in-process WebApi/driver pointed at the host compose stack, with the participating services (Orchestrator, now Processor.Sample) as real containers (D-07).
- Close gate: 3-consecutive-GREEN + multi-SHA BEFORE=AFTER, FLUSHDB forbidden, SCAN-assert-zero teardown (extended here for liveness + execution-data keys).

### Integration Points
- New `src/Processor.Sample/` project (entry assembly carrying the embedded hash + `ProcessAsync`).
- New `.targets` in `src/BaseProcessor.Core/` imported by `Processor.Sample.csproj`.
- New `processor-sample` service in `compose.yaml`; new multistage `src/Processor.Sample/Dockerfile`.
- New E2E in `tests/BaseApi.Tests/` (Orchestrator/Processor E2E area).
- The registered Processor DB row (real embedded hash, null schema Ids) seeded via the existing CRUD surface.

</code_context>

<specifics>
## Specific Ideas

- The E2E must prove the *genuine* embedded SourceHash closes the identity loop (D-08) — not a stand-in — because that is the whole point of the build-time-identity mechanism.
- The liveness-gated Start proof (SC#4) is the reason both processes are containerized (D-07): only a real heartbeating container makes the Start gate pass truthfully.

</specifics>

<deferred>
## Deferred Ideas

- **Real (non-dummy) transform logic** — POC milestone returns a dummy list; real processing is a future milestone (REQUIREMENTS non-goal).
- **A second concrete `Processor.<Purpose>`** — the family convention is established but only `Processor.Sample` ships this milestone (REQUIREMENTS non-goal).
- **NuGet packaging of `BaseProcessor.Core` / `BaseApi.Core`** — single-repo ProjectReference model retained (PROJECT.md locked decision).
- **Live schema-validation hop in the E2E** — Sample runs schema-less (D-05); a future concrete with non-null schemas would exercise the live validate→mint→write path (already unit-covered in Phase 27).

None of these were in scope — discussion stayed within the phase boundary.

</deferred>

---

*Phase: 28-sourcehash-identity-processor-sample-e2e-closeout*
*Context gathered: 2026-06-02*
