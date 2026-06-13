---
status: pending
phase: 62-live-proof-close-gate
source: [62-03-PLAN.md]
started: 2026-06-13
updated: 2026-06-13
---

# Phase 62 — Live Proof & Close Gate — Operator Runbook (HUMAN-UAT)

## Current Test

BLOCKED ON A VERIFIED GAP (2026-06-13). The live run was executed (operator-supervised via Claude Code):
the **close gate (TEST-03) PASSED** — N=3×604 GREEN, triple-SHA `psql \l` / `redis --scan` / `rabbitmq
list_queues` BEFORE==AFTER, `skp-dlq-1` depth 0, `skp:msg:*` count 0 — and **TEST-01a** (two healthy
replicas self-register) + **TEST-02** (`/health/live` 200 + summary) passed live. **TEST-01b FAILED**: a
durably-broken (Gate-A-clash) replica is observable as `Unhealthy` only for its ~60s startup-TTL window,
then goes **absent** — the liveness-timestamp refresh is coupled to `IsHealthy` (the heartbeat is
`IsHealthy`-gated; the startup terminal-clash path returns without handing off to a refresh loop). This is
a **Phase-60 product gap**, not a Phase-62 test/runbook issue — see
`62-GAP-liveness-refresh-coupling.md`. **TEST-01c was not run** (blocked by the same gap). TEST-01 /
TEST-02 / TEST-03 stay `[ ]` in `.planning/REQUIREMENTS.md` and this file stays `status: pending` until the
Phase-60 fix lands and the live proof is re-run clean.

**Phase:** 62-live-proof-close-gate
**Milestone:** v7.0.0 (Per-Replica Processor Liveness & Self-Watchdog)
**Authored:** 2026-06-13
**Status:** PENDING — close machinery + RealStack keyspace tests authored and hermetically green; the live
run IS the proof and is operator-gated (D-15).

---

## Purpose

This is the operator runbook that gates the **TEST-01 / TEST-02 / TEST-03** tick — the live proof of the
v7.0.0 per-replica-liveness milestone.

Phase 62 is the **FINAL phase of v7.0.0** — a LIVE-PROOF + CLOSE-GATE phase, **not** a behavior phase. The
autonomous build deliverables (Plans 01-03) ENABLE the proof; **the live run IS the proof** (D-15). The v7
RealStack deterministic gate-verdict proof (`GateKeyspaceE2ETests` — fabricated `skp:proc:*` keys driving
the in-process `ProcessorLivenessValidator` >=1-healthy / 422 verdicts) and the close machinery
(`scripts/phase-62-close.ps1`) are **authored and hermetically green**: they build 0-warning at Release +
Debug and the non-RealStack suite passes.

The **actual live N=3xGREEN close run is operator-gated** (D-15 — every prior milestone close, Phase
58/55/49/39/35/36/33, deferred the live run to an operator gate). It requires the rebuilt v7 stack up with
`processor-sample` as **TWO replicas** and **including the `badconfig` profile**. Until the operator records
a GREEN run in this file, **TEST-01 / TEST-02 / TEST-03 stay `[ ]` in `.planning/REQUIREMENTS.md`.**

The v7 close gate (`phase-62-close.ps1`) is a verbatim clone of the proven Phase-58 triple-SHA net-zero
protocol carrying exactly the D-09 redis-scan delta + the Phase 62 / v7.0.0 retitle: the `redis-cli --scan`
SHA now excludes the per-replica liveness namespace by PREFIX `^skp:proc:` (replacing phase-58's single
exact-key `skp:{procId}` exclusion) because v7 instanceIds are non-deterministic and there are now 2-3 of
them (2 healthy Sample replicas + index + the durably-unhealthy badconfig replica + index). It reads BOTH
embedded `SourceHash`es (Sample + BadConfig), GET-or-creates two named config-schema rows
(`gateA-sample-compatible` / `gateA-badconfig-clash`) + two processor rows CREATE-IF-ABSENT (never PUT), and
runs the unchanged triple-SHA / `skp:msg:*` count==0 / `skp-dlq-1` depth==0 / N=3 net-zero protocol. Seed
version unchanged at `3.5.0` for BOTH processors (the `SourceHash`, not the version string, distinguishes
identity — D-12).

---

## Step 1 — Clean host build (host SourceHash == container hash)

v7.0.0 is a **breaking processor-liveness-contract change** (per-instance L2 keys
`skp:proc:{procId}:{instanceId}` + instance-index SET, two-state health, dual-loop writer, in-memory L1,
>=1-healthy orchestration gate, self-watchdog probe). A mixed-version stack mis-deserializes the new
contract — every contract-changed image MUST be rebuilt before a live run is valid.

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

> WHY the clean build (D-16): a `BaseProcessor.Core` change shifts the assembly-embedded `SourceHash` on
> BOTH `Processor.Sample` and `Processor.BadConfig`. An incremental build can leave a stale hash bit on
> either host dll; the close gate reflects those hashes off the host binaries and seeds the Processor rows
> by them, but the gate polls the REAL `processor-sample` replicas' `skp:proc:{procId}:{instanceId}`
> heartbeat — which is keyed by the container image's hash. If host != container, the gate either
> false-passes against a stale identity or times out waiting for liveness. A clean `dotnet clean + build`
> (host hash == container hash) is mandatory (D-16; RESEARCH Pitfall 3).

---

## Step 2 — Rebuild the v7 stack WITH the badconfig profile

Start from a **CLEAN redis keyspace** (BEFORE-dirty trap — close-gate net-zero protocol). Flush/verify the
empty steady-state first so the BEFORE triple-SHA snapshot is genuine net-zero (only the `^skp:proc:`
per-replica liveness keys are expected, and they are the prefix-excluded namespace):

```
docker exec sk-redis redis-cli FLUSHALL
docker exec sk-redis redis-cli --scan
```

Then rebuild the five contract-changed/required services **including `processor-badconfig` behind its
profile** — use the EXACT command the close script documents:

```
docker compose --profile badconfig up -d --build baseapi-service orchestrator processor-sample keeper processor-badconfig
```

`processor-sample` now scales to **2 replicas** via `deploy.replicas: 2` (D-01) — the `up` command is
identical; compose brings up both replicas automatically. Wait until every service reports healthy:

```
docker compose --profile badconfig ps
```

> WHY two replicas (D-01): `processor-sample` was reshaped to `deploy.replicas: 2` (mirroring the `keeper`
> tier) — `container_name` dropped, `deploy.replicas: 2` added. The default `docker compose up` now runs
> TWO replicas, each writing its own `skp:proc:{procId}:{instanceId}` per-instance key + `SADD`ing its
> non-deterministic instanceId to the shared `skp:proc:{procId}` index SET. Reach a specific replica via
> `docker compose ps processor-sample` → compose-generated container name → `docker exec <name>`.
>
> WHY `--profile badconfig`: `processor-badconfig` is gated behind `profiles: ["badconfig"]` (absent from
> the default `compose up`). It is the durably-broken subject — it MarkReady's (so `/health/ready` passes →
> no crash-loop) but **withholds `MarkHealthy`** (Gate A: its `BadConfig(int Quantity)` config type clashes
> with the `gateA-badconfig-clash` schema that types `quantity` as string). In v7 it now writes a durable
> `skp:proc:{badId}:{instanceId}` **Unhealthy** key + index (the startup orchestrator writes Unhealthy per
> iteration after identity resolves — RF-01/STATE-03), unlike v6 where it wrote nothing. It is
> **net-zero-harmless** by design — its key is under the `^skp:proc:` prefix exclusion, and it binds no
> queue. It never goes Healthy; expecting it Healthy would false-fail the gate (D-09b); the close script
> deliberately excludes it from both the `$services` health-required list and the post-seed liveness wait.
>
> WHY rebuild all five: the v7 embedded `SourceHash` on `Processor.Sample` AND `Processor.BadConfig` must
> match the host build, and the new liveness contract is NOT backward-compatible. A stale image
> false-passes/times out the liveness gate or mis-deserializes a message. `keeper` runs `deploy.replicas: 2`
> — both replicas must be healthy; `processor-sample` is now likewise 2 replicas.

---

## Step 3 — Invoke the close gate

```
pwsh -File scripts/phase-62-close.ps1
```

The gate runs the **FULL test suite live (RealStack included), x3, with NO filter** — the `[Trait("Phase","62")]`
retag does NOT change which tests the gate runs; it always runs everything (including `GateKeyspaceE2ETests`,
`GateACompositionE2ETests`, the SC1/SC2/SC3 round-trip/recovery/pause-resume proofs, and the Sample
round-trip). The gate:

1. Reads BOTH genuine embedded `SourceHash`es off the built dlls (`Processor.Sample.dll` +
   `Processor.BadConfig.dll`, each `^[a-f0-9]{64}$` validated, `exit 2` on mismatch).
2. Seeds the idempotent steady-state rows CREATE-IF-ABSENT (never PUT): two config-schema rows by sentinel
   Name (`gateA-sample-compatible` → `value:string`; `gateA-badconfig-clash` → `quantity:string`) + two
   processor rows (Sample with `configSchemaId = gateA-sample-compatible`, BadConfig with
   `configSchemaId = gateA-badconfig-clash`), seed version `3.5.0` for both. Waits for **all
   `processor-sample` replicas** healthy (BadConfig is seeded-but-not-awaited — D-09b).
3. Runs the compose-stack health pre-flight (v7 canonical service list; `processor-badconfig` EXCLUDED from
   the health-required `$services`; both `keeper` and `processor-sample` are `replicas: 2`, parsed
   line-by-line all-instances-healthy).
4. Captures the BEFORE triple-SHA snapshot (psql `\l`, redis `--scan` with the `^skp:proc:` prefix +
   `_bus_` exclusions only, rabbitmq `list_queues name`).
5. Runs the BOTH-config 0-warning build gate (`-c Release` + `-c Debug`).
6. Runs the **full suite live across the 3-consecutive-GREEN cadence** — including the RealStack keyspace
   verdict facts. An identical `Passed` fact count across all 3 runs is the Smell-A guard (D-13).
7. Captures the AFTER triple-SHA snapshot and asserts BEFORE == AFTER for all three. Net-zero of the
   `skp:msg:*` slot-array index is proven by the A19 active two-key DELETE — a leak surfaces as a redis SHA
   mismatch, not a silent TTL pass. The `^skp:proc:` per-replica namespace is excluded, so a churned
   liveness key cannot cause the redis mismatch.
8. Asserts the sole DLQ `skp-dlq-1` depth == 0 (separate `list_queues name messages` read).
9. Asserts the `skp:msg:*` slot-array index count == 0 (additive A19 active-reclaim assertion).

**Exit codes:**
- `0` — both build configs 0-warning, all three runs GREEN, all three SHA-256 invariants held,
  `skp-dlq-1` depth == 0, and `skp:msg:*` count == 0.  **PASS.**
- `1` — invariant violation OR any build/test run RED OR unparseable fact count (Smell A).
- `2` — environment misconfigured (compose stack not healthy) OR an embedded `SourceHash` failed
  `^[a-f0-9]{64}$` validation.

---

## Lifecycle proofs (OUTSIDE the close-gate window — D-03 / D-11)

These four genuinely-multi-container lifecycle proofs are the v7 additions the xUnit harness cannot do
(it never lifecycles a real processor container). Run them **OUTSIDE** the close-gate window so they never
perturb the BEFORE == AFTER triple-SHA (D-11). Resolve the processor's seeded procId via
`GET /api/v1/processors/by-source-hash/<sampleHash>` (or read it from the close-gate output) — call it
`{procId:D}` below; the badconfig procId is `{badId:D}`.

### TEST-01a — two REAL replicas self-register

Each of the two `processor-sample` replicas writes its own per-instance key and `SADD`s its instanceId to
the shared index SET:

```
docker exec sk-redis redis-cli SMEMBERS skp:proc:{procId:D}
```

Expect **2 distinct instanceIds**. For each instanceId, confirm a per-instance key exists with
`status=Healthy`:

```
docker exec sk-redis redis-cli GET skp:proc:{procId:D}:{instanceId}
```

Expect a JSON value whose top-level `status` is `Healthy` (and `summary.inputSchema/outputSchema/configSchema`
present). PASS when SMEMBERS shows exactly 2 instanceIds, each with a Healthy per-instance key.

### TEST-01b — durably-broken replica observable as UNHEALTHY (NOT absent)

With `--profile badconfig up` (Step 2), the badconfig replica resolves identity, hits the Gate-A clash, and
writes a durable Unhealthy key per startup iteration (v7 NEW — replaces v6's "stably absent" assertion;
RESEARCH Pitfall 2 / RF-01). Confirm its per-instance key exists and is Unhealthy:

```
docker exec sk-redis redis-cli SMEMBERS skp:proc:{badId:D}
docker exec sk-redis redis-cli GET skp:proc:{badId:D}:{instanceId}
```

Expect the per-instance key to exist with top-level `status=Unhealthy` (NOT null/absent). **Assert
`status=Unhealthy`, NOT "absent"** — v6's "absent" assertion would false-fail here because v7 durably writes
the Unhealthy key. The orchestration-start gate still counts it as unhealthy (an unhealthy replica does not
admit), so a workflow that includes only badconfig still 422s.

### TEST-01c — dead-replica TTL-expiry + lazy-SREM

`docker stop` a HEALTHY `processor-sample` replica (resolve the name dynamically):

```
$name = (docker compose ps processor-sample --format json | ConvertFrom-Json | Select-Object -First 1).Name
docker stop $name
```

Wait **> 30s** for the heartbeat key to TTL-expire. The HEALTHY heartbeat key's TTL is
`max(interval*2, Ttl) = max(20, 30) = 30s` per RESEARCH RF-03 — explicitly **30s, NOT 60s** (60s is the
startup/unhealthy key's TTL, `max(StartupInterval*2, Ttl) = max(60, 30)`). After > 30s the stopped replica's
per-instance key GETs to null:

```
# after > 30s (recommend ~40-45s to clear the race against the last heartbeat write):
docker exec sk-redis redis-cli GET skp:proc:{procId:D}:{deadInstanceId}
```

Expect `(nil)`. The instanceId is still in the index SET (the SET member is not auto-expired). Trigger a
validator read so the absent member is lazily `SREM`'d (the gate's fire-and-forget SREM on a GET-null
member), then assert the index shrinks by one:

```
# trigger an orchestration-start that exercises the gate (any start that reads this procId's index):
curl -s -X POST http://localhost:8080/api/v1/orchestration/start -H "Content-Type: application/json" -d '[<some-workflow-id>]'
# then:
docker exec sk-redis redis-cli SMEMBERS skp:proc:{procId:D}
```

Expect `SMEMBERS` to have shrunk by one (the dead instanceId lazily SREM'd). PASS when GET→null after >30s
and SMEMBERS shrinks after the validator read.

### TEST-02-probe-live — `/health/live` 200 + summary on a HEALTHY replica (D-06 live half)

Resolve a HEALTHY replica name dynamically and `docker exec` the probe (no published port — D-02; internal
port 8082 per `ConsoleHealth:Port`):

```
$name = (docker compose ps processor-sample --format json | ConvertFrom-Json | Select-Object -First 1).Name
docker exec $name wget -qO- http://localhost:8082/health/live
```

Expect **200** + a JSON body carrying the per-schema summary keys `inputSchema` / `outputSchema` /
`configSchema` (PROBE-02 summary). The verdict half (stale L1 → Unhealthy + summary in body) is already
proven hermetically by `LivenessWatchdogHealthCheckTests` (RF-02) — only the live wiring is manual here.

> Do **NOT** target badconfig's probe — a looping-unhealthy startup keeps L1 FRESH (the watchdog only checks
> staleness, not status), so badconfig's `/health/live` returns **Healthy** by design (RESEARCH Open
> Question 1). The watchdog detects a STOPPED loop, not an unhealthy verdict.

---

## Step 4 — Record the GREEN run

On a PASS (exit 0), copy the gate's PASS-summary values into the record block below. The N=3 GREEN guard
requires the **same `Passed` fact count across all three runs** (Smell-A guard, D-13). This recorded GREEN
run is the artifact that gates the TEST-01/TEST-02/TEST-03 tick.

| Field                                            | Value |
|--------------------------------------------------|-------|
| psql `\l` SHA-256 (BEFORE == AFTER)              | `eae042b559e4bbfceb041df15aba70fc31a46490cb935d44b41ecc73fad6e8b9` ✅ HELD |
| redis `--scan` SHA-256 (BEFORE == AFTER)         | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` ✅ HELD (`^skp:proc:` prefix excluded; empty-set SHA = clean keyspace) |
| rabbitmq `list_queues` SHA-256 (BEFORE == AFTER) | `ccc40a5ed808c5f3a66b47dd2c4c8617fda462fa8160578d9cfa16c664877a9e` ✅ HELD (`_bus_` excluded; steady-state queues incl. live Sample + stable fabricated-test queue) |
| `Passed` fact count (identical across all 3 runs)| **604** ✅ (Run 1 = 604, Run 2 = 604, Run 3 = 604) |
| `skp-dlq-1` depth (== 0)                         | `0` ✅ |
| `skp:msg:*` slot-array index count (== 0)        | `0` ✅ |
| TEST-01a two replicas self-register (SMEMBERS = 2 + Healthy per-instance) | **Yes** ✅ — instanceIds `604e8c283200`, `4bfe2a739f0a`, both `status=Healthy` |
| TEST-01b durably-broken replica `status=Unhealthy` (NOT absent) | **No** ❌ **GAP** — `Unhealthy` observable only ~60s post-(re)start (instanceId `0553fe4a4749`), then key TTL-expires → **absent** (no refresh). Phase-60 liveness-refresh coupling — see `62-GAP-liveness-refresh-coupling.md` |
| TEST-01c dead-replica GET→null after >30s + SMEMBERS shrinks (lazy SREM) | **Not run** — blocked pending the Phase-60 gap fix |
| TEST-02-probe-live `/health/live` 200 + summary keys in body | **Yes** ✅ — replica `sk_p4-processor-sample-1`, 200 + `inputSchema/outputSchema/configSchema = SUCCESS` |
| Gate exit code (== 0)                            | `0` ✅ (close gate PASSED) |
| Sample / BadConfig embedded SourceHash / procId  | Sample `536d086839ffaf8c40f523aec427c0c91ca12fdbb5114be77a2bc6c0cc98a189` / `2f6f59b0-2125-4e4a-9fd9-d849441ef91e`; BadConfig `f059e6e1b935cbab0b6217176c32f3b8615a09af622c2f322f9d76ecafd60d69` / `67462a01-fca9-4793-8ad4-4eae6fc22152` |
| Run date                                         | 2026-06-13 |
| Operator                                         | User (operator-supervised, executed via Claude Code) |

**Per-run cadence:** Run 1 Exit=0 Passed=604 (~9m 08s); Run 2 Exit=0 Passed=604 (~8m 48s); Run 3 Exit=0 Passed=604 (~9m 23s).

> **VERDICT:** close gate net-zero (TEST-03) + TEST-01a + TEST-02 PASSED; **TEST-01b is a verified
> Phase-60 product gap** (alive-but-unhealthy replica not refreshed → absent + false watchdog-stale) and
> **TEST-01c is blocked** on it. TEST-01/02/03 remain **unticked**; this file stays `status: pending` until
> the Phase-60 fix lands and the live proof is re-run clean. See `62-GAP-liveness-refresh-coupling.md`.

> The three SHA values + the `Passed` count + the `skp-dlq-1` depth + the `skp:msg:*` count mirror the
> gate's operator-append line printed on PASS. If any run is RED or any SHA mismatches, OR any lifecycle
> proof fails, capture the failure detail here and return for gap closure rather than ticking the
> requirements.

---

## Step 5 — DoD gate (tick TEST-01/TEST-02/TEST-03)

The following requirements are ticked **ONLY** after the N=3 consecutive GREEN run with triple-SHA
BEFORE == AFTER, `skp-dlq-1` depth == 0, `skp:msg:*` count == 0, AND the four lifecycle proofs all pass —
and **ONLY** by the operator (D-15). Until then they stay `[ ]` in `.planning/REQUIREMENTS.md`.

- [ ] **TEST-01** — RealStack proves the per-instance keyspace live: 2 replicas each write
      `skp:proc:{procId:D}:{instanceId}` + `SADD` to `skp:proc:{procId:D}` (TEST-01a); a starting/failed
      replica observable as `Unhealthy` never absent (TEST-01b); a dead replica's key TTL-expires + lazily
      `SREM`'d (TEST-01c). Deterministic verdicts via `GateKeyspaceE2ETests` (fabricated keys); live
      lifecycle via the runbook steps above.
- [ ] **TEST-02** — RealStack proves gate + probe live: >=1-healthy admits even with unhealthy/stale
      sibling; 422 + RFC7807 when none (counts-only reason); self-watchdog `/health/live` returns 200 +
      summary on a HEALTHY replica (TEST-02-probe-live; verdict half hermetic in
      `LivenessWatchdogHealthCheckTests`).
- [ ] **TEST-03** — Close gate holds: N=3 GREEN + triple-SHA `psql \l` / `redis-cli --scan` /
      `rabbitmqctl list_queues` BEFORE == AFTER net-zero, DLQ depth 0, `skp:msg:*` count 0, at Release +
      Debug 0-warning.

These remain unticked in `.planning/REQUIREMENTS.md` until the operator records the GREEN run in Step 4.
On a recorded GREEN run, flip `[ ]` → `[x]` here AND in `.planning/REQUIREMENTS.md`, referencing the Step-4
record block, and flip this file's frontmatter `status: pending` → `status: passed`.

DoD gate condition: **N=3 GREEN + triple-SHA equality + both-config 0-warning + `skp-dlq-1` depth==0 +
`skp:msg:*` count==0 + the four lifecycle proofs (TEST-01a/b/c + TEST-02-probe-live).**

---

## Threat mitigations (recorded — PLAN threat register)

Four threat surfaces are mitigated by this runbook + the cloned gate:

1. **Stale/mixed-version image / identity divergence** (T-62-07, Spoofing) — mitigated by the operator
   rebuild (Step 2: `docker compose --profile badconfig up -d --build` of all five contract-changed
   services, `processor-sample` x2) plus the clean `dotnet clean + build` (Step 1). The close script's
   `^[a-f0-9]{64}$` hash validation on BOTH embedded hashes (`exit 2` on mismatch) + the `SourceHash` ==
   host-build liveness gate make a false-pass impossible: the gate polls the REAL `processor-sample`
   replicas' heartbeat by their hash (D-16).
2. **False GREEN / net-zero snapshot integrity** (T-62-08, Repudiation) — mitigated by starting from a
   CLEAN redis keyspace (Step 2 — BEFORE-dirty trap) and N=3 identical-fact-count consecutive GREEN, so a
   one-off coincidence cannot pass. The D-09 `^skp:proc:` prefix exclusion is SCOPED to the per-replica
   liveness namespace only, so `skp:data:*` / `skp:msg:*` leaks remain detectable as a redis SHA mismatch.
3. **Info disclosure via the runbook surface** (T-62-09) — the probe body carries only the three
   SchemaOutcome strings (no secrets); the 422 reason is counts-only. The runbook documents `redis-cli`
   reads, never logs connection strings.
4. **`processor-badconfig` DoS under the profile** (T-62-10, accept) — Gate A's stay-up posture keeps it
   net-zero-harmless (MarkReady → `/ready` passes, no crash-loop; withholds MarkHealthy → durable Unhealthy
   key under the `^skp:proc:` exclusion, binds no queue). The live run is bounded (~50 min) and
   operator-supervised. No mitigation beyond the shipped stay-up design.

---

## Tests

### 1. Live N×GREEN close-gate run + four lifecycle proofs — gates TEST-01 / TEST-02 / TEST-03
expected: After the clean host build (Step 1, so host hash == container hash for BOTH Sample and BadConfig) and the `--profile badconfig` two-replica rebuild from a clean redis keyspace (Step 2), `pwsh -File scripts/phase-62-close.ps1` exits `0` — 3 consecutive GREEN runs with identical `Passed` fact count, triple-SHA (psql `\l` / redis `--scan` with `^skp:proc:` excluded / rabbitmq `list_queues`) BEFORE == AFTER net-zero, `skp-dlq-1` depth == 0, `skp:msg:*` count == 0, at Release + Debug 0-warning. The four lifecycle proofs (TEST-01a two replicas self-register; TEST-01b badconfig durably Unhealthy; TEST-01c dead-replica >30s TTL-expiry + lazy SREM; TEST-02-probe-live `/health/live` 200 + summary) all pass live OUTSIDE the close window. Record the 3 SHA values + Passed count + DLQ depth + `skp:msg:*` count + the four lifecycle outcomes in the Step 4 record block, then tick TEST-01/TEST-02/TEST-03 in REQUIREMENTS.md.
result: ISSUE (2026-06-13) — close gate (TEST-03) PASSED net-zero N=3×604 GREEN; TEST-01a + TEST-02 passed live; **TEST-01b FAILED** (alive-but-unhealthy replica not refreshed → absent, a Phase-60 gap) and **TEST-01c not run** (blocked). TEST-01/02/03 NOT ticked. See Gaps + `62-GAP-liveness-refresh-coupling.md`.

## Summary

total: 1
passed: 0
issues: 1
pending: 0
skipped: 0
blocked: 0

## Gaps

### G-62-01 — Liveness-timestamp refresh coupled to `IsHealthy` (Phase-60 product gap) — OPEN (product fix landed in Phase 62.1; live re-run pending)

> **Phase 62.1 update (2026-06-14):** The product fix HAS landed —
> `62.1-decouple-liveness-refresh-from-ishealthy` replaced the Gate-A-clash terminal `return;` in
> `ProcessorStartupOrchestrator` with a cancellation-safe refresh loop that re-stamps the Unhealthy
> per-instance key every `IntervalSeconds` (10s) until shutdown (commits `35e6d71`, `b1ede90`), and
> added hermetic regression coverage `ClashRefreshFacts` (commits `1458e80`, `083945e`). Per CONTEXT
> D-04 the live TEST-01b/TEST-01c re-proof was DEFERRED to a Phase-62 close-gate re-run — it has NOT
> yet been executed. This gap therefore stays **OPEN** and this file stays `status: pending` until the
> operator re-runs Steps 1–5 clean and records a GREEN TEST-01b/TEST-01c.

Surfaced by **TEST-01b** during the live close run (2026-06-13). A durably-broken (Gate-A-clash)
replica writes its `Unhealthy` per-instance key only during the ~60s startup-TTL window, then goes
**absent** — the heartbeat is `IsHealthy`-gated (`ProcessorLivenessHeartbeat.cs:76`) and the startup
orchestrator's terminal-clash path returns without handing off to a refresh loop. Consequences: (1) an
unhealthy replica is `absent` rather than observably `Unhealthy` to the orchestration-start gate
(STATE-03 / RF-01 intent unmet — the gate still blocks via absent=not-healthy, so functional protection
holds); (2) the self-watchdog's L1 timestamp goes stale for an **alive** loop, so `/health/live` would
falsely report *"liveness loop stale"* and trigger a needless K8s restart (PROBE intent unmet).

Liveness-timestamp refresh must be **identity-gated, not health-gated**: once identity resolves, refresh
the timestamp every interval writing the **current** status (Healthy or Unhealthy). Full root-cause,
evidence, affected files, and the recommended fix are in `62-GAP-liveness-refresh-coupling.md`.

**Routing:** Phase-60 (`60-…dual-loop-writer-in-memory-l1-liveness-record`) fix — plan a fix phase, then
re-run this runbook (Steps 1–5) clean before ticking TEST-01/02/03 and sealing v7.0.0.

**Not affected (stays):** the close gate (TEST-03) net-zero PASS, TEST-01a, TEST-02, and the Phase-62
test fixes committed this run (fabricated-key liveness-index isolation + stable per-test processor
identities for rabbitmq net-zero).
