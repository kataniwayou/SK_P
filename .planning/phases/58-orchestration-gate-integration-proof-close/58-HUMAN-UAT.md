---
status: passed
phase: 58-orchestration-gate-integration-proof-close
source: [58-05-PLAN.md]
started: 2026-06-13
updated: 2026-06-13
---

# Phase 58 — Orchestration-Gate Integration Proof & Close — Operator Runbook (HUMAN-UAT)

## Current Test

PASS — the live N=3xGREEN close-gate run executed 2026-06-13 and exited `0`. Triple-SHA BEFORE == AFTER
held (psql/redis/rabbitmq), N=3 consecutive GREEN at an identical `Passed` fact count of **568**,
`skp-dlq-1` depth == 0, `skp:msg:*` count == 0, both build configs 0-warning. CFG-08's three-signal
causation and CFG-09 both fired. **CFG-08 / CFG-09 are now ticked `[x]` in REQUIREMENTS.md** (see the
Step-4 record block below).

**Phase:** 58-orchestration-gate-integration-proof-close
**Milestone:** v6.0.0 (Config & Payload Validation Hardening — typed base-config seam + startup Gate A)
**Authored:** 2026-06-13
**Status:** PASSED — the close gate (`scripts/phase-58-close.ps1`), the profile-gated `processor-badconfig`
tier, and the RealStack `GateACompositionE2ETests` (CFG-08 three-signal → 422, CFG-09 compatible → 204)
are authored, hermetically green, AND now live-proven. The live N=3xGREEN close run executed 2026-06-13
and exited `0` (568 facts × 3, triple-SHA BEFORE == AFTER). **CFG-08 / CFG-09 are ticked `[x]` in
REQUIREMENTS.md** — see the Step-4 record block.

---

## Purpose

This is the operator runbook that gates the **CFG-08 / CFG-09** tick — the live proof of the v6.0.0
config-validation milestone.

Phase 58 is the FINAL phase of v6.0.0 — a LIVE-PROOF + CLOSE-GATE phase, **not** a behavior phase. The
autonomous build deliverables (Plans 01-04) ENABLE the proof; **the live run IS the proof** (D-12). The
v6 RealStack composition proof (`GateACompositionE2ETests` — CFG-08 the Gate-A-incompatible
`processor-badconfig` subject → three-signal causation → orchestration-start 422; CFG-09 the Gate-A-passing
`processor-sample` subject → Healthy → 204) and the close machinery (`scripts/phase-58-close.ps1`) are
**authored and hermetically green**: they build 0-warning at Release + Debug and the non-RealStack suite
passes (558/558).

The **actual live N=3xGREEN close run is operator-gated** (D-12 — every prior milestone close, Phase
55/49/39/35/36/33, deferred the live run to an operator gate). It requires the rebuilt v6 stack up
**including the `badconfig` profile**. Until the operator records a GREEN run in this file,
**CFG-08 / CFG-09 stay `[ ]` in REQUIREMENTS.md.**

The v6 close gate (`phase-58-close.ps1`) is a verbatim clone of the proven Phase-55 triple-SHA net-zero
protocol carrying exactly the D-09 seed deltas: it reads BOTH embedded `SourceHash`es (Sample + BadConfig),
GET-or-creates two named config-schema rows (`gateA-sample-compatible` / `gateA-badconfig-clash`) + two
processor rows CREATE-IF-ABSENT (never PUT), and runs the unchanged triple-SHA / `skp:msg:*` count==0 /
`skp-dlq-1` depth==0 / N=3 net-zero protocol — adding NO badconfig SHA exclusion and NO badconfig liveness
pre-flight (Gate A withholds its `MarkHealthy`, so it never goes Healthy; expecting Healthy would
false-fail the gate — D-09b). Retitled to Phase 58 / v6.0.0; seed version unchanged at `3.5.0` for BOTH
processors (the `SourceHash`, not the version string, distinguishes identity — D-09c).

---

## Step 1 — Clean host build (host SourceHash == container hash)

v6.0.0 is a **breaking author-contract change** (typed base-config seam + startup config-schema fetch +
Gate A). A mixed-version stack mis-deserializes the new contract — every contract-changed image MUST be
rebuilt before a live run is valid.

**First, a clean host build** so the host-built `Processor.Sample.dll` AND `Processor.BadConfig.dll`
embedded `SourceHash` match what goes into the container images. A stale *incremental* host build is the
documented Phase-49/55 failure mode (an incremental build left a stale hash that mismatched the clean
docker image, so the gate seeded a hash the container could not resolve → the liveness gate false-passes
or times out):

```
dotnet clean SK_P.sln
dotnet build SK_P.sln -c Release
dotnet build SK_P.sln -c Debug
```

Confirm **0 warnings** in BOTH configs (the close gate re-runs the both-config 0-warning build itself, but
confirming first avoids a wasted ~50-min gate run).

> WHY the clean build: a `BaseProcessor.Core` change shifts the assembly-embedded `SourceHash` on BOTH
> `Processor.Sample` and `Processor.BadConfig`. An incremental build can leave a stale hash bit on either
> host dll; the close gate reflects those hashes off the host binaries and seeds the Processor rows by them,
> but `PollForHealthyLivenessAsync` polls the REAL `processor-sample` container's `skp:{procId:D}` heartbeat
> — which is keyed by the container image's hash. If host != container, the gate either false-passes against
> a stale identity or times out waiting for liveness. A clean `dotnet clean + build` (host hash == container
> hash) is mandatory (D-12).

---

## Step 2 — Rebuild the v6 stack WITH the badconfig profile

Start from a **CLEAN redis keyspace** (BEFORE-dirty trap — close-gate net-zero protocol). Flush/verify the
empty steady-state first so the BEFORE triple-SHA snapshot is genuine net-zero (only the Sample liveness
key is expected, and it is the single redis exclusion):

```
docker exec sk-redis redis-cli FLUSHALL
docker exec sk-redis redis-cli --scan
```

Then rebuild the five contract-changed/required services **including `processor-badconfig` behind its
profile** — use the EXACT command the close script documents:

```
docker compose --profile badconfig up -d --build baseapi-service orchestrator processor-sample keeper processor-badconfig
```

Wait until every service reports healthy:

```
docker compose --profile badconfig ps
```

> WHY `--profile badconfig`: `processor-badconfig` is gated behind `profiles: ["badconfig"]` (absent from
> the default `compose up`). It is the CFG-08 Gate-A-incompatible subject — it MarkReady's (so
> `/health/ready` passes → no crash-loop) but **withholds `MarkHealthy`** (Gate A: its `BadConfig(int
> Quantity)` config type clashes with the `gateA-badconfig-clash` schema that types `quantity` as string),
> so it writes NO L2 liveness key and binds no queue. It is **net-zero-harmless** by design — it converges a
> DB row into the snapshots but never goes Healthy. Expecting it Healthy would false-fail the gate (D-09b);
> the close script deliberately excludes it from both the `$services` health-required list and the post-seed
> liveness wait.
>
> WHY rebuild all five: the v6 embedded `SourceHash` on `Processor.Sample` AND `Processor.BadConfig` must
> match the host build, and the new author-contract (typed base-config seam + Gate A) is NOT
> backward-compatible. A stale image false-passes/times out the liveness gate or mis-deserializes a message.
> `keeper` runs `deploy.replicas: 2` — both replicas must be healthy.

---

## Step 3 — Invoke the close gate

```
pwsh -File scripts/phase-58-close.ps1
```

The gate runs the **FULL test suite live (RealStack included), x3, with NO filter** — the `Phase=58` retag
does NOT change which tests the gate runs; it always runs everything (including
`GateACompositionE2ETests`). The gate:

1. Reads BOTH genuine embedded `SourceHash`es off the built dlls (`Processor.Sample.dll` +
   `Processor.BadConfig.dll`, each `^[a-f0-9]{64}$` validated, `exit 2` on mismatch).
2. Seeds the idempotent steady-state rows CREATE-IF-ABSENT (never PUT): two config-schema rows by sentinel
   Name (`gateA-sample-compatible` → `value:string`; `gateA-badconfig-clash` → `quantity:string`) + two
   processor rows (Sample with `configSchemaId = gateA-sample-compatible`, BadConfig with
   `configSchemaId = gateA-badconfig-clash`), seed version `3.5.0` for both. Waits for **`processor-sample`
   only** healthy (BadConfig is seeded-but-not-awaited — D-09b).
3. Runs the compose-stack health pre-flight (v6 canonical service list; `processor-badconfig` EXCLUDED from
   the health-required `$services`; keeper `replicas: 2`).
4. Captures the BEFORE triple-SHA snapshot (psql `\l`, redis `--scan` with the Sample `skp:{procId}` +
   `_bus_` exclusions only, rabbitmq `list_queues name`).
5. Runs the BOTH-config 0-warning build gate (`-c Release` + `-c Debug`).
6. Runs the **full suite live across the 3-consecutive-GREEN cadence** — including the RealStack composition
   facts (CFG-08 `BadConfig_GateAIncompatible_ClashLogged_LivenessAbsent_Start422` + CFG-09
   `SampleCompatible_GateAPasses_Healthy_Start204`) and the Sample/keeper RealStack proofs. An identical
   `Passed` fact count across all 3 runs is the Smell-A guard (D-10).
7. Captures the AFTER triple-SHA snapshot and asserts BEFORE == AFTER for all three. Net-zero of the
   `skp:msg:*` slot-array index is proven by the A19 active two-key DELETE — a leak surfaces as a redis SHA
   mismatch, not a silent TTL pass.
8. Asserts the sole DLQ `skp-dlq-1` depth == 0 (separate `list_queues name messages` read).
9. Asserts the `skp:msg:*` slot-array index count == 0 (additive A19 active-reclaim assertion).

**Exit codes:**
- `0` — both build configs 0-warning, all three runs GREEN, all three SHA-256 invariants held,
  `skp-dlq-1` depth == 0, and `skp:msg:*` count == 0.  **PASS.**
- `1` — invariant violation OR any build/test run RED OR unparseable fact count (Smell A).
- `2` — environment misconfigured (compose stack not healthy) OR an embedded `SourceHash` failed
  `^[a-f0-9]{64}$` validation.

---

## CFG-08 three-signal causation note

CFG-08's proof is NOT "the badconfig liveness key is absent" alone — absence could mean "container down"
rather than "Gate A withheld health." The proof is the **trio**, asserted in order by
`GateACompositionE2ETests` during the full-suite run:

1. **(a) Causation — the Gate A clash log in Elasticsearch**, polled FIRST and scoped to
   `service.name == "processor-badconfig"` (this proves the container BOOTED and Gate A RAN — distinguishing
   "Gate A withheld health" from "container down"). The shipped Gate-A Error log lives at
   `ProcessorStartupOrchestrator.cs:187`; the exact ES query:

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

2. **(b) Mechanism — `skp:{badId}` stably absent** across 3 windows spanning > one 10s heartbeat interval
   (the inverse of `PollForHealthyLivenessAsync`): Gate A withheld `MarkHealthy`, so the heartbeat no-ops and
   no liveness key is ever written.

3. **(c) Outcome — orchestration-start `422`**: `POST /api/v1/orchestration/start` for a workflow that
   includes the badconfig processor → 422 UnprocessableEntity (the `ProcessorLivenessValidator` blocks on
   the absent liveness key).

**CFG-09** is the converse: `processor-sample` with `configSchemaId = gateA-sample-compatible` — Gate A
**runs and passes** (`SampleConfig(string? Value)` COVERS `value:string`), the processor goes Healthy
(`skp:{sampleId}` present), and `POST /api/v1/orchestration/start` → **204** NoContent.

---

## Step 4 — Record the GREEN run

On a PASS (exit 0), copy the gate's PASS-summary values into the record block below. The N=3 GREEN guard
requires the **same `Passed` fact count across all three runs** (Smell-A guard, D-10). This recorded GREEN
run is the artifact that gates the CFG-08/CFG-09 tick.

| Field                                            | Value |
|--------------------------------------------------|-------|
| psql `\l` SHA-256 (BEFORE == AFTER)              | `ed52e389db65fc725310a5f753d209190970f3bee5a4c7cd55308ee38e3f9d34` |
| redis `--scan` SHA-256 (BEFORE == AFTER)         | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` (net-zero keyspace; only the excluded Sample liveness key `skp:f985ffcb-d7d2-454e-811d-05fe5cbf565f`; NO badconfig exclusion needed — Gate A withheld its liveness) |
| rabbitmq `list_queues` SHA-256 (BEFORE == AFTER) | `8800097247c8b5d507b7d82d3ded780431a6aacba85b1df83ddd6683737a25f2` (transient `_bus_` queues excluded) |
| `Passed` fact count (identical across all 3 runs)| `568` (Run 1 = 568, Run 2 = 568, Run 3 = 568) |
| `skp-dlq-1` depth (== 0)                         | `0` |
| `skp:msg:*` slot-array index count (== 0)        | `0` |
| CFG-08 three-signal fired (clash log + absent liveness + 422) | `Yes` — `GateACompositionE2ETests.BadConfig_GateAIncompatible_ClashLogged_LivenessAbsent_Start422` GREEN (ES clash log via `attributes.{OriginalFormat}` + `attributes.ProcessorId=b4277f0d-2f0f-4689-a4fc-112eb3cbb67d` scoped to `service.name=processor-badconfig`; `skp:{badId}` stably absent; orchestration-start 422) |
| CFG-09 (Gate-A-pass + `skp:{sampleId}` present + 204) | `Yes` — `GateACompositionE2ETests.SampleCompatible_GateAPasses_Healthy_Start204` GREEN (Gate-A-pass + `skp:{sampleId}` present + 204) |
| Gate exit code (== 0)                            | `0` |
| Run date                                         | `2026-06-13` |
| Operator                                         | Claude Code (automated close run, user-authorized "verify and approve by yourself") |

**Per-run cadence:** Run 1 Exit=0 Passed=568 (10m03s); Run 2 Exit=0 Passed=568 (10m11s); Run 3 Exit=0 Passed=568 (9m01s).

> The three SHA values + the `Passed` count + the `skp-dlq-1` depth + the `skp:msg:*` count mirror the
> gate's operator-append line printed on PASS. If any run is RED or any SHA mismatches, capture the failure
> detail here and return for gap closure rather than ticking the requirements.

---

## Step 5 — DoD gate (tick CFG-08/CFG-09)

The following requirements are ticked **ONLY** after the N=3 consecutive GREEN run with triple-SHA
BEFORE == AFTER, `skp-dlq-1` depth == 0, `skp:msg:*` count == 0, AND the CFG-08 three-signal + CFG-09
both-fired is recorded in Step 4 above — and **ONLY** by the operator. Until then they stay `[ ]` in
`.planning/REQUIREMENTS.md`.

- [x] **CFG-08** — Live RealStack: an orchestration that includes the Gate-A-incompatible
      `processor-badconfig` is BLOCKED at orchestration start with **422**, proven by the three-signal
      causation — (a) the Gate A clash log in Elasticsearch scoped to `service.name == processor-badconfig`
      (causation: boot + Gate-A-ran), (b) `skp:{badId}` stably absent across > one heartbeat interval
      (mechanism: withheld `MarkHealthy`), (c) orchestration-start 422 (outcome) — asserted by
      `GateACompositionE2ETests.BadConfig_GateAIncompatible_ClashLogged_LivenessAbsent_Start422`.
      **TICKED 2026-06-13 — GREEN N=3 close run (exit 0), Step-4 record block above.**
- [x] **CFG-09** — Live RealStack: a compatible `processor-sample` (`SampleConfig` COVERS
      `gateA-sample-compatible`) Gate-A-passes → `skp:{sampleId}` Healthy → orchestration start returns
      **204**, asserted by `GateACompositionE2ETests.SampleCompatible_GateAPasses_Healthy_Start204`.
      **TICKED 2026-06-13 — GREEN N=3 close run (exit 0), Step-4 record block above.**

These remain unticked in `.planning/REQUIREMENTS.md` until the operator records the GREEN run in Step 4.
On a recorded GREEN run, flip `[ ]` → `[x]` here AND in `.planning/REQUIREMENTS.md`, referencing the Step-4
record block, and flip this file's frontmatter `status: pending` → `status: passed`.

DoD gate condition: **N=3 GREEN + triple-SHA equality + both-config 0-warning + `skp-dlq-1` depth==0 +
`skp:msg:*` count==0 + CFG-08 three-signal fired + CFG-09 both-fired.** (Ticks CFG-08/CFG-09 — NOT
TEST-01/TEST-02, which were the v5.0.0 Phase-55 reqs.)

---

## Threat mitigations (recorded — PLAN threat register)

Three threat surfaces are mitigated by this runbook + the cloned gate:

1. **Stale/mixed-version image / identity divergence** (T-58-12, Spoofing) — mitigated by the operator
   rebuild (Step 2: `docker compose --profile badconfig up -d --build` of all five contract-changed
   services) plus the clean `dotnet clean + build` (Step 1). The close script's `^[a-f0-9]{64}$` hash
   validation on BOTH embedded hashes (`exit 2` on mismatch) + the `SourceHash` == host-build liveness gate
   make a false-pass impossible: the gate polls the REAL `processor-sample` container heartbeat by its hash.
2. **False GREEN / net-zero snapshot integrity** (T-58-13, Repudiation) — mitigated by starting from a
   CLEAN redis keyspace (Step 2 — BEFORE-dirty trap) and N=3 identical-fact-count consecutive GREEN, so a
   one-off coincidence cannot pass. CFG-08's ES clash-log requirement (polled FIRST, load-bearing) prevents
   an absence-coincidence false GREEN. Verified by the close script's N=3 Smell-A guard + this runbook's
   clean-keyspace precondition.
3. **`processor-badconfig` DoS under the profile** (T-58-14, accept) — Gate A's stay-up posture keeps it
   net-zero-harmless (MarkReady → `/ready` passes, no crash-loop; withholds MarkHealthy → no liveness key,
   binds no queue). The live run is bounded (~50 min) and operator-supervised. No mitigation beyond the
   shipped stay-up design.

---

## Tests

### 1. Live N×GREEN close-gate run — gates CFG-08 / CFG-09
expected: After the clean host build (Step 1, so host hash == container hash for BOTH Sample and BadConfig) and the `--profile badconfig` rebuild from a clean redis keyspace (Step 2), `pwsh -File scripts/phase-58-close.ps1` exits `0` — 3 consecutive GREEN runs with identical `Passed` fact count, triple-SHA (psql `\l` / redis `--scan` / rabbitmq `list_queues`) BEFORE == AFTER net-zero (no badconfig SHA exclusion), `skp-dlq-1` depth == 0, `skp:msg:*` count == 0, at Release + Debug 0-warning. The CFG-08 three-signal (ES clash log scoped to `processor-badconfig` + `skp:{badId}` stably absent + orchestration-start 422) and CFG-09 (Gate-A-pass + `skp:{sampleId}` present + 204) RealStack facts both pass live. Record the 3 SHA values + Passed count + DLQ depth + `skp:msg:*` count + the CFG-08/09 signals in the Step 4 record block, then tick CFG-08/CFG-09 in REQUIREMENTS.md.
result: PASS — 2026-06-13. `scripts/phase-58-close.ps1` exited `0`: N=3 consecutive GREEN at an identical `Passed` fact count of 568, triple-SHA BEFORE == AFTER held (psql `ed52e389…`, redis `e3b0c442…`, rabbitmq `88000972…`), `skp-dlq-1` depth == 0, `skp:msg:*` count == 0, Release + Debug 0-warning. CFG-08 three-signal (ES clash log scoped to `processor-badconfig` via `attributes.{OriginalFormat}` + `attributes.ProcessorId` + `skp:{badId}` absent + orchestration-start 422) and CFG-09 (Gate-A-pass + `skp:{sampleId}` present + 204) both GREEN. CFG-08/CFG-09 ticked in REQUIREMENTS.md.

## Summary

total: 1
passed: 1
issues: 0
pending: 0
skipped: 0
blocked: 0

## Gaps

None. The live N=3 GREEN close gate passed (exit 0). One mid-run fix surfaced and was applied before the
PASS: the Gate-A clash-log ES poll in `GateACompositionE2ETests` queried `match: body "Gate A
incompatibility"`, but otel maps the message under the nested `body.text` (not phrase-searchable as a flat
`body`), so the first run RED'd at the ES poll. Fixed to the proven structured-attribute query (term on
`attributes.{OriginalFormat}` + `attributes.ProcessorId` scoped to `service.name == processor-badconfig` —
commit `bfa5a65`). Gate A's product behavior was always correct; only the test's ES query convention was
wrong. After the fix both GateAComposition tests verified 2/2 GREEN, then the full N=3 gate passed.
