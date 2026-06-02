# Phase 28: SourceHash Identity + Processor.Sample + E2E Closeout - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-02
**Phase:** 28-sourcehash-identity-processor-sample-e2e-closeout
**Areas discussed:** SourceHash target packaging, Processor.Sample behavior, E2E topology, E2E hash proof
**Format:** prose confirm-loop (numbered forks + recommendations; user confirmed all)

---

## 1. SourceHash MSBuild target packaging/reuse

| Option | Description | Selected |
|--------|-------------|----------|
| (a) Shared `.targets` in `BaseProcessor.Core`, explicitly `<Import>`ed by each concrete | Most reusable given ProjectReference consumption; hash logic versioned next to code | ✓ |
| (b) `Directory.Build.targets` under `src/Processor.*/` | Implicit inheritance; risks hashing non-processor projects if layout shifts | |
| (c) Inline target in `Processor.Sample.csproj` | Simplest now, copy-paste debt later | |

**User's choice:** (a). **Notes:** `BaseProcessor.Core` flows by ProjectReference, not PackageReference — a package `build/*.targets` convention file would not auto-import, so explicit `<Import>` of a `.targets` in `BaseProcessor.Core` is the correct reuse model. Hash computed via inline `RoslynCodeTaskFactory` task (no new build tool). Embed must target the concrete entry assembly because the runtime reader uses `Assembly.GetEntryAssembly()`.

---

## 2. Processor.Sample ProcessAsync behavior + schema binding

| Option | Description | Selected |
|--------|-------------|----------|
| (2i) Single fixed deterministic dummy result | Smallest output proving the live pipe | ✓ |
| (2i-alt) Echo L2 input / emit ≥2 results | Multi-result/fan-out — already unit-proven in 27-02 | |
| (2ii) Schema-less (all null schema Ids) | Minimal E2E + trivial seeding; validation already unit-covered Phase 27 | ✓ |
| (2ii-alt) Declare real output schema | Would exercise live validate→mint→write hop | |

**User's choice:** 2i (single fixed result) + 2ii (schema-less). **Notes:** Processor still reads its L2 input key; only validation is skipped. SC#4 requires only "output written to L2 + orchestrator advances."

---

## 3. E2E process topology

| Option | Description | Selected |
|--------|-------------|----------|
| (a) Both Orchestrator + Processor.Sample as real containers; test drives via WebApi Start only | Truest proof; required for liveness-gated Start | ✓ |
| (b) Sample container + orchestrator in-process | Lighter, less real | |
| (c) Both in-process | Fastest, does not prove live liveness gate | |

**User's choice:** (a). **Notes:** SC#4's liveness-gated Start only passes when a real `Processor.Sample` container heartbeats `skp:{id:D}` Healthy into Redis — mirrors `CorrelationPropagationE2ETests` (in-process WebApi vs host stack) plus one more container.

---

## 4. Proving the real embedded hash (SC#3)

| Option | Description | Selected |
|--------|-------------|----------|
| (4i) Extract the actual built SourceHash from Processor.Sample and register THAT via CRUD | Proves the genuine embed closes the identity loop | ✓ |
| (4ii) Register a hardcoded/known hash, force the container to match | Weaker; does not prove the embed | |

**User's choice:** (4i). **Notes:** The embedded value must also satisfy the DB `^[a-f0-9]{64}$` validator. E2E lives alongside `CorrelationPropagationE2ETests` in `BaseApi.Tests`.

## Claude's Discretion

- `.targets` file name + RoslynCodeTaskFactory task internals (algorithm locked, form open).
- Dummy result's concrete payload shape (any single deterministic outcome-only value).
- E2E helper/fixture structure + container readiness-wait mechanics.

## Deferred Ideas

- Real (non-dummy) transform logic — future milestone.
- A second concrete `Processor.<Purpose>` — convention established, not exercised.
- NuGet packaging of the base libraries — single-repo ProjectReference retained.
- A live schema-validation hop in the E2E — Sample is schema-less; future concrete with non-null schemas would exercise it.
