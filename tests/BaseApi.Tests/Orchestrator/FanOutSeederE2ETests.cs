using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using BaseApi.Service.Features.Assignment;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using Npgsql;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Self-verifying RealStack fan-out workflow SEEDER (D-01..D-04) — the runnable artifact Phases 67/68
/// invoke via <c>dotnet test --filter "FullyQualifiedName~FanOutSeeder"</c>, and the single fact that
/// PROVES WF-01 + WF-02.
/// </summary>
/// <remarks>
/// <para>
/// Builds the v8.0.0 fan-out topology <c>A→B→C→{D1→E1→F1, D2→E2→F2}</c> against the live host stack via
/// REST only (no raw SQL inserts): 1 workflow (entry = A, cron <c>*/30 * * * * *</c>), 9 steps all bound
/// to ONE shared <c>processor-sample</c> id, exactly 8 <c>step_next_steps</c> edges, and 9 step-bound
/// assignments each carrying a <c>{ number:int, label:"Step_*" }</c> JSON payload. The single workflow's
/// <c>AssignmentIds</c> binds all 9 (two-sided binding — Pitfall 2).
/// </para>
/// <para>
/// <b>Idempotent by sentinel name (D-04).</b> The build routine GET-matches an existing workflow named
/// <c>v8-fanout-proof</c> and returns its id without creating duplicates (workflows have no unique-name
/// constraint, so the fixed sentinel name is the idempotency key — mirrors
/// <see cref="SampleRoundTripE2ETests.SeedConfigSchemaAsync"/>). The processor is resolved by-source-hash
/// GET-or-create via the reused <see cref="SampleRoundTripE2ETests.SeedProcessorAsync"/>.
/// </para>
/// <para>
/// <b>Reverse-topological create order.</b> Both <c>step_next_steps</c> FKs are <c>OnDelete(Restrict)</c>,
/// so every <c>NextStepId</c> must already exist when its parent step is created. Steps are therefore
/// created sinks-first (F1, F2 → E1, E2 → D1, D2 → C → B → A), yielding exactly the 8 edges
/// A→B, B→C, C→D1, C→D2, D1→E1, D2→E2, E1→F1, E2→F2 with F1/F2 as zero-outgoing sinks.
/// </para>
/// <para>
/// Tagged <c>Category=RealStack</c> so the hermetic filter (<c>Category!=RealStack</c>) excludes it; it
/// runs live (against a reset-clean DB brought up by the Phase-65 harness) via the
/// <c>~FanOutSeeder</c> filter. Reuses <see cref="SampleRoundTripE2ETests.RealStackWebAppFactory"/> for the
/// in-proc WebApi → host-stack overrides (Postgres 5433 / Redis 6380 / RMQ 5673 / otel 4317).
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Collection("Observability")]
public sealed class FanOutSeederE2ETests
{
    // Stable sentinel workflow name — the idempotency key the seed routine GET-matches on (D-04).
    private const string FanOutWorkflowName = "v8-fanout-proof";

    // 6-field seconds-cron (v8.0.0 feature) — NOT the stale 5-field linear-seed cron.
    private const string FanOutCron = "*/30 * * * * *";

    // Host Postgres connection string for the Npgsql self-verification reads (mirrors
    // SampleRoundTripE2ETests.RealStackWebAppFactory.HostPostgres :474-475).
    private const string HostPostgres =
        "Host=localhost;Port=5433;Database=stepsdb;Username=postgres;Password=postgres;Maximum Pool Size=20;Timeout=15";

    // FIXED label→number mapping (D-08). Order matters ONLY for the number assignment; create order is
    // reverse-topological (sinks first) and is driven by the explicit edge wiring below.
    private static readonly IReadOnlyDictionary<string, int> NodeNumbers = new Dictionary<string, int>
    {
        ["Step_A"] = 1,
        ["Step_B"] = 2,
        ["Step_C"] = 3,
        ["Step_D1"] = 4,
        ["Step_E1"] = 5,
        ["Step_F1"] = 6,
        ["Step_D2"] = 7,
        ["Step_E2"] = 8,
        ["Step_F2"] = 9,
    };

    // The exact 8-edge node-pair set the reverse-topo build produces (WF-01).
    private static readonly HashSet<string> ExpectedEdges = new(StringComparer.Ordinal)
    {
        "Step_A->Step_B",
        "Step_B->Step_C",
        "Step_C->Step_D1",
        "Step_C->Step_D2",
        "Step_D1->Step_E1",
        "Step_D2->Step_E2",
        "Step_E1->Step_F1",
        "Step_E2->Step_F2",
    };

    // The 9-label node set every assignment payload must collectively cover (WF-02).
    private static readonly HashSet<string> AllNodeLabels = new(NodeNumbers.Keys, StringComparer.Ordinal);

    // Per-node payload label shape (WF-02): "Step_" + the verbatim node token, no extra prefix.
    private static readonly Regex LabelRegex =
        new("^Step_(A|B|C|D1|E1|F1|D2|E2|F2)$", RegexOptions.Compiled);

    /// <summary>
    /// WF-01 + WF-02 acceptance in ONE runnable RealStack fact (the artifact Phases 67/68 invoke). Seeds
    /// the fan-out graph TWICE against a reset-clean live DB, asserts the workflow id is stable across the
    /// two calls (idempotent — D-04), then self-verifies all SPEC acceptance counts, the exact 8-edge set,
    /// F1/F2 zero-outgoing, and the 9 distinct <c>{int number, Step_* label}</c> payloads via direct Npgsql
    /// reads (REST read DTOs do not surface junction rows).
    /// </summary>
    [Fact]
    public async Task FanOutSeeder_SeedsAndSelfVerifies()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new SampleRoundTripE2ETests.RealStackWebAppFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        // Run-twice idempotency in ONE fact (RESEARCH Open-Q2): the 2nd call must GET-match the sentinel
        // name and return the SAME workflow id without creating duplicates (counts stay 1/9/9/8).
        var wfId1 = await SeedFanOutAsync(client, ct);
        var wfId2 = await SeedFanOutAsync(client, ct);
        Assert.Equal(wfId1, wfId2);

        await using var conn = new NpgsqlConnection(HostPostgres);
        await conn.OpenAsync(ct);

        // ---- SPEC acceptance counts (presume a reset-clean DB; the 67/68 harness resets BEFORE the seeder) ----
        Assert.Equal(1, await ScalarCountAsync(
            conn, "SELECT count(*) FROM workflows WHERE cron_expression = '*/30 * * * * *'", ct,
            "workflows-with-6-field-cron != 1 — DB not reset-clean before seeder, or cron not 6-field."));
        Assert.Equal(9, await ScalarCountAsync(
            conn, "SELECT count(*) FROM steps", ct, "steps != 9 — DB not reset-clean before seeder."));
        Assert.Equal(1, await ScalarCountAsync(
            conn, "SELECT count(DISTINCT processor_id) FROM steps", ct,
            "distinct processor_id != 1 — all 9 steps must bind the single shared processor-sample."));
        Assert.Equal(8, await ScalarCountAsync(
            conn, "SELECT count(*) FROM step_next_steps", ct, "step_next_steps edges != 8."));
        Assert.Equal(1, await ScalarCountAsync(
            conn, "SELECT count(*) FROM workflow_entry_steps", ct, "workflow_entry_steps != 1 (entry = A only)."));
        Assert.Equal(9, await ScalarCountAsync(
            conn, "SELECT count(*) FROM assignments", ct, "assignments != 9 — DB not reset-clean before seeder."));
        Assert.Equal(9, await ScalarCountAsync(
            conn, "SELECT count(*) FROM workflow_assignments", ct,
            "workflow_assignments != 9 — two-sided binding broken (Pitfall 2)."));

        // ---- Map each step_id -> node label via its assignment payload (join steps->assignments) ----
        var stepToLabel = new Dictionary<Guid, string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT a.step_id, a.payload FROM assignments a", conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var stepId = reader.GetGuid(0);
                var payload = reader.GetString(1);
                using var doc = JsonDocument.Parse(payload);
                var label = doc.RootElement.GetProperty("label").GetString()!;
                stepToLabel[stepId] = label;
            }
        }

        // ---- Edge-set verification: read the 8 (step_id, next_step_id) rows, map both to nodes ----
        var edges = new List<(Guid From, Guid To)>();
        await using (var cmd = new NpgsqlCommand("SELECT step_id, next_step_id FROM step_next_steps", conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                edges.Add((reader.GetGuid(0), reader.GetGuid(1)));
            }
        }

        Assert.Equal(8, edges.Count);
        var edgeNodePairs = edges
            .Select(e => $"{stepToLabel[e.From]}->{stepToLabel[e.To]}")
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(ExpectedEdges, edgeNodePairs);

        // ---- Sink zero-outgoing: F1 and F2 each have 0 outgoing edges ----
        foreach (var sinkLabel in new[] { "Step_F1", "Step_F2" })
        {
            var sinkStepId = stepToLabel.First(kv => kv.Value == sinkLabel).Key;
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM step_next_steps WHERE step_id = @sink", conn);
            cmd.Parameters.AddWithValue("sink", sinkStepId);
            var outgoing = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            Assert.True(outgoing == 0, $"{sinkLabel} must have 0 outgoing edges but had {outgoing}.");
        }

        // ---- Payload shape: each of the 9 assignments has an int `number` + a Step_* `label`; 9 distinct,
        //      covering the full node set, with the fixed number<->label mapping (D-08) ----
        var seenLabels = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("SELECT payload FROM assignments", conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var payload = reader.GetString(0);
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                var hasInt = root.TryGetProperty("number", out var numberEl)
                    && numberEl.ValueKind == JsonValueKind.Number
                    && numberEl.TryGetInt32(out _);
                Assert.True(hasInt, $"assignment payload missing an integer `number`: {payload}");
                var number = numberEl.GetInt32();

                Assert.True(
                    root.TryGetProperty("label", out var labelEl) && labelEl.ValueKind == JsonValueKind.String,
                    $"assignment payload missing a string `label`: {payload}");
                var label = labelEl.GetString()!;

                Assert.Matches(LabelRegex, label);
                Assert.Equal(NodeNumbers[label], number); // fixed mapping A=1..F2=9 (D-08)
                Assert.True(seenLabels.Add(label), $"duplicate label {label} — labels must be distinct.");
            }
        }

        Assert.Equal(9, seenLabels.Count);
        Assert.Equal(AllNodeLabels, seenLabels); // full node-set coverage
    }

    /// <summary>
    /// Run a single <c>SELECT count(*)</c> and return it as an int, with a clear assertion message if the
    /// command fails to produce a scalar (mirrors <c>StepsIntegrationTests.cs:71-79</c>).
    /// </summary>
    private static async Task<int> ScalarCountAsync(
        NpgsqlConnection conn, string sql, CancellationToken ct, string failMessage)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        Assert.True(result is not null and not DBNull, failMessage);
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// POST one step-bound assignment carrying a <c>{ number, label }</c> JSON payload string (jsonb
    /// server-side) and return the new assignment id (Pattern 1; <see cref="AssignmentCreateDto"/>).
    /// </summary>
    internal static async Task<Guid> SeedAssignmentAsync(
        HttpClient client, Guid stepId, int number, string label, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { number, label }); // {"number":1,"label":"Step_A"}
        var dto = new AssignmentCreateDto(
            Name: $"asg-{label}-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            StepId: stepId,
            Payload: payload);
        var resp = await client.PostAsJsonAsync("/api/v1/assignments", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    /// <summary>
    /// Extended step seed that accepts a custom Name + <paramref name="nextStepIds"/> (the existing
    /// <see cref="SampleRoundTripE2ETests.SeedStepAsync"/> hardcodes <c>NextStepIds: null</c>). POSTs
    /// <see cref="StepCreateDto"/> with <c>StepEntryCondition.Always</c> and returns the new step id.
    /// </summary>
    internal static async Task<Guid> SeedStepWithNextAsync(
        HttpClient client, Guid processorId, string node, List<Guid>? nextStepIds, CancellationToken ct)
    {
        var dto = new StepCreateDto(
            Name: $"step-{node}-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: processorId,
            NextStepIds: nextStepIds,
            EntryCondition: StepEntryCondition.Always);
        var resp = await client.PostAsJsonAsync("/api/v1/steps", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    /// <summary>
    /// Idempotent fan-out build routine. On a clean DB it creates exactly 1 workflow / 9 steps / 8 edges /
    /// 9 assignments and returns the new workflow id; if a workflow named <see cref="FanOutWorkflowName"/>
    /// already exists it GET-matches and returns the existing id WITHOUT creating duplicates (D-04).
    /// </summary>
    internal static async Task<Guid> SeedFanOutAsync(HttpClient client, CancellationToken ct)
    {
        // 1. Resolve the genuine processor-sample SourceHash via reflection (SampleRoundTripE2ETests:111-113)
        //    — the ONE shared processor id for all 9 steps.
        var hash = typeof(global::Processor.Sample.SampleProcessor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "SourceHash").Value!;
        var procId = await SampleRoundTripE2ETests.SeedProcessorAsync(client, hash, ct);

        // 2. IDEMPOTENCY GATE (mirror SeedConfigSchemaAsync :383-400) — GET-match the sentinel name.
        var all = await client.GetFromJsonAsync<List<WorkflowReadDto>>("/api/v1/workflows", ct);
        var existing = all!.FirstOrDefault(w => w.Name == FanOutWorkflowName);
        if (existing is not null)
        {
            return existing.Id; // 2nd run no-ops; id stable (WF-02 / D-04)
        }

        // 3. REVERSE-TOPOLOGICAL step create (sinks first — both step_next_steps FKs are OnDelete(Restrict),
        //    so each NextStepId must already exist). Yields exactly 8 edges:
        //    A->B, B->C, C->D1, C->D2, D1->E1, D2->E2, E1->F1, E2->F2.
        var node = new Dictionary<string, Guid>(StringComparer.Ordinal);
        node["Step_F1"] = await SeedStepWithNextAsync(client, procId, "F1", null, ct);
        node["Step_F2"] = await SeedStepWithNextAsync(client, procId, "F2", null, ct);
        node["Step_E1"] = await SeedStepWithNextAsync(client, procId, "E1", new List<Guid> { node["Step_F1"] }, ct);
        node["Step_E2"] = await SeedStepWithNextAsync(client, procId, "E2", new List<Guid> { node["Step_F2"] }, ct);
        node["Step_D1"] = await SeedStepWithNextAsync(client, procId, "D1", new List<Guid> { node["Step_E1"] }, ct);
        node["Step_D2"] = await SeedStepWithNextAsync(client, procId, "D2", new List<Guid> { node["Step_E2"] }, ct);
        node["Step_C"] = await SeedStepWithNextAsync(
            client, procId, "C", new List<Guid> { node["Step_D1"], node["Step_D2"] }, ct);
        node["Step_B"] = await SeedStepWithNextAsync(client, procId, "B", new List<Guid> { node["Step_C"] }, ct);
        node["Step_A"] = await SeedStepWithNextAsync(client, procId, "A", new List<Guid> { node["Step_B"] }, ct);

        // 4. Per node create one step-bound assignment with the FIXED number<->label mapping (D-08). The
        //    `label` is the verbatim token (e.g. "Step_A") — NO extra prefix (Phase 64 D-10).
        var assignmentIds = new List<Guid>(NodeNumbers.Count);
        foreach (var (label, number) in NodeNumbers)
        {
            var id = await SeedAssignmentAsync(client, node[label], number, label, ct);
            assignmentIds.Add(id);
        }

        // 5. Create the single workflow: entry = A only, all 9 assignments bound, 6-field seconds-cron.
        var wfDto = new WorkflowCreateDto(
            Name: FanOutWorkflowName,
            Version: "1.0.0",
            Description: null,
            EntryStepIds: new List<Guid> { node["Step_A"] },
            AssignmentIds: assignmentIds,
            CronExpression: FanOutCron);
        var wfResp = await client.PostAsJsonAsync("/api/v1/workflows", wfDto, ct);
        wfResp.EnsureSuccessStatusCode();
        var wf = await wfResp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);
        return wf!.Id;
    }
}
