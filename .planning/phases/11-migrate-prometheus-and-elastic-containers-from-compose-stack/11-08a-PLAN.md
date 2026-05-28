---
phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
plan: 08a
type: execute
wave: 6
depends_on:
  - "11-06"
files_modified:
  - tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs
  - tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs
autonomous: true
requirements:
  - OBSERV-08
  - HEALTH-05
  - OBSERV-13
must_haves:
  truths:
    - "`HealthEndpointsTests.cs` no longer references `OtelCollectorFixture` anywhere — direct instantiation on line 80 + 4 nested `: OtelCollectorFixture` subclasses + `factory.ReadExportedLogs()` + `factory.FlushAsync()` all replaced"
    - "4 nested fixture subclasses (`HealthDeadPostgresFixture`, `HealthLiveLocalhostFixture`, `HealthFilterEnabledFixture`, `HealthNoStartupCompletionFixture`) now inherit from `Phase11WebAppFactory` (or `Phase8WebAppFactory` for fixtures that don't need OTel test-only overrides — see Task 0 case analysis)"
    - "`Test_HealthEndpoints_Absent_From_OTLP_Logs` fact migrated from file-exporter readback to ES polling: drives 10 health probes, polls ES via `ElasticsearchTestClient.PollEsForLog` with a regex query OR `match_phrase` query against `/health/` substring in the body field; asserts ZERO hits within an 8s budget (negative-assertion asymmetric polling shape per Plan 11-08b LogLevelFilterTests precedent)"
    - "`OtelCollectorFixture.cs` is DELETED — after this plan's edits, `grep -rn 'OtelCollectorFixture' tests/ src/` returns ZERO matches (HealthEndpointsTests was the LAST consumer outside the Wave-5 migration targets)"
    - "All 7 Phase 5 health-probe facts in `HealthEndpointsTests` still GREEN against the live stack (no behavioral regression from the rebase)"
    - "`dotnet build SK_P.sln -c Release --no-restore` exits 0 zero-warning after the rebase + fixture deletion"
  artifacts:
    - path: "tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs"
      provides: "Rebased Phase 5 health-probe facts — 4 nested fixtures inherit from Phase11WebAppFactory; OTLP-log-absence fact uses ES polling"
      contains: "Phase11WebAppFactory"
      absent_pattern: "OtelCollectorFixture|ReadExportedLogs|FlushAsync"
  key_links:
    - from: "tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs (4 nested fixtures + Test_HealthEndpoints_Absent_From_OTLP_Logs fact)"
      to: "tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs"
      via: "inheritance — each nested fixture replaces `: OtelCollectorFixture` with `: Phase11WebAppFactory` (or `: Phase8WebAppFactory` for the 2 non-OTel fixtures)"
      pattern: ": Phase11WebAppFactory"
    - from: "Test_HealthEndpoints_Absent_From_OTLP_Logs"
      to: "tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs"
      via: "fact body constructs `new ElasticsearchTestClient()` and queries ES for log docs containing `/health/` path strings; asserts ZERO hits"
      pattern: "PollEsForLog"
---

<objective>
**Pre-determined HealthEndpointsTests rebase case** (resolves checker BLOCKER #2 — original Plan 11-08 Task 0 was a non-Nyquist-compliant decision tree). The planner read `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` at revision time and verified the file IS **Case A** (HealthEndpointsTests references OtelCollectorFixture as a base class for nested fixtures AND uses its file-exporter API directly in one fact).

Specifically, the file contains:
- **Line 80** (`Test_HealthStartup_200_After_GateFlipped_By_HostedService`): direct `await using var factory = new OtelCollectorFixture();` — REPLACE with `Phase11WebAppFactory`.
- **Line 215** (nested subclass): `private sealed class HealthDeadPostgresFixture : OtelCollectorFixture` — REBASE to `Phase8WebAppFactory` (no OTel test-only overrides needed; only env-var-based ConnectionStrings__Postgres override in ctor).
- **Line 241** (nested subclass): `private sealed class HealthLiveLocalhostFixture : OtelCollectorFixture` — REBASE to `Phase8WebAppFactory` (same pattern as HealthDeadPostgresFixture; only env-var override in ctor).
- **Line 267** (nested subclass + lines 162-182 fact): `private sealed class HealthFilterEnabledFixture : OtelCollectorFixture` + `Test_HealthEndpoints_Absent_From_OTLP_Logs` fact body — REBASE inheritance to `Phase11WebAppFactory` AND migrate the fact body from `factory.ReadExportedLogs()` (file-exporter API) to `new ElasticsearchTestClient().PollEsForLog(...)` with a negative assertion.
- **Line 288** (nested subclass): `private sealed class HealthNoStartupCompletionFixture : OtelCollectorFixture` — REBASE to `Phase8WebAppFactory` (no OTel needs; only DI service removal in ConfigureWebHost).

This plan executes the rebase + fact migration + `OtelCollectorFixture.cs` deletion in a single atomic commit. The deletion is safe because Plan 11-08b (Wave 6, runs after this plan) migrates LogExportTests + LogLevelFilterTests + MetricsExportTests — but those 3 classes are migrated independently of HealthEndpointsTests (no inheritance chain). After Plan 11-08a lands, the last 3 OtelCollectorFixture consumers in the suite are the 3 Wave-5-target test classes Plan 11-08b rewrites; deleting `OtelCollectorFixture.cs` HERE (after rebasing HealthEndpointsTests) would break those 3 classes until Plan 11-08b runs.

**Critical sequencing decision:** Plan 11-08a rebases HealthEndpointsTests but DOES NOT delete `OtelCollectorFixture.cs`. The 3 Wave-5-migration-targets (LogExportTests, LogLevelFilterTests, MetricsExportTests) still reference `OtelCollectorFixture` until Plan 11-08b lands. Plan 11-08c (the final wave) deletes `OtelCollectorFixture.cs` after Plan 11-08b's commit lands. This preserves the build-green invariant between plans.

**Revised commit scope for Plan 11-08a (this plan):**
1. Replace `new OtelCollectorFixture()` at line 80 of HealthEndpointsTests with `new Phase11WebAppFactory()`.
2. Rebase the 4 nested fixture subclasses (3 of them off `Phase8WebAppFactory`; 1 off `Phase11WebAppFactory`).
3. Migrate `Test_HealthEndpoints_Absent_From_OTLP_Logs` from file-exporter readback to ES polling (negative assertion, ZERO hits expected).
4. Build verification + commit.

NOT in this plan's scope (deferred to Plan 11-08c):
- Deletion of `OtelCollectorFixture.cs` (still needed by LogExportTests + LogLevelFilterTests + MetricsExportTests until Plan 11-08b migrates them).
- Phase 11 closing 3-consecutive-GREEN cadence.
- psql `\l` SHA-256 BEFORE/AFTER snapshot.
- Phase 11 SUMMARY narrative.

Purpose: pre-determine the HealthEndpointsTests rebase case during planning (per checker fix-hint #2 — "No more conditional decision trees in auto-typed tasks"); deliver a single surgical edit + 1 fact migration to a single test file; keep the working tree green between plans by leaving `OtelCollectorFixture.cs` in place for Plan 11-08b's consumers.

Output: A single atomic commit `refactor(observability): rebase HealthEndpointsTests onto Phase11WebAppFactory + migrate health-OTLP-absence fact to ES polling` modifying exactly 1 file. After commit, `dotnet build SK_P.sln -c Release --no-restore` exits 0 zero-warning AND `dotnet test SK_P.sln --no-restore -c Release --filter "FullyQualifiedName~HealthEndpointsTests"` exits 0 with all 7 facts GREEN.
</objective>

<execution_context>
@$HOME/.claude/get-shit-done/workflows/execute-plan.md
@$HOME/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/STATE.md
@.planning/ROADMAP.md
@.planning/REQUIREMENTS.md
@.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-CONTEXT.md
@.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-RESEARCH.md
@.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-PATTERNS.md
@tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs
@tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs
@tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs
@tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs
@tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs

<interfaces>
<!-- HealthEndpointsTests current shape (verified by Read at revision time) -->

Direct OtelCollectorFixture consumer (line 80):
```csharp
[Fact]
public async Task Test_HealthStartup_200_After_GateFlipped_By_HostedService()
{
    var ct = TestContext.Current.CancellationToken;
    await using var factory = new OtelCollectorFixture();   // <-- REPLACE with new Phase11WebAppFactory()
    await factory.InitializeAsync();
    using var client = factory.CreateClient();
    // ... assertions ...
}
```

File-exporter-API consumer (`Test_HealthEndpoints_Absent_From_OTLP_Logs`, lines 134-182):
```csharp
[Fact]
public async Task Test_HealthEndpoints_Absent_From_OTLP_Logs()
{
    var ct = TestContext.Current.CancellationToken;
    // ... 1-second drain pre-wait ...
    await Task.Delay(TimeSpan.FromSeconds(1), ct);
    await using var factory = new HealthFilterEnabledFixture();
    await factory.InitializeAsync();
    using var client = factory.CreateClient();
    for (int i = 0; i < 10; i++)
    {
        _ = await client.GetAsync("/health/live", ct);
        _ = await client.GetAsync("/health/ready", ct);
        _ = await client.GetAsync("/health/startup", ct);
    }
    await factory.FlushAsync(TimeSpan.FromSeconds(1));                                 // <-- REMOVE (file-exporter API)

    var logs = factory.ReadExportedLogs();                                              // <-- REPLACE with ES poll
    var rawJoined = string.Concat(logs.Select(l => l.GetRawText()));
    Assert.DoesNotContain("/health/live", rawJoined);
    Assert.DoesNotContain("/health/ready", rawJoined);
    Assert.DoesNotContain("/health/startup", rawJoined);
}
```

Nested fixture subclasses (lines 215, 241, 267, 288):
```csharp
private sealed class HealthDeadPostgresFixture       : OtelCollectorFixture { ... }   // line 215; only env-var override in ctor; REBASE to Phase8WebAppFactory
private sealed class HealthLiveLocalhostFixture      : OtelCollectorFixture { ... }   // line 241; only env-var override in ctor; REBASE to Phase8WebAppFactory
private sealed class HealthFilterEnabledFixture      : OtelCollectorFixture { ... }   // line 267; uses ConfigureAppConfiguration for LogLevel override; REBASE to Phase11WebAppFactory
private sealed class HealthNoStartupCompletionFixture: OtelCollectorFixture { ... }   // line 288; uses ConfigureTestServices to remove StartupCompletionService; REBASE to Phase8WebAppFactory
```

3 of the 4 nested fixtures don't need OTel test-only overrides (Phase11WebAppFactory's `PeriodicExportingMetricReaderOptions` override + env-var pin + LogLevel override ctor) — they can rebase to `Phase8WebAppFactory` for minimum-diff posture. The `HealthFilterEnabledFixture` IS LogLevel-override-dependent (sets `Microsoft.AspNetCore=Warning`), so it benefits from `Phase11WebAppFactory` — but since Phase8WebAppFactory also accepts arbitrary `ConfigureAppConfiguration` overrides via `ConfigureWebHost`, either base works. Recommendation: REBASE all 4 to Phase8WebAppFactory for minimum-coupling; the OTel-specific fact (`Test_HealthEndpoints_Absent_From_OTLP_Logs`) gets Phase11WebAppFactory directly OR continues to use HealthFilterEnabledFixture (which now subclasses Phase8WebAppFactory + adds the LogLevel config).

**Executor decision** (baked here, not at execution time): Rebase ALL 4 nested fixtures to `Phase8WebAppFactory`. The OTel-export-interval override that lives in Phase11WebAppFactory is irrelevant to HealthEndpointsTests (health probes don't emit metrics — they're filtered by the collector D-04 processor). The env-var pin is the only Phase11 carry-over; since `HealthDeadPostgresFixture` and `HealthLiveLocalhostFixture` already set `ConnectionStrings__Postgres` in their ctors before the base ctor runs, the env-var pin pattern is identical. The `Test_HealthEndpoints_Absent_From_OTLP_Logs` fact gets `Phase11WebAppFactory` directly (for the metric-export-interval override) BUT the OTel pipeline still ships the log to ES regardless of metric interval — so even Phase8WebAppFactory would work. Going with Phase11WebAppFactory for `HealthFilterEnabledFixture` is the safest posture.

EsIndexNames constants (Wave 0 verified in Plan 11-06):
- EsIndexNames.LogsDataStream — index path
- EsIndexNames.CorrelationIdFieldPath — for term queries
- EsIndexNames.ResourceAttributesFieldPath
- EsIndexNames.FieldShape — "raw" or "otel"
</interfaces>
</context>

<tasks>

<task type="auto">
  <name>Task 0: Sanity-grep HealthEndpointsTests to confirm the rebase case at execution time</name>
  <files>tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs</files>
  <read_first>
    - tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs (full file — verify the 5 OtelCollectorFixture references the planner found at revision time are still there at execution time; if the file has changed since revision, abort and re-route)
    - tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs (verify still exists — Plan 11-06 + 11-07 should NOT have deleted it)
  </read_first>
  <action>
    Defensive re-verification of the rebase case before any edits land. Run:

    ```bash
    grep -n "OtelCollectorFixture" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs
    ```

    Expected output (matches planner's revision-time observation; line numbers may shift slightly):
    - Line 80: `await using var factory = new OtelCollectorFixture();`
    - Line 215: `private sealed class HealthDeadPostgresFixture : OtelCollectorFixture`
    - Line 241: `private sealed class HealthLiveLocalhostFixture : OtelCollectorFixture`
    - Line 267: `private sealed class HealthFilterEnabledFixture : OtelCollectorFixture`
    - Line 288: `private sealed class HealthNoStartupCompletionFixture : OtelCollectorFixture`

    AND:
    ```bash
    grep -n "ReadExportedLogs\|FlushAsync" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs
    ```

    Expected output:
    - Line 174 (approx): `await factory.FlushAsync(TimeSpan.FromSeconds(1));`
    - Line 176 (approx): `var logs = factory.ReadExportedLogs();`

    If grep output differs SIGNIFICANTLY (e.g., zero OtelCollectorFixture references — file already rebased by a prior plan), STOP and mark this plan as no-op. If grep output matches (allowing line-number drift), proceed to Task 1.

    Also verify OtelCollectorFixture.cs still exists (it should — Plans 11-05 + 11-06 + 11-07 do not touch it; it's deleted only in Plan 11-08c):
    ```bash
    test -f tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs && echo "still present"
    ```
  </action>
  <verify>
    <automated>test -f tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs exits 0; test -f tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs exits 0; grep -c "OtelCollectorFixture" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs returns at least 5 (verified case A still applies); grep -c "ReadExportedLogs\|FlushAsync" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs returns at least 2 (file-exporter API still in place — fact still needs migration)</automated>
  </verify>
  <done>Rebase case A re-confirmed at execution time; HealthEndpointsTests still has 5 OtelCollectorFixture references + the file-exporter-API consumer fact still in its original shape; OtelCollectorFixture.cs still present (Plan 11-08c will delete it).</done>
</task>

<task type="auto" tdd="true">
  <name>Task 1: Rebase HealthEndpointsTests — replace 5 OtelCollectorFixture references + migrate OTLP-absence fact to ES polling</name>
  <files>tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs</files>
  <read_first>
    - tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs (full file — current shape from Task 0 sanity grep)
    - tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs (the Phase 11 OTel-aware fixture; used for the OTLP-absence fact)
    - tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs (the Postgres-throwaway base used by the 3 non-OTel fixtures)
    - tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs (PollEsForLog API)
    - tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs (constants for ES query construction)
  </read_first>
  <behavior>
    - Existing 7 facts MUST still pass after the rebase (zero behavioral regression):
      - Test_HealthLive_Always_200_NoDbCheck (uses HealthDeadPostgresFixture)
      - Test_HealthReady_503_When_Postgres_Unreachable (uses HealthDeadPostgresFixture)
      - Test_HealthReady_200_When_Postgres_Reachable (uses HealthLiveLocalhostFixture)
      - Test_HealthStartup_200_After_GateFlipped_By_HostedService (uses direct fixture — migrate from new OtelCollectorFixture() to new Phase11WebAppFactory())
      - Test_HealthStartup_503_Before_GateFlipped (uses HealthNoStartupCompletionFixture)
      - Test_HealthReady_Body_Has_Per_Check_Status_But_No_Sensitive_Fields (uses HealthLiveLocalhostFixture)
      - Test_HealthEndpoints_Absent_From_OTLP_Logs (uses HealthFilterEnabledFixture — fact body migrated from file-exporter readback to ES polling, NEGATIVE assertion)
    - The 4 nested fixture subclasses MUST preserve their ctor body / env-var pattern / ConfigureWebHost override semantics — only the base class identifier changes.
    - The OTLP-absence fact MUST drive 10 probes (3 endpoints × 10 iterations) and assert ZERO ES hits for log docs containing `/health/` substrings within an 8-second budget.
  </behavior>
  <action>
    Five surgical edits in `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs`.

    **Edit 1 — line 80**: replace `new OtelCollectorFixture()` with `new Phase11WebAppFactory()` in `Test_HealthStartup_200_After_GateFlipped_By_HostedService`:
    - BEFORE: `await using var factory = new OtelCollectorFixture();`
    - AFTER:  `await using var factory = new Phase11WebAppFactory();`

    No other changes to this fact's body. The `await factory.InitializeAsync(); using var client = factory.CreateClient();` lines stay byte-identical (Phase11WebAppFactory implements IAsyncLifetime via Phase8WebAppFactory inheritance, same pattern).

    **Edit 2 — lines 215-232 (`HealthDeadPostgresFixture`)**: change base class from `OtelCollectorFixture` to `Phase8WebAppFactory`:
    - BEFORE: `private sealed class HealthDeadPostgresFixture : OtelCollectorFixture`
    - AFTER:  `private sealed class HealthDeadPostgresFixture : Phase8WebAppFactory`

    Preserve the entire ctor body + DisposeAsync override byte-identical (the env-var-in-ctor pattern is the same; the `base.DisposeAsync()` call resolves to Phase8WebAppFactory's DisposeAsync now, which handles the Postgres connection cleanup).

    **Edit 3 — lines 241-255 (`HealthLiveLocalhostFixture`)**: change base class:
    - BEFORE: `private sealed class HealthLiveLocalhostFixture : OtelCollectorFixture`
    - AFTER:  `private sealed class HealthLiveLocalhostFixture : Phase8WebAppFactory`

    Preserve ctor body + DisposeAsync byte-identical.

    **Edit 4 — lines 267-282 (`HealthFilterEnabledFixture`)**: change base class to `Phase11WebAppFactory` (this is the one fixture that benefits from OTel test-only overrides since its consumer `Test_HealthEndpoints_Absent_From_OTLP_Logs` exercises the full SDK → collector → ES path):
    - BEFORE: `private sealed class HealthFilterEnabledFixture : OtelCollectorFixture`
    - AFTER:  `private sealed class HealthFilterEnabledFixture : Phase11WebAppFactory`

    Preserve the `ConfigureWebHost` override body byte-identical (the `base.ConfigureWebHost(builder)` call resolves to Phase11WebAppFactory's version which composes Phase8's Postgres wiring + the metric-export-interval override + the optional log-level override — the `Microsoft.AspNetCore=Warning` AddInMemoryCollection still works exactly the same because IConfiguration sources are additive).

    **Edit 5 — lines 288-304 (`HealthNoStartupCompletionFixture`)**: change base class:
    - BEFORE: `private sealed class HealthNoStartupCompletionFixture : OtelCollectorFixture`
    - AFTER:  `private sealed class HealthNoStartupCompletionFixture : Phase8WebAppFactory`

    Preserve the entire `ConfigureWebHost` override body byte-identical (removing the StartupCompletionService registration via Type predicate is purely DI-graph manipulation; base class identity is irrelevant).

    **Edit 6 — Test_HealthEndpoints_Absent_From_OTLP_Logs (lines 134-182)**: migrate from file-exporter readback to ES polling. The fact body needs significant rewrite. Replace the entire body (preserve attribute decorations + comment block + 10-iteration probe loop; replace the FlushAsync + ReadExportedLogs + DoesNotContain assertions with ES polling):

    ```csharp
    [Fact]
    public async Task Test_HealthEndpoints_Absent_From_OTLP_Logs()
    {
        var ct = TestContext.Current.CancellationToken;
        // This test's assertions are path-string-only — it only checks that "/health/live",
        // "/health/ready", and "/health/startup" do NOT appear in any exported OTLP log
        // record. Whether the underlying NpgSql health check returns Healthy or Unhealthy is
        // irrelevant; even if /health/ready returns 503, the path-string negation is still
        // what is being verified. NO Postgres reachability dependency.
        //
        // PHASE 11 D-16 MIGRATION (Plan 11-08a): was Phase 5 file-exporter + position-marker
        // readback against telemetry.jsonl; now polls Elasticsearch via ElasticsearchTestClient
        // and asserts NO log doc contains `/health/` substrings within an 8s budget. Negative
        // assertion budget is shorter than positive (30s in LogExportTests) — long enough for
        // ES indexing pipeline to flush any actual hit, short enough to keep suite wall-clock
        // manageable (RESEARCH PATTERNS option a + Plan 11-08b LogLevelFilterTests precedent).
        //
        // RACE-CONDITION GUARD: the 1-second pre-wait before fixture init lets the Collector
        // drain prior-test buffered records before our probe loop starts. Carries forward from
        // Phase 5 fix-forward.
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        await using var factory = new HealthFilterEnabledFixture();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        // Per-probe-cycle unique correlation id so a positive-control "this probe set was here"
        // sentinel exists in ES (defensive — the fact asserts negative, but a unique id lets us
        // distinguish "no /health/* hits because filtering works" from "no /health/* hits
        // because OTLP transport silently dropped everything").
        var probeBatchId = $"{Guid.NewGuid():N}";
        client.DefaultRequestHeaders.Add("X-Probe-Batch-Id", probeBatchId);

        // Issue 10 probe requests to swamp the export stream IF the filter is broken.
        // Status codes are intentionally ignored — see comment above.
        for (int i = 0; i < 10; i++)
        {
            _ = await client.GetAsync("/health/live", ct);
            _ = await client.GetAsync("/health/ready", ct);
            _ = await client.GetAsync("/health/startup", ct);
        }

        // Poll ES with a regex query against the body / scope / attributes for any `/health/`
        // substring. Short budget (8s) for negative assertion — RESEARCH PATTERNS option a.
        using var es = new ElasticsearchTestClient();

        // Use a query_string query that matches ANY doc containing the literal `/health/`
        // substring in any indexed text field. The query body shape is field-shape-agnostic
        // (works for both `mapping.mode: none` (raw OTLP) and `mapping.mode: otel` (normalized)
        // outputs since query_string searches all _source by default).
        var queryBody = """
          {
            "size": 10,
            "query": { "query_string": { "query": "\\\"/health/\\\"" } }
          }
          """;

        var hit = await es.PollEsForLog(queryBody, timeoutMs: 8_000);
        Assert.Null(hit);
    }
    ```

    The query body uses ES `query_string` syntax with the literal string `/health/` (quoted with escaped backslashes inside the C# string). The 8-second budget is calibrated against ES indexing lag (1-3s typical per RESEARCH Pattern 2; 8s gives ~3x safety margin). If any of the 30 probe requests (10 iterations × 3 endpoints) leaked into ES, the 8-second budget would surface them.

    The `probeBatchId` correlation header (line `client.DefaultRequestHeaders.Add("X-Probe-Batch-Id", probeBatchId);`) is defensive forensic — not asserted on. Future debugging can grep ES for the batch id to verify the OTLP path was alive even though no hits appeared.

    Do NOT touch the 6 other facts (Test_HealthLive_Always_200_NoDbCheck, Test_HealthReady_503_When_Postgres_Unreachable, etc.) — their bodies stay byte-identical except for the base-class change in their backing fixtures (handled by Edits 2-5).

    **Critical**: do NOT delete `OtelCollectorFixture.cs` in this plan. Plans 11-08b (LogExport/LogLevelFilter/MetricsExport migration) + 11-08c (Phase 11 close + OtelCollectorFixture deletion) handle the file's lifecycle. After this plan's commit lands, `OtelCollectorFixture.cs` should still exist and the 3 Wave-5-migration-target test classes should still compile.
  </action>
  <verify>
    <automated>! grep "OtelCollectorFixture" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs (negation — no references after rebase + migration); ! grep "ReadExportedLogs\|FlushAsync" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs (negation — file-exporter API gone); grep -c "Phase8WebAppFactory" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs returns at least 3 (3 nested fixtures rebase to Phase8); grep -c "Phase11WebAppFactory" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs returns at least 2 (line 80 direct use + HealthFilterEnabledFixture base); grep "ElasticsearchTestClient" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs returns at least 1 (OTLP-absence fact migrated to ES poll); grep "PollEsForLog" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs returns 1; grep "timeoutMs: 8_000" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs returns 1 (negative-assertion 8s budget); grep "Assert.Null(hit)" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs returns 1; test -f tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs exits 0 (file PRESERVED — Plan 11-08c will delete it); dotnet build SK_P.sln -c Release --no-restore exits 0 zero-warning; dotnet test SK_P.sln --no-restore -c Release --filter "FullyQualifiedName~HealthEndpointsTests" exits 0 with 7 facts green</automated>
  </verify>
  <done>HealthEndpointsTests has zero OtelCollectorFixture references; 3 nested fixtures inherit Phase8WebAppFactory; 1 nested fixture + 1 direct-use call site use Phase11WebAppFactory; OTLP-absence fact migrated to ES polling with 8s negative-assertion budget; all 7 facts GREEN against live stack; OtelCollectorFixture.cs preserved for Plan 11-08b consumers.</done>
</task>

<task type="auto">
  <name>Task 2: Commit HealthEndpointsTests rebase + OTLP-absence migration</name>
  <files>tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs</files>
  <read_first>
    - git status (confirm scope: exactly 1 file modified — HealthEndpointsTests.cs)
  </read_first>
  <action>
    Stage ONLY `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs`:
    - `git add tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs`

    Create commit with the exact message:
    ```
    refactor(observability): rebase HealthEndpointsTests onto Phase11WebAppFactory + migrate health-OTLP-absence fact to ES polling
    ```

    Use a HEREDOC for the commit message body. Verify `git status --porcelain` returns empty after commit.

    Do NOT push. Do NOT delete OtelCollectorFixture.cs — it's still in use by LogExportTests + LogLevelFilterTests + MetricsExportTests until Plan 11-08b lands.
  </action>
  <verify>
    <automated>git log -1 --format=%s returns "refactor(observability): rebase HealthEndpointsTests onto Phase11WebAppFactory + migrate health-OTLP-absence fact to ES polling"; git show --stat HEAD lists exactly 1 file modified (HealthEndpointsTests.cs); git status --porcelain returns empty; test -f tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs exits 0 (fixture file preserved for Plan 11-08b)</automated>
  </verify>
  <done>Single-file commit landed; working tree clean; OtelCollectorFixture.cs intentionally preserved for Plan 11-08b's consumers; HealthEndpointsTests fully migrated off the file-exporter path.</done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| (no new) | Same boundaries as Plans 11-06 + 11-07. This plan migrates existing facts; no new attack surface. |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-11-08a-T1 (rebase introduces silent behavioral regression in 7 health-probe facts) | T (Test correctness) | tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs | mitigate | Task 1 build + test gate runs `dotnet test --filter "FullyQualifiedName~HealthEndpointsTests"` post-rebase; all 7 facts must be GREEN. The 4 nested-fixture rebases are pure base-class swaps (no body changes); the 1 direct-fixture call site (line 80) is a single-token swap; the OTLP-absence fact body is rewritten but the assertion contract is preserved (negative assertion on `/health/` substring). **Verify:** Task 1 verify gate requires 7 facts GREEN. |
| T-11-08a-T2 (deleting OtelCollectorFixture.cs in this plan would break LogExport/LogLevelFilter/MetricsExportTests build) | A (Availability — build failure between plans) | tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs | mitigate | Plan explicitly preserves OtelCollectorFixture.cs until Plan 11-08b migrates the 3 remaining consumers. Task 1 verify gate includes `test -f tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs exits 0` to catch any accidental deletion. **Verify:** Plan 11-08b runs `! grep -rn "OtelCollectorFixture" tests/ src/` after migrating its 3 targets; Plan 11-08c performs the final `git rm`. |
| T-11-08a-T3 (negative-assertion fact false-positive — `/health/` substring present in body but 8s budget too short to see it) | T (Test correctness) | Test_HealthEndpoints_Absent_From_OTLP_Logs | mitigate | 8s budget exceeds typical ES indexing lag (1-3s per RESEARCH Pattern 2). If a `/health/` substring leaked into any of 30 probe results (10 iterations × 3 endpoints), at least one would appear in ES within 8s. Probe batch id (defensive forensic — not asserted on) lets future debugging distinguish "filter works" from "OTLP transport silently dropped everything". **Verify:** Task 1 verify gate runs the fact GREEN against live stack. |
| T-11-08a-T4 (ES query_string syntax escaping differences across mapping.mode shapes) | T (Test correctness — false negative when filter IS broken) | ES query body | mitigate | `query_string` searches all `_source` fields by default, working for both `mapping.mode: none` (raw OTLP shape with capital "Attributes") and `mapping.mode: otel` (normalized lowercase). The literal substring `/health/` is the same in both shapes. If a future ES version changes `query_string` semantics, switch to an explicit `multi_match` query against `body`/`Body` + `attributes.*`/`Attributes.*`. **Verify:** Task 1 verify gate confirms 0 hits within 8s budget — if the gate fails, fall back to multi_match. |
</threat_model>

<verification>
- `! grep "OtelCollectorFixture" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` (no references after rebase).
- `! grep "ReadExportedLogs\|FlushAsync" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` (file-exporter API gone).
- `grep -c "Phase8WebAppFactory" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` returns at least 3.
- `grep -c "Phase11WebAppFactory" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` returns at least 2.
- `grep "ElasticsearchTestClient" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` returns at least 1.
- `grep "PollEsForLog" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` returns 1.
- `grep "timeoutMs: 8_000" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` returns 1.
- `grep "Assert.Null(hit)" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` returns 1.
- `test -f tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` exits 0 (preserved for Plan 11-08b).
- `dotnet build SK_P.sln -c Release --no-restore` exits 0 zero-warning.
- `dotnet test SK_P.sln --no-restore -c Release --filter "FullyQualifiedName~HealthEndpointsTests"` exits 0 with 7 facts green.
- `git log -1 --format=%s` matches `refactor(observability): rebase HealthEndpointsTests onto Phase11WebAppFactory + migrate health-OTLP-absence fact to ES polling`.
- `git show --stat HEAD` shows exactly 1 file modified.
- `git status --porcelain` returns empty post-commit.
</verification>

<success_criteria>
1. HealthEndpointsTests.cs has ZERO references to `OtelCollectorFixture` (5 references at planning time, all replaced).
2. 4 nested fixture subclasses inherit from `Phase8WebAppFactory` (3 of them) and `Phase11WebAppFactory` (1 of them — `HealthFilterEnabledFixture`).
3. Direct fixture call site on line 80 (`Test_HealthStartup_200_After_GateFlipped_By_HostedService`) uses `new Phase11WebAppFactory()`.
4. `Test_HealthEndpoints_Absent_From_OTLP_Logs` fact body migrated from `factory.ReadExportedLogs()` + `factory.FlushAsync()` to `new ElasticsearchTestClient().PollEsForLog(queryBody, timeoutMs: 8_000)` with `Assert.Null(hit)` negative assertion.
5. `OtelCollectorFixture.cs` PRESERVED at this commit's HEAD (Plan 11-08b's consumers still reference it).
6. Solution builds zero-warning Release.
7. All 7 HealthEndpointsTests facts GREEN against the live stack.
8. Single git commit `refactor(observability): rebase HealthEndpointsTests onto Phase11WebAppFactory + migrate health-OTLP-absence fact to ES polling` exists at HEAD; modifies exactly 1 file; working tree clean post-commit.
</success_criteria>

<output>
After completion, create `.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-08a-SUMMARY.md`.
</output>
</content>
