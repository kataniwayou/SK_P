# Phase 62: Live Proof & Close Gate - Research

**Researched:** 2026-06-13
**Domain:** RealStack E2E live proof + triple-SHA net-zero close gate (v7.0.0 milestone capstone)
**Confidence:** HIGH (all three research flags resolved against source with file:line evidence; harness/protocol are proven and present)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions (D-01..D-16)
- **D-01** — Reshape `processor-sample` → `deploy.replicas: 2`: drop `container_name` (`sk-processor-sample`) and any published port, add `deploy.replicas: 2`, mirroring the `keeper` tier. Default `docker compose up` now runs TWO replicas.
- **D-02** — Per-replica probe via `docker exec <replica-container> wget -qO- localhost:8082/health/live` (no published port; internal port 8082 per `ConsoleHealth:Port`).
- **D-03** — Mix: xUnit RealStack for the DETERMINISTIC gate + keyspace assertions (in-process WebAPI via `RealStackWebAppFactory` against host stack); operator runbook (`62-HUMAN-UAT.md`) for the genuinely-multi-container LIFECYCLE proofs + N=3 GREEN.
- **D-04** — Deterministic gate verdicts via FABRICATED Redis keys (Phase-61 WR-01/WR-02 craft-redis-state pinning style): write crafted per-instance keys (`status=unhealthy`, or old `timestamp` for stale) + `SADD` to the index alongside the real healthy replica, assert the in-process gate verdict.
- **D-05** — Restarting→`unhealthy` (never absent) via a DURABLY-BROKEN extra replica that reaches the startup loop, writes `unhealthy` each iteration, never flips healthy. **RESEARCH FLAG RF-01 (resolved below).**
- **D-06** — Stale-L1 probe trip: hermetic verdict (clock/`TimeProvider`) + live-wiring proof (`docker exec wget /health/live`). No production fault seam. **RESEARCH FLAG RF-02 (resolved below).**
- **D-07** — Dead-replica TTL-expiry + lazy-`SREM` via `docker stop`; wait past per-key TTL, then trigger a validator read so the absent member is lazily `SREM`'d. **RESEARCH FLAG RF-03 (resolved below).**
- **D-08** — Clone `scripts/phase-58-close.ps1` → `scripts/phase-62-close.ps1`, triple-SHA verbatim; retitle to Phase 62.
- **D-09** — `redis --scan` SHA excludes the per-replica liveness namespace by PREFIX `skp:proc:*` (replaces phase-58's single exact-key `skp:{procId:D}` exclusion). instanceIds are non-deterministic.
- **D-10** — Full accumulated regression retag `[Trait("Phase","62")]` + `badconfig` profile up: retag the new TEST-01/02 tests + carry forward SC1/2/3, Gate-A CFG-08/09, Sample round-trip.
- **D-11** — N=3 SHA runs against the CLEAN steady state (2 healthy replicas + optional badconfig); ALL TEST-01 lifecycle manipulations are SEPARATE operator-runbook steps OUTSIDE the close-gate window.
- **D-12** — v7 seed deltas vs phase-58: two-replica bring-up, badconfig profile, CREATE-IF-ABSENT seed, verify the version string (CONTEXT says `3.5.0` at discuss time).
- **D-13** — N=3 consecutive GREEN with identical-fact-count Smell-A guard.
- **D-14** — Build gate FIRST: `dotnet build SK_P.sln -c Release` AND `-c Debug` both 0-warning, new RealStack tests COMPILE; `scripts/phase-62-close.ps1` exists + AST-valid.
- **D-15** — Live N=3×GREEN run operator-gated via `62-HUMAN-UAT.md`. TEST-01/02/03 stay unticked until the operator's GREEN run.
- **D-16** — Embedded SourceHash must match host build (rebuilt v7 images) or the gate false-passes/times out.

### Claude's Discretion
- The exact never-healthy mechanism for the durably-broken replica (D-05): missing-definition vs Gate-A-incompatible reuse of `Processor.BadConfig` (see RF-01 verdict for the recommendation).
- The exact crafted-key shapes/timestamps for the fabricated unhealthy/stale siblings (D-04).
- xUnit collection/parallelization shaping; reuse `RealStackWebAppFactory` + host-Redis poll + `PollForHealthyLivenessAsync`/`SMEMBERS` precedent + a fabricated-key seeding helper.
- `docker stop` vs `docker kill` for the dead-replica step (D-07).
- The Compose profile name for the durably-broken fault instance.
- Whether the gate test reads per-instance values sequentially or pipelined.
- The `data`-dictionary key names the probe carries the `summary` in (already shipped: `inputSchema`/`outputSchema`/`configSchema`).

### Deferred Ideas (OUT OF SCOPE)
- K8s liveness/startupProbe wiring + pod-restart on a failed self-watchdog — milestone "Future".
- A minimal in-process fault-injection seam (e.g. `FreezeLoopAfter=N`) for a fully-live stale-L1 trip — rejected (no test-seams-in-prod; D-06 covers the verdict hermetically).
- Any change to the liveness writer/reader/probe SEMANTICS (built 59–61).
- Mid-life `healthy → unhealthy` flip; workflow-root liveness.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| TEST-01 | RealStack proves the per-instance keyspace live — 2 replicas each write `skp:proc:{procId:D}:{instanceId}` + `SADD` to `skp:proc:{procId:D}`; a starting/failed replica observable as `unhealthy` (never absent); a dead replica's key TTL-expires + lazily `SREM`'d. | Exact key formats confirmed (Exact Contracts §). The "never absent" + durable-unhealthy mechanism confirmed live via the startup orchestrator's per-iteration `WriteUnhealthyAsync` (RF-01). TTL-expiry budget sized via RF-03. Live proof is operator-runbook (D-03); deterministic verdicts are xUnit fabricated-key (D-04). |
| TEST-02 | RealStack proves gate + probe live — ≥1-healthy admits even with unhealthy/stale sibling; 422+RFC7807 when none; self-watchdog probe returns `unhealthy`+summary on stale L1. | Gate logic + 422 path confirmed in `ProcessorLivenessValidator.cs` (already shipped Phase 61). Fabricated-key deterministic gate verdicts via D-04. Probe stale verdict already hermetically tested (RF-02); live-wiring via `docker exec wget /health/live`. |
| TEST-03 | Close gate holds — N=3 GREEN + triple-SHA `psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues` BEFORE==AFTER net-zero, DLQ depth 0, at Release+Debug 0-warning. | `phase-58-close.ps1` is the verbatim clone source (Clone Deltas §). Only D-09 prefix exclusion + two-replica seed/version delta change. |
</phase_requirements>

## Summary

This phase is a **direct adaptation of the proven Phase 49/55/58 close-gate + RealStack E2E harness** — not new infrastructure. Every load-bearing asset exists and is verified present: the close script (`scripts/phase-58-close.ps1`, 487 lines), the `RealStackWebAppFactory` harness (`SampleRoundTripE2ETests.cs`), the net-zero sweep fixture, the Phase-61 hermetic probe tests, the `Processor.BadConfig` durably-broken tier, and the `keeper` replica-2 template. The v7 keyspace reshape (per-instance keys + index SET) forces exactly three mechanical changes in the close script and one durable-state change in the net-zero math.

**All three research flags resolved with HIGH confidence:**
- **RF-01 VERDICT: CONFIRMED.** A never-healthy processor that resolves identity DOES durably write `status=Unhealthy` per startup-loop iteration via `ProcessorStartupOrchestrator.WriteUnhealthyAsync` (and `SADD`s itself to the index). The recommended D-05 mechanism is **reuse `Processor.BadConfig`** (the Gate-A-clash tier) — it is already profile-gated, already carried up by D-10, and its terminal clash path writes a durable `configOutcome=Fail` → `Unhealthy` entry that never flips healthy.
- **RF-02 VERDICT: ALREADY EXISTS.** `tests/BaseApi.Tests/Features/Liveness/LivenessWatchdogHealthCheckTests.cs` (Phase 61, `[Trait("Phase","61")]`, `FakeTimeProvider`) already has the clock-driven stale-L1→Unhealthy verdict test (`Stale_L1_Reports_Unhealthy_With_Summary_In_Data`), plus null/fresh/boundary cases, plus end-to-end Kestrel proofs (`ProcessorHealthLiveTests`/`ProcessorHealthLiveNullTests`). This phase REUSES them and only adds the live-wiring `docker exec` probe proof in the runbook.
- **RF-03 VERDICT: CORRECTED.** The CONTEXT.md assumption (TTL=30s) is correct for HEARTBEAT (healthy) entries (interval=10 → `max(20,30)=30`) but WRONG for STARTUP (unhealthy) entries, which record interval=30 → `max(60,30)=60`. The dead-replica `docker stop` step (D-07) acts on a HEALTHY replica's heartbeat key (30s TTL). The durably-broken replica (D-05) holds a 60s-TTL unhealthy key.

**Primary recommendation:** Clone `phase-58-close.ps1` verbatim; change the redis-scan `Where-Object` filter from the exact single-key match to a `-notmatch '^skp:proc:'` prefix exclusion; add the two-replica bring-up to the runbook; reuse `Processor.BadConfig` as the durably-broken replica; reuse the existing Phase-61 hermetic probe tests for the D-06 verdict half. Add a fabricated-key seeding helper alongside `RealStackWebAppFactory` for the D-04 deterministic gate verdicts.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Per-instance liveness write (`unhealthy`/`healthy`) | Processor container (`BaseProcessor.Core`) | Host Redis (L2) | Writers ship in 59–61; this phase observes them live, never changes them. |
| Instance-index `SADD`/lazy-`SREM` | Processor write-side (`SADD`) / WebAPI gate read-side (`SREM`) | Host Redis | `SADD` in `ProcessorLivenessWriter`; lazy `SREM` in `ProcessorLivenessValidator` (fire-and-forget). |
| ≥1-healthy gate verdict + 422 | WebAPI in-process (`RealStackWebAppFactory`) | Host Redis | xUnit RealStack runs the WebAPI in-process against host Redis; fabricated keys drive the verdict (D-04). |
| Self-watchdog probe verdict (stale→Unhealthy) | Processor in-process health check | — | Pure in-memory L1 read; proven hermetically (clock) — no Redis/RMQ. |
| Self-watchdog probe live wiring | Processor container `/health/live` (port 8082) | `docker exec` | Reached per-replica via `docker exec` (no published port). |
| Net-zero triple-SHA snapshot | Operator PowerShell close script | docker exec (redis/psql/rabbitmq) | Snapshot-and-compare; production A19 active reclaim + E2E teardown do the actual cleanup. |
| Multi-container lifecycle (restart→unhealthy, TTL-expiry+SREM, real self-register) | Operator runbook (`docker` CLI) | — | xUnit does NOT lifecycle the processor (D-03); these are genuinely multi-container. |

## Research Flags Resolved

### RF-01 (load-bearing for D-05): Does the v7 writer persist `status=unhealthy` durably for a never-healthy processor?

**VERDICT: CONFIRMED — YES, durably, per startup-loop iteration, but ONLY after identity resolves.**

The v7 unhealthy write does NOT live in the heartbeat (which still no-ops when not healthy — see below). It lives in the **startup orchestrator**, which is the v7 mechanism that fulfils STATE-03/LOOP-01.

Evidence (`src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs`):
- **Loop A (identity):** on `ProcessorIdentityFound`, the FIRST inline unhealthy write fires — `await WriteUnhealthyAsync();` (line 128). Before this, `context.Id` is null and the method early-returns (no key written) — line 311: `if (context.Id is not { } procId) return;`.
- **Loop B (per-schema):** `await WriteUnhealthyAsync();` at the top of EVERY iteration (line 173), so the L2 summary tracks per-schema progress while any non-null schema is unresolved.
- **Gate-A clash (terminal):** `await WriteUnhealthyAsync(configOutcomeOverride: SchemaOutcome.Fail);` (line 228), then `gate.MarkReady(); return;` — MarkHealthy is NEVER reached. This is a DURABLE unhealthy entry: definitions are all resolved (so naive outcome would be all-Success → Healthy), but the explicit `configOutcome=Fail` forces `Create` to derive `Unhealthy`.
- `WriteUnhealthyAsync` (lines 300–328) calls the SAME shared `ProcessorLivenessWriter.WriteAsync` the heartbeat uses → `StringSetAsync(PerInstance, ttl)` + `SetAddAsync(InstanceIndex, instanceId)` + `_l1.Update(entry)`.

Contrast with the heartbeat (`ProcessorLivenessHeartbeat.cs:76`): `if (_context.IsHealthy && _context.Id is { } id)` — a not-yet-healthy replica no-ops the beat (writes nothing). So the durable-unhealthy guarantee comes from the startup orchestrator, NOT the heartbeat. (This is the v7 fix to the v6 "only a Healthy replica writes" rule that Phase-58 D-05 relied on.)

**Critical nuance for D-05 planning:** the unhealthy write requires `context.Id` (identity) to be resolved first. A replica that crashes BEFORE identity resolves writes NO key (it is "absent", not "unhealthy"). Therefore the durably-broken mechanism MUST resolve identity, then fail. The two candidate mechanisms:

1. **RECOMMENDED — reuse `Processor.BadConfig` (Gate-A-clash).** It resolves identity + all definitions, then hits the Gate-A clash path → `WriteUnhealthyAsync(configOutcome=Fail)` → durable `Unhealthy` entry present in the index, never flips healthy. It is ALREADY `profile: ["badconfig"]`, ALREADY carried up by D-10, ALREADY has a stable seeded DB row + schema. **Zero new code/compose.** Confirmed present: `src/Processor.BadConfig/{Program.cs,BadConfig.cs,BadConfigProcessor.cs}` + the compose tier (`compose.yaml:298-324`).
2. Missing-definition replica (a processor whose input/output schema never registers): resolves identity, then loops Loop B forever writing `Unhealthy` (a still-null definition ⇒ `Fail`). Also durable, but requires a new processor/compose tier — strictly more work than option 1.

**Behavioral note vs Phase 58:** In v6, `processor-badconfig` wrote NO liveness key (heartbeat no-op'd) — the 58-HUMAN-UAT three-signal proof relied on `skp:{badId}` being *stably absent*. In v7 it now writes a durable `skp:proc:{badId}:{instanceId}` `unhealthy` key + index. This is a deliberate semantic change (STATE-03) and is NET-ZERO-HARMLESS for the close gate ONLY because D-09 excludes the whole `skp:proc:*` prefix (see Risks §). A 62-era "badconfig 422" proof must assert on `status=unhealthy` (or the 422 verdict), NOT on "absent".

---

### RF-02 (D-06 hermetic half): Does Phase 61 already have a hermetic clock-driven stale-L1→Unhealthy verdict test?

**VERDICT: YES — already exists. This phase REUSES it; only the live-wiring `docker exec` probe proof is new.**

Exact file/factory: **`tests/BaseApi.Tests/Features/Liveness/LivenessWatchdogHealthCheckTests.cs`** (`[Trait("Phase","61")]`, sealed class `LivenessWatchdogHealthCheckTests`). It uses `Microsoft.Extensions.Time.Testing.FakeTimeProvider` pinned to a fixed `Now` and positions entries relative to it:
- `Stale_L1_Reports_Unhealthy_With_Summary_In_Data` (line 57): timestamp older than `interval*2` ⇒ `Unhealthy` ("liveness loop stale") + summary in `Data`.
- `Null_L1_Reports_Unhealthy_LoopNotStarted` (line 45): null L1 ⇒ Unhealthy.
- `Fresh_L1_Reports_Healthy_With_Summary_In_Data` (line 71).
- `ExactBoundary_NowEqualsDeadline_Reports_Unhealthy_Stale` (line 85) and `OneTickBeforeBoundary_StrictlyFresh_Reports_Healthy` (line 100) — boundary pinning.

End-to-end (real embedded Kestrel listener) coverage also already exists per `61-VERIFICATION.md`: `ProcessorHealthLiveTests.Live_Is_Unhealthy_When_L1_Stale` (503) + `Live_Is_Healthy_And_Carries_Summary_When_L1_Fresh` (200 + summary in body) + `ProcessorHealthLiveNullTests.Live_Is_Unhealthy_When_L1_Null` (503).

**D-06 plan consequence:** the hermetic verdict half is DONE (no new test). This phase adds only the **operator-runbook live-wiring step**: `docker exec sk-... wget -qO- localhost:8082/health/live` on a real healthy replica, asserting 200 + the per-schema `summary` keys (`inputSchema`/`outputSchema`/`configSchema`) in the JSON body. A genuinely-live STALE trip is explicitly NOT attempted (D-06 rationale: L1 is in-memory, `docker pause` kills the probe too, Redis faults don't stop L1 — a live stale would need a fault seam the project refuses to add).

---

### RF-03 (D-07 wait budget): What is the effective per-key TTL for a heartbeat-written entry?

**VERDICT: 30s for a HEALTHY heartbeat key (D-07's target); 60s for a never-healthy STARTUP key. CONTEXT.md's 30s figure is right for the dead-replica step but the startup-key figure must be corrected.**

The writer's derived TTL (`ProcessorLivenessWriter.cs:70`): `var ttl = Math.Max(entry.Interval * 2, _options.TtlSeconds);` where `entry.Interval` is the active interval baked into the entry by the caller.

| Entry kind | Active interval recorded | TTL = `max(interval×2, Ttl)` | Set by |
|------------|--------------------------|------------------------------|--------|
| Heartbeat (healthy) | `IntervalSeconds` = **10** | `max(20, 30)` = **30s** | `ProcessorLivenessHeartbeat.cs:91` (`interval: _options.IntervalSeconds`) |
| Startup (unhealthy) | `StartupIntervalSeconds` = **30** | `max(60, 30)` = **60s** | `ProcessorStartupOrchestrator.cs:324` (`interval: options.Value.StartupIntervalSeconds`) |

Config defaults (`ProcessorLivenessOptions.cs`): `IntervalSeconds=10` (line 22), `StartupIntervalSeconds=30` (line 28), `TtlSeconds=30` (line 36). Confirmed un-overridden in `src/Processor.Sample/appsettings.json` (`Interval:10, Ttl:30, BackoffCap:30`; NO `StartupInterval` key → default 30). Compose sets only `Processor__ExecutionDataTtl: "5"` (unrelated to liveness TTL).

**D-07 planning consequence:** the dead-replica step `docker stop`s a HEALTHY replica → its heartbeat key has a **30s TTL**. Wait budget should be > 30s (recommend ~40–45s) after the last heartbeat write for the per-instance key to TTL-expire (`GET`→null), THEN trigger a validator read (orchestration-start) so the absent member is lazily `SREM`'d. Note the heartbeat refreshes every 10s while the container is up, so timing is "after `docker stop`, wait > 30s". If the durably-broken replica is `docker stop`'d instead, budget > 60s.

## Exact Contracts

### Per-replica liveness keys (`L2ProjectionKeys.cs:31,44-51`)

| Builder | Exact string format | Notes |
|---------|---------------------|-------|
| `L2ProjectionKeys.Prefix` | `skp:` | compile-time const |
| `PerInstance(Guid processorId, string instanceId)` | `skp:proc:{processorId:D}:{instanceId}` | GUID is `:D` (hyphenated, lowercase, e.g. `f985ffcb-d7d2-454e-811d-05fe5cbf565f`); `instanceId` is a PLAIN STRING (pod identity `POD_NAME → HOSTNAME → MachineName → GUID`), NOT a Guid. |
| `InstanceIndex(Guid processorId)` | `skp:proc:{processorId:D}` | the per-processor instance-index SET; EXACT PREFIX of `PerInstance` (before `:{instanceId}`). |

**D-09 SHA-exclusion prefix:** both the index SET and all per-instance keys begin `skp:proc:` → exclude with `-notmatch '^skp:proc:'` (matches BOTH families with one filter). Both healthy (Sample ×2) and unhealthy (badconfig) keys live under this prefix.

### `ProcessorLivenessEntry` JSON shape (for fabricating D-04 keys) — `ProcessorLivenessEntry.cs:14-58`

Serialized with `JsonSerializer.Serialize(entry)` using DEFAULT options; the `[property: JsonPropertyName]` pins carry the wire shape. To fabricate a key an executor writes this exact JSON:

```json
{
  "timestamp": "2026-06-13T12:00:00Z",
  "interval": 10,
  "status": "Healthy",
  "summary": {
    "inputSchema": "SUCCESS",
    "outputSchema": "SUCCESS",
    "configSchema": "SUCCESS"
  }
}
```

Property names (load-bearing — the gate/probe read these exact names):
- top-level: `timestamp` (DateTime), `interval` (int, SECONDS), `status` (string), `summary` (object).
- `summary`: `inputSchema`, `outputSchema`, `configSchema` (each a `SchemaOutcome` string).

The reader (`ProcessorLivenessValidator.cs:59-68`) deserializes, then checks: `entry?.Summary is null` ⇒ malformed; `Status != "Healthy"` (ordinal) ⇒ unhealthy; `Timestamp.AddSeconds(Interval * 2) <= now` ⇒ stale. To fabricate:
- **healthy sibling:** `status="Healthy"`, fresh `timestamp` (now), `interval=10`.
- **unhealthy sibling:** `status="Unhealthy"` (any summary).
- **stale sibling:** `status="Healthy"` but `timestamp = now - (interval*2 + slack)` (e.g. `now-25s` with interval=10 → deadline `now-5s ≤ now` ⇒ stale).
- For the SADD: add the fabricated instanceId string to `skp:proc:{procId:D}`.

**Prefer construction via the factory in C# tests:** `ProcessorLivenessEntry.Create(inputOutcome, outputOutcome, configOutcome, timestamp, interval)` then `JsonSerializer.Serialize(entry)` — guarantees the invariant (any `Fail` ⇒ `Unhealthy`) and the exact wire shape without hand-writing JSON. `Create` is the ONLY sanctioned construction path.

### `LivenessStatus` consts (`LivenessStatus.cs:12-13`) — use the const, never a literal

| Const | Value |
|-------|-------|
| `LivenessStatus.Healthy` | `"Healthy"` |
| `LivenessStatus.Unhealthy` | `"Unhealthy"` |

`SchemaOutcome.Success`/`.Fail` are the summary-field consts (values `"SUCCESS"`/`"FAIL"` — confirmed via the JSON above and `ProcessorLivenessEntry.Create` usage; the SoT is `SchemaOutcome` in `Messaging.Contracts.Projections`).

### TTL / interval numbers (RF-03)

`IntervalSeconds=10`, `StartupIntervalSeconds=30`, `TtlSeconds=30`. Heartbeat key TTL = 30s; startup/unhealthy key TTL = 60s. Gate/probe staleness window = `timestamp + interval×2` (20s for healthy, 60s for startup).

## Clone Deltas: phase-58-close.ps1 → phase-62-close.ps1

The clone is **verbatim** except for the items below. `phase-58-close.ps1` is 487 lines; the protocol (idempotent CREATE-IF-ABSENT seed, compose-health pre-flight, both-config 0-warning build gate, N=3 identical-fact-count cadence, triple-SHA BEFORE==AFTER, `skp-dlq-1` depth==0, `skp:msg:*` count==0, `_bus_` exclusion) is unchanged.

### D-09 — the redis-scan exclusion (the load-bearing change)

**OLD (phase-58, exact single-key match — lines 303 and 379):**
```powershell
$beforeRedis = (docker exec sk-redis redis-cli --scan | Where-Object { $_ -ne "skp:$($procId.ToString().ToLower())" } | Sort-Object -CaseSensitive | Out-String).Trim()
# ...and identically for $afterRedis (line 379)
```

**NEW (phase-62, prefix exclusion — replaces BOTH the BEFORE and AFTER lines):**
```powershell
$beforeRedis = (docker exec sk-redis redis-cli --scan | Where-Object { $_ -notmatch '^skp:proc:' } | Sort-Object -CaseSensitive | Out-String).Trim()
# ...and identically for $afterRedis
```

Rationale: v7 has TWO healthy `processor-sample` replicas (each writing `skp:proc:{procId:D}:{instanceId}` with a non-deterministic instanceId) + the index SET `skp:proc:{procId:D}`, PLUS the durably-unhealthy `processor-badconfig` replica's `skp:proc:{badId:D}:{instanceId}` + its index. An exact single-key match cannot exclude non-deterministic instanceIds; the prefix `^skp:proc:` excludes the entire per-replica liveness namespace (both healthy and unhealthy, both keys and indexes) in one filter. The `skp:data:*` / `skp:msg:*` leak detection is unaffected (those do not start `skp:proc:`).

### D-12 — two-replica bring-up + version verify

- The `$services` health-required pre-flight list (phase-58 line 275): `processor-sample` is in the list, but it is now a 2-replica service. The phase-58 health-check loop (lines 280-292) ALREADY handles multi-replica services correctly (it was written for `keeper` replicas:2 — parses NDJSON line-by-line, requires ALL instances healthy). **No change needed to the health loop** — it already requires both `processor-sample` replicas healthy. `processor-badconfig` stays EXCLUDED from `$services` (it never goes Healthy — its /ready passes but it withholds MarkHealthy).
- The post-seed liveness wait (lines 248-266): waits for `processor-sample` healthy via `docker compose ps processor-sample`. For replicas:2 this must require BOTH instances healthy (mirror the `$services` loop's all-instances-healthy logic, OR rely on the `$services` pre-flight which already does). Confirm the wait does not assume a single instance.
- **Version string (D-12):** `src/Processor.Sample/appsettings.json:11` reads `"Version": "3.5.0"` (CONFIRMED unchanged at research time — v7 has NOT bumped it). The seed (`Get-OrCreateProcessorId`, phase-58 line 224) hardcodes `version = '3.5.0'`. Carry `3.5.0` forward UNLESS the planner/operator decides v7 should bump it; the SourceHash (not the version) distinguishes identity (phase-58 D-09c precedent). **Verify again at plan time** — a stale version in the seed is harmless to identity (keyed on `uq_processor_source_hash`) but should be intentional.

### D-08 / D-10 — retitle + retag

- Retitle all "Phase 58" header/usage/operator-append strings to "Phase 62 / v7.0.0".
- The gate runs the FULL suite live (no filter) — `[Trait("Phase","62")]` does NOT change what the gate runs (it runs everything). The retag matters for documentation/intent only; the gate command is unchanged: `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release --no-build` (phase-58 line 336).
- The rebuild command in the runbook (phase-58 line 105): `docker compose --profile badconfig up -d --build baseapi-service orchestrator processor-sample keeper processor-badconfig` — UNCHANGED (processor-sample now scales to 2 via `deploy.replicas`; the up command is the same).

### UNCHANGED (do NOT touch)

- Dual SourceHash read (Sample + BadConfig), both `^[a-f0-9]{64}$` validated, `exit 2` on mismatch (lines 106-158).
- Two-schema GET-or-create by sentinel Name (`gateA-sample-compatible` / `gateA-badconfig-clash`) + two processor rows CREATE-IF-ABSENT, never PUT (lines 162-242).
- `_bus_` rabbitmq exclusion (lines 309, 385); `list_queues name` (NOT `name messages`) for the SHA.
- `skp-dlq-1` depth==0 single-element loop (lines 436); `skp:msg:*` count==0 (line 461).
- N=3 identical-`Passed`-count Smell-A guard (lines 358-363).
- `psql -lqt` SHA (row-level seed does not move the db-LIST SHA).

## Harness Reuse Surface

All new xUnit RealStack tests reuse `RealStackWebAppFactory` (nested in `SampleRoundTripE2ETests.cs`, extends `Composition.Phase8WebAppFactory`). Exact reusable members:

| Member | Signature / shape | Use |
|--------|-------------------|-----|
| `RealStackWebAppFactory()` ctor | sets `RabbitMq__*` (localhost:5673), `ConnectionStrings__Redis` (localhost:6380), `__Postgres` (localhost:5433), OTEL endpoint | in-process WebAPI → host stack |
| `factory.InitializeAsync()` / `CreateClient()` | standard WAF | drive the in-process gate via HTTP `POST /api/v1/orchestration/start` |
| `factory.L2KeysToCleanup` | `List<RedisKey>` drained in `DisposeAsync` (`KeyDeleteAsync`) | register fabricated `skp:proc:*` keys for net-zero teardown |
| `factory.ParentIndexMembersToSrem` | `List<RedisValue>` (SREM from `skp:` parent index on teardown) | for workflow root index members |
| `RealStackWebAppFactory.DisposeAsync()` | deletes `L2KeysToCleanup`, SREMs `ParentIndexMembersToSrem`, restores env | automatic net-zero |
| `SeedProcessorAsync(client, sourceHash, ct, configSchemaId=null)` | GET-or-create by source-hash, returns procId | seed the Processor DB row idempotently |
| `SeedConfigSchemaAsync(client, sentinelName, definition, ct)` | GET-or-create by sentinel Name, never PUT | seed config schemas |
| `SeedStepAsync` / `SeedWorkflowAsync` | standard create DTOs | build the graph for a Start that exercises the gate |
| `PollForHealthyLivenessAsync(procId, ct)` | `static`; SMEMBERS index → GET each per-instance → accept on ≥1 Healthy+fresh | the LIVE-poll precedent (mirrors the gate); already updated to the per-instance keyspace (lines 218-255) |
| `SampleCompatibleSchemaName` / `SampleCompatibleSchemaDefinition` | `internal const` sentinels | reuse the exact Gate-A-compatible schema the close script + GateAComposition use |
| host-Redis const | `localhost:6380,abortConnect=false,connectTimeout=5000` | connect a `ConnectionMultiplexer` to write fabricated keys directly |

**Where the fabricated-key seeding helper should live:** add a `static` helper (e.g. `SeedFabricatedLivenessAsync(Guid procId, string instanceId, ProcessorLivenessEntry entry, ...)`) co-located with `PollForHealthyLivenessAsync` (same file family in `tests/BaseApi.Tests/Orchestrator/`), or in a small shared static helper class in that namespace so both the new TEST-01/TEST-02 fixtures and any future test can reuse it. It should: connect host Redis, `StringSet(PerInstance(procId, instanceId), JsonSerializer.Serialize(entry))`, `SetAdd(InstanceIndex(procId), instanceId)`, AND register both into `factory.L2KeysToCleanup` + the index member into a SREM list so teardown is net-zero. Mirror the Phase-61 WR-01/WR-02 craft-redis-state pinning style.

**Net-zero sweep:** `RealStackNetZeroSweepFixture` (collection fixture, wired into the `"Observability"` + `"RedisOutageSerial"` collections) sweeps residual `skp:data:*`/`skp:msg:*` and purges `skp-dlq-1` after the LAST host-stack test. It does NOT sweep `skp:proc:*` (steady-state, gate-excluded). New fabricated `skp:proc:*` keys must therefore be cleaned by the test's own `L2KeysToCleanup` registration — the sweep will not catch them. **Landmine: a fabricated `skp:proc:{procId}:{fakeInstance}` that is NOT registered for cleanup AND is NOT under the gate exclusion would be excluded by D-09's `^skp:proc:` filter anyway, so it would NOT trip the redis SHA — but it WOULD pollute the live gate's SMEMBERS index and could affect a later test's verdict. Always SREM fabricated index members in teardown.**

## Compose Reshape

### D-01 — current `processor-sample` tier (`compose.yaml:265-290`) → target

**CURRENT:**
```yaml
  processor-sample:
    build:
      context: .
      dockerfile: src/Processor.Sample/Dockerfile
    container_name: sk-processor-sample
    restart: unless-stopped
    depends_on:
      rabbitmq: { condition: service_healthy }
      redis: { condition: service_healthy }
      baseapi-service: { condition: service_healthy }
    environment:
      RabbitMq__Host: rabbitmq
      RabbitMq__Username: guest
      RabbitMq__Password: guest
      ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
      Processor__ExecutionDataTtl: "5"
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:8082/health/ready"]
      interval: 10s
      timeout: 3s
      retries: 5
      start_period: 30s
```
Note: this tier already has NO `ports:` block (8082 is container-internal). So D-01's "remove the published port" is effectively only "remove `container_name`" + "add `deploy.replicas: 2`". There is no published-port line to delete.

**TARGET (mirror `keeper` tier `compose.yaml:229-252` — `deploy.replicas:2`, no container_name, no ports):**
```yaml
  processor-sample:
    build:
      context: .
      dockerfile: src/Processor.Sample/Dockerfile
    deploy:
      replicas: 2
    restart: unless-stopped
    depends_on:
      rabbitmq: { condition: service_healthy }
      redis: { condition: service_healthy }
      baseapi-service: { condition: service_healthy }
    environment:
      RabbitMq__Host: rabbitmq
      RabbitMq__Username: guest
      RabbitMq__Password: guest
      ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
      Processor__ExecutionDataTtl: "5"
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:8082/health/ready"]
      interval: 10s
      timeout: 3s
      retries: 5
      start_period: 30s
```
**Change set:** delete `container_name: sk-processor-sample` (line 269); add `deploy:\n  replicas: 2`. Both replicas share the internal 8082 health port harmlessly (same as `keeper` on 8083). With no `container_name`, replicas get compose-generated names (e.g. `sk_p4-processor-sample-1` / `-2`) — reach a specific one via `docker compose ps processor-sample` → container name → `docker exec <name> wget -qO- localhost:8082/health/live` (D-02).

### D-05 — durably-broken instance tier

**RECOMMENDED: reuse the EXISTING `processor-badconfig` tier (`compose.yaml:298-324`) AS-IS.** It is already:
- `profiles: ["badconfig"]` (excluded from default `up`; brought up with `--profile badconfig`).
- A faithful processor clone whose `BadConfig(int Quantity)` clashes with the `gateA-badconfig-clash` schema (`quantity: string`) → Gate A withholds MarkHealthy.
- `container_name: sk-processor-badconfig` (single instance — fine, it is the one durably-broken replica).
- Health-check on 8082 `/health/ready` (its /ready passes → no crash-loop; it never goes /health/live-healthy because the loop is stuck unhealthy — actually the watchdog reads L1 which IS being updated each startup iteration with a fresh timestamp, so /health/live would report... see Risks: the watchdog reports stale only if the loop STOPS; a looping-unhealthy startup keeps L1 fresh-but-Unhealthy-status. The probe maps null/stale→Unhealthy but a FRESH Unhealthy-status L1 still returns Healthy from the watchdog because the watchdog only checks staleness, not status. This is correct: the watchdog detects a STOPPED loop, not an unhealthy verdict.)

No new compose tier is required for D-05. If the planner prefers a missing-definition variant instead (discretion), that needs a new processor project + tier (more work, no benefit).

## Common Pitfalls

### Pitfall 1: Sizing the dead-replica wait to 30s when stopping the durably-broken replica
**What goes wrong:** D-07 says "wait ~30s for TTL". That is correct for a HEALTHY heartbeat key (interval=10 → TTL 30s) but the durably-broken/startup key has interval=30 → TTL 60s. Stopping the wrong replica with a 30s wait leaves the key alive and the test/runbook step false-fails.
**How to avoid:** `docker stop` a HEALTHY `processor-sample` replica for the TTL-expiry+SREM proof (30s TTL, > 30s wait). If proving the unhealthy key's expiry, budget > 60s.

### Pitfall 2: Asserting "absent" for the badconfig 422 proof (v6 carryover)
**What goes wrong:** Phase-58's CFG-08 proof asserted `skp:{badId}` stably ABSENT. In v7 the badconfig replica writes a durable `skp:proc:{badId}:{instanceId}` UNHEALTHY key. An "absent" assertion will fail.
**How to avoid:** Assert `status=Unhealthy` (or the 422 verdict from the gate, which counts it as `unhealthy`), NOT absence. The 422 still holds (an unhealthy replica fails the gate).

### Pitfall 3: SourceHash mismatch (the documented Phase-49/55 failure mode)
**What goes wrong:** An incremental host build leaves a stale `SourceHash` on `Processor.Sample.dll`/`Processor.BadConfig.dll`; the close script seeds a DB row by the host hash, but the live container image carries a different hash → identity never resolves → liveness gate false-passes or times out.
**How to avoid:** `dotnet clean SK_P.sln` then `dotnet build -c Release` + `-c Debug` BEFORE the live run; `docker compose --profile badconfig up -d --build` to rebuild images. The close script's `^[a-f0-9]{64}$` validation + the real-container liveness poll make a false-pass impossible but a timeout still wastes a ~50-min run.

### Pitfall 4: Non-deterministic instanceId breaks exact-key exclusion
**What goes wrong:** Using phase-58's exact `$_ -ne "skp:..."` single-key exclusion fails because v7 instanceIds are pod/hostname/GUID-derived and non-deterministic, and there are now 2–3 of them.
**How to avoid:** D-09 prefix exclusion `-notmatch '^skp:proc:'` (covers all instances + indexes).

### Pitfall 5: `[JsonPropertyName]` drift in fabricated keys
**What goes wrong:** Hand-writing fabricated JSON with wrong casing (`Status` vs `status`, `inputSchema` vs `InputSchema`) → the gate deserializes a null/default and counts the replica as malformed/unhealthy unexpectedly.
**How to avoid:** Build the entry via `ProcessorLivenessEntry.Create(...)` and `JsonSerializer.Serialize(entry)` (default options) — never hand-author the JSON. The pins are on `ProcessorLivenessEntry.cs:14-58`.

### Pitfall 6: Fabricated index members polluting the live gate
**What goes wrong:** A fabricated instanceId SADDed to `skp:proc:{procId}` for a deterministic verdict test stays in the index after the test (the D-09 prefix exclusion hides it from the SHA, so it looks net-zero) and a LATER live test's gate SMEMBERS picks it up.
**How to avoid:** SREM every fabricated index member in teardown (register in a SREM list drained in `DisposeAsync`); use a DISTINCT throwaway processorId for pure-fabrication verdict tests where possible, so the index does not collide with the live replicas' index.

## Code Examples

### Fabricating a deterministic gate verdict (D-04) — C# in xUnit RealStack

```csharp
// Source: pattern from ProcessorLivenessValidator.cs:59-68 (reader) + ProcessorLivenessEntry.Create
// + SampleRoundTripE2ETests.PollForHealthyLivenessAsync (host-Redis connect)
await using var mux = await ConnectionMultiplexer.ConnectAsync("localhost:6380,abortConnect=false,connectTimeout=5000");
var db = mux.GetDatabase();
var now = DateTime.UtcNow;

// healthy sibling (fresh) — admits
var healthy = ProcessorLivenessEntry.Create(null, null, null, now, interval: 10);
await db.StringSetAsync(L2ProjectionKeys.PerInstance(procId, "fab-healthy"), JsonSerializer.Serialize(healthy), TimeSpan.FromSeconds(60));
await db.SetAddAsync(L2ProjectionKeys.InstanceIndex(procId), "fab-healthy");

// unhealthy sibling — fails THAT replica; gate still admits because healthy qualifies first
var unhealthy = ProcessorLivenessEntry.Create(SchemaOutcome.Fail, null, null, now, interval: 10); // any Fail => Unhealthy
await db.StringSetAsync(L2ProjectionKeys.PerInstance(procId, "fab-unhealthy"), JsonSerializer.Serialize(unhealthy), TimeSpan.FromSeconds(60));
await db.SetAddAsync(L2ProjectionKeys.InstanceIndex(procId), "fab-unhealthy");

// teardown (net-zero): register for cleanup
factory.L2KeysToCleanup.Add(L2ProjectionKeys.PerInstance(procId, "fab-healthy"));
factory.L2KeysToCleanup.Add(L2ProjectionKeys.PerInstance(procId, "fab-unhealthy"));
// + SREM "fab-healthy"/"fab-unhealthy" from InstanceIndex(procId) in DisposeAsync
```

### Stale sibling (for the "stale fails the replica" verdict)
```csharp
// deadline = timestamp + interval*2 = (now-25) + 20 = now-5 <= now => stale (ProcessorLivenessValidator.cs:68)
var stale = ProcessorLivenessEntry.Create(null, null, null, now.AddSeconds(-25), interval: 10);
```

### Live probe wiring proof (D-06 live half) — operator runbook
```powershell
# reach a specific real replica's /health/live (no published port — D-02)
$name = (docker compose ps processor-sample --format json | ConvertFrom-Json | Select-Object -First 1).Name
docker exec $name wget -qO- http://localhost:8082/health/live
# expect 200 + a JSON body carrying inputSchema/outputSchema/configSchema (PROBE-02 summary)
```

## State of the Art

| Old Approach (Phase 58 / v6) | Current Approach (Phase 62 / v7) | When Changed | Impact |
|------------------------------|----------------------------------|--------------|--------|
| Single flat liveness key `skp:{procId:D}` | Per-instance `skp:proc:{procId:D}:{instanceId}` + index SET `skp:proc:{procId:D}` | Phase 59 | redis-scan exclusion changes from exact-key to `^skp:proc:` prefix (D-09) |
| Never-healthy processor writes NO liveness key (heartbeat no-op) | Never-healthy processor (post-identity) writes durable `Unhealthy` per startup iteration | Phase 60 (STATE-03) | badconfig now contributes a key; D-05 mechanism works; CFG-08 "absent" proof becomes "unhealthy" proof |
| `processor-sample` single container (`container_name: sk-processor-sample`) | `deploy.replicas: 2`, no container_name | Phase 62 (D-01) | default stack runs 2 replicas; reach via compose-generated names |
| Gate reads single key (present ⟺ live) | Gate SMEMBERS index → GET each → ≥1 healthy+fresh, lazy-SREM absent | Phase 61 | fabricated multi-key verdicts now possible/required |

**Deprecated/outdated:**
- `ProcessorProjection.cs` / `L2ProjectionKeys.Processor(Guid)` — DELETED Phase 61 (D-11). Do not reference.
- Phase-58 redis exclusion `$_ -ne "skp:$($procId...)"` — superseded by the prefix filter.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `SchemaOutcome.Success`/`.Fail` serialize to `"SUCCESS"`/`"FAIL"` | Exact Contracts | LOW — confirmed by the JSON-shape comment + `Create` usage; if the SoT differs, fabricated summaries mismatch (but tests build via `Create`, so the on-wire value is whatever the SoT emits — self-consistent). Verify `SchemaOutcome.cs` at plan time if hand-authoring JSON. |
| A2 | v7 does NOT bump `Processor.Sample` version from `3.5.0` | Clone Deltas D-12 | LOW — confirmed `3.5.0` in appsettings at research time; version is not identity-load-bearing (SourceHash is). Operator should confirm intent. |
| A3 | Compose-generated replica names follow `<project>-processor-sample-N` | Compose Reshape | LOW — standard compose v2 behavior; the runbook should resolve names dynamically via `docker compose ps processor-sample --format json` rather than hardcoding. |

**All other claims in this research are VERIFIED against source files (file:line cited) — no user confirmation needed.**

## Open Questions (RESOLVED)

1. **Does the durably-broken (badconfig) replica's `/health/live` report Healthy or Unhealthy?**
   - What we know: the watchdog (`LivenessWatchdogHealthCheck.cs`) maps null/STALE L1 → Unhealthy; it does NOT check `status`. A looping-unhealthy startup keeps L1 FRESH (timestamp refreshed each iteration) with `status=Unhealthy`. So the watchdog returns **Healthy** ("the loop is alive") for a badconfig replica — by design (the probe detects a STOPPED loop, not an unhealthy verdict).
   - What's unclear: whether any runbook step expects badconfig's `/health/live` to be Unhealthy. It should NOT.
   - Recommendation: the D-06 live-wiring proof should target a HEALTHY `processor-sample` replica (200 + summary). Do not assert badconfig's probe verdict.

2. **Does `processor-badconfig` actually reach the Gate-A clash path (write the durable unhealthy key) given it must first resolve identity + definitions over the live bus?**
   - What we know: phase-58 proved badconfig boots, Gate A RUNS (ES clash log scoped to `processor-badconfig`), and orchestration-start is 422. In v6 it then wrote no key; in v7 the same code path now calls `WriteUnhealthyAsync(configOutcome=Fail)`.
   - What's unclear: nothing structurally — the code path is the same; only the write at the end is new (and confirmed present at `ProcessorStartupOrchestrator.cs:228`).
   - Recommendation: a runbook step can confirm `skp:proc:{badId:D}:*` exists with `status=Unhealthy` after badconfig boots (new v7 observable, replaces the v6 "absent" assertion).

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Docker + compose v2 | live stack (2 replicas + badconfig) | assumed ✓ (prior closes ran) | — | none — live run is operator-gated |
| PowerShell (pwsh) | `phase-62-close.ps1` | ✓ (Windows project, .ps1) | — | none |
| host Redis :6380 / RMQ :5673 / PG :5433 | RealStackWebAppFactory | ✓ when stack up | — | hermetic tests run without it (Category!=RealStack) |
| .NET 8 SDK | build gate + tests | assumed ✓ | net8.0 | none |

**Missing dependencies with no fallback:** none new — this phase uses the exact stack prior closes (58/55/49) ran. The live run requires the full v7 rebuilt stack up healthy (operator step).

## Validation Architecture

> nyquist_validation: check `.planning/config.json`. Section included as the orchestrator generates VALIDATION.md for a deterministic gate + keyspace + close-gate phase.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit v3 on Microsoft.Testing.Platform (MTP) |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| Quick run command (hermetic) | compiled `BaseApi.Tests.exe --filter-not-trait Category=RealStack` (NOT `dotnet test --filter` — MTP0001 ignores it) |
| Full suite command (live, gate) | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release --no-build` (runs RealStack live, no filter) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| TEST-01 (keyspace) | 2 replicas write distinct per-instance keys + SADD index; restarting→unhealthy never absent; dead→TTL-expiry+SREM | manual/operator (lifecycle) | runbook `62-HUMAN-UAT.md` steps via `docker`/`redis-cli` | ❌ Wave (new runbook) |
| TEST-01 (deterministic) | gate reads per-instance keys correctly | xUnit RealStack | full-suite run; new fabricated-key test | ❌ Wave (new test) |
| TEST-02 (gate ≥1-healthy / 422) | fabricated unhealthy/stale sibling + healthy → admit; none → 422 RFC7807 | xUnit RealStack (fabricated keys, D-04) | full-suite run; new test mirroring `GateACompositionE2ETests` | ❌ Wave (new test) |
| TEST-02 (probe stale verdict) | stale L1 → Unhealthy + summary | hermetic (clock) | `LivenessWatchdogHealthCheckTests` (REUSE) | ✅ exists (Phase 61) |
| TEST-02 (probe live wiring) | `/health/live` 200 + summary on real replica | manual/operator | runbook `docker exec wget /health/live` | ❌ Wave (new runbook) |
| TEST-03 (close gate) | N=3 GREEN + triple-SHA net-zero + DLQ 0 + 0-warning | operator gate | `pwsh -File scripts/phase-62-close.ps1` | ❌ Wave (clone of 58) |

### Sampling Rate
- **Per task commit:** hermetic suite (`--filter-not-trait Category=RealStack`) — proves new tests COMPILE + hermetic green (D-14).
- **Per wave merge:** build gate both configs 0-warning + hermetic suite green.
- **Phase gate:** operator-gated live N=3×GREEN close run (`phase-62-close.ps1`, ~50 min/run, D-15) + the multi-container lifecycle runbook steps. TEST-01/02/03 stay unticked until the operator records GREEN.

### Wave 0 Gaps
- [ ] `scripts/phase-62-close.ps1` — clone of `phase-58-close.ps1` with the D-09 prefix exclusion + retitle (covers TEST-03). AST-valid + exists (D-14).
- [ ] New xUnit RealStack test(s) for the deterministic gate verdicts (D-04 fabricated keys) — `[Trait("Category","RealStack")]` + `[Trait("Phase","62")]` (covers TEST-01 deterministic, TEST-02 gate).
- [ ] Fabricated-key seeding helper co-located with `PollForHealthyLivenessAsync`.
- [ ] `62-HUMAN-UAT.md` operator runbook (mirror `58-HUMAN-UAT.md` structure) — lifecycle proofs (2-replica self-register, restart→unhealthy, TTL-expiry+SREM, `docker exec` probe) + N=3 GREEN record block.
- [ ] Retag SC1/SC2/SC3 + GateAComposition `[Trait("Phase","58")]` → `[Trait("Phase","62")]` (D-10).
- [ ] `compose.yaml` `processor-sample` reshape to `deploy.replicas:2` (D-01).
- REUSE (no Wave-0 work): `LivenessWatchdogHealthCheckTests` (probe verdict), `RealStackWebAppFactory`, `RealStackNetZeroSweepFixture`, `Processor.BadConfig` tier.

## Security Domain

> `security_enforcement` absent in CONTEXT → treated as enabled.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V5 Input Validation | yes | The gate treats every per-instance value as EXTERNAL self-registered data: invalid JSON/null summary → malformed (422), never 500 (`ProcessorLivenessValidator.cs:59-64`). Fabricated keys must not break this. |
| V7 Error/Logging (info disclosure) | yes | The 422 reason is COUNTS ONLY — never instanceIds/connection strings/stack traces (`ProcessorLivenessValidator.cs:74-76`). The probe `Data` carries ONLY the three SchemaOutcome strings, no secrets (`LivenessWatchdogHealthCheck.cs` + `LivenessWatchdogHealthCheckTests.AssertSummaryDataPresent` no-secrets guard). New tests/runbook must not surface secrets. |
| V6 Cryptography | no (SHA-256 is integrity snapshot, not crypto secret) | `Get-FileHash -Algorithm SHA256` for net-zero — never hand-roll. |

### Known Threat Patterns for this stack
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Stale/mixed-version image / identity divergence | Spoofing | clean host build + `--build` rebuild + `^[a-f0-9]{64}$` SourceHash validation + real-container liveness poll (D-16; phase-58 T-58-12) |
| False GREEN / net-zero snapshot integrity | Repudiation | clean redis keyspace BEFORE-dirty trap + N=3 identical-fact-count Smell-A guard (D-11/D-13; phase-58 T-58-13) |
| Fabricated-key index pollution leaking into a live test | Tampering | SREM fabricated index members in teardown; distinct throwaway procId where possible (Pitfall 6) |
| `processor-badconfig` DoS under profile | accept | Gate A stay-up posture (MarkReady, no crash-loop, withholds MarkHealthy); bounded operator-supervised run (phase-58 T-58-14) |

## Sources

### Primary (HIGH confidence — read this session, file:line cited)
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — key formats (PerInstance/InstanceIndex, prefix const).
- `src/Messaging.Contracts/Projections/ProcessorLivenessEntry.cs` — JSON shape + `[JsonPropertyName]` pins + `Create` factory.
- `src/Messaging.Contracts/Projections/LivenessStatus.cs` — Healthy/Unhealthy consts.
- `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` — Interval/StartupInterval/Ttl defaults + TTL formula.
- `src/BaseProcessor.Core/Liveness/ProcessorLivenessWriter.cs` — derived TTL `max(interval×2, Ttl)`, SET+SADD+L1.
- `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` — healthy-only write (no-op when not healthy), interval=10.
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` — RF-01 durable-unhealthy write (lines 124-128, 169-173, 218-231, 300-328), interval=30.
- `src/BaseProcessor.Core/Liveness/LivenessWatchdogHealthCheck.cs` — probe verdict (null/stale→Unhealthy, summary in Data).
- `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` — ≥1-healthy gate, 422, lazy-SREM, malformed/unhealthy/stale counting.
- `tests/BaseApi.Tests/Features/Liveness/LivenessWatchdogHealthCheckTests.cs` — RF-02 hermetic stale-L1 verdict tests (FakeTimeProvider).
- `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` — `RealStackWebAppFactory`, seed helpers, `PollForHealthyLivenessAsync`, net-zero teardown.
- `tests/BaseApi.Tests/Orchestrator/RealStackNetZeroSweepFixture.cs` — net-zero sweep (skp:data:*/skp:msg:* + DLQ purge).
- `scripts/phase-58-close.ps1` — verbatim clone source; D-09 exclusion at lines 303/379.
- `compose.yaml:229-324` — keeper (replicas:2 template), processor-sample (D-01 target), processor-badconfig (D-05).
- `src/Processor.Sample/appsettings.json` — version 3.5.0, Interval/Ttl/BackoffCap.
- `.planning/phases/61-.../61-VERIFICATION.md` — confirms hermetic + Kestrel probe coverage already exists.
- `.planning/phases/58-.../58-HUMAN-UAT.md` — runbook structure to mirror; v6 "absent" three-signal proof (now superseded by "unhealthy" in v7).
- `.planning/REQUIREMENTS.md` / `.planning/ROADMAP.md` — TEST-01/02/03, success criteria, Out-of-Scope.

### Secondary (MEDIUM)
- `Processor.BadConfig` source files present (`src/Processor.BadConfig/*.cs`) — confirms the D-05 reuse target exists; did not read the clash-type body (not load-bearing — Gate A path already verified in phase-58).

## Metadata

**Confidence breakdown:**
- Exact contracts (keys/JSON/TTL): HIGH — read directly from source with file:line.
- RF-01/02/03 verdicts: HIGH — each resolved against the actual writer/test/options source.
- Clone deltas: HIGH — phase-58-close.ps1 read in full; the single load-bearing change (D-09) located at exact lines.
- Compose reshape: HIGH — current tiers + keeper template read directly.
- `SchemaOutcome` literal values (SUCCESS/FAIL): MEDIUM — inferred from JSON comment + usage, not the SoT file directly (A1).

**Research date:** 2026-06-13
**Valid until:** stable until the v7 liveness contract or close protocol changes (no churn expected within this milestone) — ~30 days.

## RESEARCH COMPLETE
