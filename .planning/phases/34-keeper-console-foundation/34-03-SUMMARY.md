---
phase: 34-keeper-console-foundation
plan: 03
subsystem: keeper
tags: [keeper, compose, replicas, masstransit, competing-consumer, round-robin, dependency-firewall, compose-facts, console-mirror]

# Dependency graph
requires:
  - phase: 34-keeper-console-foundation
    plan: 01
    provides: "Keeper.csproj (leaner reference closure) + KeeperQueues.FaultRecovery const + SK_P.sln entry"
  - phase: 34-keeper-console-foundation
    plan: 02
    provides: "Keeper console body — Program.cs (thin-shell), PlaceholderConsumer/Definition, KeeperPlaceholder, appsettings (8083), Dockerfile (aspnet:8.0)"
  - phase: 19-orchestrator-console
    provides: "compose orchestrator: tier + FanOutBroadcastTests — the compose-tier + round-robin-inverse analogs"
  - phase: 18-baseconsole-core
    provides: "ConsoleTestHostFixture / ConsoleHostBootTests / ConsoleDependencyFirewallTests — the boot + firewall analogs"
  - phase: 28-processor-sample
    provides: "ComposeYamlFacts block-scoped processor-sample facts — the compose-fact analog"
provides:
  - "compose.yaml keeper: tier — deploy.replicas:2, NO container_name, NO baseapi-service dep, NO Orchestrator__InstanceId, NO ports:, 8083 /health/ready, build src/Keeper/Dockerfile (KEEP-03 / D-04/D-05)"
  - "ComposeYamlFacts 4 keeper facts (block-scoped via tempered-greedy regex) — CI guard that would catch a container_name / baseapi-dep / replicas regression in the keeper block"
  - "KeeperRoundRobinTests — KEEP-02 binding-shape proof (ONE shared endpoint + ONE consumer => count==1, load-balance not fan-out)"
  - "KeeperHostBootFixture/Tests — KEEP-01 boot/readiness proof (three-call seam + placeholder + RetryOptions against dead deps; IBusControl resolvable)"
  - "KeeperDependencyFirewallTests — KEEP-01 reference-closure guard (no BaseApi.*/EF/Npgsql/Quartz/Cronos)"
  - "BaseApi.Tests.csproj ProjectReference to src/Keeper/Keeper.csproj"
affects: [35-keeper-fault-consumers, 38-close-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Replicated compose tier: mirror the single-instance console tier MINUS container_name (a named container cannot scale) PLUS deploy.replicas:N — docker compose up brings up N replicas round-robining the single durable shared queue"
    - "Tempered-greedy block-scoped compose regex `(?ms)^  <tier>:(?:(?!^  \\S).)*?<assertion>` — bounds a negative DoesNotMatch to ONE service block so a neighbouring tier's legitimate container_name cannot false-pass (a plain prefix-glob WOULD cross the boundary and false-pass — empirically verified)"
    - "Round-robin binding-shape proof = the INVERSE of the fan-out test: plain AddConsumer + stable EndpointName, ONE consumer type => GetConsumerHarness<T>().Consumed count==1 (materialized to an int local to dodge the xUnit2013 collection-size analyzer)"

key-files:
  created:
    - tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs
    - tests/BaseApi.Tests/Keeper/KeeperHostBootFixture.cs
    - tests/BaseApi.Tests/Keeper/KeeperHostBootTests.cs
    - tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs
  modified:
    - compose.yaml
    - tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs
    - tests/BaseApi.Tests/BaseApi.Tests.csproj

key-decisions:
  - "Negative compose facts use a TEMPERED-greedy window `(?:(?!^  \\S).)*?` instead of the plan's draft `[\\s\\S]*?...(?=^  \\S|\\z)` — the draft FALSE-PASSES on the real file (the lazy prefix-glob walks past the keeper block into the processor-sample/baseapi-service tiers that legitimately carry container_name/baseapi-service). Verified empirically: real keeper block => no match (passes); planted container_name/baseapi line inside keeper block => match (regression caught)."
  - "Added a 4th keeper fact (ComposeYaml_Keeper_Has_No_BaseApi_Dependency, D-05) beyond the plan's 3 — Rule 2 missing-critical: the keeper tier's defining divergence vs processor-sample is the ABSENCE of the baseapi-service dependency, so a block-scoped negative guard for it is part of the KEEP-03 acceptance surface."
  - "Round-robin count materialized to `var consumedCount = ...Count(); Assert.Equal(1, consumedCount)` (the FanOut analog's countA/countB int-local pattern) — Assert.Equal(1, <collection>.Count()) directly trips the xUnit2013 'use Assert.Single' analyzer under TreatWarningsAsErrors. The plan must_have `contains: Assert.Equal(1,` is satisfied by the int-local form."
  - "RetryOptions registered in the round-robin harness via .Configure<RetryOptions>(o => o.Limit = 3) (the IServiceCollection extension, returns IServiceCollection so the fluent chain continues to BuildServiceProvider) — cleaner than the plan's draft .AddOptions<>().Configure().Services chain; both register IOptions<RetryOptions> so the definition ctor resolves."

patterns-established:
  - "Pattern: replicated console compose tier = single-instance tier MINUS container_name PLUS deploy.replicas:N, validated by docker compose config + block-scoped ComposeYamlFacts"
  - "Pattern: tempered-greedy block-scoped negative compose regex to prevent cross-tier false-pass on shared keys (container_name, baseapi-service)"

requirements-completed: [KEEP-01, KEEP-02, KEEP-03]

# Metrics
duration: 9min
completed: 2026-06-05
---

# Phase 34 Plan 03: Keeper Compose Tier + Hermetic Tests Summary

**Integrated Keeper into the compose stack (the `keeper:` tier — `deploy.replicas: 2`, NO `container_name`, NO `baseapi-service` dep, NO `Orchestrator__InstanceId`, NO `ports:`, 8083 `/health/ready`, builds `src/Keeper/Dockerfile`; KEEP-03 / D-04/D-05) and closed KEEP-01/02/03 with CI-enforceable guards: 4 block-scoped `ComposeYamlFacts` (tempered-greedy regex, empirically proven to catch a planted `container_name`/`baseapi-service` regression), the `KeeperRoundRobinTests` binding-shape proof (count==1, load-balance not fan-out), the `KeeperHostBoot` boot/readiness proof (IBusControl resolvable against dead deps), and the `KeeperDependencyFirewallTests` reference-closure guard (no BaseApi.*/EF/Npgsql/Quartz/Cronos). `docker compose config` parses, the keeper image builds, the 3 Keeper tests + 18 ComposeYamlFacts are GREEN, and the full hermetic suite is 454-pass zero-regression. The live multi-replica + compose-health smoke is authored as an operator runbook (Pending-Verification) and auto-approved per the project's human-verify precedent — the authoritative live gate is Phase 38.**

## Performance

- **Duration:** ~9 min
- **Started:** 2026-06-05T14:41:02Z
- **Completed:** 2026-06-05T14:49:34Z
- **Tasks:** 2 autonomous executed + 1 operator gate auto-approved (runbook authored)
- **Files modified:** 7 (4 created, 3 modified)

## Accomplishments

- **compose.yaml `keeper:` tier (KEEP-03 / D-04/D-05)** — mirrors the `orchestrator:` tier with the scaling divergences: NO `container_name` (a named container cannot scale), `deploy.replicas: 2` (`docker compose up` brings up 2 replicas), depends_on rabbitmq+redis `service_healthy` only (NO `baseapi-service` — Keeper resolves no identity over the WebApi), NO `Orchestrator__InstanceId` env, NO `ports:` block (8083 is container-internal), 8083 `/health/ready` healthcheck, builds `src/Keeper/Dockerfile`. `docker compose config keeper` resolves the service cleanly; the keeper image builds end-to-end via the compose build target.
- **ComposeYamlFacts — 4 block-scoped keeper facts (KEEP-03 CI guard)** — `_Has_Keeper_Service_Block` (Dockerfile + 8083), `_Keeper_Declares_Two_Replicas` (deploy.replicas:2), `_Keeper_Has_No_ContainerName` (D-04), `_Keeper_Has_No_BaseApi_Dependency` (D-05). The two negatives use a TEMPERED-greedy window `(?ms)^  keeper:(?:(?!^  \S).)*?<key>` that refuses to cross a top-level service header, so a neighbouring tier's legitimate `container_name`/`baseapi-service` cannot false-pass. Empirically verified BOTH directions (real keeper block => no match/pass; planted line inside keeper block => match/regression caught).
- **KeeperRoundRobinTests (KEEP-02)** — the inverse of `FanOutBroadcastTests`: plain `AddConsumer<PlaceholderConsumer, PlaceholderConsumerDefinition>` + the stable `EndpointName` => ONE shared durable endpoint; a single publish is consumed exactly once (`Assert.Equal(1, consumedCount)`, load-balance not broadcast). RetryOptions bound so the definition's `IOptions<RetryOptions>` ctor resolves.
- **KeeperHostBootFixture/Tests (KEEP-01)** — `KeeperHostBootFixture` subclasses `ConsoleTestHostFixture` and overrides `ConfigureBuilder` to compose Keeper's exact seam (three AddBaseConsole* calls + the placeholder consumer + `RetryOptions` binding) against dead Redis + unreachable RabbitMQ; `KeeperHostBootTests` asserts the host boots and `IBusControl` is resolvable (readiness flips via the kept default readiness service — D-06).
- **KeeperDependencyFirewallTests (KEEP-01)** — anchors on `typeof(global::Keeper.Consumers.PlaceholderConsumer).Assembly`; `ForbiddenPrefixes` extends the inherited `BaseApi.Core`/`Microsoft.EntityFrameworkCore`/`Npgsql` bans with `Quartz` + `Cronos` (D-07 — Keeper does not schedule). The reflection over `GetReferencedAssemblies()` finds zero violations.
- **BaseApi.Tests.csproj** — `ProjectReference` to `src/Keeper/Keeper.csproj` (same ItemGroup as the Orchestrator ref) so the 4 Keeper test files compile and the firewall test can reflect the Keeper assembly.

## Task Commits

Each task committed atomically (scoped paths ONLY — the in-progress `.planning/` archive deletions + untracked `launchSettings.json`/`psql-*.txt` left untouched, NOT staged, NOT reverted, per established project precedent):

1. **Task 1: keeper compose tier + 4 block-scoped ComposeYamlFacts (KEEP-03)** — `b07967a` (feat)
2. **Task 2: 4 Keeper hermetic tests (round-robin/host-boot/firewall) + csproj ProjectReference (KEEP-01/02)** — `57a859b` (test)

## Files Created/Modified

- `compose.yaml` — added the `keeper:` tier (replicas:2, no container_name, no baseapi dep, 8083 health, src/Keeper/Dockerfile).
- `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` — 4 block-scoped keeper facts (tempered-greedy negatives).
- `tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs` — KEEP-02 count==1 binding-shape proof.
- `tests/BaseApi.Tests/Keeper/KeeperHostBootFixture.cs` — KEEP-01 boot fixture (subclasses ConsoleTestHostFixture).
- `tests/BaseApi.Tests/Keeper/KeeperHostBootTests.cs` — KEEP-01 IBusControl-resolvable boot assertion.
- `tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs` — KEEP-01 reference-closure guard (+ Quartz/Cronos).
- `tests/BaseApi.Tests/BaseApi.Tests.csproj` — ProjectReference to src/Keeper/Keeper.csproj.

## Verification Evidence

- `docker compose config --quiet` → **EXIT 0** (keeper tier with `deploy.replicas` is schema-valid); `docker compose config keeper` resolves the service with replicas:2, no container_name, no baseapi-service dep, 8083 health, no ports.
- `docker build -f src/Keeper/Dockerfile -t keeper-3403-check .` → **BUILD_EXIT 0** (the compose build target produces the image end-to-end; throwaway image cleaned up).
- `dotnet build tests/BaseApi.Tests` → **0 Warning(s) / 0 Error(s)**; `dotnet build SK_P.sln -c Debug` → **0/0**.
- `dotnet test ... --filter-class "*Keeper*"` → **3 passed / 0 failed** (round-robin, host-boot, firewall).
- `dotnet test ... --filter-class "*ComposeYamlFacts*"` → **18 passed / 0 failed** (14 prior + 4 new keeper facts).
- `dotnet test ... --filter-not-trait "Category=RealStack"` → **454 passed / 0 failed / 0 skipped** (full hermetic suite, zero regression vs the prior baseline).
- Negative-regex empirical proof (PowerShell .NET regex against the on-disk compose.yaml): real keeper block => container_name match `False` + baseapi match `False` (assertions PASS); planted `container_name`/`baseapi-service` line inside the keeper block => match `True` (regression caught); replicas:2 => match `True`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Plan's draft negative-regex window FALSE-PASSES on the real compose.yaml**
- **Found during:** Task 1 (empirical regex verification, as the plan explicitly mandated before shipping).
- **Issue:** The plan's draft `Assert.DoesNotMatch(@"(?ms)^  keeper:[\s\S]*?container_name:[\s\S]*?(?=^  \S|\z)")` uses a lazy prefix-glob `[\s\S]*?` that the lookahead `(?=^  \S|\z)` does NOT confine — it walks PAST the keeper block into the following `processor-sample:`/`baseapi-service:` tiers (which legitimately carry `container_name`), so it MATCHES against the real file and the `DoesNotMatch` would FAIL (a false positive). Confirmed: real-file match returned `True` for both the container_name and baseapi negatives.
- **Fix:** Replaced the unconfined glob with a TEMPERED-greedy window `(?:(?!^  \S).)*?` that matches only characters NOT beginning a new `^  \S` top-level service header — bounding the search strictly to the keeper block. Re-verified: real keeper block => no match (passes); planted line inside keeper block => match (catches the regression). The plan itself flagged this exact risk ("do not ship a regex that matches the whole file") and required the empirical check — this is that check resolving to the correct pattern.
- **Files modified:** tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs
- **Committed in:** `b07967a` (Task 1).

**2. [Rule 2 - Missing Critical] Added a 4th keeper fact: no baseapi-service dependency (D-05)**
- **Found during:** Task 1.
- **Issue:** The plan listed 3 keeper facts but the keeper tier's defining divergence from the processor-sample tier is the ABSENCE of the `baseapi-service` dependency (D-05 — Keeper resolves no identity over the WebApi). Without a block-scoped guard, a future copy-paste of the processor-sample tier into keeper could silently re-introduce the baseapi dependency and the CI suite would not catch it.
- **Fix:** Added `ComposeYaml_Keeper_Has_No_BaseApi_Dependency` with the same tempered-greedy block-scoping; empirically proven to catch a planted `baseapi-service:` line inside the keeper block.
- **Files modified:** tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs
- **Committed in:** `b07967a` (Task 1).

**3. [Rule 1 - Bug] Round-robin Assert.Equal(1, ...Count()) trips xUnit2013 under TreatWarningsAsErrors**
- **Found during:** Task 2 (first build).
- **Issue:** `Assert.Equal(1, consumer.Consumed.Select<KeeperPlaceholder>(ct).Count())` directly fires the xUnit2013 analyzer ("Do not use Assert.Equal() to check for collection size; use Assert.Single") — a build ERROR under TreatWarningsAsErrors.
- **Fix:** Materialized the count to an int local first — `var consumedCount = ...Count(); Assert.Equal(1, consumedCount);` — exactly the `countA`/`countB` int-local pattern the FanOutBroadcastTests analog uses to dodge the same analyzer. The plan must_have `contains: "Assert.Equal(1,"` is satisfied by the int-local form. (Assert.Single was rejected because the load-bearing assertion must be a literal `Assert.Equal(1,` per the plan artifact spec, and Single does not express the "exactly one, not two" count discrimination as explicitly.)
- **Files modified:** tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs
- **Committed in:** `57a859b` (Task 2).

---

**Total deviations:** 3 auto-fixed (2 bugs — false-passing regex + analyzer error; 1 missing-critical — D-05 guard). **Impact on plan:** the regex fix is load-bearing (the draft would have shipped a false-passing CI guard); the others are correctness/coverage. No architectural change, no scope creep.

## Pending-Verification (Operator Runbook — LIVE multi-replica + compose-health smoke, KEEP-02/03 live half)

Auto-approved per the project's human-verify precedent (Phases 31/31.1/32.1/33). Phase progression is **NOT** blocked on this run — the hermetic Task-2 tests are the CI guard; this live smoke is the KEEP-02/03 acceptance proof captured for the operator. **The authoritative live close gate is Phase 38.** All commands are operator-run against the full Docker compose stack with REBUILT containers; none is executable/observable in the non-interactive execution environment.

**Pre-req:** rebuild the keeper image so the embedded SourceHash/closure matches current source (per the project's close-gate rebuild discipline):

```
docker compose build keeper
```

**Runbook:**

1. **Bring up the keeper tier (2 replicas):**
   ```
   docker compose up -d --build keeper
   ```
2. **Confirm 2 healthy replicas:**
   ```
   docker compose ps
   ```
   Expect `sk_p-keeper-1` and `sk_p-keeper-2` (compose may name them `keeper-1`/`keeper-2`) both `healthy`.
   - **A1 fallback:** if only ONE replica appears, the installed Docker may not honor `deploy.replicas` on `up`. Use:
     ```
     docker compose up -d --scale keeper=2 keeper
     ```
     Record which path was needed.
3. **Confirm the durable shared queue exists (net-zero readiness for Phase 38):**
   ```
   docker compose exec rabbitmq rabbitmqctl list_queues name | grep keeper-fault-recovery
   ```
   Expect exactly ONE durable queue named `keeper-fault-recovery` — NOT a GUID-suffixed / auto-delete name.
4. **Round-robin smoke:** publish N (e.g. 6) `KeeperPlaceholder` messages to `keeper-fault-recovery` (RabbitMQ management UI at the published rabbitmq port, or a small publisher — operator's choice), then:
   ```
   docker compose logs keeper
   ```
   Expect the `Keeper placeholder consumed (topology proof only)` log line SPLIT across BOTH replicas (≈3 per replica for N=6), NOT duplicated to both per message (that would be fan-out, a KEEP-02 regression).
5. **Full-stack health:**
   ```
   docker compose up -d
   docker compose ps
   ```
   Expect the keeper replicas `healthy` alongside orchestrator / processor-sample / baseapi-service.

**Expected results to record:** which compose-up path was used (`deploy.replicas` honored vs `--scale` fallback), the queue durability (durable, single, non-GUID name), and the log split across replicas.

**Failure triage:**
- Replicas not `healthy` → check `docker compose logs keeper` for bus-start failure; `/health/ready` is HARD-on-broker, so it stays unhealthy until rabbitmq is reachable (depends_on gates this, but a broker crash mid-run would flip it).
- Queue appears GUID-suffixed / auto-delete → the placeholder definition lost its stable `EndpointName` (would also break the close-gate net-zero SHA) — regression in `PlaceholderConsumerDefinition`.
- Log NOT split (every message in both replicas) → fan-out regression: a per-replica `.Endpoint(InstanceId/Temporary)` crept into the registration (the explicit anti-pattern).

## Net-Zero / Close-Gate Note (Phase 38)

The durable `keeper-fault-recovery` queue is **intentional and enduring** — it survives into Phase 35 (the real Fault<T> consumers reuse the same stable `KeeperQueues.FaultRecovery` endpoint name). The **Phase-38 close gate must account for this new queue in its `rabbitmqctl list_queues` baseline** (BEFORE and AFTER snapshots both include `keeper-fault-recovery`, so the triple-SHA stays net-zero). The plain-AddConsumer + stable-EndpointName shape (no per-replica auto-delete fan-out) is precisely what keeps the queue present in both snapshots.

## Issues Encountered

- The `--filter-method "*ComposeYaml_*Keeper*"` MTP wildcard surfaced the runner help text and reported a spurious "run failed" (no method matched the wildcard form). Re-ran with `--filter-class "*ComposeYamlFacts*"` (the repo's established MTP filter idiom) → 18 passed. Not a test failure.

## User Setup Required

None for the autonomous work. The operator runbook above requires a running Docker compose stack (operator-run, do-not-block).

## Next Phase Readiness

- **Phase 35** (real Keeper Fault<T> consumers) swaps `PlaceholderConsumer` + the local `KeeperPlaceholder` message wholesale, reusing the SAME stable `KeeperQueues.FaultRecovery` endpoint name and the SAME compose keeper tier — the queue (and its close-gate SHA), the replicas:2 tier, and the firewall/round-robin/boot guards all carry forward unchanged.
- **Phase 38** (close gate): add `keeper-fault-recovery` to the rabbitmq baseline; rebuild the keeper container before the live gate.
- No blockers.

## Threat Surface

No new surface beyond the plan's `<threat_model>`:
- **T-34-07** (Info Disclosure, secrets in compose): accept — guest/guest + Redis conn come from the compose env block, never the image; NO `ports:` block (no published host port → no new external attack surface). Confirmed in `docker compose config keeper`.
- **T-34-08** (Spoofing/EoP, replicated tier without container_name): accept — dropping container_name is REQUIRED for scaling (D-04) and matches the Compose Spec; the Dockerfile runs `USER app` non-root (Plan 02); no privilege change.
- **T-34-09** (DoS, keeper-fault-recovery queue churn): mitigated — the stable DURABLE queue (Plan 02 definition, no Temporary) + the round-robin test asserting the binding shape => the queue is in both close-gate rabbitmq snapshots (net-zero, Pitfall 1). Operator runbook step 3 verifies durability live.
No new network endpoint, auth path, file-access pattern, or schema change at a trust boundary.

## Self-Check: PASSED

- Files: `compose.yaml`, `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs`, `tests/BaseApi.Tests/BaseApi.Tests.csproj`, `tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs`, `tests/BaseApi.Tests/Keeper/KeeperHostBootFixture.cs`, `tests/BaseApi.Tests/Keeper/KeeperHostBootTests.cs`, `tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs`, `.planning/phases/34-keeper-console-foundation/34-03-SUMMARY.md` — all FOUND.
- Commits: `b07967a` (Task 1), `57a859b` (Task 2) — all FOUND.

---
*Phase: 34-keeper-console-foundation*
*Completed: 2026-06-05*
